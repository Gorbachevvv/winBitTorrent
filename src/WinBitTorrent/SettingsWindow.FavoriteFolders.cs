using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinBitTorrent.Core.Models;
using WinBitTorrent.Services;

namespace WinBitTorrent;

public sealed partial class SettingsWindow
{
    private readonly List<FavoriteFolder> _favoriteFolders = ClientSettings.GetFavoriteFolders();

    private FrameworkElement CreateFavoriteFoldersCard()
    {
        var list = new StackPanel { Spacing = 5 };
        void Refresh()
        {
            list.Children.Clear();
            foreach (var folder in _favoriteFolders)
            {
                var label = new StackPanel();
                label.Children.Add(new TextBlock { Text = folder.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                label.Children.Add(new TextBlock { Text = folder.Path, Style = SecondaryTextStyle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(label);
                var pencil = new FontIcon { Glyph = "\uE70F", FontSize = 14 };
                Grid.SetColumn(pencil, 1);
                row.Children.Add(pencil);
                var edit = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(edit, $"{Localizer.Get("FavoriteFolders_Edit", "Edit folder")} · {folder.Name}");
                ToolTipService.SetToolTip(edit, folder.Path);
                edit.Click += async (_, _) => await EditAsync(folder);
                list.Children.Add(edit);
            }
            var add = new Button { Content = Localizer.Get("FavoriteFolders_Add", "Add folder") };
            add.Click += async (_, _) => await EditAsync(null);
            list.Children.Add(add);
        }

        async Task EditAsync(FavoriteFolder? folder)
        {
            var name = new TextBox { Header = Localizer.Get("FavoriteFolders_Name", "Display name"), Text = folder?.Name ?? "" };
            var path = new TextBox { Header = Localizer.Get("AddTorrentSavePathLabel.Text", "Save path"), Text = folder?.Path ?? "" };
            var fields = new StackPanel { Spacing = 10 };
            fields.Children.Add(name);
            if (_main.SelectedProfile?.Kind == ProfileKind.LocalLibtorrent)
            {
                fields.Children.Add(EditorButtonGrid(path, Localizer.Get("CommonBrowse.Content", "Browse…"), async (_, _) =>
                {
                    var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    if (await picker.PickSingleFolderAsync() is { } selected)
                    {
                        path.Text = selected.Path;
                        if (string.IsNullOrWhiteSpace(name.Text))
                            name.Text = selected.DisplayName;
                    }
                }));
            }
            else
                fields.Children.Add(path);

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
                Title = folder is null ? Localizer.Get("FavoriteFolders_Add", "Add folder") : Localizer.Get("FavoriteFolders_Edit", "Edit folder"),
                Content = fields,
                PrimaryButtonText = Localizer.Get("Common_OK", "OK"),
                SecondaryButtonText = folder is null ? "" : Localizer.Get("CommonDelete.Content", "Delete"),
                CloseButtonText = Localizer.Get("CommonCancel.Content", "Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            void Validate() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(path.Text);
            name.TextChanged += (_, _) => Validate();
            path.TextChanged += (_, _) => Validate();
            Validate();
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
                return;
            var index = _favoriteFolders.FindIndex(item => ReferenceEquals(item, folder));
            if (result == ContentDialogResult.Secondary)
                _favoriteFolders.RemoveAt(index);
            else
            {
                var updated = new FavoriteFolder(name.Text.Trim(), path.Text.Trim());
                if (folder is null)
                    _favoriteFolders.Add(updated);
                else
                    _favoriteFolders[index] = updated;
            }
            _localValues[FavoriteFolder.SettingsKey] = _favoriteFolders.ToArray();
            Refresh();
        }

        Refresh();
        return CreateSettingsCard(Localizer.Get("FavoriteFolders_Title", "Favorite folders"),
            Localizer.Get("FavoriteFolders_Description", "Choose a save path with one click when adding a torrent."), list, fullWidthEditor: true);
    }
}
