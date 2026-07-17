using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBitTorrent.Core.Abstractions;
using WinBitTorrent.Core.Models;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public sealed partial class ProfilesWindow : Window
{
    private readonly IServerProfileStore _profiles;
    private readonly ICredentialStore _credentials;
    private readonly MainViewModel _main;
    private List<ServerProfile> _items = [];
    private ServerProfile? _selected;

    public ProfilesWindow()
    {
        InitializeComponent();
        Title = WinBitTorrent.Services.Localizer.Get("WindowTitle_Profiles", "Server profiles");
        this.ConfigureOwned(820, 590);
        _profiles = App.Services.GetRequiredService<IServerProfileStore>();
        _credentials = App.Services.GetRequiredService<ICredentialStore>();
        _main = App.Services.GetRequiredService<MainViewModel>();
        Activated += ProfilesWindow_Activated;
    }

    private async void ProfilesWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= ProfilesWindow_Activated;
        await ReloadAsync();
    }

    private async Task ReloadAsync(Guid? select = null)
    {
        _items = (await _profiles.GetAllAsync()).ToList();
        ProfilesList.ItemsSource = _items;
        ProfilesList.SelectedItem = _items.FirstOrDefault(item => item.Id == select)
            ?? await _profiles.GetSelectedAsync()
            ?? _items.FirstOrDefault();
    }

    private async void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ProfilesList.SelectedItem as ServerProfile;
        if (_selected is null)
            return;
        NameBox.Text = _selected.Name;
        AddressBox.Text = _selected.BaseAddress.ToString();
        AuthenticationBox.SelectedIndex = _selected.Authentication == AuthenticationMode.ApiKey ? 1 : 0;
        UserNameBox.Text = _selected.UserName ?? string.Empty;
        SecretBox.Password = _selected.Kind == ProfileKind.Remote ? await _credentials.GetSecretAsync(_selected.Id) ?? string.Empty : string.Empty;
        FingerprintBox.Text = _selected.TrustedCertificateSha256 ?? string.Empty;
        FingerprintConfirmedBox.IsChecked = !string.IsNullOrWhiteSpace(_selected.TrustedCertificateSha256);
        var editable = !_selected.IsBuiltIn;
        SetEditorEnabled(editable);
        SaveButton.IsEnabled = editable;
        DeleteButton.IsEnabled = editable;
        ConnectButton.IsEnabled = true;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        ProfilesList.SelectedItem = null;
        SetEditorEnabled(true);
        SaveButton.IsEnabled = true;
        DeleteButton.IsEnabled = false;
        NameBox.Text = "Remote qBittorrent";
        AddressBox.Text = "https://";
        AuthenticationBox.SelectedIndex = 0;
        UserNameBox.Text = string.Empty;
        SecretBox.Password = string.Empty;
        FingerprintBox.Text = string.Empty;
        FingerprintConfirmedBox.IsChecked = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveEditorAsync();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var profile = _selected?.IsBuiltIn == true ? _selected : await SaveEditorAsync();
        if (profile is null)
            return;
        await _profiles.SelectAsync(profile.Id);
        _main.Profiles.Clear();
        foreach (var item in await _profiles.GetAllAsync())
            _main.Profiles.Add(item);
        _main.SelectedProfile = _main.Profiles.First(item => item.Id == profile.Id);
        await _main.ConnectSelectedProfileAsync();
        Close();
    }

    private async Task<ServerProfile?> SaveEditorAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                throw new InvalidOperationException("Enter a profile name.");
            if (!Uri.TryCreate(AddressBox.Text.Trim(), UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Enter a valid HTTP or HTTPS server address.");
            var fingerprint = NormalizeFingerprint(FingerprintBox.Text);
            if (!string.IsNullOrEmpty(fingerprint) && FingerprintConfirmedBox.IsChecked != true)
                throw new InvalidOperationException("Confirm that you verified the TLS certificate fingerprint.");
            if (!string.IsNullOrEmpty(fingerprint) && fingerprint.Length != 64)
                throw new InvalidOperationException("A SHA-256 fingerprint must contain 64 hexadecimal characters.");

            var authentication = AuthenticationBox.SelectedIndex == 1 ? AuthenticationMode.ApiKey : AuthenticationMode.UserNamePassword;
            var profile = new ServerProfile(
                _selected?.Id ?? Guid.NewGuid(),
                NameBox.Text.Trim(),
                ProfileKind.Remote,
                address,
                authentication,
                authentication == AuthenticationMode.UserNamePassword ? EmptyToNull(UserNameBox.Text) : null,
                EmptyToNull(fingerprint));
            await _profiles.SaveAsync(profile);
            if (!string.IsNullOrEmpty(SecretBox.Password))
                await _credentials.SetSecretAsync(profile.Id, SecretBox.Password);
            _selected = profile;
            ShowMessage("Profile saved.", InfoBarSeverity.Success);
            await ReloadAsync(profile.Id);
            return profile;
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, InfoBarSeverity.Error);
            return null;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selected.IsBuiltIn)
            return;
        await _profiles.DeleteAsync(_selected.Id);
        await _credentials.DeleteSecretAsync(_selected.Id);
        await ReloadAsync();
    }

    private void AuthenticationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UserNameBox.Visibility = AuthenticationBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SetEditorEnabled(bool enabled)
    {
        foreach (var control in Editor.Children.OfType<Control>())
            control.IsEnabled = enabled;
    }

    private void ShowMessage(string text, InfoBarSeverity severity) { MessageBar.Message = text; MessageBar.Severity = severity; MessageBar.IsOpen = true; }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeFingerprint(string value) => new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
