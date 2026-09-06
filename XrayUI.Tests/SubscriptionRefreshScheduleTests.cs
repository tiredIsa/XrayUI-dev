using System.Text.Json;
using XrayUI.Models;

namespace XrayUI.Tests
{
    public class SubscriptionRefreshScheduleTests
    {
        public static TheoryData<int> EnabledIntervals => new()
        {
            60,
            360,
            720,
            1440,
        };

        [Fact]
        public void AllowedIntervals_AreExactlyTheSupportedChoices()
        {
            Assert.Equal(
                new[] { 0, 60, 360, 720, 1440 },
                SubscriptionRefreshSchedule.AllowedIntervalsMinutes);
            Assert.Equal(0, SubscriptionRefreshSchedule.NormalizeInterval(30));
            Assert.Equal(0, SubscriptionRefreshSchedule.GetIntervalAt(-1));
            Assert.Equal(0, SubscriptionRefreshSchedule.GetIntervalAt(99));
        }

        [Theory]
        [MemberData(nameof(EnabledIntervals))]
        public void IsDue_UsesTheConfiguredBoundary(int intervalMinutes)
        {
            var lastAttempt = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);
            var dueAt = lastAttempt.AddMinutes(intervalMinutes);

            Assert.False(SubscriptionRefreshSchedule.IsDue(
                intervalMinutes, lastAttempt, dueAt.AddTicks(-1)));
            Assert.True(SubscriptionRefreshSchedule.IsDue(
                intervalMinutes, lastAttempt, dueAt));
            Assert.Equal(
                dueAt,
                SubscriptionRefreshSchedule.GetNextRefreshAt(intervalMinutes, lastAttempt));
        }

        [Fact]
        public void DisabledScheduleWaits_ButNeverUpdatedSubscriptionIsDue()
        {
            var now = DateTimeOffset.UtcNow;

            Assert.False(SubscriptionRefreshSchedule.IsDue(0, now.AddDays(-2), now));
            Assert.True(SubscriptionRefreshSchedule.IsDue(60, null, now));
            Assert.Null(SubscriptionRefreshSchedule.GetNextRefreshAt(0, now));
            Assert.Equal(DateTimeOffset.MinValue, SubscriptionRefreshSchedule.GetNextRefreshAt(60, null));
        }

        [Fact]
        public void MissingFieldsFromOldJson_DefaultToDisabled()
        {
            var entry = JsonSerializer.Deserialize<SubscriptionEntry>(
                """{"Name":"Legacy","Url":"https://example.com/sub"}""");

            Assert.NotNull(entry);
            Assert.Equal(0, entry.AutoRefreshIntervalMinutes);
            Assert.Null(entry.LastRefreshAttempt);
            Assert.False(entry.IsAutoRefreshEnabled);
        }

        [Fact]
        public void ScheduleFields_RoundTripThroughJson()
        {
            var attemptedAt = new DateTimeOffset(2026, 7, 26, 12, 34, 56, TimeSpan.Zero);
            var original = new SubscriptionEntry
            {
                Name = "Scheduled",
                Url = "https://example.com/sub",
                AutoRefreshIntervalMinutes = 720,
                LastRefreshAttempt = attemptedAt,
            };

            var json = JsonSerializer.Serialize(original);
            var restored = JsonSerializer.Deserialize<SubscriptionEntry>(json);

            Assert.NotNull(restored);
            Assert.Equal(720, restored.AutoRefreshIntervalMinutes);
            Assert.Equal(attemptedAt, restored.LastRefreshAttempt);
            Assert.True(restored.IsAutoRefreshEnabled);
        }

        private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        private static SubscriptionEntry Overdue() => new()
        {
            AutoRefreshIntervalMinutes = 360,
            LastUpdated = Now.AddHours(-7),
        };

        [Fact]
        public void AttemptDoesNotPostponeSuccessfulRefreshDeadline()
        {
            var sub = Overdue();
            sub.LastRefreshAttempt = Now;
            Assert.True(SubscriptionRefreshSchedule.IsDue(sub, Now));
        }

        [Fact]
        public void TransientFailureBackoffCapsAtOneHour_AndPreservesLastSuccess()
        {
            var sub = Overdue();
            var success = sub.LastUpdated;
            var time = Now;
            foreach (var minutes in new[] { 1, 5, 15, 30, 60, 60, 60 })
            {
                SubscriptionRefreshSchedule.RecordFailure(sub, time, 503);
                Assert.Equal(time.AddMinutes(minutes), sub.NextRetryAt);
                Assert.False(SubscriptionRefreshSchedule.IsDue(sub, time));
                Assert.False(SubscriptionRefreshSchedule.IsDue(sub, time.AddMinutes(minutes).AddTicks(-1)));
                Assert.True(SubscriptionRefreshSchedule.IsDue(sub, time.AddMinutes(minutes)));
                Assert.Equal(success, sub.LastUpdated);
                time = time.AddMinutes(minutes);
            }
        }

        [Theory]
        [InlineData(401)]
        [InlineData(403)]
        [InlineData(404)]
        public void PermanentHttpFailureWaitsNormalInterval_EvenAfterReconnection(int status)
        {
            var sub = Overdue();
            SubscriptionRefreshSchedule.RecordFailure(sub, Now, status, direct: true);
            Assert.Equal(Now.AddHours(6), sub.NextRetryAt);
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now, networkRestored: true, proxyConnected: true));
            Assert.True(SubscriptionRefreshSchedule.IsDue(sub, Now.AddHours(6)));
        }

        [Fact]
        public void ConnectingProxyRetriesDirectFailure_WithoutBypassingProxiedFailureDelay()
        {
            var sub = Overdue();
            SubscriptionRefreshSchedule.RecordFailure(sub, Now, direct: true);
            Assert.True(SubscriptionRefreshSchedule.IsDue(sub, Now, proxyConnected: true));
            SubscriptionRefreshSchedule.RecordFailure(sub, Now, direct: false);
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now, proxyConnected: true));
            Assert.True(SubscriptionRefreshSchedule.IsDue(sub, Now, networkRestored: true));
        }

        [Fact]
        public void NetworkEventsDoNotRefreshFreshSubscriptions()
        {
            var sub = Overdue();
            SubscriptionRefreshSchedule.RecordSuccess(sub, Now);
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now, true, true));
        }

        [Fact]
        public void RateLimitSurvivesRestart_AndCannotBeBypassedByReconnection()
        {
            var sub = Overdue();
            SubscriptionRefreshSchedule.RecordFailure(sub, Now, 429, Now.AddHours(2), direct: true);
            var restored = JsonSerializer.Deserialize<SubscriptionEntry>(JsonSerializer.Serialize(sub))!;
            Assert.Equal(Now.AddHours(2), restored.RetryAfterUtc);
            Assert.Equal(sub.NextRetryAt, restored.NextRetryAt);
            Assert.Equal(sub.RefreshFailureCount, restored.RefreshFailureCount);
            Assert.True(restored.LastFailureWasDirect);
            Assert.False(SubscriptionRefreshSchedule.IsDue(restored, Now.AddMinutes(30), true, true));
            Assert.True(SubscriptionRefreshSchedule.IsDue(restored, Now.AddHours(2)));
        }

        [Fact]
        public void RateLimitWithoutHeaderUsesBackoff_AlsoForManualOnlySubscriptions()
        {
            var sub = new SubscriptionEntry();
            SubscriptionRefreshSchedule.RecordFailure(sub, Now, 429);
            Assert.Equal(Now.AddMinutes(1), sub.RetryAfterUtc);
            Assert.Null(sub.NextRetryAt);
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now.AddDays(1), true, true));
        }

        [Fact]
        public void SuccessResetsFailures_AndStartsNormalInterval()
        {
            var sub = Overdue();
            SubscriptionRefreshSchedule.RecordFailure(sub, Now, 429, Now.AddMinutes(1));
            SubscriptionRefreshSchedule.RecordSuccess(sub, Now.AddMinutes(1));
            Assert.Null(sub.NextRetryAt);
            Assert.Null(sub.RetryAfterUtc);
            Assert.Equal(0, sub.RefreshFailureCount);
            Assert.Null(sub.LastError);
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now.AddHours(6)));
            Assert.True(SubscriptionRefreshSchedule.IsDue(sub, Now.AddHours(6).AddMinutes(1)));
            SubscriptionRefreshSchedule.RecordFailure(sub, Now.AddHours(7));
            Assert.Equal(Now.AddHours(7).AddMinutes(1), sub.NextRetryAt);
        }

        [Fact]
        public void ChangingIntervalUsesLastSuccess_DisablingCancelsAutomaticRetries()
        {
            var sub = Overdue();
            sub.AutoRefreshIntervalMinutes = 720;
            SubscriptionRefreshSchedule.ClearRetry(sub);
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now));
            sub.AutoRefreshIntervalMinutes = 60;
            Assert.True(SubscriptionRefreshSchedule.IsDue(sub, Now));
            SubscriptionRefreshSchedule.RecordFailure(sub, Now);
            sub.AutoRefreshIntervalMinutes = 0;
            SubscriptionRefreshSchedule.ClearRetry(sub);
            Assert.Null(sub.NextRetryAt);
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now.AddDays(1), true, true));
        }

        [Fact]
        public void ChangingScheduleDoesNotClearProviderRateLimit()
        {
            var sub = Overdue();
            SubscriptionRefreshSchedule.RecordFailure(sub, Now, 429, Now.AddHours(1));
            SubscriptionRefreshSchedule.ClearRetry(sub);
            sub.AutoRefreshIntervalMinutes = 60;
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now, true, true));
            Assert.True(SubscriptionRefreshSchedule.IsDue(sub, Now.AddHours(1)));
        }

        [Fact]
        public void ExpiredRetryAfterUsesBackoffInsteadOfImmediateRetry()
        {
            var sub = Overdue();
            SubscriptionRefreshSchedule.RecordFailure(sub, Now, 429, Now.AddHours(-1));
            Assert.Equal(Now.AddMinutes(1), sub.RetryAfterUtc);
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now, true, true));
        }

        [Fact]
        public void QueuedSubscriptionIsNoLongerDueAfterIntervalIsExtended()
        {
            var sub = Overdue();
            Assert.True(SubscriptionRefreshSchedule.IsDue(sub, Now));
            sub.AutoRefreshIntervalMinutes = 1440;
            SubscriptionRefreshSchedule.ClearRetry(sub);
            Assert.False(SubscriptionRefreshSchedule.IsDue(sub, Now, true, true));
        }
    }
}
