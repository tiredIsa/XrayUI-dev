using System;
using System.Collections.Immutable;

namespace XrayUI.Models.Traffic;

public enum TrafficCounterScope { Inbound, Outbound }
public enum TrafficDirection { Upload, Download }
public enum TrafficTransport { Unknown, Tcp, Udp }
public enum TrafficAccessStatus { Accepted, Rejected }
public enum TrafficRouteKind { Unknown, Proxy, Direct, Blocked }
public enum TrafficMonitorStatus { Stopped, WaitingForSample, Live, Unavailable }
public enum TrafficSampleKind { Baseline, Measurement, CounterReset, Gap }
public enum TrafficUnavailableReason { ApiUnavailable, InvalidCounters, IncompleteCounters }

/// <summary>Identifies one process launch, not the selected server. Restart always creates a new ID.</summary>
public sealed record TrafficCoreSession(Guid Id, Uri StatsEndpoint, DateTimeOffset StartedAt);
public readonly record struct TrafficCounterKey(TrafficCounterScope Scope, string Tag, TrafficDirection Direction);
public sealed record TrafficCounter(TrafficCounterKey Key, long Bytes);

/// <summary>Elapsed is measured with a monotonic clock from session start. UTC is only for display.</summary>
public sealed record TrafficCounterSnapshot(
    Guid SessionId, TimeSpan Elapsed, DateTimeOffset ObservedAt, ImmutableArray<TrafficCounter> Counters);

/// <summary>Address as reported by Xray. Never reverse-resolve or unmask it implicitly.</summary>
public sealed record TrafficAddress(string Host, int? Port, bool IsMasked = false);

/// <summary>Accepted means accepted by the inbound, not delivered or still connected.
/// Process fields are optional enrichment; ordinary access logs do not supply them.</summary>
public sealed record TrafficAccessEvent(
    DateTimeOffset ObservedAt,
    TrafficAddress? Source,
    TrafficAddress? Destination,
    TrafficTransport Transport,
    TrafficAccessStatus Status,
    string? InboundTag,
    string? OutboundTag,
    TrafficRouteKind Route,
    string? ProcessName = null,
    int? ProcessId = null);

/// <summary>Local sequence gives table rows stable identity, even when timestamps coincide.</summary>
public sealed record TrafficAccessRow(long Sequence, Guid SessionId, TrafficAccessEvent Access);
public readonly record struct TrafficVolume(long UploadBytes, long DownloadBytes);
public readonly record struct TrafficSpeed(double UploadBytesPerSecond, double DownloadBytesPerSecond);
public sealed record TrafficRouteMeasurement(string OutboundTag, TrafficVolume Volume, TrafficSpeed? Speed);

/// <summary>Null speed is a gap/baseline, not zero traffic. Route volumes are independent;
/// summing them would count chain hops more than once.</summary>
public sealed record TrafficSample(
    Guid SessionId, TimeSpan Elapsed, DateTimeOffset ObservedAt, TrafficSampleKind Kind,
    TrafficVolume Volume, TrafficSpeed? Speed, ImmutableArray<TrafficRouteMeasurement> Routes);

/// <summary>An immutable read model. Volume covers observed deltas since session start/clear;
/// first sample and recovery from a gap establish baselines rather than backfilling unknown traffic.</summary>
public sealed record TrafficMonitorSnapshot(
    TrafficCoreSession? Session,
    TrafficMonitorStatus Status,
    TrafficUnavailableReason? UnavailableReason,
    TrafficVolume Volume,
    TrafficSpeed? Speed,
    long AccessCount,
    long EvictedAccessCount,
    ImmutableArray<TrafficSample> Samples,
    ImmutableArray<TrafficAccessRow> Accesses);
