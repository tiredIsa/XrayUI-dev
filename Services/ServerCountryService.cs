using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace XrayUI.Services;

/// <summary>Local GeoIP lookup; only DNS resolution uses the network.</summary>
public sealed class ServerCountryService
{
    public static ServerCountryService Shared { get; } = new(
        () => GeoIpDatabase.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "rules", "geoip.dat")),
        (host, token) => Dns.GetHostAddressesAsync(host, token));

    private sealed record Result(string? Country, DateTimeOffset Expires);
    private readonly Lazy<Task<GeoIpDatabase>> _database;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly SemaphoreSlim _concurrency = new(4);
    private readonly Dictionary<string, Task<Result>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ServerCountryService(Func<GeoIpDatabase> load, Func<string, CancellationToken, Task<IPAddress[]>> resolve)
    { _database = new(() => Task.Run(load)); _resolve = resolve; }

    public static string NormalizeHost(string? host) => (host ?? "").Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();

    public async Task<string?> GetCountryAsync(string? host)
    {
        var key = NormalizeHost(host);
        if (key.Length == 0 || IsReservedName(key)) return null;
        Task<Result> pending;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) &&
                (!cached.IsCompletedSuccessfully || cached.Result.Expires > DateTimeOffset.UtcNow)) pending = cached;
            else
            {
                if (_cache.Count >= 2048)
                    foreach (var old in _cache.Where(p => p.Value.IsCompleted).Take(256).Select(p => p.Key).ToArray()) _cache.Remove(old);
                // Fast scrolling must not accumulate an unbounded DNS queue.
                if (_cache.Count >= 4096) return null;
                _cache[key] = pending = LookupAsync(key);
            }
        }
        return (await pending.ConfigureAwait(false)).Country;
    }

    private async Task<Result> LookupAsync(string host)
    {
        await _concurrency.WaitAsync().ConfigureAwait(false);
        string? country = null;
        try
        {
            var database = await _database.Value.ConfigureAwait(false);
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var address)) addresses = new[] { address };
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                addresses = await _resolve(host, timeout.Token).ConfigureAwait(false);
            }
            // Multiple known countries (e.g. a CDN) have no unambiguous single flag.
            var matches = addresses.Select(database.FindCountry).Where(c => c != null).Distinct().Take(2).ToArray();
            if (matches.Length == 1) country = matches[0];
        }
        catch (Exception ex)
        { System.Diagnostics.Debug.WriteLine($"[GeoIP] {ex.GetType().Name}"); }
        finally { _concurrency.Release(); }
        return new(country, DateTimeOffset.UtcNow.AddMinutes(country == null ? 5 : 30));
    }

    private static bool IsReservedName(string host)
    {
        if (IPAddress.TryParse(host, out _)) return false;
        if (!host.Contains('.')) return true;
        return new[] { ".test", ".invalid", ".example", ".localhost", ".local", ".onion" }
            .Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal));
    }
}
