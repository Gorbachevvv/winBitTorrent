using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.System;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public sealed partial class SettingsWindow : Window
{
    private readonly MainViewModel _main;
    private readonly Dictionary<string, Control> _editors = [];
    private readonly Dictionary<string, object?> _localValues = [];
    private JsonObject _preferences = [];
    private readonly JsonObject _changedPreferences = [];
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
        new("Connection", "bittorrent_protocol", "Peer connection protocol", SettingKind.PeerProtocol, Group: "Protocol"),
        new("Connection", "listen_port", "Incoming connections port", SettingKind.Number, Group: "Protocol", Minimum: 0, Maximum: 65535),
        new("Connection", "random_port", "Choose an available port automatically on startup", SettingKind.Boolean, Group: "Protocol"),
        new("Connection", "upnp", "Use UPnP / NAT-PMP port forwarding", SettingKind.Boolean, Group: "Protocol"),
        new("Connection", "max_connec", "Global maximum connections (-1 = unlimited)", SettingKind.Number, Group: "Limits", Minimum: -1, Maximum: int.MaxValue),
        new("Connection", "max_connec_per_torrent", "Maximum connections per torrent (-1 = unlimited)", SettingKind.Number, Group: "Limits", Minimum: -1, Maximum: int.MaxValue),
        new("Connection", "max_uploads", "Global maximum upload slots (-1 = unlimited)", SettingKind.Number, Group: "Limits", Minimum: -1, Maximum: int.MaxValue),
        new("Connection", "max_uploads_per_torrent", "Maximum upload slots per torrent (-1 = unlimited)", SettingKind.Number, Group: "Limits", Minimum: -1, Maximum: int.MaxValue),
        new("Connection", "proxy_type", "Proxy type", SettingKind.ProxyType, Group: "Proxy"),
        new("Connection", "proxy_ip", "Proxy host", SettingKind.Text, Group: "Proxy"),
        new("Connection", "proxy_port", "Proxy port", SettingKind.Number, Group: "Proxy", Minimum: 0, Maximum: 65535),
        new("Connection", "proxy_hostname_lookup", "Resolve host names through the proxy", SettingKind.Boolean, Group: "Proxy"),
        new("Connection", "proxy_auth_enabled", "Proxy requires authentication", SettingKind.Boolean, Group: "Proxy"),
        new("Connection", "proxy_username", "Proxy username", SettingKind.Text, Group: "Proxy"),
        new("Connection", "proxy_password", "Proxy password", SettingKind.Password, Group: "Proxy"),
        new("Connection", "proxy_bittorrent", "Use proxy for BitTorrent traffic", SettingKind.Boolean, Group: "Proxy"),
        new("Connection", "proxy_peer_connections", "Use proxy for peer connections", SettingKind.Boolean, Group: "Proxy"),
        new("Connection", "proxy_rss", "Use proxy for RSS", SettingKind.Boolean, Group: "Proxy"),
        new("Connection", "proxy_misc", "Use proxy for general traffic", SettingKind.Boolean, Group: "Proxy"),
        new("Connection", "i2p_enabled", "Enable I2P (experimental)", SettingKind.Boolean, Group: "I2P"),
        new("Connection", "i2p_address", "I2P SAM bridge host", SettingKind.Text, Group: "I2P"),
        new("Connection", "i2p_port", "I2P SAM bridge port", SettingKind.Number, Group: "I2P", Minimum: 0, Maximum: 65535),
        new("Connection", "i2p_mixed_mode", "Allow regular peers for I2P torrents (mixed mode)", SettingKind.Boolean, Group: "I2P"),
        new("Connection", "ip_filter_enabled", "Enable IP filter", SettingKind.Boolean, Group: "IpFilter"),
        new("Connection", "ip_filter_path", "IP filter file (.dat, .p2p, .p2b)", SettingKind.Text, Group: "IpFilter"),
        new("Connection", "ip_filter_trackers", "Apply IP filter to trackers", SettingKind.Boolean, Group: "IpFilter"),
        new("Connection", "banned_IPs", "Manually banned IP addresses (one per line)", SettingKind.Multiline, Group: "IpFilter"),
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
        new("Catalog", "catalog.tmdb.apiKey", "TMDB API key", SettingKind.Password, true),
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
        var connected = _main.Api is not null;
        var sectionSpecs = Specs.Where(spec => spec.Section == _section).ToArray();

        if (!connected && sectionSpecs.Any(static spec => !spec.Local))
            SettingsPanel.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = Localizer.Get("Settings_NotConnectedTitle", "qBittorrent is not connected"),
                Message = Localizer.Get("Settings_NotConnectedMessage", "Connect to a qBittorrent profile to view and change these settings.")
            });

        if (_section == "Connection")
        {
            foreach (var group in sectionSpecs.GroupBy(static spec => spec.Group ?? "General"))
            {
                var groupPanel = new StackPanel { Spacing = 12 };
                foreach (var spec in group)
                    AddEditor(groupPanel, spec, localManaged, connected);
                SettingsPanel.Children.Add(CreateConnectionGroup(group.Key, groupPanel));
            }

            if (connected)
                ConfigureConnectionDependencies();
        }
        else
        {
            foreach (var spec in sectionSpecs)
                AddEditor(SettingsPanel, spec, localManaged, connected);
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

    private void AddEditor(Panel parent, SettingSpec spec, bool localManaged, bool connected)
    {
        var view = CreateEditorView(spec, ReadValue(spec), localManaged, out var editor);
        if ((!connected && !spec.Local)
            || (localManaged && spec.Section == "WebUI" && spec.Key is "web_ui_address" or "web_ui_port" or "web_ui_username"))
        {
            editor.IsEnabled = false;
            if (localManaged && spec.Section == "WebUI")
                ToolTipService.SetToolTip(editor, Localizer.Get("Settings_ManagedTooltip", "Managed locally by WinBitTorrent and restricted to loopback."));
        }

        _editors[spec.Key] = editor;
        parent.Children.Add(view);
    }

    private static Expander CreateConnectionGroup(string group, StackPanel content)
    {
        var (title, description, expanded) = group switch
        {
            "Protocol" => (
                Localizer.Get("SettingsConnection_ProtocolTitle", "Protocol and listening port"),
                Localizer.Get("SettingsConnection_ProtocolDescription", "Controls how peers reach this client. Port 0 lets the operating system select an available port."),
                true),
            "Limits" => (
                Localizer.Get("SettingsConnection_LimitsTitle", "Connection limits"),
                Localizer.Get("SettingsConnection_LimitsDescription", "Use -1 for no limit. Conservative limits can reduce router and memory load."),
                true),
            "Proxy" => (
                Localizer.Get("SettingsConnection_ProxyTitle", "Proxy server"),
                Localizer.Get("SettingsConnection_ProxyDescription", "Choose which qBittorrent traffic is routed through the proxy."),
                true),
            "I2P" => (
                Localizer.Get("SettingsConnection_I2pTitle", "I2P (experimental)"),
                Localizer.Get("SettingsConnection_I2pDescription", "Requires a running I2P router with a SAM bridge."),
                false),
            "IpFilter" => (
                Localizer.Get("SettingsConnection_FilterTitle", "IP filtering"),
                Localizer.Get("SettingsConnection_FilterDescription", "Load a compatible filter list or block individual addresses manually."),
                false),
            _ => (group, string.Empty, true)
        };

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(description))
            header.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });

        content.Margin = new Thickness(0, 8, 0, 4);
        return new Expander
        {
            Header = header,
            Content = content,
            IsExpanded = expanded,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private FrameworkElement CreateEditorView(SettingSpec spec, object? value, bool localManaged, out Control editor)
    {
        editor = CreateEditor(spec, value);
        FrameworkElement view = editor;

        if (spec.Key == "listen_port")
            view = EditorButtonGrid(editor, Localizer.Get("SettingsConnection_RandomPort", "Random port"), RandomPort_Click);
        else if (spec.Key == "ip_filter_path" && localManaged)
            view = EditorButtonGrid(editor, Localizer.Get("CommonBrowse.Content", "Browse…"), BrowseIpFilter_Click);
        else if (spec.Key == "catalog.tmdb.apiKey")
            view = EditorButtonGrid(editor, Localizer.Get("Settings_GetTmdbKey", "Get API key…"), GetTmdbKey_Click);

        var description = spec.Key switch
        {
            "random_port" => Localizer.Get("Setting_random_port_Description", "Overrides the fixed port with automatic selection at every backend start."),
            "proxy_password" => Localizer.Get("Setting_proxy_password_Description", "qBittorrent stores this password without encryption."),
            "proxy_peer_connections" => Localizer.Get("Setting_proxy_peer_connections_Description", "Enable this to prevent direct peer connections when BitTorrent proxying is active."),
            "i2p_mixed_mode" => Localizer.Get("Setting_i2p_mixed_mode_Description", "Mixed mode can connect to regular IP peers and therefore does not provide I2P anonymity."),
            "ip_filter_path" when !localManaged => Localizer.Get("Setting_ip_filter_path_RemoteDescription", "Enter a path that is accessible on the remote qBittorrent server."),
            "catalog.tmdb.apiKey" => Localizer.Get("Setting_catalog_tmdb_apiKey_Description", "Free API key from themoviedb.org, used to load the movie/TV catalog. You need to register on themoviedb.org yourself and generate the key — WinBitTorrent cannot do this for you."),
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(description))
            return view;

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(view);
        stack.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });
        return stack;
    }

    private static Grid EditorButtonGrid(Control editor, string buttonText, RoutedEventHandler click)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(editor);
        var button = new Button { Content = buttonText, VerticalAlignment = VerticalAlignment.Bottom };
        button.SetBinding(Control.IsEnabledProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = editor,
            Path = new PropertyPath(nameof(Control.IsEnabled))
        });
        button.Click += click;
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    private static Control CreateEditor(SettingSpec spec, object? value)
    {
        var label = Localizer.Get($"Setting_{spec.Key.Replace('.', '_')}", spec.Label);
        return spec.Kind switch
        {
            SettingKind.Boolean => new ToggleSwitch { Header = label, IsOn = value as bool? ?? false },
            SettingKind.Number => new NumberBox
            {
                Header = label,
                Value = NumberValue(value),
                Minimum = spec.Minimum ?? double.MinValue,
                Maximum = spec.Maximum ?? double.MaxValue,
                SmallChange = 1,
                LargeChange = 10,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten
            },
            SettingKind.Password => new PasswordBox { Header = label, Password = value?.ToString() ?? string.Empty },
            SettingKind.Multiline => new TextBox { Header = label, Text = value?.ToString() ?? string.Empty, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110 },
            SettingKind.Language => ChoiceBox(label, value, [("", "Choice_System", "System default"), ("en-US", "Choice_English", "English"), ("ru-RU", "Choice_Russian", "Русский")]),
            SettingKind.Theme => ChoiceBox(label, value, [("Default", "Choice_SystemTheme", "Use system setting"), ("Light", "Choice_Light", "Light"), ("Dark", "Choice_Dark", "Dark")]),
            SettingKind.ProxyType => ChoiceBox(label, value, [("None", "Choice_None", "None"), ("HTTP", "Choice_HttpProxy", "HTTP"), ("SOCKS4", "Choice_Socks4", "SOCKS4"), ("SOCKS5", "Choice_Socks5", "SOCKS5")]),
            SettingKind.PeerProtocol => ChoiceBox(label, value, [(0, "Choice_ProtocolBoth", "TCP and µTP"), (1, "Choice_ProtocolTcp", "TCP only"), (2, "Choice_ProtocolUtp", "µTP only")]),
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

    private void ConfigureConnectionDependencies()
    {
        if (_editors.GetValueOrDefault("random_port") is ToggleSwitch randomPort)
        {
            randomPort.Toggled += (_, _) => SetEditorEnabled("listen_port", !randomPort.IsOn);
            SetEditorEnabled("listen_port", !randomPort.IsOn);
        }

        if (_editors.GetValueOrDefault("proxy_type") is ComboBox proxyType)
            proxyType.SelectionChanged += (_, _) => UpdateProxyEditors();
        if (_editors.GetValueOrDefault("proxy_auth_enabled") is ToggleSwitch proxyAuthentication)
            proxyAuthentication.Toggled += (_, _) => UpdateProxyEditors();
        if (_editors.GetValueOrDefault("proxy_bittorrent") is ToggleSwitch proxyBitTorrent)
            proxyBitTorrent.Toggled += (_, _) => UpdateProxyEditors();
        UpdateProxyEditors();

        if (_editors.GetValueOrDefault("i2p_enabled") is ToggleSwitch i2pEnabled)
        {
            i2pEnabled.Toggled += (_, _) => UpdateI2pEditors();
            UpdateI2pEditors();
        }

        if (_editors.GetValueOrDefault("ip_filter_enabled") is ToggleSwitch filterEnabled)
        {
            filterEnabled.Toggled += (_, _) => UpdateIpFilterEditors();
            UpdateIpFilterEditors();
        }
    }

    private void UpdateProxyEditors()
    {
        var proxyEnabled = _editors.GetValueOrDefault("proxy_type") is ComboBox
        {
            SelectedItem: ComboBoxItem { Tag: string type }
        } && !type.Equals("None", StringComparison.OrdinalIgnoreCase);
        var authenticationEnabled = proxyEnabled
            && _editors.GetValueOrDefault("proxy_auth_enabled") is ToggleSwitch { IsOn: true };
        var bitTorrentEnabled = proxyEnabled
            && _editors.GetValueOrDefault("proxy_bittorrent") is ToggleSwitch { IsOn: true };

        foreach (var key in new[] { "proxy_ip", "proxy_port", "proxy_hostname_lookup", "proxy_auth_enabled", "proxy_bittorrent", "proxy_rss", "proxy_misc" })
            SetEditorEnabled(key, proxyEnabled);
        SetEditorEnabled("proxy_username", authenticationEnabled);
        SetEditorEnabled("proxy_password", authenticationEnabled);
        SetEditorEnabled("proxy_peer_connections", bitTorrentEnabled);
    }

    private void UpdateI2pEditors()
    {
        var enabled = _editors.GetValueOrDefault("i2p_enabled") is ToggleSwitch { IsOn: true };
        foreach (var key in new[] { "i2p_address", "i2p_port", "i2p_mixed_mode" })
            SetEditorEnabled(key, enabled);
    }

    private void UpdateIpFilterEditors()
    {
        var enabled = _editors.GetValueOrDefault("ip_filter_enabled") is ToggleSwitch { IsOn: true };
        SetEditorEnabled("ip_filter_path", enabled);
        SetEditorEnabled("ip_filter_trackers", enabled);
    }

    private void SetEditorEnabled(string key, bool enabled)
    {
        if (_editors.TryGetValue(key, out var editor))
            editor.IsEnabled = enabled;
    }

    private void RandomPort_Click(object sender, RoutedEventArgs e)
    {
        if (_editors.GetValueOrDefault("listen_port") is NumberBox port)
            port.Value = Random.Shared.Next(49152, 65536);
        if (_editors.GetValueOrDefault("random_port") is ToggleSwitch automatic)
            automatic.IsOn = false;
    }

    private async void BrowseIpFilter_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".dat");
        picker.FileTypeFilter.Add(".p2p");
        picker.FileTypeFilter.Add(".p2b");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null && _editors.GetValueOrDefault("ip_filter_path") is TextBox path)
            path.Text = file.Path;
    }

    private static async void GetTmdbKey_Click(object sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(new Uri("https://www.themoviedb.org/settings/api"));

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
            if (value.TryGetValue<int>(out var integer)) return integer;
            if (value.TryGetValue<long>(out var longInteger)) return longInteger;
            if (value.TryGetValue<double>(out var number)) return number;
            if (value.TryGetValue<string>(out var text)) return text;
        }
        return null;
    }

    private void CaptureSection()
    {
        foreach (var spec in Specs.Where(spec => spec.Section == _section))
        {
            if (!_editors.TryGetValue(spec.Key, out var editor))
                continue;
            if (!editor.IsEnabled)
            {
                if (!spec.Local)
                    _changedPreferences.Remove(spec.Key);
                continue;
            }
            object value = editor switch
            {
                ToggleSwitch toggle => toggle.IsOn,
                NumberBox number => double.IsNaN(number.Value) ? 0 : Convert.ToInt64(Math.Round(number.Value), CultureInfo.InvariantCulture),
                ComboBox { SelectedItem: ComboBoxItem item } when spec.Kind is SettingKind.Language or SettingKind.Theme or SettingKind.ProxyType or SettingKind.PeerProtocol or SettingKind.Encryption or SettingKind.ResumeStorage => item.Tag ?? string.Empty,
                PasswordBox password => password.Password,
                TextBox text => text.Text,
                _ => string.Empty
            };
            if (spec.Local)
                _localValues[spec.Key] = value;
            else
            {
                _preferences[spec.Key] = JsonValue.Create(value);
                _changedPreferences[spec.Key] = JsonValue.Create(value);
            }
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CaptureSection();
        SaveButton.IsEnabled = false;
        try
        {
            IReadOnlyList<string> mismatched = [];
            var hadRemoteChanges = _changedPreferences.Count > 0;
            if (hadRemoteChanges)
            {
                if (_main.Api is null)
                {
                    ShowMessage(Localizer.Get("Settings_NotConnectedMessage", "Connect to a qBittorrent profile to view and change these settings."), InfoBarSeverity.Error);
                    return;
                }

                var requested = (JsonObject)_changedPreferences.DeepClone();
                await _main.Api.Application.SetPreferencesAsync(requested);
                _preferences = await _main.Api.Application.GetPreferencesAsync();
                mismatched = PreferenceVerifier.FindMismatchedKeys(requested, _preferences);
                if (mismatched.Count == 0)
                    _changedPreferences.Clear();
            }

            foreach (var (key, value) in _localValues)
                ClientSettings.SetValue(key, value);
            var language = _localValues.GetValueOrDefault("ui.language") as string;
            App.ApplyLanguageOverride(language ?? string.Empty);

            if (mismatched.Count > 0)
            {
                var labels = string.Join(", ", mismatched.Select(SettingDisplayName));
                ShowMessage(string.Format(
                    Localizer.Get("Settings_VerificationFailed", "qBittorrent did not apply these settings: {0}"),
                    labels), InfoBarSeverity.Warning);
            }
            else
            {
                ShowMessage(hadRemoteChanges
                    ? Localizer.Get("Settings_AppliedVerified", "Settings were applied and verified by qBittorrent.")
                    : Localizer.Get("Settings_Applied", "Settings applied. Restart the app to apply language, theme, or density changes."),
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void ShowMessage(string message, InfoBarSeverity severity) { MessageBar.Message = message; MessageBar.Severity = severity; MessageBar.IsOpen = true; }
    private static string SettingDisplayName(string key)
    {
        var spec = Specs.FirstOrDefault(spec => spec.Key == key);
        return spec is null ? key : Localizer.Get($"Setting_{key.Replace('.', '_')}", spec.Label);
    }
    private static string SectionDisplayName(string value) => Localizer.Get($"SettingsSection_{value}", value == "WebUI" ? "Web UI" : value);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record SettingSpec(
        string Section,
        string Key,
        string Label,
        SettingKind Kind,
        bool Local = false,
        object? DefaultValue = null,
        string? Group = null,
        double? Minimum = null,
        double? Maximum = null);

    private enum SettingKind { Boolean, Number, Text, Password, Multiline, Language, Theme, ProxyType, PeerProtocol, Encryption, ResumeStorage }
}
