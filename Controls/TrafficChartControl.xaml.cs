using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using XrayUI.Helpers;
using XrayUI.Models.Traffic;

namespace XrayUI.Controls;

public sealed partial class TrafficChartControl : UserControl
{
    private ImmutableArray<TrafficSample> _samples = [];
    private int _minutes = 5;
    private readonly ToolTip _tooltip = new();
    public TrafficChartControl()
    {
        InitializeComponent();
        ToolTipService.SetToolTip(PlotArea, _tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, Loc.GetString("Traffic_ChartAccessibleName"));
    }
    public void SetSamples(ImmutableArray<TrafficSample> samples, int minutes)
    {
        _samples = samples; _minutes = minutes; Draw();
    }
    private void PlotSizeChanged(object sender, SizeChangedEventArgs e) => Draw();
    private void Draw()
    {
        if (PlotArea == null) return;
        var plot = TrafficPresentation.Plot(_samples, _minutes, PlotArea.ActualWidth, PlotArea.ActualHeight);
        DownloadLine.Data = Geometry(plot.Download);
        UploadLine.Data = Geometry(plot.Upload);
        ScaleLabel.Text = plot.Download.IsEmpty && plot.Upload.IsEmpty ? "—" : TrafficPresentation.Rate(plot.Maximum);
        RangeLabel.Text = Loc.Format("Traffic_Range", _minutes);
        EmptyLabel.Visibility = plot.Download.IsEmpty && plot.Upload.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    private static PathGeometry Geometry(ImmutableArray<ImmutableArray<TrafficPlotPoint>> segments)
    {
        var geometry = new PathGeometry();
        foreach (var segment in segments)
        {
            if (segment.IsEmpty) continue;
            var figure = new PathFigure { StartPoint = new Point(segment[0].X, segment[0].Y), IsClosed = false, IsFilled = false };
            if (segment.Length == 1)
                figure.Segments.Add(new LineSegment { Point = new Point(segment[0].X + 0.01, segment[0].Y) });
            foreach (var point in segment.Skip(1)) figure.Segments.Add(new LineSegment { Point = new Point(point.X, point.Y) });
            geometry.Figures.Add(figure);
        }
        return geometry;
    }
    private void PlotPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_samples.IsEmpty || PlotArea.ActualWidth <= 0) return;
        var end = _samples[^1].Elapsed.TotalSeconds;
        var target = end - _minutes * 60 + e.GetCurrentPoint(PlotArea).Position.X / PlotArea.ActualWidth * _minutes * 60;
        var sample = _samples.MinBy(s => Math.Abs(s.Elapsed.TotalSeconds - target));
        if (sample == null || Math.Abs(sample.Elapsed.TotalSeconds - target) > 3) { _tooltip.IsOpen = false; return; }
        _tooltip.Content = $"{sample.ObservedAt.ToLocalTime():HH:mm:ss}  ↓ {TrafficPresentation.Rate(sample.Speed?.DownloadBytesPerSecond)}  ↑ {TrafficPresentation.Rate(sample.Speed?.UploadBytesPerSecond)}";
        _tooltip.IsOpen = true;
    }
    private void PlotPointerExited(object sender, PointerRoutedEventArgs e) => _tooltip.IsOpen = false;
}
