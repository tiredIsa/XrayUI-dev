using System;
using System.Collections.Generic;

namespace XrayUI.Models
{
    /// <summary>
    /// Pure scheduling policy for subscription refreshes. Keeping the supported intervals and
    /// due-time calculation here gives the WinUI timer, the edit UI and unit tests one source of
    /// truth without coupling the policy to a dispatcher or wall-clock service.
    /// </summary>
    public static class SubscriptionRefreshSchedule
    {
        private static readonly int[] Intervals = [0, 60, 360, 720, 1440];

        public static IReadOnlyList<int> AllowedIntervalsMinutes => Intervals;

        public static int NormalizeInterval(int minutes) =>
            Array.IndexOf(Intervals, minutes) >= 0 ? minutes : 0;

        // NormalizeInterval always lands on a member of Intervals, so the lookup cannot miss.
        public static int GetIndex(int minutes) =>
            Array.IndexOf(Intervals, NormalizeInterval(minutes));

        public static int GetIntervalAt(int index) =>
            (uint)index < (uint)Intervals.Length ? Intervals[index] : 0;

        /// <summary>
        /// Expressed through <see cref="GetNextRefreshAt"/> rather than recomputing the boundary,
        /// so a later policy change (jitter, an exclusive comparison, ...) cannot leave the
        /// predicate and the displayed time disagreeing.
        /// </summary>
        public static bool IsDue(
            int intervalMinutes,
            DateTimeOffset? lastSuccess,
            DateTimeOffset now) =>
            GetNextRefreshAt(intervalMinutes, lastSuccess) is { } next && next <= now;

        /// <summary>The instant <see cref="IsDue"/> starts returning true; null when no schedule
        /// is enabled. An entry with no successful fetch is due immediately.</summary>
        public static DateTimeOffset? GetNextRefreshAt(
            int intervalMinutes,
            DateTimeOffset? lastSuccess)
        {
            var normalized = NormalizeInterval(intervalMinutes);
            return normalized > 0
                ? lastSuccess?.AddMinutes(normalized) ?? DateTimeOffset.MinValue
                : null;
        }

        public static bool IsDue(SubscriptionEntry sub, DateTimeOffset now, bool networkRestored = false,
            bool proxyConnected = false)
        {
            if (!sub.IsAutoRefreshEnabled || sub.RetryAfterUtc > now) return false;
            if (sub.NextRetryAt is { } retry)
                return retry <= now || (!sub.LastFailurePermanent &&
                    (networkRestored || (proxyConnected && sub.LastFailureWasDirect)));
            return IsDue(sub.AutoRefreshIntervalMinutes, sub.LastUpdated, now);
        }

        public static void RecordFailure(SubscriptionEntry sub, DateTimeOffset now,
            int? statusCode = null, DateTimeOffset? retryAfter = null, bool direct = false)
        {
            sub.RefreshFailureCount = Math.Min(sub.RefreshFailureCount + 1, 5);
            sub.LastFailurePermanent = statusCode is 401 or 403 or 404;
            sub.LastFailureWasDirect = direct;
            int[] delays = [1, 5, 15, 30, 60];
            var minutes = sub.LastFailurePermanent
                ? (sub.AutoRefreshIntervalMinutes > 0 ? sub.AutoRefreshIntervalMinutes : 360)
                : delays[Math.Clamp(sub.RefreshFailureCount - 1, 0, 4)];
            sub.RetryAfterUtc = statusCode == 429 ? GetRateLimitDeadline(now, retryAfter, minutes) : null;
            sub.NextRetryAt = sub.IsAutoRefreshEnabled
                ? sub.RetryAfterUtc ?? now.AddMinutes(minutes) : null;
        }

        public static DateTimeOffset GetRateLimitDeadline(DateTimeOffset now,
            DateTimeOffset? retryAfter, int fallbackMinutes) =>
            retryAfter is { } deadline && deadline > now ? deadline : now.AddMinutes(fallbackMinutes);

        public static void ClearRetry(SubscriptionEntry sub)
        {
            sub.NextRetryAt = null;
            sub.RefreshFailureCount = 0;
            sub.LastFailurePermanent = false;
            sub.LastFailureWasDirect = false;
        }

        public static void RecordSuccess(SubscriptionEntry sub, DateTimeOffset now)
        {
            // The optional metadata request may have rate-limited us after the nodes succeeded.
            if (!(sub.RetryAfterUtc > now)) sub.RetryAfterUtc = null;
            ClearRetry(sub);
            sub.LastUpdated = now;
            sub.LastError = null;
        }
    }
}
