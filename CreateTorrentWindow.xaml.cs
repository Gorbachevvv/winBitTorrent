using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent;

public sealed partial class CreateTorrentWindow : Window
{
    private readonly MainViewModel _main;
    private StorageFile? _outputFile;
    private CancellationTokenSource? _lifetime;

    public CreateTorrentWindow()
    {
        InitializeComponent();
        Title = WinBitTorrent.Services.Localizer.Get("WindowTitle_CreateTorrent", "Create torrent");
        this.ConfigureOwned(850, 660);
        _main = App.Services.GetRequiredService<MainViewModel>();
    }

    private async void PickFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*"); Initialize(picker);
        if (await picker.PickSingleFileAsync() is { } file) SourceBox.Text = file.Path;
    }

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); Initialize(picker);
        if (await picker.PickSingleFolderAsync() is { } folder) SourceBox.Text = folder.Path;
    }

    private async void PickOutput_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileName(SourceBox.Text) + ".torrent" };
        picker.FileTypeChoices.Add("Torrent file", [".torrent"]); Initialize(picker);
        if (await picker.PickSaveFileAsync() is { } file) { _outputFile = file; OutputBox.Text = file.Path; }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Api is null || string.IsNullOrWhiteSpace(SourceBox.Text))
            return;
        if (_outputFile is null)
        {
            Show("Choose an output .torrent file.", InfoBarSeverity.Warning);
            return;
        }
        _lifetime = new CancellationTokenSource();
        CreateButton.IsEnabled = false;
        try
        {
            var pieceSize = PieceSizeBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var parsed) ? parsed : 0;
            var request = new JsonObject
            {
                ["sourcePath"] = SourceBox.Text,
                ["torrentFilePath"] = _outputFile.Path,
                ["pieceSize"] = pieceSize,
                ["isPrivate"] = PrivateBox.IsChecked == true,
                ["startSeeding"] = SeedBox.IsChecked == true,
                ["comment"] = CommentBox.Text,
                ["trackers"] = TrackersBox.Text
            };
            var created = await _main.Api.TorrentCreator.AddTaskAsync(request, _lifetime.Token);
            var taskId = created["taskID"]?.GetValue<int>() ?? created["taskId"]?.GetValue<int>() ?? created["id"]?.GetValue<int>() ?? throw new InvalidOperationException("The backend did not return a torrent creator task id.");
            while (!_lifetime.IsCancellationRequested)
            {
                var status = await _main.Api.TorrentCreator.GetStatusAsync(taskId, _lifetime.Token);
                var progress = status["progress"]?.GetValue<double>() ?? 0;
                Progress.Value = progress <= 1 ? progress * 100 : progress;
                var state = status["status"]?.GetValue<string>() ?? status["state"]?.GetValue<string>() ?? "Running";
                StatusText.Text = $"{state} — {Progress.Value:0}%";
                if (state.Contains("complete", StringComparison.OrdinalIgnoreCase) || state.Contains("success", StringComparison.OrdinalIgnoreCase))
                    break;
                if (state.Contains("fail", StringComparison.OrdinalIgnoreCase) || state.Contains("error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(status["error"]?.ToString() ?? "Torrent creation failed.");
                await Task.Delay(300, _lifetime.Token);
            }
            var bytes = await _main.Api.TorrentCreator.GetTorrentFileAsync(taskId, _lifetime.Token);
            await FileIO.WriteBytesAsync(_outputFile, bytes);
            await _main.Api.TorrentCreator.DeleteTaskAsync(taskId);
            Progress.Value = 100;
            Show("Torrent created successfully.", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Show(exception.Message, InfoBarSeverity.Error); }
        finally { CreateButton.IsEnabled = true; }
    }

    private void Initialize(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
    private void Show(string text, InfoBarSeverity severity) { MessageBar.Message = text; MessageBar.Severity = severity; MessageBar.IsOpen = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _lifetime?.Cancel(); Close(); }
}
