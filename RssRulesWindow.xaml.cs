using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public sealed partial class RssRulesWindow : Window
{
    private readonly MainViewModel _main;
    private JsonObject _rules = [];
    private string? _originalName;

    public RssRulesWindow()
    {
        InitializeComponent();
        Title = WinBitTorrent.Services.Localizer.Get("WindowTitle_RssRules", "RSS downloader rules");
        this.ConfigureOwned(900, 690);
        _main = App.Services.GetRequiredService<MainViewModel>();
        Activated += RssRulesWindow_Activated;
    }

    private async void RssRulesWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= RssRulesWindow_Activated;
        await RefreshAsync();
    }

    private async Task RefreshAsync(string? selected = null)
    {
        if (_main.Api is null) return;
        try
        {
            _rules = await _main.Api.Rss.GetRulesAsync();
            RulesList.ItemsSource = _rules.Select(static item => item.Key).OrderBy(static name => name).ToList();
            RulesList.SelectedItem = selected ?? _rules.FirstOrDefault().Key;
        }
        catch (Exception exception) { Show(exception.Message, InfoBarSeverity.Error); }
    }

    private void RulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _originalName = RulesList.SelectedItem as string;
        if (_originalName is null || _rules[_originalName] is not JsonObject rule) return;
        RuleNameBox.Text = _originalName;
        EnabledSwitch.IsOn = Bool(rule, "enabled", true);
        MustContainBox.Text = Text(rule, "mustContain");
        MustNotContainBox.Text = Text(rule, "mustNotContain");
        EpisodeBox.Text = Text(rule, "episodeFilter");
        CategoryBox.Text = Text(rule, "assignedCategory");
        SavePathBox.Text = Text(rule, "savePath");
        RegexBox.IsChecked = Bool(rule, "useRegex");
        SmartFilterBox.IsChecked = Bool(rule, "smartFilter", true);
        PausedBox.IsChecked = Bool(rule, "addPaused");
        FeedsBox.Text = rule["affectedFeeds"] is JsonArray feeds ? string.Join(Environment.NewLine, feeds.Select(static item => item?.ToString())) : string.Empty;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        RulesList.SelectedItem = null; _originalName = null; RuleNameBox.Text = "New rule"; EnabledSwitch.IsOn = true; MustContainBox.Text = MustNotContainBox.Text = EpisodeBox.Text = CategoryBox.Text = SavePathBox.Text = FeedsBox.Text = string.Empty; RegexBox.IsChecked = false; SmartFilterBox.IsChecked = true; PausedBox.IsChecked = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Api is null || string.IsNullOrWhiteSpace(RuleNameBox.Text)) return;
        var name = RuleNameBox.Text.Trim();
        var feeds = new JsonArray(FeedsBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => (JsonNode?)JsonValue.Create(value))
            .ToArray());
        var existing = _originalName is not null && _rules[_originalName] is JsonObject prior ? (JsonObject)prior.DeepClone() : new JsonObject();
        existing["enabled"] = EnabledSwitch.IsOn;
        existing["mustContain"] = MustContainBox.Text;
        existing["mustNotContain"] = MustNotContainBox.Text;
        existing["useRegex"] = RegexBox.IsChecked == true;
        existing["episodeFilter"] = EpisodeBox.Text;
        existing["smartFilter"] = SmartFilterBox.IsChecked == true;
        existing["affectedFeeds"] = feeds;
        existing["addPaused"] = PausedBox.IsChecked == true;
        existing["assignedCategory"] = CategoryBox.Text;
        existing["savePath"] = SavePathBox.Text;
        try
        {
            if (_originalName is not null && !name.Equals(_originalName, StringComparison.Ordinal) && _rules.ContainsKey(name))
                throw new InvalidOperationException("A rule with that name already exists.");
            if (_originalName is not null && !name.Equals(_originalName, StringComparison.Ordinal))
                await _main.Api.Rss.PostAsync("removeRule", new Dictionary<string, string?> { ["ruleName"] = _originalName });
            await _main.Api.Rss.PostAsync("setRule", new Dictionary<string, string?> { ["ruleName"] = name, ["ruleDef"] = existing.ToJsonString() });
            _originalName = name;
            await RefreshAsync(name);
            Show("Rule saved.", InfoBarSeverity.Success);
        }
        catch (Exception exception) { Show(exception.Message, InfoBarSeverity.Error); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Api is null || _originalName is null) return;
        await _main.Api.Rss.PostAsync("removeRule", new Dictionary<string, string?> { ["ruleName"] = _originalName });
        _originalName = null; await RefreshAsync();
    }

    private static string Text(JsonObject value, string key) => value[key]?.GetValue<string>() ?? string.Empty;
    private static bool Bool(JsonObject value, string key, bool fallback = false) => value[key]?.GetValue<bool>() ?? fallback;
    private void Show(string text, InfoBarSeverity severity) { MessageBar.Message = text; MessageBar.Severity = severity; MessageBar.IsOpen = true; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
