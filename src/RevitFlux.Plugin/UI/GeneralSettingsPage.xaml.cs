using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace RevitFlux.Plugin.UI;

public partial class GeneralSettingsPage : Page
{
    private const int DefaultPort = 8090;
    private const string DefaultLogLevel = "Info";
    private DispatcherTimer? _saveFeedbackTimer;
    private int _originalPort;

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".revitflux", "settings.json");

    public GeneralSettingsPage()
    {
        InitializeComponent();
        LoadSettings();
        LoadVersionInfo();
        RefreshConnectionStatus();
        // Subscribe to real-time server state changes so the status banner
        // updates immediately when the user clicks RevitFlux Switch.
        if (RevitFluxApp.Instance != null)
            RevitFluxApp.Instance.ServiceStateChanged += OnServiceStateChanged;

        Unloaded += (_, _) =>
        {
            if (RevitFluxApp.Instance != null)
                RevitFluxApp.Instance.ServiceStateChanged -= OnServiceStateChanged;
        };
    }

    private void OnServiceStateChanged()
    {
        // The event may fire from the Revit main thread or a background thread.
        // Dispatcher.Invoke ensures we update WPF controls on the UI thread.
        Dispatcher.Invoke(RefreshConnectionStatus);
    }

    private void RefreshConnectionStatus()
    {
        var app = RevitFluxApp.Instance;
        bool running = app?.IsServiceRunning ?? false;
        int port = app?.Port ?? 8090;

        if (running)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(46, 125, 50));   // green
            StatusBanner.Background = new SolidColorBrush(Color.FromRgb(232, 245, 233));
            StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(165, 214, 167));
            StatusTitle.Text = "Server running";
            StatusDetail.Text = $"Listening on localhost:{port} — ready for AI commands";
            PortBadgeText.Text = $"Port {port}";
            PortBadge.Background = new SolidColorBrush(Color.FromRgb(165, 214, 167));
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(158, 158, 158)); // gray
            StatusBanner.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
            StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224));
            StatusTitle.Text = "Server stopped";
            StatusDetail.Text = "Click 'RevitFlux Switch' in the ribbon to start";
            PortBadgeText.Text = $"Port {port}";
            PortBadge.Background = new SolidColorBrush(Color.FromRgb(224, 224, 224));
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonConvert.DeserializeObject<CortexSettings>(json);
                if (settings != null)
                {
                    _originalPort = settings.Port;
                    PortTextBox.Text = settings.Port.ToString();
                    SetComboSelection(LogLevelComboBox, settings.LogLevel ?? DefaultLogLevel);
                    ReadOnlyCheckBox.IsChecked = settings.ReadOnlyMode;
                    return;
                }
            }
        }
        catch { }

        SetDefaults();
    }

    private void LoadVersionInfo()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            PluginVersionText.Text = version?.ToString() ?? "Unknown";
        }
        catch { PluginVersionText.Text = "Unknown"; }

        try
        {
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string[] parts = assemblyPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string revitYear = "Unknown";
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Equals("Addins", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                {
                    revitYear = parts[i + 1];
                    break;
                }
            }
            RevitVersionText.Text = revitYear;
        }
        catch { RevitVersionText.Text = "Unknown"; }
    }

    private void SetDefaults()
    {
        _originalPort = DefaultPort;
        PortTextBox.Text = DefaultPort.ToString();
        SetComboSelection(LogLevelComboBox, DefaultLogLevel);
    }

    private static void SetComboSelection(ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Content?.ToString()?.Equals(value, StringComparison.OrdinalIgnoreCase) == true)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 1;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Please enter a valid port number (1-65535).", "Invalid Port",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string logLevel = (LogLevelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? DefaultLogLevel;

        try
        {
            // Merge-write: preserve keys managed by other pages (e.g. EnableCodeExecution,
            // DisabledTools) by loading the existing JSON first and updating only our fields.
            JObject settings = File.Exists(SettingsFilePath)
                ? JObject.Parse(File.ReadAllText(SettingsFilePath))
                : new JObject();

            settings["Port"] = port;
            settings["LogLevel"] = logLevel;
            settings["ReadOnlyMode"] = ReadOnlyCheckBox.IsChecked == true;

            string dir = Path.GetDirectoryName(SettingsFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(SettingsFilePath, settings.ToString(Formatting.Indented));

            // Apply read-only mode immediately (no restart needed)
            if (RevitFluxApp.Instance?.Router != null)
                RevitFluxApp.Instance.Router.ReadOnlyMode = ReadOnlyCheckBox.IsChecked == true;

            bool portChanged = port != _originalPort;
            if (portChanged)
            {
                ShowSaveFeedback("Saved \u2713  Restart Revit for port change", success: true, restartHint: true);
                _originalPort = port;
            }
            else
            {
                ShowSaveFeedback("Saved \u2713", success: true);
            }
        }
        catch (Exception ex)
        {
            ShowSaveFeedback($"Save failed: {ex.Message}", success: false);
        }
    }

    private void ShowSaveFeedback(string message, bool success, bool restartHint = false)
    {
        SaveFeedbackText.Text = message;
        SaveFeedbackText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(46, 125, 50)        // green
            : Color.FromRgb(198, 40, 40));      // red
        SaveFeedbackText.Visibility = Visibility.Visible;

        // Restart hint stays visible longer (4s) so the user can read it.
        var ttl = restartHint ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(2.5);

        _saveFeedbackTimer?.Stop();
        _saveFeedbackTimer = new DispatcherTimer { Interval = ttl };
        _saveFeedbackTimer.Tick += (_, _) =>
        {
            _saveFeedbackTimer?.Stop();
            _saveFeedbackTimer = null;
            SaveFeedbackText.Visibility = Visibility.Collapsed;
        };
        _saveFeedbackTimer.Start();
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e) => SetDefaults();

}

internal class CortexSettings
{
    public int Port { get; set; } = 8090;
    public string? LogLevel { get; set; } = "Info";
    public bool ReadOnlyMode { get; set; }
}
