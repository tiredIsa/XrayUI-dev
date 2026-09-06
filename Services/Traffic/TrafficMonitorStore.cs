using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using XrayUI.Models.Traffic;

namespace XrayUI.Services.Traffic;

/// <summary>Thread-safe domain state only: no timers, transport, UI dispatcher or process control.
/// The collector serializes poll/gap notifications and feeds access events from the log adapter.
/// Old-session and non-monotonic responses are ignored. Storage is bounded, append is O(1).</summary>
public sealed class TrafficMonitorStore : ITrafficMonitorReader
{
    private readonly object _gate = new();
    private readonly TrafficMonitorOptions _options;
    private readonly HashSet<TrafficCounterKey> _measuredKeys;
    // Once a nonzero counter has been seen, omission is not proof of a zero value.
    private readonly HashSet<TrafficCounterKey> _requiredCounters = new();
    private readonly Queue<TrafficAccessRow> _accesses = new();
    private readonly Queue<TrafficSample> _samples = new();
    private readonly Dictionary<string, TrafficVolume> _routeVolumes = new(StringComparer.Ordinal);
    private Dictionary<TrafficCounterKey, long>? _baseline;
    private TimeSpan? _lastElapsed;
    private TrafficCoreSession? _session;
    private TrafficMonitorStatus _status = TrafficMonitorStatus.Stopped;
    private TrafficUnavailableReason? _unavailableReason;
    private TrafficVolume _volume;
    private TrafficSpeed? _speed;
    private long _sequence;
    private long _accessCount;
    private long _evictedAccessCount;

    public TrafficMonitorStore(TrafficMonitorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _measuredKeys = MeasuredKeys().ToHashSet();
    }

    public void BeginSession(TrafficCoreSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Id == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(session));
        if (session.StatsEndpoint is null || !session.StatsEndpoint.IsAbsoluteUri || !session.StatsEndpoint.IsLoopback ||
            session.StatsEndpoint.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Stats endpoint must be local HTTP (gRPC).", nameof(session));
        lock (_gate)
        {
            if (_session?.Id == session.Id) throw new InvalidOperationException("Restart requires a new session ID.");
            _session = session;
            _requiredCounters.Clear();
            ResetHistory();
            _lastElapsed = null;
            _status = TrafficMonitorStatus.WaitingForSample;
        }
    }

    public bool RecordCounters(TrafficCounterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (!CanAccept(snapshot.SessionId, snapshot.Elapsed)) return false;
            var counters = new Dictionary<TrafficCounterKey, long>();
            if (snapshot.Counters.IsDefault || snapshot.Counters.Any(c => c is null || (_measuredKeys.Contains(c.Key) &&
                    (c.Bytes < 0 || !counters.TryAdd(c.Key, c.Bytes)))))
            {
                RecordGap(snapshot.Elapsed, snapshot.ObservedAt, TrafficUnavailableReason.InvalidCounters);
                return false;
            }

            foreach (var (key, value) in counters)
                if (value > 0) _requiredCounters.Add(key);
            if (_requiredCounters.Any(key => !counters.ContainsKey(key)))
            {
                RecordGap(snapshot.Elapsed, snapshot.ObservedAt, TrafficUnavailableReason.IncompleteCounters);
                return false;
            }

            var kind = TrafficSampleKind.Baseline;
            _speed = null;
            var routeSpeeds = new Dictionary<string, TrafficSpeed>(StringComparer.Ordinal);
            if (_baseline != null && _lastElapsed is { } previousTime)
            {
                // An explicit decrease is a reset. Missing counters were rejected above;
                // a reset sample itself is a usable baseline for the next interval.
                var reset = _measuredKeys.Any(key => Value(counters, key) < Value(_baseline, key));
                kind = reset ? TrafficSampleKind.CounterReset : TrafficSampleKind.Measurement;
                if (!reset)
                {
                    var seconds = (snapshot.Elapsed - previousTime).TotalSeconds;
                    var delta = Delta(counters, _baseline, TrafficCounterScope.Inbound, _options.InboundTags);
                    _volume = Add(_volume, delta);
                    _speed = Speed(delta, seconds);
                    foreach (var tag in _options.OutboundTags)
                    {
                        var routeDelta = Delta(counters, _baseline, TrafficCounterScope.Outbound, [tag]);
                        _routeVolumes[tag] = Add(_routeVolumes.GetValueOrDefault(tag), routeDelta);
                        routeSpeeds[tag] = Speed(routeDelta, seconds);
                    }
                }
            }
            _baseline = counters;
            _lastElapsed = snapshot.Elapsed;
            _status = TrafficMonitorStatus.Live;
            _unavailableReason = null;
            var routes = _options.OutboundTags.Select(tag => new TrafficRouteMeasurement(tag,
                _routeVolumes.GetValueOrDefault(tag), routeSpeeds.TryGetValue(tag, out var speed) ? speed : null)).ToImmutableArray();
            AppendSample(new(snapshot.SessionId, snapshot.Elapsed, snapshot.ObservedAt, kind, _volume, _speed, routes));
            return true;
        }
    }

    public bool RecordUnavailable(Guid sessionId, TimeSpan elapsed, DateTimeOffset observedAt,
        TrafficUnavailableReason reason = TrafficUnavailableReason.ApiUnavailable)
    {
        lock (_gate)
        {
            if (!CanAccept(sessionId, elapsed)) return false;
            RecordGap(elapsed, observedAt, reason);
            return true;
        }
    }

    public bool RecordAccess(Guid sessionId, TrafficAccessEvent access)
    {
        ArgumentNullException.ThrowIfNull(access);
        lock (_gate)
        {
            if (_session?.Id != sessionId || _status == TrafficMonitorStatus.Stopped) return false;
            if (_accesses.Count == _options.AccessCapacity) { _accesses.Dequeue(); _evictedAccessCount++; }
            _accesses.Enqueue(new(++_sequence, sessionId, access));
            _accessCount++;
            return true;
        }
    }

    public void EndSession(Guid sessionId)
    {
        lock (_gate)
        {
            if (_session?.Id != sessionId) return;
            _status = TrafficMonitorStatus.Stopped;
            _speed = null;
            _baseline = null;
            _unavailableReason = null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            var reason = _unavailableReason;
            ResetHistory();
            // Clearing history does not restore an unavailable source. Keep known counter keys
            // and the monotonic watermark so late/missing data cannot manufacture traffic.
            if (_status == TrafficMonitorStatus.Unavailable) _unavailableReason = reason;
            else if (_status != TrafficMonitorStatus.Stopped) _status = TrafficMonitorStatus.WaitingForSample;
        }
    }

    public TrafficMonitorSnapshot GetSnapshot()
    {
        lock (_gate)
            return new(_session, _status, _unavailableReason, _volume, _speed, _accessCount,
                _evictedAccessCount, _samples.ToImmutableArray(), _accesses.ToImmutableArray());
    }

    private bool CanAccept(Guid id, TimeSpan elapsed) => _session?.Id == id &&
        _status != TrafficMonitorStatus.Stopped && elapsed >= TimeSpan.Zero &&
        (!_lastElapsed.HasValue || elapsed > _lastElapsed.Value);

    private void ResetHistory()
    {
        _samples.Clear(); _accesses.Clear(); _routeVolumes.Clear();
        _baseline = null; _volume = default; _speed = null;
        _accessCount = _evictedAccessCount = 0;
        _unavailableReason = null;
    }

    private void RecordGap(TimeSpan elapsed, DateTimeOffset observedAt, TrafficUnavailableReason reason)
    {
        _baseline = null; _speed = null; _lastElapsed = elapsed;
        _status = TrafficMonitorStatus.Unavailable; _unavailableReason = reason;
        AppendSample(new(_session!.Id, elapsed, observedAt, TrafficSampleKind.Gap, _volume, null, []));
    }

    private void AppendSample(TrafficSample sample)
    {
        if (_samples.Count == _options.SampleCapacity) _samples.Dequeue();
        _samples.Enqueue(sample);
    }

    private IEnumerable<TrafficCounterKey> MeasuredKeys()
    {
        foreach (var tag in _options.InboundTags)
            foreach (var direction in new[] { TrafficDirection.Upload, TrafficDirection.Download })
                yield return new(TrafficCounterScope.Inbound, tag, direction);
        foreach (var tag in _options.OutboundTags)
            foreach (var direction in new[] { TrafficDirection.Upload, TrafficDirection.Download })
                yield return new(TrafficCounterScope.Outbound, tag, direction);
    }

    private static long Value(Dictionary<TrafficCounterKey, long> counters, TrafficCounterKey key) => counters.GetValueOrDefault(key);
    private static TrafficVolume Delta(Dictionary<TrafficCounterKey, long> current,
        Dictionary<TrafficCounterKey, long> baseline, TrafficCounterScope scope, IEnumerable<string> tags)
    {
        long up = 0, down = 0;
        foreach (var tag in tags)
        {
            var upKey = new TrafficCounterKey(scope, tag, TrafficDirection.Upload);
            var downKey = new TrafficCounterKey(scope, tag, TrafficDirection.Download);
            up = SaturatingAdd(up, Value(current, upKey) - Value(baseline, upKey));
            down = SaturatingAdd(down, Value(current, downKey) - Value(baseline, downKey));
        }
        return new(up, down);
    }

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    private static TrafficVolume Add(TrafficVolume a, TrafficVolume b) =>
        new(SaturatingAdd(a.UploadBytes, b.UploadBytes), SaturatingAdd(a.DownloadBytes, b.DownloadBytes));
    private static TrafficSpeed Speed(TrafficVolume delta, double seconds) =>
        new(delta.UploadBytes / seconds, delta.DownloadBytes / seconds);
}
