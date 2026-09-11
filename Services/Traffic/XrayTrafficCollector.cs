using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Models.Traffic;

namespace XrayUI.Services.Traffic;

/// <summary>Polls Xray's local StatsService through the bundled CLI. Using the CLI keeps
/// the app NativeAOT-safe and avoids shipping a second protobuf/gRPC runtime.</summary>
public sealed class XrayTrafficCollector : IAsyncDisposable
{
    private const int ApiPort = 10085;
    private readonly TrafficMonitorStore _store;
    private readonly IXrayAccessEventParser _accessParser = new XrayAccessLogParser();
    private readonly string _xrayPath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _sessionCancellation;
    private Task? _pollTask;
    private TrafficCoreSession? _session;

    public XrayTrafficCollector(TrafficMonitorStore store, string xrayPath)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _xrayPath = xrayPath ?? throw new ArgumentNullException(nameof(xrayPath));
    }

    public void StartSession()
    {
        lock (_gate)
        {
            StopSessionLocked();
            var session = new TrafficCoreSession(Guid.NewGuid(),
                new Uri($"http://127.0.0.1:{ApiPort}"), DateTimeOffset.UtcNow);
            _session = session;
            _store.BeginSession(session);
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _pollTask = PollAsync(session, _sessionCancellation.Token);
        }
    }

    public void StopSession()
    {
        lock (_gate) StopSessionLocked();
    }

    public void RecordLogLine(string line)
    {
        TrafficCoreSession? session;
        lock (_gate) session = _session;
        if (session is null) return;
        var access = _accessParser.Parse(line, DateTimeOffset.UtcNow);
        if (access is not null) _store.RecordAccess(session.Id, access);
    }

    private void StopSessionLocked()
    {
        var session = _session;
        _session = null;
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        _pollTask = null;
        if (session is not null) _store.EndSession(session.Id);
    }

    private async Task PollAsync(TrafficCoreSession session, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            var elapsed = stopwatch.Elapsed;
            var observedAt = DateTimeOffset.UtcNow;
            try
            {
                var counters = await QueryCountersAsync(cancellationToken).ConfigureAwait(false);
                if (counters.IsDefaultOrEmpty)
                    _store.RecordUnavailable(session.Id, elapsed, observedAt);
                else
                    _store.RecordCounters(new(session.Id, elapsed, observedAt, counters));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                _store.RecordUnavailable(session.Id, elapsed, observedAt);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<ImmutableArray<TrafficCounter>> QueryCountersAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _xrayPath,
            Arguments = $"api statsquery --server=127.0.0.1:{ApiPort}",
            WorkingDirectory = System.IO.Path.GetDirectoryName(_xrayPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = new Process { StartInfo = psi };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0) return ImmutableArray<TrafficCounter>.Empty;
        return ParseCounters(output);
    }

    private static ImmutableArray<TrafficCounter> ParseCounters(string output)
    {
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("stat", out var stats) || stats.ValueKind != JsonValueKind.Array)
            return ImmutableArray<TrafficCounter>.Empty;
        var builder = ImmutableArray.CreateBuilder<TrafficCounter>();
        foreach (var item in stats.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameElement)) continue;
            var name = nameElement.GetString() ?? string.Empty;
            var parts = name.Split(">>>", StringSplitOptions.None);
            if (parts.Length != 4 || parts[2] != "traffic") continue;
            var scope = parts[0] switch
            {
                "inbound" => TrafficCounterScope.Inbound,
                "outbound" => TrafficCounterScope.Outbound,
                _ => (TrafficCounterScope?)null
            };
            var direction = parts[3] switch
            {
                "uplink" => TrafficDirection.Upload,
                "downlink" => TrafficDirection.Download,
                _ => (TrafficDirection?)null
            };
            if (!scope.HasValue || !direction.HasValue) continue;
            long value = 0;
            if (item.TryGetProperty("value", out var valueElement))
                long.TryParse(valueElement.ToString(), out value);
            builder.Add(new(new(scope.Value, parts[1], direction.Value), Math.Max(0, value)));
        }
        return builder.ToImmutable();
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        lock (_gate) StopSessionLocked();
        try { if (_pollTask is not null) await _pollTask.ConfigureAwait(false); } catch { }
        _lifetime.Dispose();
    }
}
