using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;

namespace RevitFlux.Plugin.UI;

/// <summary>Connects the settings window to the running add-in.</summary>
internal sealed class RevitSettingsHost : ISettingsHost
{
    private readonly RevitFluxApp _app;

    public RevitSettingsHost(RevitFluxApp app, string revitVersion)
    {
        _app = app;
        RevitVersion = revitVersion;
    }

    public bool IsRunning => _app.IsServiceRunning;
    public int Port => _app.Port;
    public string Version => typeof(RevitFluxApp).Assembly.GetName().Version?.ToString(3) ?? "";
    public string RevitVersion { get; }
    public bool IsDark => IsRevitDark;

    public static bool IsRevitDark
    {
        get
        {
#if REVIT2024_OR_GREATER
            return UIThemeManager.CurrentTheme == UITheme.Dark;
#else
            return false;
#endif
        }
    }

    public event Action? StateChanged
    {
        add => _app.ServiceStateChanged += value;
        remove => _app.ServiceStateChanged -= value;
    }

    public void ToggleServer() => _app.RequestToggle();

    public IReadOnlyList<ToolInfo> GetTools() =>
        (_app.Router?.GetAllToolInfo() ?? Array.Empty<(string Name, string Category, string Description, bool IsEnabled)>())
        .Select(t => new ToolInfo(t.Name, t.Category, t.Description, t.IsEnabled)).ToList();

    public void ApplyDisabledTools(IReadOnlyCollection<string> names) => _app.Router?.SetDisabledTools(names);

    public void ApplyReadOnly(bool readOnly)
    {
        if (_app.Router != null) _app.Router.ReadOnlyMode = readOnly;
    }
}

/// <summary>Starts or stops the service inside a Revit API context.</summary>
internal sealed class ToggleServiceHandler : IExternalEventHandler
{
    public string GetName() => "RevitFlux toggle";

    public void Execute(UIApplication app)
    {
        var plugin = RevitFluxApp.Instance;
        if (plugin == null) return;
        try
        {
            if (plugin.IsServiceRunning) plugin.StopService();
            else plugin.StartService(app.ActiveUIDocument?.Document);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitFlux", $"RevitFlux could not be switched: {ex.Message}");
        }
    }
}
