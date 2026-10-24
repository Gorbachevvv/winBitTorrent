using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent.Views;

public sealed partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<LogViewModel>();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private LogViewModel ViewModel => (LogViewModel)DataContext;
}
