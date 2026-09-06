using System.Collections.Immutable;
using XrayUI.Helpers;
using XrayUI.Models.Traffic;
using XrayUI.Services.Traffic;
using XrayUI.ViewModels;

namespace XrayUI.Tests;

public class TrafficMonitorViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static (TrafficMonitorStore Store, TrafficCoreSession Session, TrafficMonitorViewModel ViewModel) Create()
    {
        var store = new TrafficMonitorStore(new TrafficMonitorOptions(["mixed-in"], ["proxy", "direct"]));
        var session = new TrafficCoreSession(Guid.NewGuid(), new Uri("http://127.0.0.1:10085"), Now);
        store.BeginSession(session);
        return (store, session, new TrafficMonitorViewModel(store));
    }
    private static TrafficAccessEvent Access(string host, TrafficRouteKind route = TrafficRouteKind.Proxy) =>
        new(Now, new("127.0.0.1", 5000), new(host, 443), TrafficTransport.Tcp,
            TrafficAccessStatus.Accepted, "mixed-in", route.ToString(), route);

    [Fact]
    public void DisconnectedPageShowsNoInventedMeasurements()
    {
        var vm = new TrafficMonitorViewModel();
        Assert.False(vm.HasSource); Assert.Empty(vm.Rows); Assert.Empty(vm.Samples);
        Assert.Equal("—", vm.Download); Assert.Equal("—", vm.Volume);
        Assert.False(vm.CanClear);
        Assert.Equal("Traffic_SourcePending", vm.Status);
    }

    [Fact]
    public void FiltersAndSortingUseTheSameSnapshot()
    {
        var (store, session, vm) = Create();
        store.RecordAccess(session.Id, Access("z.example"));
        store.RecordAccess(session.Id, Access("a.example", TrafficRouteKind.Direct));
        vm.Refresh();
        Assert.Equal("a.example:443", vm.Rows[0].Destination);
        vm.RouteFilter = 1;
        Assert.Single(vm.Rows); Assert.Equal("z.example:443", vm.Rows[0].Destination);
        vm.RouteFilter = 0; vm.SearchText = "Z.EXAMPLE";
        Assert.Single(vm.Rows);
        vm.SearchText = ""; vm.SortIndex = 1;
        Assert.Equal("a.example:443", vm.Rows[0].Destination);
        vm.SearchText = "not-found"; Assert.False(vm.HasRows);
    }

    [Fact]
    public void PauseFreezesDisplayButDoesNotStopCollection()
    {
        var (store, session, vm) = Create();
        store.RecordAccess(session.Id, Access("first")); vm.Refresh(); vm.IsPaused = true;
        store.RecordAccess(session.Id, Access("second")); vm.Refresh();
        Assert.Single(vm.Rows); Assert.Equal(2, store.GetSnapshot().AccessCount);
        vm.IsPaused = false; Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void ClearWhilePausedKeepsPauseAndClearsSelection()
    {
        var (store, session, vm) = Create();
        store.RecordAccess(session.Id, Access("first")); vm.Refresh(); vm.SelectedRow = vm.Rows[0];
        vm.IsPaused = true; vm.Clear();
        Assert.True(vm.IsPaused); Assert.Empty(vm.Rows); Assert.False(vm.HasSelection);
        Assert.Equal(0, store.GetSnapshot().AccessCount);
    }

    [Fact]
    public void RefreshWithoutNewEventsDoesNotResetListOrSelection()
    {
        var (store, session, vm) = Create();
        store.RecordAccess(session.Id, Access("first")); vm.Refresh();
        var selected = vm.Rows[0]; vm.SelectedRow = selected;
        var changes = 0; vm.Rows.CollectionChanged += (_, _) => changes++;
        vm.Refresh(); Assert.Equal(0, changes); Assert.Same(selected, vm.SelectedRow);
        for (var i = 0; i < 100; i++) store.RecordAccess(session.Id, Access("burst"));
        vm.Refresh(); Assert.Equal(1, changes);
        Assert.Equal(selected.Row, vm.SelectedRow!.Row);
    }

    [Fact]
    public void AddressFormattingPreservesIpv6AndMaskedAddresses()
    {
        Assert.Equal("[::1]:443", TrafficPresentation.Address(new("::1", 443)));
        Assert.Equal("192.0.*.*:443", TrafficPresentation.Address(new("192.0.*.*", 443, true)));
        Assert.Equal("—", TrafficPresentation.Address(null));
        Assert.Equal("—", TrafficPresentation.Rate(null));
        Assert.Equal("0 B/s", TrafficPresentation.Rate(0));
    }

    [Fact]
    public void ChartDoesNotJoinAcrossGapsOrPollStalls()
    {
        TrafficSample Sample(int seconds, TrafficSpeed? speed) =>
            new(Guid.Empty, TimeSpan.FromSeconds(seconds), Now.AddSeconds(seconds),
                speed.HasValue ? TrafficSampleKind.Measurement : TrafficSampleKind.Gap, default, speed, []);
        var plot = TrafficPresentation.Plot([Sample(0, new(10, 100)), Sample(1, null),
            Sample(2, new(10, 100)), Sample(20, new(20, 200))], 5, 300, 100);
        Assert.Equal(3, plot.Download.Length);
        Assert.All(plot.Download.SelectMany(x => x), p => { Assert.InRange(p.X, 0, 300); Assert.InRange(p.Y, 0, 100); });
        Assert.Equal(300, plot.Download[^1][0].X);
    }

    [Fact]
    public void ChartUsesSelectedWindowAndMonotonicTime()
    {
        TrafficSample Sample(int seconds) => new(Guid.Empty, TimeSpan.FromSeconds(seconds), Now,
            TrafficSampleKind.Measurement, default, new(10, 20), []);
        var plot = TrafficPresentation.Plot([Sample(0), Sample(301), Sample(302)], 5, 300, 100);
        Assert.Equal(2, plot.Download.SelectMany(x => x).Count());
        Assert.Empty(TrafficPresentation.Plot([], 5, 300, 100).Download);
    }
}
