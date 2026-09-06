using System.Collections.Immutable;
using XrayUI.Models.Traffic;
using XrayUI.Services.Traffic;

namespace XrayUI.Tests;

public class TrafficMonitorStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static TrafficCoreSession Session() => new(Guid.NewGuid(), new Uri("http://127.0.0.1:10085"), Now);
    private static TrafficMonitorStore Store(int access = 5000, int samples = 900) =>
        new(new TrafficMonitorOptions(["mixed-in", "tun-in"], ["proxy", "direct"], access, samples));
    private static TrafficCounter Counter(string tag, long bytes, TrafficDirection direction = TrafficDirection.Download,
        TrafficCounterScope scope = TrafficCounterScope.Inbound) => new(new(scope, tag, direction), bytes);
    private static TrafficCounterSnapshot Counters(TrafficCoreSession session, double seconds, params TrafficCounter[] values) =>
        new(session.Id, TimeSpan.FromSeconds(seconds), Now.AddSeconds(seconds), [.. values]);
    private static TrafficAccessEvent Access() => new(Now, null, new("example.com", 443), TrafficTransport.Tcp,
        TrafficAccessStatus.Accepted, "mixed-in", "proxy", TrafficRouteKind.Proxy);

    [Fact]
    public void FirstSampleIsBaseline_ThenRatesUseElapsedTimeNotWallClock()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 1, Counter("mixed-in", 100)));
        Assert.Null(store.GetSnapshot().Speed);
        var later = Counters(session, 3.5, Counter("mixed-in", 600)) with { ObservedAt = Now.AddDays(-1) };
        store.RecordCounters(later);
        Assert.Equal(200, store.GetSnapshot().Speed!.Value.DownloadBytesPerSecond);
        Assert.Equal(500, store.GetSnapshot().Volume.DownloadBytes);
    }

    [Fact]
    public void InboundsAreTotals_OutboundsAreIndependentAndNotDoubleCounted()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0));
        store.RecordCounters(Counters(session, 1,
            Counter("mixed-in", 100), Counter("tun-in", 200), Counter("api", 999999),
            Counter("proxy", 300, scope: TrafficCounterScope.Outbound),
            Counter("direct", 20, scope: TrafficCounterScope.Outbound),
            Counter("chain-entry", 300, scope: TrafficCounterScope.Outbound)));
        var snapshot = store.GetSnapshot();
        Assert.Equal(300, snapshot.Volume.DownloadBytes);
        Assert.Equal(2, snapshot.Samples[^1].Routes.Length);
        Assert.Equal(300, snapshot.Samples[^1].Routes[0].Volume.DownloadBytes);
        Assert.Equal(20, snapshot.Samples[^1].Routes[1].Volume.DownloadBytes);
    }

    [Fact]
    public void UploadAndDownloadAreNotReversed()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0));
        store.RecordCounters(Counters(session, 2, Counter("mixed-in", 80, TrafficDirection.Upload), Counter("mixed-in", 200)));
        Assert.Equal(new TrafficSpeed(40, 100), store.GetSnapshot().Speed);
        Assert.Equal(new TrafficVolume(80, 200), store.GetSnapshot().Volume);
    }

    [Fact]
    public void ResetOrDisappearingCounterCannotProduceNegativeRatesOrRecoverySpike()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0, Counter("mixed-in", 100)));
        store.RecordCounters(Counters(session, 1, Counter("mixed-in", 200)));
        store.RecordCounters(Counters(session, 2));
        Assert.Equal(TrafficSampleKind.Gap, store.GetSnapshot().Samples[^1].Kind);
        Assert.Null(store.GetSnapshot().Speed);
        store.RecordCounters(Counters(session, 3, Counter("mixed-in", 10000)));
        Assert.Null(store.GetSnapshot().Speed);
        store.RecordCounters(Counters(session, 4, Counter("mixed-in", 10020)));
        Assert.Equal(20, store.GetSnapshot().Speed!.Value.DownloadBytesPerSecond);
        Assert.Equal(120, store.GetSnapshot().Volume.DownloadBytes);
    }

    [Fact]
    public void ApiFailureCreatesGap_AndRecoveryEstablishesNewBaseline()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0));
        store.RecordCounters(Counters(session, 1, Counter("mixed-in", 100)));
        store.RecordUnavailable(session.Id, TimeSpan.FromSeconds(2), Now);
        Assert.Equal(TrafficMonitorStatus.Unavailable, store.GetSnapshot().Status);
        Assert.Equal(TrafficSampleKind.Gap, store.GetSnapshot().Samples[^1].Kind);
        Assert.Null(store.GetSnapshot().Speed);
        store.RecordCounters(Counters(session, 10, Counter("mixed-in", 5000)));
        Assert.Null(store.GetSnapshot().Speed);
        Assert.Equal(100, store.GetSnapshot().Volume.DownloadBytes);
        store.RecordCounters(Counters(session, 12, Counter("mixed-in", 5100)));
        Assert.Equal(50, store.GetSnapshot().Speed!.Value.DownloadBytesPerSecond);
    }

    [Fact]
    public void RestartRejectsStaleCountersAccessesAndStopNotifications()
    {
        var store = Store(); var old = Session(); var current = Session();
        store.BeginSession(old); store.RecordAccess(old.Id, Access());
        store.BeginSession(current);
        Assert.False(store.RecordCounters(Counters(old, 10, Counter("mixed-in", 5000))));
        Assert.False(store.RecordAccess(old.Id, Access()));
        Assert.False(store.RecordUnavailable(old.Id, TimeSpan.FromSeconds(20), Now));
        store.EndSession(old.Id);
        Assert.Equal(TrafficMonitorStatus.WaitingForSample, store.GetSnapshot().Status);
        Assert.Empty(store.GetSnapshot().Accesses);
        Assert.True(store.RecordCounters(Counters(current, 0)));
    }

    [Fact]
    public void DuplicateOrOutOfOrderResponsesDoNotChangeState()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 3, Counter("mixed-in", 100)));
        Assert.False(store.RecordCounters(Counters(session, 3, Counter("mixed-in", 999))));
        Assert.False(store.RecordUnavailable(session.Id, TimeSpan.FromSeconds(2), Now));
        Assert.False(store.RecordCounters(Counters(session, -1)));
        Assert.Single(store.GetSnapshot().Samples);
    }

    [Fact]
    public void ClearOnlyResetsLocalHistory_AndDoesNotReuseOldBaseline()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0));
        store.RecordCounters(Counters(session, 1, Counter("mixed-in", 100)));
        store.RecordAccess(session.Id, Access()); var sequence = store.GetSnapshot().Accesses[0].Sequence;
        store.Clear();
        Assert.False(store.RecordCounters(Counters(session, 0.5, Counter("mixed-in", 50))));
        store.RecordCounters(Counters(session, 2, Counter("mixed-in", 300)));
        Assert.Equal(0, store.GetSnapshot().Volume.DownloadBytes);
        Assert.Null(store.GetSnapshot().Speed);
        store.RecordCounters(Counters(session, 3, Counter("mixed-in", 320)));
        Assert.Equal(20, store.GetSnapshot().Volume.DownloadBytes);
        store.RecordAccess(session.Id, Access());
        Assert.True(store.GetSnapshot().Accesses[0].Sequence > sequence);
    }

    [Fact]
    public void SnapshotsAreImmutableAndStorageIsBounded()
    {
        var store = Store(2, 2); var session = Session(); store.BeginSession(session);
        store.RecordAccess(session.Id, Access()); var first = store.GetSnapshot();
        for (var i = 0; i < 10; i++)
        {
            store.RecordAccess(session.Id, Access());
            store.RecordCounters(Counters(session, i, Counter("mixed-in", i)));
        }
        var last = store.GetSnapshot();
        Assert.Single(first.Accesses);
        Assert.Equal(2, last.Accesses.Length);
        Assert.Equal(2, last.Samples.Length);
        Assert.Equal(11, last.AccessCount);
        Assert.Equal(9, last.EvictedAccessCount);
        Assert.Null(last.Accesses[0].Access.ProcessName);
        Assert.Null(last.Accesses[0].Access.ProcessId);
    }

    [Fact]
    public async Task ConcurrentReadersAndAccessWritersKeepConsistentCounts()
    {
        var store = Store(100); var session = Session(); store.BeginSession(session);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) { store.RecordAccess(session.Id, Access()); store.GetSnapshot(); }
        }, TestContext.Current.CancellationToken)));
        Assert.Equal(400, store.GetSnapshot().AccessCount);
        Assert.Equal(300, store.GetSnapshot().EvictedAccessCount);
        Assert.Equal(100, store.GetSnapshot().Accesses.Select(x => x.Sequence).Distinct().Count());
    }

    [Fact]
    public void MalformedCountersBecomeUnavailableWithoutPartialUpdates()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0));
        Assert.False(store.RecordCounters(Counters(session, 1, Counter("mixed-in", -1))));
        Assert.Equal(TrafficUnavailableReason.InvalidCounters, store.GetSnapshot().UnavailableReason);
        Assert.False(store.RecordCounters(Counters(session, 2, Counter("mixed-in", 1), Counter("mixed-in", 2))));
        Assert.Equal(0, store.GetSnapshot().Volume.DownloadBytes);
    }

    [Fact]
    public void StopRetainsHistoryButRejectsNewData()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordAccess(session.Id, Access()); store.EndSession(session.Id);
        Assert.False(store.RecordCounters(Counters(session, 10)));
        Assert.False(store.RecordAccess(session.Id, Access()));
        Assert.Single(store.GetSnapshot().Accesses);
        Assert.Null(store.GetSnapshot().Speed);
        Assert.Equal(TrafficMonitorStatus.Stopped, store.GetSnapshot().Status);
    }

    [Fact]
    public void ConfigurationCopiesTagsAndRejectsRemoteEndpoint()
    {
        var tags = new List<string> { "mixed-in", "mixed-in" };
        var options = new TrafficMonitorOptions(tags, []); tags.Clear();
        Assert.Single(options.InboundTags);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrafficMonitorOptions(["mixed-in"], [], 0));
        var store = Store();
        Assert.Throws<ArgumentException>(() => store.BeginSession(Session() with { StatsEndpoint = new Uri("http://example.com:10085") }));
    }

    [Fact]
    public void CounterMissingForSeveralPollsCannotProduceSpikeWhenItReturns()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0, Counter("mixed-in", 100)));
        store.RecordCounters(Counters(session, 1));
        store.RecordCounters(Counters(session, 2));
        store.RecordCounters(Counters(session, 3));
        store.RecordCounters(Counters(session, 4, Counter("mixed-in", 10000)));
        Assert.Null(store.GetSnapshot().Speed);
        Assert.Equal(0, store.GetSnapshot().Volume.DownloadBytes);
        store.RecordCounters(Counters(session, 5, Counter("mixed-in", 10020)));
        Assert.Equal(20, store.GetSnapshot().Speed!.Value.DownloadBytesPerSecond);
    }

    [Fact]
    public void NullCounterIsInvalidDataRatherThanAnException()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        Assert.False(store.RecordCounters(Counters(session, 0, new TrafficCounter[] { null! })));
        Assert.Equal(TrafficUnavailableReason.InvalidCounters, store.GetSnapshot().UnavailableReason);
    }

    [Fact]
    public void ClearDoesNotHideAnApiOutage()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordUnavailable(session.Id, TimeSpan.Zero, Now);
        store.Clear();
        Assert.Equal(TrafficMonitorStatus.Unavailable, store.GetSnapshot().Status);
        Assert.Equal(TrafficUnavailableReason.ApiUnavailable, store.GetSnapshot().UnavailableReason);
    }

    [Fact]
    public void NullEndpointIsRejectedWithoutMutatingCurrentSession()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        Assert.Throws<ArgumentException>(() => store.BeginSession(Session() with { StatsEndpoint = null! }));
        Assert.Equal(session, store.GetSnapshot().Session);
    }

    [Fact]
    public void ExplicitCounterResetUsesNewBaselineWithoutLosingFollowingInterval()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0, Counter("mixed-in", 100)));
        store.RecordCounters(Counters(session, 1, Counter("mixed-in", 0)));
        Assert.Equal(TrafficSampleKind.CounterReset, store.GetSnapshot().Samples[^1].Kind);
        Assert.Null(store.GetSnapshot().Speed);
        store.RecordCounters(Counters(session, 2, Counter("mixed-in", 20)));
        Assert.Equal(20, store.GetSnapshot().Speed!.Value.DownloadBytesPerSecond);
    }

    [Fact]
    public void ClearCannotTurnMissingCounterIntoZero_ButNewSessionForgetsOldKeys()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0, Counter("mixed-in", 100)));
        store.Clear();
        Assert.False(store.RecordCounters(Counters(session, 1)));
        Assert.Equal(TrafficUnavailableReason.IncompleteCounters, store.GetSnapshot().UnavailableReason);
        store.RecordCounters(Counters(session, 2, Counter("mixed-in", 10000)));
        Assert.Null(store.GetSnapshot().Speed);
        var next = Session(); store.BeginSession(next);
        Assert.True(store.RecordCounters(Counters(next, 0)));
        Assert.True(store.RecordCounters(Counters(next, 1)));
        Assert.Equal(new TrafficSpeed(0, 0), store.GetSnapshot().Speed);
    }

    [Fact]
    public void VeryLargeTotalsSaturateRatherThanOverflow()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0));
        store.RecordCounters(Counters(session, 1, Counter("mixed-in", long.MaxValue), Counter("tun-in", long.MaxValue)));
        Assert.Equal(long.MaxValue, store.GetSnapshot().Volume.DownloadBytes);
        Assert.True(double.IsFinite(store.GetSnapshot().Speed!.Value.DownloadBytesPerSecond));
    }

    [Fact]
    public void UnmeasuredCounterDisappearanceDoesNotInterruptUserTraffic()
    {
        var store = Store(); var session = Session(); store.BeginSession(session);
        store.RecordCounters(Counters(session, 0, Counter("api", 999)));
        store.RecordCounters(Counters(session, 1, Counter("mixed-in", 20)));
        Assert.Equal(TrafficMonitorStatus.Live, store.GetSnapshot().Status);
        Assert.Equal(20, store.GetSnapshot().Volume.DownloadBytes);
    }
}
