using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Models.Traffic;

namespace XrayUI.Services.Traffic;

/// <summary>A client is bound to one endpoint/process launch. The transport queries with reset=false,
/// never falls back to system proxy, and honours cancellation. The owner disposes it on restart.
/// Poll timing and session identity are stamped by the collector, not by the transport.</summary>
public interface IXrayStatsClient : IAsyncDisposable
{
    Task<ImmutableArray<TrafficCounter>> QueryCountersAsync(CancellationToken cancellationToken);
}

/// <summary>Non-access log lines return null. No UI types, DNS lookup, or process lookup here.</summary>
public interface IXrayAccessEventParser
{
    TrafficAccessEvent? Parse(string line, DateTimeOffset receivedAt);
}

/// <summary>UI reads bounded immutable snapshots on its own dispatcher. Pausing rendering must
/// not stop collection. Clear is local only and must never reset Xray counters.</summary>
public interface ITrafficMonitorReader
{
    TrafficMonitorSnapshot GetSnapshot();
    void Clear();
}
