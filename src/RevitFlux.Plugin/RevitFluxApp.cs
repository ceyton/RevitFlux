using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitFlux.Core.Session;
using RevitFlux.Plugin.Caching;
using RevitFlux.Plugin.Communication;
using RevitFlux.Plugin.Discovery;
using RevitFlux.Plugin.Threading;
using RevitFlux.Plugin.UI;
using System;
using System.Reflection;

namespace RevitFlux.Plugin;

public class RevitFluxApp : IExternalApplication
{
    private SocketService? _socketService;
    private CortexRouter? _router;
    private CortexSession? _session;
    private DocumentChangeWatcher? _cacheWatcher;
    private UIApplication? _uiApplication;
    private int _port = 8090;
    private Autodesk.Revit.UI.PushButton? _connectButton;
    private UI.AutoModeWindow? _autoModeWindow;

    public static RevitFluxApp? Instance { get; private set; }

    /// <summary>
    /// Fired on the calling thread whenever the server starts, stops, or crashes.
    /// Subscribers must marshal to the UI thread themselves if needed.
    /// </summary>
    public event Action? ServiceStateChanged;

    /// <summary>
    /// Returns true only if the socket service flag is set AND a live TCP
    /// connection to localhost:port succeeds. This catches cases where the
    /// listener thread died unexpectedly while the flag remained true.
    /// </summary>
    public bool IsServiceRunning
    {
        get
        {
            if (_socketService?.IsRunning != true) return false;
            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                probe.Connect("127.0.0.1", _port);
                return true;
            }
            catch
            {
                // Listener is gone — sync internal flag so next call is fast
                _socketService.Stop();
                return false;
            }
        }
    }

    public int Port => _port;
    public UIApplication? UiApplication => _uiApplication;
    public CortexRouter? Router => _router;
    public CortexSession? Session => _session;

    public Result OnStartup(UIControlledApplication application)
    {
        Instance = this;

        // Roslyn (send_code_to_revit, net8+) needs Microsoft.CodeAnalysis 4.12 plus its
        // System.Collections.Immutable / System.Reflection.Metadata 8.0 dependencies.
        // Revit loads every add-in into ONE shared AssemblyLoadContext and does not probe
        // our plugin folder for these, so a sibling add-in's older copy (or the missing
        // 8.0 deps) breaks the bind with "Could not load Microsoft.CodeAnalysis 4.12.0.0".
        // Serve our own bundled copies from the plugin folder so they always win.
        AppDomain.CurrentDomain.AssemblyResolve += ResolveBundledDependency;

        try
        {
            // Create ribbon panel
            CreateRibbonPanel(application);

            // Initialize session, router, and tools
            var store = new SessionStore();
            _session = new CortexSession(store);
            _session.ConfirmAction = (action, count, desc) =>
                ConfirmationHelper.ConfirmWithSession(action, count, desc, _session);
            _session.CriticalConfirmAction = ConfirmationHelper.ConfirmCritical;
            _session.AutoModeActivity += OnAutoModeActivity;
            ConfirmationHelper.AutoModeChanged += OnAutoModeChanged;
            var analyzer = new DocumentAnalyzer();

            _router = new CortexRouter(_session, analyzer);

            var toolsAssembly = LoadToolsAssembly();
            if (toolsAssembly != null)
            {
                _router.RegisterToolsFromAssembly(toolsAssembly);
            }

            // Create thread dispatcher for Revit main thread execution
            var executionHandler = new ToolExecutionHandler();
            var externalEvent = ExternalEvent.Create(executionHandler);
            var dispatcher = new RevitThreadDispatcher(executionHandler, externalEvent);
            _router.SetDispatcher(dispatcher);

            // Load disabled tools, read-only mode, and port from settings
            LoadDisabledTools();
            LoadReadOnlyMode();
            LoadPort();

            // Create socket service but do NOT start automatically
            _socketService = new SocketService(_router, _port);

            // Listen for document events
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
            application.ControlledApplication.DocumentClosing += OnDocumentClosing;

            // Subscribe the tool-result cache to model-change events. The watcher
            // bumps DocumentVersion and drops Document/Transaction entries so
            // cached reads can never outlive the model state they describe.
            _cacheWatcher = new DocumentChangeWatcher(_session);
            _cacheWatcher.Attach(application.ControlledApplication);

            // Capture UIApplication when Revit is idle (needed for ViewActivated hook)
            application.Idling += OnIdling;

            System.Diagnostics.Trace.WriteLine(
                $"[RevitFlux] Started. {_router.TotalToolCount} tools registered.");

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[RevitFlux] Startup failed: {ex}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            ConfirmationHelper.AutoModeChanged -= OnAutoModeChanged;
            _socketService?.Stop();
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
            application.Idling -= OnIdling;
            if (_uiApplication != null)
                _uiApplication.ViewActivated -= OnViewActivated;

            _cacheWatcher?.Dispose();
            _cacheWatcher = null;

            // Delete all TEMP scripts generated during this session
            CleanupTempScripts();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[RevitFlux] Shutdown error: {ex.Message}");
        }
        return Result.Succeeded;
    }

    public void StartService(Document? activeDocument = null)
    {
        if (_socketService != null && !_socketService.IsRunning)
        {
            // Initialize session with active document if available
            if (activeDocument != null && _router != null)
            {
                var locale = LocaleDetector.Detect(activeDocument);
                _router.OnDocumentChanged(activeDocument, locale);
                // Re-store UIApplication after session reinitialize (needed by send_code_to_revit)
                if (_uiApplication != null)
                    _session?.Store.Set("uiApplication", _uiApplication);
                System.Diagnostics.Trace.WriteLine(
                    $"[RevitFlux] Session initialized with document: {activeDocument.Title}, locale: {locale}");
            }

            _socketService.Start();

            UpdateConnectionButtonIcon();
            ServiceStateChanged?.Invoke();
        }
    }

    public void StopService()
    {
        _socketService?.Stop();
        UpdateConnectionButtonIcon();
        ServiceStateChanged?.Invoke();
    }

    private void UpdateConnectionButtonIcon()
    {
        if (_connectButton == null) return;
        bool active = IsServiceRunning;
        _connectButton.Image = IconFactory.CreateConnectionIcon(16, active);
        _connectButton.LargeImage = IconFactory.CreateConnectionIcon(32, active);
        _connectButton.ToolTip = active
            ? $"RevitFlux running on port {_port} — click to stop"
            : "Start RevitFlux server";
    }

    /// <summary>
    /// Called when Auto mode is activated or deactivated. Shows a non-modal
    /// floating window while Auto is active and closes it when Auto stops.
    /// Marshals to the UI thread since the activating "Auto" click and the
    /// deactivation paths can originate off the UI thread.
    /// </summary>
    private void OnAutoModeChanged(bool active)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke((System.Action)(() => OnAutoModeChanged(active)));
            return;
        }

        if (active)
        {
            if (_autoModeWindow != null) return; // already showing
            try
            {
                var revitHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                _autoModeWindow = new UI.AutoModeWindow(revitHandle);
                _autoModeWindow.StopRequested += OnAutoModeWindowStopRequested;
                _autoModeWindow.Closed += (_, _) => _autoModeWindow = null;
                _autoModeWindow.Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[RevitFlux] Could not show Auto mode window: {ex.Message}");
                _autoModeWindow = null;
            }
        }
        else
        {
            // Auto turned off from somewhere other than the window (e.g. document
            // close). Close without re-triggering the stop callback.
            _autoModeWindow?.CloseFromHost();
            _autoModeWindow = null;
        }
    }

    /// <summary>
    /// Forwards an Auto-mode auto-approval to the floating window. Marshals to
    /// the UI thread because the signal originates on the tool-execution thread.
    /// </summary>
    private void OnAutoModeActivity()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke((System.Action)(() => _autoModeWindow?.RegisterActivity()));
    }

    /// <summary>
    /// The floating window asked to stop Auto mode (button or X). Turn Auto off
    /// on the session; the resulting AutoModeChanged(false) is a no-op for the
    /// window since it is already closing.
    /// </summary>
    private void OnAutoModeWindowStopRequested()
    {
        if (_session != null)
            _session.AutoMode = false;
        ConfirmationHelper.NotifyAutoModeChanged(false);
    }

    private void CreateRibbonPanel(UIControlledApplication application)
    {
        const string tabName = "RevitFlux";
        application.CreateRibbonTab(tabName);
        RibbonPanel panel = application.CreateRibbonPanel(tabName, "AI Assistant");
        string assemblyLocation = Assembly.GetExecutingAssembly().Location;

        // Connection toggle button
        var connectBtnData = new PushButtonData(
            "ID_REVITFLUX_TOGGLE", "RevitFlux\r\nSwitch",
            assemblyLocation, "RevitFlux.Plugin.Commands.ToggleConnection");
        connectBtnData.ToolTip = "Start RevitFlux server";
        connectBtnData.Image = IconFactory.CreateConnectionIcon(16, false);
        connectBtnData.LargeImage = IconFactory.CreateConnectionIcon(32, false);
        _connectButton = panel.AddItem(connectBtnData) as Autodesk.Revit.UI.PushButton;

        // Settings button
        var settingsBtn = new PushButtonData(
            "ID_REVITFLUX_SETTINGS", "Settings",
            assemblyLocation, "RevitFlux.Plugin.Commands.OpenSettings");
        settingsBtn.ToolTip = "RevitFlux settings";
        settingsBtn.Image = IconFactory.CreateSettingsIcon(16);
        settingsBtn.LargeImage = IconFactory.CreateSettingsIcon(32);
        panel.AddItem(settingsBtn);

        // Note: Auto mode is stopped via a floating AutoModeWindow shown while
        // active (see OnAutoModeChanged), not a ribbon button.
    }

    private void OnDocumentOpened(object? sender, DocumentOpenedEventArgs args)
    {
        var doc = args.Document;
        if (doc == null) return;

        var locale = LocaleDetector.Detect(doc);
        _router!.OnDocumentChanged(doc, locale);

        // Re-store UIApplication after session reinitialize (needed by send_code_to_revit)
        if (_uiApplication != null)
            _session?.Store.Set("uiApplication", _uiApplication);

        System.Diagnostics.Trace.WriteLine(
            $"[RevitFlux] Document opened. Locale: {locale}, " +
            $"Capabilities: {_router!.GetAvailableToolNames().Count} tools available");
    }

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        try
        {
            // Stop the TCP server to prevent stale commands reaching a different document
            if (_socketService != null && _socketService.IsRunning)
            {
                _socketService.Stop();
                UpdateConnectionButtonIcon();
                ServiceStateChanged?.Invoke();
                System.Diagnostics.Trace.WriteLine(
                    "[RevitFlux] Server stopped: document closing");
            }

            // Clear session state (store, capabilities, locale). Reinitialize
            // sets AutoMode=false on the session; notify the UI so the floating
            // Auto mode window closes with the document.
            _session?.Reinitialize(new Core.Discovery.DocumentCapabilities(), "en");
            ConfirmationHelper.NotifyAutoModeChanged(false);

            System.Diagnostics.Trace.WriteLine(
                "[RevitFlux] Session reset: document closing");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[RevitFlux] Error on document closing: {ex.Message}");
        }
    }

    private void OnIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if (_uiApplication != null) return;
        _uiApplication = sender as UIApplication;

        // Hook ViewActivated to detect document switches
        if (_uiApplication != null)
        {
            _uiApplication.ViewActivated += OnViewActivated;

            // Store UIApplication in session for send_code_to_revit
            _session?.Store.Set("uiApplication", _uiApplication);

            // If a document is already open, initialize the session now
            var doc = _uiApplication.ActiveUIDocument?.Document;
            if (doc != null && _router != null &&
                _session?.Store.Get<object>("activeDocument") == null)
            {
                var locale = LocaleDetector.Detect(doc);
                _router.OnDocumentChanged(doc, locale);
                System.Diagnostics.Trace.WriteLine(
                    $"[RevitFlux] Session initialized from Idling: {doc.Title}, locale: {locale}");
            }
        }
    }

    private void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        var doc = e.CurrentActiveView?.Document;
        if (doc == null || _router == null) return;

        // Only update if document changed
        var currentDoc = _session?.Store.Get<object>("activeDocument");
        if (currentDoc != doc)
        {
            var locale = LocaleDetector.Detect(doc);
            _router.OnDocumentChanged(doc, locale);
            // Re-store UIApplication after session reinitialize (needed by send_code_to_revit)
            if (_uiApplication != null)
                _session?.Store.Set("uiApplication", _uiApplication);
            System.Diagnostics.Trace.WriteLine(
                $"[RevitFlux] Document switched: {doc.Title}, locale: {locale}");
        }
    }

    private void LoadPort()
    {
        try
        {
            string settingsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".revitflux", "settings.json");
            if (System.IO.File.Exists(settingsPath))
            {
                var json = System.IO.File.ReadAllText(settingsPath);
                var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    Newtonsoft.Json.Linq.JObject>(json);
                var port = settings?["Port"]?.ToObject<int>();
                if (port.HasValue && port.Value > 0 && port.Value <= 65535)
                {
                    _port = port.Value;
                    System.Diagnostics.Trace.WriteLine(
                        $"[RevitFlux] Port configured: {_port}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[RevitFlux] Could not load port setting: {ex.Message}");
        }
    }

    private void LoadReadOnlyMode()
    {
        try
        {
            string settingsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".revitflux", "settings.json");
            if (System.IO.File.Exists(settingsPath))
            {
                var json = System.IO.File.ReadAllText(settingsPath);
                var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    Newtonsoft.Json.Linq.JObject>(json);
                var readOnly = settings?["ReadOnlyMode"]?.ToObject<bool>() ?? false;
                _router!.ReadOnlyMode = readOnly;
                if (readOnly)
                    System.Diagnostics.Trace.WriteLine("[RevitFlux] Read-only mode is ON");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[RevitFlux] Could not load read-only setting: {ex.Message}");
        }
    }

    private void LoadDisabledTools()
    {
        try
        {
            string settingsPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".revitflux", "settings.json");
            if (System.IO.File.Exists(settingsPath))
            {
                var json = System.IO.File.ReadAllText(settingsPath);
                var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<
                    Newtonsoft.Json.Linq.JObject>(json);
                var disabled = settings?["DisabledTools"]?
                    .ToObject<string[]>() ?? Array.Empty<string>();
                _router!.SetDisabledTools(disabled);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[RevitFlux] Could not load disabled tools: {ex.Message}");
        }
    }

    private static void CleanupTempScripts()
    {
        var scriptsFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".revitflux", "scripts");
        if (!System.IO.Directory.Exists(scriptsFolder)) return;
        foreach (var file in System.IO.Directory.GetFiles(scriptsFolder, "*.cs"))
        {
            try
            {
                using var reader = new System.IO.StreamReader(file);
                var firstLine = reader.ReadLine() ?? "";
                if (firstLine.TrimStart().StartsWith("// TEMP", StringComparison.OrdinalIgnoreCase))
                    System.IO.File.Delete(file);
            }
            catch { }
        }
    }

    private Assembly? LoadToolsAssembly()
    {
        try
        {
            var pluginDir = System.IO.Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location)!;
            var toolsPath = System.IO.Path.Combine(pluginDir, "RevitFlux.Tools.dll");
            return Assembly.LoadFrom(toolsPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[RevitFlux] Could not load Tools assembly: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves Roslyn (Microsoft.CodeAnalysis*) and its System.Collections.Immutable /
    /// System.Reflection.Metadata 8.0 dependencies from the plugin's own folder. Revit's
    /// shared AssemblyLoadContext does not probe our folder for these, so without this a
    /// sibling add-in's older copy can win the bind (or the 8.0 deps go unfound), which
    /// breaks send_code_to_revit's Roslyn compiler on Revit 2025+.
    /// Scoped to the Roslyn dependency graph so it never hijacks Revit or other add-ins.
    /// </summary>
    private static Assembly? ResolveBundledDependency(object? sender, ResolveEventArgs args)
    {
        var requested = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(requested))
            return null;

        bool wanted = requested.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)
            || requested == "System.Collections.Immutable"
            || requested == "System.Reflection.Metadata";
        if (!wanted)
            return null;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dir == null)
                return null;
            var candidate = System.IO.Path.Combine(dir, requested + ".dll");
            return System.IO.File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }
        catch
        {
            return null;
        }
    }
}
