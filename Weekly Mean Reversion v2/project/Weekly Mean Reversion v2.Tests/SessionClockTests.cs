using System;
using PearlrockBots.WeeklyMeanReversion.Core;
using PearlrockBots.WeeklyMeanReversion.Tests.Helpers;
using Xunit;

namespace PearlrockBots.WeeklyMeanReversion.Tests
{
    public class SessionClockTests
    {
        // We pin a TimeZoneInfo manually so the test passes whether the runner is
        // on Windows ("GMT Standard Time") or Linux/macOS ("Europe/London").
        private static readonly TimeZoneInfo LondonTz = TryFindTz("Europe/London", "GMT Standard Time");

        private static TimeZoneInfo TryFindTz(params string[] ids)
        {
            foreach (var id in ids)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { }
            }
            throw new InvalidOperationException("No London TZ available on this runner");
        }

        // ── BST/GMT ──────────────────────────────────────────────────

        [Fact]
        public void Winter_GMT_London_Equals_UTC()
        {
            // 15 January 2026 12:00 UTC — winter, GMT, no offset.
            var utc = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
            var clock = new SessionClock(new FakeClock(utc), LondonTz);
            Assert.Equal(new DateTime(2026, 1, 15, 12, 0, 0), clock.LondonNow);
        }

        [Fact]
        public void Summer_BST_London_Is_UTC_Plus_One()
        {
            // 15 July 2026 12:00 UTC → 13:00 London BST.
            var utc = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
            var clock = new SessionClock(new FakeClock(utc), LondonTz);
            Assert.Equal(new DateTime(2026, 7, 15, 13, 0, 0), clock.LondonNow);
        }

        [Fact]
        public void DST_Spring_Forward_Boundary_Handled_Correctly()
        {
            // BST 2026 starts at 01:00 UTC on Sunday 29 March 2026.
            // 00:30 UTC = 00:30 London (still GMT)
            var beforeUtc = new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Utc);
            var clockBefore = new SessionClock(new FakeClock(beforeUtc), LondonTz);
            Assert.Equal(new DateTime(2026, 3, 29, 0, 30, 0), clockBefore.LondonNow);

            // 01:30 UTC = 02:30 London (BST, clocks just sprang forward)
            var afterUtc = new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc);
            var clockAfter = new SessionClock(new FakeClock(afterUtc), LondonTz);
            Assert.Equal(new DateTime(2026, 3, 29, 2, 30, 0), clockAfter.LondonNow);
        }

        [Fact]
        public void DST_Fall_Back_Boundary_Handled_Correctly()
        {
            // BST 2026 ends at 01:00 UTC on Sunday 25 October 2026.
            // 00:30 UTC = 01:30 London (still BST)
            var beforeUtc = new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc);
            var clockBefore = new SessionClock(new FakeClock(beforeUtc), LondonTz);
            Assert.Equal(new DateTime(2026, 10, 25, 1, 30, 0), clockBefore.LondonNow);

            // 01:30 UTC = 01:30 London (clocks fell back, ambiguous hour)
            var afterUtc = new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc);
            var clockAfter = new SessionClock(new FakeClock(afterUtc), LondonTz);
            Assert.Equal(new DateTime(2026, 10, 25, 1, 30, 0), clockAfter.LondonNow);
        }

        // ── Week start ───────────────────────────────────────────────

        [Theory]
        [InlineData(2026, 5, 4, 2026, 5, 4)]   // Monday → itself
        [InlineData(2026, 5, 6, 2026, 5, 4)]   // Wednesday → previous Monday
        [InlineData(2026, 5, 10, 2026, 5, 4)]  // Sunday → previous Monday
        public void WeekStart_Returns_Monday(int y, int m, int d, int wy, int wm, int wd)
        {
            var date = new DateTime(y, m, d);
            Assert.Equal(new DateTime(wy, wm, wd), SessionClock.WeekStartDate(date));
        }

        // ── Trade hours ──────────────────────────────────────────────

        [Fact]
        public void InTradeHours_Same_Day_Window()
        {
            // 14:30 London is inside 08:00–17:00.
            var utc = new DateTime(2026, 5, 6, 13, 30, 0, DateTimeKind.Utc);  // 14:30 BST
            var clock = new SessionClock(new FakeClock(utc), LondonTz);
            Assert.True(clock.InTradeHours(TimeSpan.FromHours(8), TimeSpan.FromHours(17)));
            Assert.False(clock.InTradeHours(TimeSpan.FromHours(15), TimeSpan.FromHours(17)));
        }

        [Fact]
        public void InTradeHours_Wraps_Midnight()
        {
            // 23:30 London inside 22:00–06:00 wrap window.
            var utc = new DateTime(2026, 5, 6, 22, 30, 0, DateTimeKind.Utc); // 23:30 BST
            var clock = new SessionClock(new FakeClock(utc), LondonTz);
            Assert.True(clock.InTradeHours(TimeSpan.FromHours(22), TimeSpan.FromHours(6)));
        }

        // ── Day filter ───────────────────────────────────────────────

        [Fact]
        public void DayFilter_Disabled_Always_Allows()
        {
            var clock = new SessionClock(new FakeClock(DateTime.UtcNow), LondonTz);
            var anyFilter = new DayFilter(false, false, false, false, false, false);
            foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
                Assert.True(clock.IsTradingDayEnabled(d, anyFilter));
        }

        [Fact]
        public void DayFilter_Tue_Wed_Only()
        {
            var clock = new SessionClock(new FakeClock(DateTime.UtcNow), LondonTz);
            var f = new DayFilter(true, false, true, true, false, false);
            Assert.False(clock.IsTradingDayEnabled(DayOfWeek.Monday, f));
            Assert.True(clock.IsTradingDayEnabled(DayOfWeek.Tuesday, f));
            Assert.True(clock.IsTradingDayEnabled(DayOfWeek.Wednesday, f));
            Assert.False(clock.IsTradingDayEnabled(DayOfWeek.Thursday, f));
            Assert.False(clock.IsTradingDayEnabled(DayOfWeek.Friday, f));
            Assert.False(clock.IsTradingDayEnabled(DayOfWeek.Saturday, f));
            Assert.False(clock.IsTradingDayEnabled(DayOfWeek.Sunday, f));
        }
    }
}
