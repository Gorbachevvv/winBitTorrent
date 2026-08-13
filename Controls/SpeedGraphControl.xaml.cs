using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WinBitTorrent.Core.Services;
using WinBitTorrent.Services;

namespace WinBitTorrent.Controls;

public sealed partial class SpeedGraphControl : UserControl
{
    private const double MaximumHistorySeconds = 900;
    private const double LeftInset = 78;
    private const double RightInset = 14;
    private const double TopInset = 14;
    private const double BottomInset = 28;
    private const int HorizontalDivisions = 4;

    private static readonly Color DownloadColor = Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
    private static readonly Color UploadColor = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B);
    private static readonly Color GridColor = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);

    private readonly Queue<SpeedSample> _samples = new();
    private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private double _historySeconds = 300;
    private double _axisMaximum = 1024;
    private int _axisShrinkTicks;

    public SpeedGraphControl()
    {
        InitializeComponent();
        _sampleTimer.Tick += (_, _) => RecordSample();
        Loaded += SpeedGraphControl_Loaded;
        Unloaded += SpeedGraphControl_Unloaded;
        ActualThemeChanged += (_, _) => DrawGraph();
        UpdateSummary();
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

    public string SourceId
    {
        get => (string?)GetValue(SourceIdProperty) ?? string.Empty;
        set => SetValue(SourceIdProperty, value);
    }

    public static readonly DependencyProperty SourceIdProperty = DependencyProperty.Register(
        nameof(SourceId), typeof(string), typeof(SpeedGraphControl), new PropertyMetadata(string.Empty, OnSourceChanged));

    private static void OnSpeedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (SpeedGraphControl)dependencyObject;
        control.UpdateSummary();
        control.DrawGraph();
    }

    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (SpeedGraphControl)dependencyObject;
        control._samples.Clear();
        control._axisMaximum = 1024;
        control._axisShrinkTicks = 0;
        control.UpdateSummary();
        control.DrawGraph();
    }

    private void SpeedGraphControl_Loaded(object sender, RoutedEventArgs e)
    {
        RecordSample();
        _sampleTimer.Start();
    }

    private void SpeedGraphControl_Unloaded(object sender, RoutedEventArgs e)
        => _sampleTimer.Stop();

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => DrawGraph();

    private void HistoryRangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryRangeCombo.SelectedItem is ComboBoxItem { Tag: string seconds }
            && double.TryParse(seconds, out var parsed))
            _historySeconds = Math.Clamp(parsed, 60, MaximumHistorySeconds);

        UpdateAxisMaximum(force: true);
        UpdateSummary();
        DrawGraph();
    }

    private void RecordSample()
    {
        var now = DateTimeOffset.UtcNow;
        _samples.Enqueue(new SpeedSample(now, Math.Max(0, DownloadSpeed), Math.Max(0, UploadSpeed)));
        TrimExpiredSamples(now);
        UpdateAxisMaximum();
        UpdateSummary();
        DrawGraph(now);
    }

    private void TrimExpiredSamples(DateTimeOffset now)
    {
        var oldest = now.AddSeconds(-MaximumHistorySeconds);
        while (_samples.TryPeek(out var sample) && sample.Timestamp < oldest)
            _samples.Dequeue();
    }

    private void UpdateAxisMaximum(bool force = false)
    {
        var oldest = DateTimeOffset.UtcNow.AddSeconds(-_historySeconds);
        var visibleSamples = _samples.Where(sample => sample.Timestamp >= oldest).ToArray();
        var largest = Math.Max(
            Math.Max(DownloadSpeed, UploadSpeed),
            visibleSamples.Length == 0 ? 0 : visibleSamples.Max(static sample => Math.Max(sample.Download, sample.Upload)));
        var target = NiceCeiling(Math.Max(1024, largest * 1.08));

        if (force)
        {
            _axisMaximum = target;
            _axisShrinkTicks = 0;
        }
        else if (target > _axisMaximum)
        {
            _axisMaximum = target;
            _axisShrinkTicks = 0;
        }
        else if (target < _axisMaximum * 0.5)
        {
            if (++_axisShrinkTicks >= 10)
            {
                _axisMaximum = target;
                _axisShrinkTicks = 0;
            }
        }
        else
        {
            _axisShrinkTicks = 0;
        }
    }

    private void UpdateSummary()
    {
        if (DownloadCurrentValue is null)
            return;

        var currentDownload = Math.Max(0, DownloadSpeed);
        var currentUpload = Math.Max(0, UploadSpeed);
        var oldest = DateTimeOffset.UtcNow.AddSeconds(-_historySeconds);
        var visibleSamples = _samples.Where(sample => sample.Timestamp >= oldest).ToArray();
        var peakDownload = visibleSamples.Length == 0 ? currentDownload : Math.Max(currentDownload, visibleSamples.Max(static sample => sample.Download));
        var peakUpload = visibleSamples.Length == 0 ? currentUpload : Math.Max(currentUpload, visibleSamples.Max(static sample => sample.Upload));
        var averageDownload = visibleSamples.Length == 0 ? currentDownload : (long)visibleSamples.Average(static sample => sample.Download);
        var averageUpload = visibleSamples.Length == 0 ? currentUpload : (long)visibleSamples.Average(static sample => sample.Upload);

        DownloadCurrentValue.Text = ValueFormatter.Speed(currentDownload);
        UploadCurrentValue.Text = ValueFormatter.Speed(currentUpload);
        DownloadPeakValue.Text = ValueFormatter.Speed(peakDownload);
        UploadPeakValue.Text = ValueFormatter.Speed(peakUpload);
        DownloadAverageValue.Text = ValueFormatter.Speed(averageDownload);
        UploadAverageValue.Text = ValueFormatter.Speed(averageUpload);
    }

    private void DrawGraph(DateTimeOffset? timestamp = null)
    {
        if (GraphCanvas is null)
            return;

        GraphCanvas.Children.Clear();
        var width = GraphCanvas.ActualWidth;
        var height = GraphCanvas.ActualHeight;
        var plotWidth = width - LeftInset - RightInset;
        var plotHeight = height - TopInset - BottomInset;
        if (plotWidth <= 1 || plotHeight <= 1)
            return;

        DrawAxes(width, height, plotWidth, plotHeight);

        var now = timestamp ?? DateTimeOffset.UtcNow;
        TrimExpiredSamples(now);
        var visibleSamples = _samples
            .Where(sample => (now - sample.Timestamp).TotalSeconds <= _historySeconds)
            .ToArray();
        if (visibleSamples.Length == 0)
            return;

        var downloadPoints = CreatePoints(visibleSamples, now, plotWidth, plotHeight, static sample => sample.Download);
        var uploadPoints = CreatePoints(visibleSamples, now, plotWidth, plotHeight, static sample => sample.Upload);
        DrawSeriesFill(downloadPoints, DownloadColor);
        DrawSeriesFill(uploadPoints, UploadColor);
        DrawSeriesLine(downloadPoints, DownloadColor);
        DrawSeriesLine(uploadPoints, UploadColor);
    }

    private void DrawAxes(double width, double height, double plotWidth, double plotHeight)
    {
        var gridBrush = new SolidColorBrush(GridColor) { Opacity = ActualTheme == ElementTheme.Light ? 0.22 : 0.18 };
        var labelBrush = new SolidColorBrush(GridColor) { Opacity = ActualTheme == ElementTheme.Light ? 0.9 : 0.82 };

        for (var index = 0; index <= HorizontalDivisions; index++)
        {
            var y = TopInset + plotHeight * index / HorizontalDivisions;
            GraphCanvas.Children.Add(new Line
            {
                X1 = LeftInset,
                X2 = width - RightInset,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = index == HorizontalDivisions ? 1.2 : 1
            });

            var value = (long)(_axisMaximum * (HorizontalDivisions - index) / HorizontalDivisions);
            var label = AxisLabel(ValueFormatter.Speed(value), labelBrush, 68, TextAlignment.Right);
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, Math.Clamp(y - 9, 0, height - 18));
            GraphCanvas.Children.Add(label);
        }

        const int timeDivisions = 5;
        var timeLabels = Enumerable.Range(0, timeDivisions + 1)
            .Select(index => TimeAxisLabel(_historySeconds * (timeDivisions - index) / timeDivisions))
            .ToArray();
        for (var index = 0; index < timeLabels.Length; index++)
        {
            var x = LeftInset + plotWidth * index / timeDivisions;
            GraphCanvas.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = TopInset,
                Y2 = height - BottomInset,
                Stroke = gridBrush,
                StrokeThickness = 1
            });

            var alignment = index == 0 ? TextAlignment.Left : index == timeLabels.Length - 1 ? TextAlignment.Right : TextAlignment.Center;
            var label = AxisLabel(timeLabels[index], labelBrush, 54, alignment);
            Canvas.SetLeft(label, Math.Clamp(x - (index == 0 ? 0 : index == timeLabels.Length - 1 ? 54 : 27), LeftInset, width - RightInset - 54));
            Canvas.SetTop(label, height - BottomInset + 5);
            GraphCanvas.Children.Add(label);
        }
    }

    private Point[] CreatePoints(
        IReadOnlyList<SpeedSample> samples,
        DateTimeOffset now,
        double plotWidth,
        double plotHeight,
        Func<SpeedSample, long> selector)
        => samples.Select(sample =>
        {
            var age = Math.Clamp((now - sample.Timestamp).TotalSeconds, 0, _historySeconds);
            var x = LeftInset + plotWidth * (1 - age / _historySeconds);
            var ratio = Math.Clamp(selector(sample) / _axisMaximum, 0, 1);
            var y = TopInset + plotHeight * (1 - ratio);
            return new Point(x, y);
        }).ToArray();

    private void DrawSeriesFill(IReadOnlyList<Point> points, Color color)
    {
        if (points.Count == 0)
            return;

        var baseline = GraphCanvas.ActualHeight - BottomInset;
        var fillPoints = new PointCollection { new(points[0].X, baseline) };
        foreach (var point in points)
            fillPoints.Add(point);
        fillPoints.Add(new Point(points[^1].X, baseline));

        GraphCanvas.Children.Add(new Polygon
        {
            Points = fillPoints,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0x35, color.R, color.G, color.B), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0x05, color.R, color.G, color.B), Offset = 1 }
                }
            }
        });
    }

    private void DrawSeriesLine(IReadOnlyList<Point> points, Color color)
    {
        if (points.Count == 0)
            return;

        var line = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2.2,
            StrokeLineJoin = PenLineJoin.Round,
            Points = new PointCollection()
        };
        foreach (var point in points)
            line.Points.Add(point);
        GraphCanvas.Children.Add(line);

        var last = points[^1];
        var marker = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(color),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.2
        };
        Canvas.SetLeft(marker, last.X - marker.Width / 2);
        Canvas.SetTop(marker, last.Y - marker.Height / 2);
        GraphCanvas.Children.Add(marker);
    }

    private static TextBlock AxisLabel(string text, Brush foreground, double width, TextAlignment alignment)
        => new()
        {
            Width = width,
            FontSize = 11,
            Foreground = foreground,
            Text = text,
            TextAlignment = alignment,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

    private static string TimeAxisLabel(double seconds)
    {
        if (seconds < 1)
            return Localizer.Get("SpeedGraph_Now", "Now");
        if (seconds < 60)
            return string.Format(Localizer.Get("SpeedGraph_SecondsAgoFormat", "{0} sec"), Math.Round(seconds));
        return string.Format(Localizer.Get("SpeedGraph_MinutesAgoFormat", "{0} min"), Math.Round(seconds / 60));
    }

    private static double NiceCeiling(double value)
    {
        var exponent = Math.Pow(1024, Math.Floor(Math.Log(Math.Max(1024, value), 1024)));
        var normalized = value / exponent;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 4 ? 4 : normalized <= 8 ? 8 : 16;
        return nice * exponent;
    }

    private sealed record SpeedSample(DateTimeOffset Timestamp, long Download, long Upload);
}
