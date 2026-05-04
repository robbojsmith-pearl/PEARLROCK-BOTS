using System;

namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>
    /// London-time helpers. Replaces v1's hand-rolled IsBST with .NET's TimeZoneInfo
    /// so DST transitions are handled correctly.
    /// </summary>
    public class SessionClock
    {
        private readonly IClock _clock;
        private readonly TimeZoneInfo _londonTz;

        public SessionClock(IClock clock)
            : this(clock, ResolveLondonTimeZone())
        {
        }

        // Internal ctor used by tests so they can inject a TimeZoneInfo
        // (handy if a test environment is missing tzdata).
        internal SessionClock(IClock clock, TimeZoneInfo londonTz)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _londonTz = londonTz ?? throw new ArgumentNullException(nameof(londonTz));
        }

        public DateTime LondonNow => ConvertToLondon(_clock.UtcNow);

        public DateTime ConvertToLondon(DateTime utc)
        {
            // Normalise — TimeZoneInfo.ConvertTimeFromUtc throws if Kind != Utc/Unspecified.
            var asUtc = utc.Kind == DateTimeKind.Utc
                ? utc
                : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(asUtc, _londonTz);
        }

        /// <summary>Date of Monday 00:00 London for the week containing LondonNow.</summary>
        public DateTime CurrentWeekStartDate()
        {
            return WeekStartDate(LondonNow);
        }

        public static DateTime WeekStartDate(DateTime londonTime)
        {
            var d = londonTime.Date;
            int daysFromMonday = ((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return d.AddDays(-daysFromMonday);
        }

        public bool InTradeHours(TimeSpan start, TimeSpan end)
        {
            var t = LondonNow.TimeOfDay;
            if (start <= end) return t >= start && t < end;
            return t >= start || t < end;  // wraps midnight
        }

        public bool IsTradingDayEnabled(DayOfWeek day, DayFilter filter)
        {
            if (!filter.Enabled) return true;
            switch (day)
            {
                case DayOfWeek.Monday:    return filter.Monday;
                case DayOfWeek.Tuesday:   return filter.Tuesday;
                case DayOfWeek.Wednesday: return filter.Wednesday;
                case DayOfWeek.Thursday:  return filter.Thursday;
                case DayOfWeek.Friday:    return filter.Friday;
                default:                  return false;  // weekends
            }
        }

        private static TimeZoneInfo ResolveLondonTimeZone()
        {
            // .NET on Windows uses Windows tz IDs; Linux/macOS use IANA. Try both.
            string[] candidates = { "Europe/London", "GMT Standard Time" };
            foreach (var id in candidates)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { /* try next */ }
                catch (InvalidTimeZoneException) { /* try next */ }
            }
            throw new InvalidOperationException(
                "Could not find London time zone. Tried 'Europe/London' and 'GMT Standard Time'.");
        }
    }

    public readonly struct DayFilter
    {
        public bool Enabled { get; }
        public bool Monday { get; }
        public bool Tuesday { get; }
        public bool Wednesday { get; }
        public bool Thursday { get; }
        public bool Friday { get; }

        public DayFilter(bool enabled, bool mon, bool tue, bool wed, bool thu, bool fri)
        {
            Enabled = enabled;
            Monday = mon; Tuesday = tue; Wednesday = wed; Thursday = thu; Friday = fri;
        }
    }
}
