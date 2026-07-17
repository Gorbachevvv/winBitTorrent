using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace WinBitTorrent.Controls;

public sealed class SpeedGraphControl : Canvas
{
    private readonly Queue<(long Download, long Upload)> _samples = new();

    public SpeedGraphControl()
    {
        MinHeight = 140;
        SizeChanged += (_, _) => DrawGraph();
    }

    public long DownloadSpeed
    {
        get => (long)GetValue(DownloadSpeedProperty);
        set => SetValue(DownloadSpeedProperty, value);
    }

    public static readonly DependencyProperty DownloadSpeedProperty = DependencyProperty.Register(
        nameof(DownloadSpeed), typeof(long), typeof(SpeedGraphControl), new PropertyMetadata(0L, OnSpeedChanged));

    public long UploadSpeed
    {
        get => (long)GetValue(UploadSpeedProperty);
        set => SetValue(UploadSpeedProperty, value);
    }

    public static readonly DependencyProperty UploadSpeedProperty = DependencyProperty.Register(
        nameof(UploadSpeed), typeof(long), typeof(SpeedGraphControl), new PropertyMetadata(0L, OnSpeedChanged));

    private static void OnSpeedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (SpeedGraphControl)dependencyObject;
        control._samples.Enqueue((control.DownloadSpeed, control.UploadSpeed));
        while (control._samples.Count > 180)
            control._samples.Dequeue();
        control.DrawGraph();
    }

    private void DrawGraph()
    {
        Children.Clear();
        if (ActualWidth <= 1 || ActualHeight <= 1)
            return;

        for (var index = 1; index < 4; index++)
        {
            var y = ActualHeight * index / 4;
            Children.Add(new Line
            {
                X1 = 0,
                X2 = ActualWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Colors.Gray),
                Opacity = 0.18,
                StrokeThickness = 1
            });
        }

        if (_samples.Count < 2)
            return;

        var max = Math.Max(1, _samples.Max(static sample => Math.Max(sample.Download, sample.Upload)));
        var download = new Polyline { Stroke = new SolidColorBrush(Colors.DodgerBlue), StrokeThickness = 2 };
        var upload = new Polyline { Stroke = new SolidColorBrush(Colors.Orange), StrokeThickness = 2 };
        var values = _samples.ToArray();
        for (var index = 0; index < values.Length; index++)
        {
            var x = values.Length == 1 ? 0 : index * ActualWidth / (values.Length - 1);
            download.Points.Add(new Windows.Foundation.Point(x, ActualHeight - values[index].Download * ActualHeight / max));
            upload.Points.Add(new Windows.Foundation.Point(x, ActualHeight - values[index].Upload * ActualHeight / max));
        }

        Children.Add(download);
        Children.Add(upload);
    }
}
