using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

namespace RevitFlux.Plugin.UI;

/// <summary>The RevitFlux window. Every switch applies and saves at once.</summary>
public partial class SettingsWindow : Window
{
    private const double CompactWidth = 680;

    private readonly ISettingsHost _host;
    private readonly List<ToolRow> _tools;
    private readonly ICollectionView _toolView;
    private readonly List<TextBlock> _navTexts = new();
    private readonly FrameworkElement[] _views;
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private int _savedPort;
    private bool _bulk;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        Theme.Apply(this, host.IsDark);
        Icon = IconFactory.CreateBrand(32);
        BrandImage.Source = IconFactory.CreateBrand(32);

        _views = new FrameworkElement[] { HomeView, SafetyView, ToolsView, AboutView };
        AddNav("\uE80F", "Home");
        AddNav("\uEA18", "Safety");
        AddNav("\uEC7A", "Tools");
        AddNav("\uE946", "About");
        Nav.SelectedIndex = 0;

        _tools = host.GetTools().Select(t => new ToolRow(t)).ToList();
        foreach (var t in _tools) t.PropertyChanged += Tool_Changed;
        _toolView = CollectionViewSource.GetDefaultView(_tools);
        _toolView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToolRow.Category)));
        _toolView.Filter = o => ((ToolRow)o).Matches(SearchBox.Text);
        ToolList.ItemsSource = _toolView;

        VersionText.Text = $"RevitFlux {host.Version}";
        RevitText.Text = $"Revit {host.RevitVersion}";
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); MessageBar.Visibility = Visibility.Collapsed; };

        LoadSettings();
        UpdateStatus();
        UpdateToolCount();
        host.StateChanged += Host_StateChanged;
        Closed += (_, _) => { host.StateChanged -= Host_StateChanged; _messageTimer.Stop(); };
    }

    // ── Navigation ───────────────────────────────────────────────────────

    private void AddNav(string glyph, string text)
    {
        var icon = new TextBlock { Text = glyph, Width = 20 };
        icon.SetResourceReference(StyleProperty, "Glyph");
        var label = new TextBlock { Text = text, Margin = new Thickness(12, 0, 0, 0) };
        label.SetResourceReference(StyleProperty, "Body");
        _navTexts.Add(label);
        var item = new ListBoxItem
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } }
        };
        AutomationProperties.SetName(item, text);
        Nav.Items.Add(item);
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_views == null) return;
        for (var i = 0; i < _views.Length; i++)
            _views[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = ActualWidth < CompactWidth;
        NavPanel.Width = compact ? 60 : 208;
        BrandText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < _navTexts.Count; i++)
        {
            _navTexts[i].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ((ListBoxItem)Nav.Items[i]).ToolTip = compact ? _navTexts[i].Text : null;
        }
    }

    public void SelectPage(int index) => Nav.SelectedIndex = index;

    // ── Status ───────────────────────────────────────────────────────────

    private void Host_StateChanged() => Dispatcher.BeginInvoke(new Action(UpdateStatus));

    private void UpdateStatus()
    {
        var on = _host.IsRunning;
        ToggleButton.IsEnabled = true;
        StatusPill.SetResourceReference(BackgroundProperty, on ? "SuccessBackground" : "ControlHover");
        StatusGlyph.Text = on ? "\uE73E" : "\uE71A";
        StatusText.Text = on ? "On" : "Off";
        var color = on ? "SuccessText" : "TextSecondary";
        StatusGlyph.SetResourceReference(TextBlock.ForegroundProperty, color);
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, color);
        StatusTitle.Text = on ? "Ready for your AI assistant" : "Not connected";
        StatusDetail.Text = on
            ? $"Listening on this computer only, port {_host.Port}."
            : "Open a project, then turn RevitFlux on. It is off each time Revit starts.";
        ToggleGlyph.Text = on ? "\uE71A" : "\uE768";
        ToggleText.Text = on ? "Turn off" : "Turn on";
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        ToggleButton.IsEnabled = false;
        _host.ToggleServer();
        // Revit may be busy; let the user try again if nothing happened.
        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        retry.Tick += (_, _) => { retry.Stop(); if (!ToggleButton.IsEnabled) { UpdateStatus(); } };
        retry.Start();
    }

    // ── Settings ─────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        JObject s;
        try
        {
            s = SettingsStore.Read();
        }
        catch (Exception ex)
        {
            s = new JObject();
            ShowError($"settings.json could not be read: {ex.Message}");
        }
        _savedPort = (int?)s["Port"] ?? 8090;
        PortBox.Text = _savedPort.ToString(CultureInfo.InvariantCulture);
        ReadOnlySwitch.IsChecked = (bool?)s["ReadOnlyMode"] ?? false;
        ScriptsSwitch.IsChecked = (bool?)s["EnableCodeExecution"] ?? false;
        SkipSwitch.IsChecked = (bool?)s["SkipScriptConfirmation"] ?? false;
        UpdateSafety();
    }

    private void UpdateSafety()
    {
        var scripts = ScriptsSwitch.IsChecked == true;
        var skip = scripts && SkipSwitch.IsChecked == true;
        SkipSwitch.IsEnabled = scripts;
        SkipWarning.Visibility = skip ? Visibility.Visible : Visibility.Collapsed;
        SafetySummary.Text = ReadOnlySwitch.IsChecked == true
            ? "Read-only: the AI cannot change the model."
            : !scripts ? "The AI can edit the model with its tools. Scripts are off."
            : skip ? "The AI can edit the model and run scripts without asking."
            : "The AI can edit the model. Scripts ask you first.";
    }

    private bool Save(params (string key, JToken value)[] values)
    {
        try
        {
            SettingsStore.Write(values);
            return true;
        }
        catch (Exception ex)
        {
            ShowError($"Not saved: {ex.Message}");
            return false;
        }
    }

    private void ReadOnly_Click(object sender, RoutedEventArgs e)
    {
        var on = ReadOnlySwitch.IsChecked == true;
        if (Save(("ReadOnlyMode", on))) _host.ApplyReadOnly(on);
        else ReadOnlySwitch.IsChecked = !on;
        UpdateSafety();
    }

    private void Scripts_Click(object sender, RoutedEventArgs e)
    {
        var on = ScriptsSwitch.IsChecked == true;
        // Turning scripts off also clears "without asking", so it cannot come back unnoticed.
        var ok = on
            ? Save(("EnableCodeExecution", true))
            : Save(("EnableCodeExecution", false), ("SkipScriptConfirmation", false));
        if (!ok) ScriptsSwitch.IsChecked = !on;
        else if (!on) SkipSwitch.IsChecked = false;
        UpdateSafety();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        var on = SkipSwitch.IsChecked == true;
        if (on && MessageBox.Show(this,
                "Scripts written by the AI will run immediately, without asking you first.\n\n" +
                "They can change or delete anything in the open model. Work on a saved or detached copy.\n\n" +
                "Turn confirmation off?",
                "Run scripts without asking", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            SkipSwitch.IsChecked = false;
            return;
        }
        if (!Save(("SkipScriptConfirmation", on))) SkipSwitch.IsChecked = !on;
        UpdateSafety();
    }

    private bool TryPort(out int port) =>
        int.TryParse(PortBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port >= 1024 && port <= 65535;

    private void Port_TextChanged(object sender, TextChangedEventArgs e) =>
        PortApply.IsEnabled = PortBox.Text.Trim() != _savedPort.ToString(CultureInfo.InvariantCulture);

    private void Port_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PortApply.IsEnabled) PortApply_Click(sender, e);
    }

    private void PortApply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPort(out var port))
        {
            ShowError("Enter a port from 1024 to 65535.");
            PortBox.Focus();
            return;
        }
        if (!Save(("Port", port))) return;
        _savedPort = port;
        PortApply.IsEnabled = false;
        ShowSuccess("Saved. Restart Revit and your AI app to use the new port.");
    }

    // ── Tools ────────────────────────────────────────────────────────────

    private void Tool_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (_bulk || sender is not ToolRow row) return;
        if (SaveTools()) return;
        _bulk = true;
        row.IsEnabled = !row.IsEnabled;
        _bulk = false;
    }

    private bool SaveTools()
    {
        var disabled = _tools.Where(t => !t.IsEnabled).Select(t => t.Name).ToList();
        if (!Save(("DisabledTools", new JArray(disabled)))) return false;
        _host.ApplyDisabledTools(disabled);
        UpdateToolCount();
        return true;
    }

    private void AllOn_Click(object sender, RoutedEventArgs e)
    {
        var off = _tools.Where(t => !t.IsEnabled).ToList();
        _bulk = true;
        foreach (var t in off) t.IsEnabled = true;
        if (!SaveTools())
        {
            foreach (var t in off) t.IsEnabled = false;
        }
        _bulk = false;
        UpdateToolCount();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_toolView == null) return;
        _toolView.Refresh();
        UpdateToolCount();
    }

    private void UpdateToolCount()
    {
        var on = _tools.Count(t => t.IsEnabled);
        ToolCount.Text = $"{on} of {_tools.Count} on";
        AllOnButton.IsEnabled = on < _tools.Count;
        NoTools.Visibility = _toolView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        NoTools.Text = _tools.Count == 0 ? "No tools are loaded." : "No tools match your search.";
    }

    // ── Messages ─────────────────────────────────────────────────────────

    private void ShowSuccess(string text)
    {
        Show(text, "\uE73E", "SuccessText", "SuccessBackground");
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    private void ShowError(string text)
    {
        _messageTimer.Stop();
        Show(text, "\uE7BA", "ErrorText", "ErrorBackground");
    }

    private void Show(string text, string glyph, string foreground, string background)
    {
        MessageText.Text = text;
        MessageGlyph.Text = glyph;
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        MessageGlyph.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        MessageBar.SetResourceReference(BackgroundProperty, background);
        MessageBar.Visibility = Visibility.Visible;
    }
}

public sealed class ToolRow : INotifyPropertyChanged
{
    private bool _isEnabled;

    public ToolRow(ToolInfo info)
    {
        Name = info.Name;
        Category = info.Category;
        Description = info.Description;
        _isEnabled = info.Enabled;
        var title = info.Name.Replace('_', ' ');
        Title = title.Length == 0 ? title : char.ToUpperInvariant(title[0]) + title.Substring(1);
    }

    public string Name { get; }
    public string Title { get; }
    public string Category { get; }
    public string Description { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public bool Matches(string query)
    {
        query = query.Trim();
        return query.Length == 0 || Has(Name, query) || Has(Title, query) || Has(Description, query) || Has(Category, query);
    }

    private static bool Has(string text, string query) => text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    public event PropertyChangedEventHandler? PropertyChanged;
}
