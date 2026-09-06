using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XrayUI.Helpers;
using XrayUI.Models;

namespace XrayUI.Services
{
    // Separate from the UI so response negotiation and failure handling can be tested
    // with an HTTP handler, without a live provider or Windows UI runtime.
    public static class SubscriptionFetcher
    {
        private const string SubscriptionUserAgent = "v2rayN/7.22";
        private const string ClashUserAgent = "clash-verge/v2.5.2";
        private static readonly TimeSpan SubscriptionMetaTimeout = TimeSpan.FromSeconds(8);

        // Pin the route for the entire fetch, including metadata and format negotiation.
        // A failed SOCKS request must never fall back to a direct request.
        public static HttpClient CreateClient(int? port)
        {
            var client = new HttpClient(new HttpClientHandler
            {
                Proxy = port is int value ? new WebProxy($"socks5://127.0.0.1:{value}") : null,
                UseProxy = port.HasValue,
            }) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(SubscriptionUserAgent);
            return client;
        }

        public static async Task<(List<ServerEntry>? entries, string? error)> FetchNodesAsync(
            SubscriptionEntry sub, HttpClient client, bool direct)
        {
            var url = sub.Url;
            var main = await FetchSubscriptionAsync(client, url, userAgent: null);

            (List<ServerEntry>?, string?) Failed(string? message, int? status, DateTimeOffset? retryAfter)
            {
                if (sub.Url == url)
                    SubscriptionRefreshSchedule.RecordFailure(sub, DateTimeOffset.UtcNow,
                        status, retryAfter, direct: direct);
                return (null, message);
            }

            if (main.raw == null) return Failed(main.error, main.status, main.retryAfter);
            var usage = main.usage;
            var entries = ParseSubscriptionText(main.raw);
            if (entries.Count == 0)
            {
                var clash = await FetchSubscriptionAsync(client, url, ClashUserAgent);
                if (clash.raw == null) return Failed(clash.error, clash.status, clash.retryAfter);
                entries = ParseSubscriptionText(clash.raw);
                usage = clash.usage ?? usage;
            }
            else if (usage == null)
            {
                // Metadata is best effort, but a provider's rate limit applies to later requests too.
                var meta = await FetchSubscriptionAsync(client, url, ClashUserAgent,
                    headersOnly: true, timeout: SubscriptionMetaTimeout);
                usage = meta.usage;
                if (meta.status == 429 && sub.Url == url)
                    sub.RetryAfterUtc = SubscriptionRefreshSchedule.GetRateLimitDeadline(
                        DateTimeOffset.UtcNow, meta.retryAfter, 1);
            }
            if (entries.Count == 0) return Failed(L.Subscription_NoParsed, null, null);
            if (sub.Url == url && usage is { } u) sub.Usage = u;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (string.IsNullOrEmpty(entry.Name))
                    entry.Name = $"{sub.Name} #{i + 1}";
                entry.SubscriptionId = sub.Id;
            }

            return (entries, null);
        }

        // One subscription request, shared by the node fetch, the Clash-UA retry and the traffic probe.
        // A null userAgent leaves the request with no UA of its own, so it inherits the shared client's
        // default v2rayN UA. headersOnly skips the body (raw comes back null) for probes that only want
        // the `subscription-userinfo` header; timeout overrides the shared client's. Never throws —
        // failures come back in error, and usage is simply null when the provider didn't send the header.
        private static async Task<(string? raw, SubscriptionUserInfo? usage, string? error, int? status, DateTimeOffset? retryAfter)> FetchSubscriptionAsync(
            HttpClient client, string url, string? userAgent, bool headersOnly = false, TimeSpan? timeout = null)
        {
            try
            {
                using var cts = timeout is { } t ? new CancellationTokenSource(t) : null;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (userAgent != null)
                    req.Headers.UserAgent.ParseAdd(userAgent);

                using var resp = await client.SendAsync(
                    req,
                    headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                    cts?.Token ?? CancellationToken.None);
                if (!resp.IsSuccessStatusCode)
                {
                    var retry = resp.Headers.RetryAfter;
                    var retryAt = retry?.Date ?? (retry?.Delta is { } delta
                        ? DateTimeOffset.UtcNow.Add(delta) : (DateTimeOffset?)null);
                    var error = $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}";
                    if ((int)resp.StatusCode is 401 or 403 or 404)
                        error += " — " + L.Subscription_CheckUrl;
                    return (null, null, error, (int)resp.StatusCode, retryAt);
                }

                return (headersOnly ? null : await resp.Content.ReadAsStringAsync(), ReadUserInfo(resp), null, null, null);
            }
            catch (Exception ex)
            {
                return (null, null, ex.Message, null, null);
            }
        }

        // `subscription-userinfo` lands on the response or the content headers depending on the server.
        // Null when absent, which the caller reads as "no news" rather than "quota cleared".
        private static SubscriptionUserInfo? ReadUserInfo(HttpResponseMessage resp) =>
            resp.Headers.TryGetValues("subscription-userinfo", out var values) ||
            resp.Content.Headers.TryGetValues("subscription-userinfo", out values)
                ? ParseSubscriptionUserInfo(values.FirstOrDefault())
                : null;

        // Decodes a subscription body (base64 or plain) and parses it as a v2rayN link list, falling
        // back to a Clash/Clash.Meta YAML parse only when no line parsed as a share link — so a normal
        // link-list subscription never pays for a YAML parse attempt.
        private static List<ServerEntry> ParseSubscriptionText(string raw)
        {
            var trimmed = raw.Trim();
            var decoded = new byte[trimmed.Length];
            var text = Convert.TryFromBase64String(trimmed, decoded, out var written)
                ? Encoding.UTF8.GetString(decoded, 0, written)
                : raw;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var entries = new List<ServerEntry>();
            foreach (var line in lines)
            {
                var entry = NodeLinkParser.Parse(line.Trim());
                if (entry != null) entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                try
                {
                    entries.AddRange(ClashConfigParser.Parse(text).Nodes);
                }
                catch
                {
                    // Not valid YAML either - caller treats an empty list as "nothing parsed".
                }
            }

            return entries;
        }

        // Parses `upload=..; download=..; total=..; expire=..` (bytes; expire in unix seconds).
        private static SubscriptionUserInfo ParseSubscriptionUserInfo(string? value)
        {
            long? up = null, down = null, total = null;
            DateTimeOffset? expire = null;
            if (string.IsNullOrWhiteSpace(value))
                return default;

            foreach (var part in value.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2) continue;
                var key = kv[0].Trim();
                var val = kv[1].Trim();
                switch (key)
                {
                    case "upload":   if (long.TryParse(val, out var u)) up = u; break;
                    case "download": if (long.TryParse(val, out var d)) down = d; break;
                    case "total":    if (long.TryParse(val, out var t)) total = t; break;
                    case "expire":   if (long.TryParse(val, out var e) && e > 0 && e <= 253402300799L)
                                         expire = DateTimeOffset.FromUnixTimeSeconds(e); break;
                }
            }
            return new SubscriptionUserInfo(up, down, total, expire);
        }

    }
}
