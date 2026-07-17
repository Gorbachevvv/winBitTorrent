using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public sealed partial class SettingsWindow : Window
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, Control> _editors = [];
    private readonly Dictionary<string, object?> _localValues = [];
    private JsonObject _preferences = [];
    private string _section = "Behavior";

    private static readonly SettingSpec[] Specs =
    [
        new("Behavior", "ui.language", "Language", SettingKind.Language, true, ""),
        new("Behavior", "ui.theme", "Application theme", SettingKind.Theme, true, "Default"),
        new("Behavior", "ui.confirmDelete", "Confirm torrent deletion", SettingKind.Boolean, true, true),
        new("Downloads", "save_path", "Default save path", SettingKind.Text),
        new("Downloads", "temp_path_enabled", "Keep incomplete torrents in a separate folder", SettingKind.Boolean),
        new("Downloads", "temp_path", "Incomplete torrent path", SettingKind.Text),
        new("Downloads", "create_subfolder_enabled", "Create subfolder for torrents with multiple files", SettingKind.Boolean),
        new("Downloads", "auto_tmm_enabled", "Automatic torrent management", SettingKind.Boolean),
        new("Downloads", "preallocate_all", "Pre-allocate disk space", SettingKind.Boolean),
        new("Connection", "listen_port", "Incoming connections port", SettingKind.Number),
        new("Connection", "upnp", "Use UPnP / NAT-PMP port forwarding", SettingKind.Boolean),
        new("Connection", "random_port", "Use a different port on each startup", SettingKind.Boolean),
        new("Connection", "proxy_type", "Proxy type", SettingKind.ProxyType),
        new("Connection", "proxy_ip", "Proxy host", SettingKind.Text),
        new("Connection", "proxy_port", "Proxy port", SettingKind.Number),
        new("Speed", "dl_limit", "Global download limit (KiB/s, 0 = unlimited)", SettingKind.Number),
        new("Speed", "up_limit", "Global upload limit (KiB/s, 0 = unlimited)", SettingKind.Number),
        new("Speed", "alt_dl_limit", "Alternative download limit (KiB/s)", SettingKind.Number),
        new("Speed", "alt_up_limit", "Alternative upload limit (KiB/s)", SettingKind.Number),
        new("Speed", "scheduler_enabled", "Schedule alternative rate limits", SettingKind.Boolean),
        new("BitTorrent", "dht", "Enable DHT", SettingKind.Boolean),
        new("BitTorrent", "pex", "Enable Peer Exchange (PeX)", SettingKind.Boolean),
        new("BitTorrent", "lsd", "Enable Local Peer Discovery", SettingKind.Boolean),
        new("BitTorrent", "encryption", "Encryption mode", SettingKind.Encryption),
        new("BitTorrent", "queueing_enabled", "Torrent queueing", SettingKind.Boolean),
        new("BitTorrent", "max_active_downloads", "Maximum active downloads", SettingKind.Number),
        new("BitTorrent", "max_active_uploads", "Maximum active uploads", SettingKind.Number),
        new("Search", "search_enabled", "Enable Search Engine", SettingKind.Boolean),
        new("Search", "python_executable_path", "Python executable", SettingKind.Text),
        new("RSS", "rss_processing_enabled", "Enable fetching RSS feeds", SettingKind.Boolean),
        new("RSS", "rss_refresh_interval", "Feed refresh interval (minutes)", SettingKind.Number),
        new("RSS", "rss_max_articles_per_feed", "Maximum articles per feed", SettingKind.Number),
        new("RSS", "rss_auto_downloading_enabled", "Enable RSS automatic downloading", SettingKind.Boolean),
        new("WebUI", "web_ui_address", "IP address", SettingKind.Text),
        new("WebUI", "web_ui_port", "Port", SettingKind.Number),
        new("WebUI", "web_ui_username", "Username", SettingKind.Text),
        new("WebUI", "web_ui_upnp", "Use UPnP / NAT-PMP", SettingKind.Boolean),
        new("WebUI", "web_ui_csrf_protection_enabled", "Enable CSRF protection", SettingKind.Boolean),
        new("Advanced", "resume_data_storage_type", "Resume data storage type", SettingKind.ResumeStorage),
        new("Advanced", "memory_working_set_limit", "Physical memory usage limit (MiB)", SettingKind.Number),
        new("Advanced", "disk_cache", "Disk cache (MiB, -1 = auto)", SettingKind.Number),
        new("Advanced", "async_io_threads", "Asynchronous I/O threads", SettingKind.Number),
        new("Advanced", "recheck_completed_torrents", "Recheck torrents on completion", SettingKind.Boolean),
        new("Advanced", "anonymous_mode", "Enable anonymous mode", SettingKind.Boolean)
    ];

    public SettingsWindow()
    {
        InitializeComponent();
        Title = Localizer.Get("WindowTitle_Settings", "Settings");
        this.ConfigureOwned(980, 720);
        _main = App.Services.GetRequiredService<MainViewModel>();
        foreach (var spec in Specs.Where(static spec => spec.Local))
            _localValues[spec.Key] = ClientSettings.GetValue(spec.Key) ?? spec.DefaultValue;
        Activated += SettingsWindow_Activated;
    }

    private async void SettingsWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= SettingsWindow_Activated;
        try
        {
            if (_main.Api is not null)
                _preferences = await _main.Api.Application.GetPreferencesAsync();
            Sections.SelectedItem = Sections.MenuItems[0];
            RenderSection();
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void Sections_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string section)
        {
            CaptureSection();
            _section = section;
            RenderSection();
        }
    }

    private void RenderSection()
    {
        SettingsPanel.Children.Clear();
        _editors.Clear();
        SectionTitle = new TextBlock { Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], Text = SectionDisplayName(_section) };
        SettingsPanel.Children.Add(SectionTitle);
        SettingsPanel.Children.Add(MessageBar);

        var localManaged = _main.SelectedProfile?.Kind == ProfileKind.LocalManaged;
        foreach (var spec in Specs.Where(spec => spec.Section == _section))
        {
            var editor = CreateEditor(spec, ReadValue(spec));
            if (localManaged && spec.Section == "WebUI" && spec.Key is "web_ui_address" or "web_ui_port" or "web_ui_username")
            {
                editor.IsEnabled = false;
                ToolTipService.SetToolTip(editor, Localizer.Get("Settings_ManagedTooltip", "Managed locally by WinBitTorrent and restricted to loopback."));
            }
            _editors[spec.Key] = editor;
            SettingsPanel.Children.Add(editor);
        }

        if (localManaged && _section == "WebUI")
            SettingsPanel.Children.Insert(1, new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Informational,
                Title = Localizer.Get("Settings_ManagedTitle", "Managed local profile"),
                Message = Localizer.Get("Settings_ManagedMessage", "Web UI address, port, and authentication are controlled by WinBitTorrent.")
            });
    }

    private static Control CreateEditor(SettingSpec spec, object? value)
    {
        var label = Localizer.Get($"Setting_{spec.Key.Replace('.', '_')}", spec.Label);
        return spec.Kind switch
        {
            SettingKind.Boolean => new ToggleSwitch { Header = label, IsOn = value as bool? ?? false },
            SettingKind.Number => new NumberBox { Header = label, Value = NumberValue(value), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact },
            SettingKind.Language => ChoiceBox(label, value, [("", "Choice_System", "System default"), ("en-US", "Choice_English", "English"), ("ru-RU", "Choice_Russian", "Русский")]),
            SettingKind.Theme => ChoiceBox(label, value, [("Default", "Choice_SystemTheme", "Use system setting"), ("Light", "Choice_Light", "Light"), ("Dark", "Choice_Dark", "Dark")]),
            SettingKind.ProxyType => ChoiceBox(label, value, [("None", "Choice_None", "None"), ("HTTP", "Choice_HttpProxy", "HTTP"), ("SOCKS4", "Choice_Socks4", "SOCKS4"), ("SOCKS5", "Choice_Socks5", "SOCKS5")]),
            SettingKind.Encryption => ChoiceBox(label, value, [(0, "Choice_EncryptionPrefer", "Prefer encryption"), (1, "Choice_EncryptionForce", "Require encryption"), (2, "Choice_EncryptionDisable", "Disable encryption")]),
            SettingKind.ResumeStorage => ChoiceBox(label, value, [("Legacy", "Choice_ResumeLegacy", "Fastresume files"), ("SQLite", "Choice_ResumeSqlite", "SQLite database")]),
            _ => new TextBox { Header = label, Text = value?.ToString() ?? string.Empty }
        };
    }

    private static ComboBox ChoiceBox(string header, object? value, IReadOnlyList<(object Value, string ResourceKey, string Fallback)> choices)
    {
        var combo = new ComboBox { Header = header };
        foreach (var choice in choices)
            combo.Items.Add(new ComboBoxItem { Content = Localizer.Get(choice.ResourceKey, choice.Fallback), Tag = choice.Value });
        combo.SelectedIndex = choices.Select(static choice => choice.Value).ToList().FindIndex(choice => ValuesEqual(choice, value));
        if (combo.SelectedIndex < 0)
            combo.SelectedIndex = 0;
        return combo;
    }

    private static bool ValuesEqual(object left, object? right)
        => string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    private static double NumberValue(object? value)
    {
        if (value is null)
            return 0;
        if (value is IConvertible convertible)
        {
            try { return convertible.ToDouble(CultureInfo.InvariantCulture); }
            catch (FormatException) { }
            catch (InvalidCastException) { }
        }
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private object? ReadValue(SettingSpec spec)
    {
        if (spec.Local)
            return _localValues.GetValueOrDefault(spec.Key) ?? spec.DefaultValue;
        var node = _preferences[spec.Key];
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean)) return boolean;
            if (value.TryGetValue<double>(out var number)) return number;
            if (value.TryGetValue<string>(out var text)) return text;
        }
        return null;
    }

    private void CaptureSection()
    {
        foreach (var spec in Specs.Where(spec => spec.Section == _section))
        {
            if (!_editors.TryGetValue(spec.Key, out var editor) || !editor.IsEnabled)
                continue;
            object value = editor switch
            {
                ToggleSwitch toggle => toggle.IsOn,
                NumberBox number => double.IsNaN(number.Value) ? 0 : number.Value,
                ComboBox { SelectedItem: ComboBoxItem item } when spec.Kind is SettingKind.Language or SettingKind.Theme or SettingKind.ProxyType or SettingKind.Encryption or SettingKind.ResumeStorage => item.Tag ?? string.Empty,
                TextBox text => text.Text,
                _ => string.Empty
            };
            if (spec.Local)
                _localValues[spec.Key] = value;
            else
                _preferences[spec.Key] = JsonValue.Create(value);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureSection();
        try
        {
            if (_main.Api is not null)
                await _main.Api.Application.SetPreferencesAsync(_preferences);
            foreach (var (key, value) in _localValues)
                ClientSettings.SetValue(key, value);
            var language = _localValues.GetValueOrDefault("ui.language") as string;
            if (App.HasPackageIdentity())
                ApplicationLanguages.PrimaryLanguageOverride = language ?? string.Empty;
            ShowMessage(Localizer.Get("Settings_Applied", "Settings applied. Restart the app to apply language, theme, or density changes."), InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowMessage(string message, InfoBarSeverity severity) { MessageBar.Message = message; MessageBar.Severity = severity; MessageBar.IsOpen = true; }
    private static string SectionDisplayName(string value) => Localizer.Get($"SettingsSection_{value}", value == "WebUI" ? "Web UI" : value);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record SettingSpec(string Section, string Key, string Label, SettingKind Kind, bool Local = false, object? DefaultValue = null);
    private enum SettingKind { Boolean, Number, Text, Language, Theme, ProxyType, Encryption, ResumeStorage }
}
