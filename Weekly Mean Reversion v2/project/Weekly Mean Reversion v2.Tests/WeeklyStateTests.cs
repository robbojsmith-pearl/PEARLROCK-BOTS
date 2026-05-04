using System;
using PearlrockBots.WeeklyMeanReversion.Core;
using PearlrockBots.WeeklyMeanReversion.Tests.Helpers;
using Xunit;

namespace PearlrockBots.WeeklyMeanReversion.Tests
{
    public class WeeklyStateTests
    {
        private static readonly TimeZoneInfo LondonTz = ResolveLondonTz();
        private static TimeZoneInfo ResolveLondonTz()
        {
            foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { }
            }
            throw new InvalidOperationException("No London TZ");
        }

        private SessionClock NewClock() =>
            new SessionClock(new FakeClock(new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc)), LondonTz);

        // ── Reset on new week ────────────────────────────────────────

        [Fact]
        public void Reset_Stamps_New_Week_And_Clears_State()
        {
            var s = new WeeklyState();
            Assert.True(s.ResetIfNewWeek(new DateTime(2026, 5, 4), 100_000));
            Assert.Equal(new DateTime(2026, 5, 4), s.CurrentWeekStart);
            Assert.False(s.MondayOpenSet);
            Assert.Equal(0, s.TradesThisWeek);
            Assert.Equal(100_000, s.StartOfWeekEquity);
            Assert.False(s.CircuitBroken);
        }

        [Fact]
        public void Reset_NoOp_If_Same_Week()
        {
            var s = new WeeklyState();
            s.ResetIfNewWeek(new DateTime(2026, 5, 4), 100_000);
            s.RecordEntry();
            Assert.False(s.ResetIfNewWeek(new DateTime(2026, 5, 4), 99_000));
            Assert.Equal(1, s.TradesThisWeek);          // not cleared
            Assert.Equal(100_000, s.StartOfWeekEquity); // not overwritten
        }

        // ── Monday open detection ────────────────────────────────────

        [Fact]
        public void Captures_First_Monday_M5_Bar_Even_When_Bot_Starts_Mid_Week()
        {
            // Build M5 bars from Sunday 22:00 UTC through Wednesday morning.
            // Monday 4 May 2026 is a BST week (BST runs Mar 29 – Oct 25 in 2026),
            // so London Monday 00:00 = Sunday 23:00 UTC.
            var bars = new FakeBarSeries();
            var startUtc = new DateTime(2026, 5, 3, 22, 0, 0, DateTimeKind.Utc); // Sun 22:00 UTC

            // Add 80 hours of M5 bars (sun 22:00 → Wed 06:00)
            int n = 80 * 12;
            for (int i = 0; i < n; i++)
            {
                var t = startUtc.AddMinutes(5 * i);
                double price = 1.2000 + i * 0.00001; // gentle drift
                bars.Add(t, price, price + 0.0001, price - 0.0001, price + 0.00005);
            }

            var s = new WeeklyState();
            var weekStart = new DateTime(2026, 5, 4); // Monday
            bool ok = s.TryCaptureMondayOpen(bars, NewClock(), weekStart);

            Assert.True(ok);
            Assert.True(s.MondayOpenSet);

            // Expected: Monday 00:00 London BST = Sunday 23:00 UTC = 12 bars after start.
            int expectedIdx = 12;
            double expectedOpen = 1.2000 + expectedIdx * 0.00001;
            Assert.Equal(expectedOpen, s.MondayOpen, 5);
        }

        [Fact]
        public void Returns_False_When_No_Monday_Data()
        {
            // Bars only cover Tuesday — no Monday history.
            var bars = new FakeBarSeries();
            var startUtc = new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc); // Tue 00:00 UTC
            for (int i = 0; i < 100; i++)
                bars.AddFlat(startUtc.AddMinutes(5 * i), 1.2000);

            var s = new WeeklyState();
            bool ok = s.TryCaptureMondayOpen(bars, NewClock(), new DateTime(2026, 5, 4));

            Assert.False(ok);
            Assert.False(s.MondayOpenSet);
        }

        [Fact]
        public void Idempotent_After_First_Capture()
        {
            var bars = new FakeBarSeries();
            var startUtc = new DateTime(2026, 5, 3, 22, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 200; i++)
                bars.Add(startUtc.AddMinutes(5 * i), 1.2000 + i * 0.00001,
                         1.2001, 1.1999, 1.20005);

            var s = new WeeklyState();
            s.TryCaptureMondayOpen(bars, NewClock(), new DateTime(2026, 5, 4));
            double captured = s.MondayOpen;

            // Add more bars and re-call — value must not change.
            for (int i = 200; i < 300; i++)
                bars.Add(startUtc.AddMinutes(5 * i), 1.3000, 1.3001, 1.2999, 1.30005);

            s.TryCaptureMondayOpen(bars, NewClock(), new DateTime(2026, 5, 4));
            Assert.Equal(captured, s.MondayOpen);
        }

        [Fact]
        public void WeekStart_Must_Be_Monday()
        {
            var s = new WeeklyState();
            var bars = new FakeBarSeries();
            bars.AddFlat(new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc), 1.2);
            Assert.Throws<ArgumentException>(() =>
                s.TryCaptureMondayOpen(bars, NewClock(), new DateTime(2026, 5, 5)));  // Tuesday
        }

        // ── Circuit breaker ──────────────────────────────────────────

        [Theory]
        [InlineData(100_000, 99_000, 1.0, true)]    // exactly hits 1%
        [InlineData(100_000, 99_500, 1.0, false)]   // 0.5%
        [InlineData(100_000, 100_500, 1.0, false)]  // up
        [InlineData(100_000, 99_000, 0.0, false)]   // disabled
        [InlineData(100_000, 99_000, -1.0, false)]  // disabled
        public void CircuitBreaker_Threshold(double startEq, double currentEq, double limit, bool expected)
        {
            var s = new WeeklyState();
            s.ResetIfNewWeek(new DateTime(2026, 5, 4), startEq);
            Assert.Equal(expected, s.ShouldTripCircuitBreaker(currentEq, limit));
        }
    }
}
