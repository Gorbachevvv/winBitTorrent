using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinBitTorrent.ViewModels;

namespace WinBitTorrent.Controls;

public enum TorrentMapMode
{
    Progress,
    Availability
}

/// <summary>
/// Renders compact, resolution-aware torrent maps. One visual column represents at most one
/// physical screen pixel, so torrents with hundreds of thousands of pieces remain inexpensive.
/// </summary>
public sealed partial class TorrentMapBar : UserControl
{
    private const int MaximumColumns = 640;

    public TorrentMapBar()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Draw();
    }

    public TorrentMapMode Mode
    {
        get => (TorrentMapMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(TorrentMapMode), typeof(TorrentMapBar),
        new PropertyMetadata(TorrentMapMode.Progress, OnVisualPropertyChanged));

    public IReadOnlyList<int>? PieceStates
    {
        get => GetValue(PieceStatesProperty) as IReadOnlyList<int>;
        set => SetValue(PieceStatesProperty, value);
    }

    public static readonly DependencyProperty PieceStatesProperty = DependencyProperty.Register(
        nameof(PieceStates), typeof(object), typeof(TorrentMapBar),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public IReadOnlyList<TorrentAvailabilitySegment>? AvailabilitySegments
    {
        get => GetValue(AvailabilitySegmentsProperty) as IReadOnlyList<TorrentAvailabilitySegment>;
        set => SetValue(AvailabilitySegmentsProperty, value);
    }

    public static readonly DependencyProperty AvailabilitySegmentsProperty = DependencyProperty.Register(
        nameof(AvailabilitySegments), typeof(object), typeof(TorrentMapBar),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(TorrentMapBar),
        new PropertyMetadata(0d, OnVisualPropertyChanged));

    public double Availability
    {
        get => (double)GetValue(AvailabilityProperty);
        set => SetValue(AvailabilityProperty, value);
    }

    public static readonly DependencyProperty AvailabilityProperty = DependencyProperty.Register(
        nameof(Availability), typeof(double), typeof(TorrentMapBar),
        new PropertyMetadata(-1d, OnVisualPropertyChanged));

    private static void OnVisualPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((TorrentMapBar)sender).Draw();

    private void MapCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Draw();

    private void Draw()
    {
        if (MapCanvas is null)
            return;

        MapCanvas.Children.Clear();
        var width = MapCanvas.ActualWidth;
        var height = MapCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
            return;

        if (Mode == TorrentMapMode.Progress)
            DrawProgress(width, height);
        else
            DrawAvailability(width, height);
    }

    private void DrawProgress(double width, double height)
    {
        var states = PieceStates;
        if (states is null || states.Count == 0)
        {
            DrawFallbackProgress(width, height);
            return;
        }

        var columns = Math.Min(MaximumColumns, Math.Max(1, (int)Math.Ceiling(width)));
        var colors = new Color[columns];
        var activeColumns = new bool[columns];
        for (var column = 0; column < columns; column++)
        {
            var start = column * states.Count / columns;
            var end = Math.Max(start + 1, (column + 1) * states.Count / columns);
            var complete = 0;
            var active = 0;
            for (var index = start; index < Math.Min(end, states.Count); index++)
            {
                if (states[index] >= 2)
                    complete++;
                else if (states[index] == 1)
                    active++;
            }

            var count = Math.Max(1, end - start);
            var completionDensity = (double)complete / count;
            colors[column] = Blend(ProgressMissingColor(), ProgressCompleteColor(), completionDensity);
            activeColumns[column] = active > 0;
        }

        DrawColorRuns(colors, width, height);
        DrawActiveRuns(activeColumns, width, height);

        // Individual piece boundaries are useful only while they can be resolved visually.
        if (states.Count <= width / 3)
            DrawBoundaries(states.Count, width, height, 0.16);
    }

    private void DrawFallbackProgress(double width, double height)
    {
        AddRectangle(0, 0, width, height, ProgressMissingColor());
        var completedWidth = width * Math.Clamp(Progress / 100d, 0d, 1d);
        if (completedWidth > 0)
            AddRectangle(0, 0, completedWidth, height, ProgressCompleteColor());
    }

    private void DrawAvailability(double width, double height)
    {
        var segments = AvailabilitySegments?
            .Where(static segment => segment.Size > 0)
            .ToArray();
        if (segments is null || segments.Length == 0)
        {
            AddRectangle(0, 0, width, height, AvailabilityColor(Availability));
            return;
        }

        var totalSize = segments.Sum(static segment => (double)segment.Size);
        if (totalSize <= 0)
            return;

        var columns = Math.Min(MaximumColumns, Math.Max(1, (int)Math.Ceiling(width)));
        var colors = new Color[columns];
        var segmentIndex = 0;
        double segmentEnd = segments[0].Size;
        for (var column = 0; column < columns; column++)
        {
            var midpoint = totalSize * (column + 0.5) / columns;
            while (segmentIndex < segments.Length - 1 && midpoint >= segmentEnd)
            {
                segmentIndex++;
                segmentEnd += segments[segmentIndex].Size;
            }
            colors[column] = AvailabilityColor(segments[segmentIndex].Availability);
        }
        DrawColorRuns(colors, width, height);

        if (segments.Length <= 180)
        {
            double consumed = 0;
            foreach (var segment in segments.Take(segments.Length - 1))
            {
                consumed += segment.Size;
                var x = width * consumed / totalSize;
                if (x >= 1 && x <= width - 1)
                    AddRectangle(x, 0, 1, height, BoundaryColor(0.28));
            }
        }
    }

    private void DrawColorRuns(IReadOnlyList<Color> colors, double width, double height)
    {
        if (colors.Count == 0)
            return;
        var runStart = 0;
        for (var index = 1; index <= colors.Count; index++)
        {
            if (index < colors.Count && colors[index] == colors[runStart])
                continue;
            var left = width * runStart / colors.Count;
            var right = width * index / colors.Count;
            AddRectangle(left, 0, Math.Max(1, right - left + 0.25), height, colors[runStart]);
            runStart = index;
        }
    }

    private void DrawActiveRuns(IReadOnlyList<bool> active, double width, double height)
    {
        var runStart = -1;
        for (var index = 0; index <= active.Count; index++)
        {
            var isActive = index < active.Count && active[index];
            if (isActive && runStart < 0)
                runStart = index;
            if (isActive || runStart < 0)
                continue;
            var left = width * runStart / active.Count;
            var right = width * index / active.Count;
            AddRectangle(left, 0, Math.Max(1, right - left), height, ProgressActiveColor());
            runStart = -1;
        }
    }

    private void DrawBoundaries(int count, double width, double height, double opacity)
    {
        for (var index = 1; index < count; index++)
            AddRectangle(width * index / count, 0, 1, height, BoundaryColor(opacity));
    }

    private void AddRectangle(double left, double top, double width, double height, Color color)
    {
        var rectangle = new Rectangle
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, height),
            Fill = new SolidColorBrush(color),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        MapCanvas.Children.Add(rectangle);
    }

    private Color ProgressMissingColor() => ActualTheme == ElementTheme.Light
        ? Color.FromArgb(0xFF, 0xE2, 0xE7, 0xEE)
        : Color.FromArgb(0xFF, 0x2B, 0x31, 0x39);

    private static Color ProgressCompleteColor() => Color.FromArgb(0xFF, 0x4C, 0x8D, 0xF6);
    private static Color ProgressActiveColor() => Color.FromArgb(0xFF, 0xFF, 0xB0, 0x20);

    private Color AvailabilityColor(double availability)
    {
        if (double.IsNaN(availability) || availability < 0)
            return ActualTheme == ElementTheme.Light
                ? Color.FromArgb(0xFF, 0xD5, 0xDA, 0xE1)
                : Color.FromArgb(0xFF, 0x3B, 0x42, 0x4C);
        if (availability <= 0.001)
            return Color.FromArgb(0xFF, 0xD9, 0x5C, 0x5C);
        if (availability < 0.5)
            return Blend(Color.FromArgb(0xFF, 0xD9, 0x5C, 0x5C), Color.FromArgb(0xFF, 0xF2, 0xA7, 0x2C), availability * 2);
        if (availability < 1)
            return Blend(Color.FromArgb(0xFF, 0xF2, 0xA7, 0x2C), Color.FromArgb(0xFF, 0x2A, 0xB3, 0x85), (availability - 0.5) * 2);
        return Blend(Color.FromArgb(0xFF, 0x2A, 0xB3, 0x85), Color.FromArgb(0xFF, 0x32, 0x78, 0xC8), Math.Clamp((availability - 1) / 2, 0, 1));
    }

    private Color BoundaryColor(double opacity)
        => ActualTheme == ElementTheme.Light
            ? Color.FromArgb((byte)(255 * opacity), 0xFF, 0xFF, 0xFF)
            : Color.FromArgb((byte)(255 * opacity), 0x00, 0x00, 0x00);

    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            0xFF,
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
