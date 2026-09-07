using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace XrayUI.Diagnostics;

public sealed partial class RepeaterProbeGroup
{
    public string Title { get; }
    public ObservableCollection<string> Rows { get; } = new();
    public RepeaterProbeGroup(int index, int count)
    { Title = "Group " + index; for (var i = 0; i < count; i++) Rows.Add("Server " + i); }
}

public sealed partial class RepeaterProbeView : UserControl
{
    public ObservableCollection<RepeaterProbeGroup> Data { get; } = new();
    public int LiveRows { get; private set; }
    public int PeakRows { get; private set; }
    public RepeaterProbeView(int groups = 50, int rows = 100)
    {
        for (var i = 0; i < groups; i++) Data.Add(new(i, rows));
        InitializeComponent();
    }
    private void RowPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    { LiveRows++; PeakRows = Math.Max(PeakRows, LiveRows); }
    private void RowClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args) => LiveRows--;
    public static async Task RunAsync()
    {
        var report = Path.Combine(AppContext.BaseDirectory, "repeater-probe.txt");
        Application.Current.UnhandledException += (_, e) => { File.WriteAllText(report, "FAIL: " + e.Exception); Environment.Exit(1); };
        try
        {
            var window = new Window();
            var view = new RepeaterProbeView { Width = 600, Height = 500 };
            window.Content = view;
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.Activate(); window.AppWindow.Hide();
            await Task.Delay(300);
            view.UpdateLayout();
            if (view.LiveRows <= 0 || view.LiveRows > 200 || view.PeakRows > 500)
                throw new InvalidOperationException($"Virtualization failed: live={view.LiveRows}, peak={view.PeakRows}");
            var group = (Expander)view.Groups.GetOrCreateElement(1);
            group.UpdateLayout(); group.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            await Task.Delay(300);
            view.Scroll.ChangeView(null, Math.Max(0, view.Scroll.VerticalOffset - 40), null, true);
            await Task.Delay(100);
            var anchor = (UIElement)group.Header;
            view.Scroll.RegisterAnchorCandidate(anchor);
            view.Scroll.AnchorRequested += (_, args) => args.Anchor = anchor;
            // Establish the native anchor before resizing the group's content.
            view.Scroll.InvalidateArrange(); view.Scroll.UpdateLayout();
            var before = group.TransformToVisual(view.Scroll).TransformPoint(new Point()).Y;
            group.IsExpanded = false;
            await Task.Delay(250);
            var collapsed = group.TransformToVisual(view.Scroll).TransformPoint(new Point()).Y;
            group.IsExpanded = true;
            await Task.Delay(250);
            var expanded = group.TransformToVisual(view.Scroll).TransformPoint(new Point()).Y;
            if (Math.Abs(before - collapsed) > 2 || Math.Abs(before - expanded) > 2)
                throw new InvalidOperationException($"Anchor failed: {before} -> {collapsed} -> {expanded}");
            await File.WriteAllTextAsync(report, $"PASS: 50 x 100 rows; live={view.LiveRows}, peak={view.PeakRows}; header Y={before}/{collapsed}/{expanded}");
            window.Close(); Environment.Exit(0);
        }
        catch (Exception ex) { await File.WriteAllTextAsync(report, "FAIL: " + ex); Environment.Exit(1); }
    }
}
