using System;

namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>
    /// Weekly trading state: Monday open, week-start equity, trade counters, circuit breaker.
    ///
    /// Monday open detection
    /// ---------------------
    /// v1 set _mondayOpen = Bars.OpenPrices.Last(1) on whichever Monday M5 bar
    /// OnBar first fired on. Two bugs:
    ///   (a) Last(1) is the previous M5 bar - not the first Monday bar - so the
    ///       captured open could be any time on Monday.
    ///   (b) If the bot started mid-week, no Monday bar fired, so MondayOpenSet
    ///       stayed false for the entire week and no signals could fire.
    ///
    /// v2 walks back through the M5 bar series to find the earliest closed M5 bar
    /// whose London time falls on the current week's Monday, and uses that bar's
    /// open. Works correctly when the bot starts mid-week, and correctly when DST
    /// shifts the broker's bar boundary.
    /// </summary>
    public class WeeklyState
    {
        public DateTime CurrentWeekStart { get; private set; } = DateTime.MinValue;
        public double MondayOpen { get; private set; }
        public bool MondayOpenSet { get; private set; }
        public int TradesThisWeek { get; private set; }
        public double StartOfWeekEquity { get; private set; }
        public bool CircuitBroken { get; private set; }

        /// <summary>
        /// If weekStart differs from the tracked week, reset all weekly state and
        /// stamp the new week. Returns true if a reset occurred.
        /// </summary>
        public bool ResetIfNewWeek(DateTime weekStart, double currentEquity)
        {
            if (weekStart == CurrentWeekStart) return false;

            CurrentWeekStart = weekStart;
            MondayOpen = 0;
            MondayOpenSet = false;
            TradesThisWeek = 0;
            StartOfWeekEquity = currentEquity;
            CircuitBroken = false;
            return true;
        }

        /// <summary>
        /// Try to capture the Monday open by scanning the M5 bar history for the
        /// earliest closed bar whose London time falls on weekStart.
        /// Returns true if a Monday open was captured (now or previously),
        /// false if no Monday data is yet available.
        /// </summary>
        public bool TryCaptureMondayOpen(IBarSeries m5Bars, SessionClock clock, DateTime weekStart)
        {
            if (MondayOpenSet) return true;
            if (m5Bars == null) throw new ArgumentNullException(nameof(m5Bars));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (weekStart.DayOfWeek != DayOfWeek.Monday)
                throw new ArgumentException("weekStart must be a Monday date", nameof(weekStart));
            if (m5Bars.Count < 1) return false;

            // Scan from oldest closed bar to newest. The first bar we see whose
            // London date == weekStart.Date is the earliest bar of this Monday.
            for (int barsAgo = m5Bars.Count; barsAgo >= 1; barsAgo--)
            {
                DateTime utcOpen = m5Bars.OpenTime(barsAgo);
                DateTime londonOpen = clock.ConvertToLondon(utcOpen);

                if (londonOpen.Date < weekStart.Date)
                {
                    continue;
                }
                if (londonOpen.Date == weekStart.Date)
                {
                    MondayOpen = m5Bars.Open(barsAgo);
                    MondayOpenSet = true;
                    return true;
                }
                // londonOpen.Date > weekStart.Date - no Monday data in history.
                return false;
            }

            return false;
        }

        public void RecordEntry()
        {
            TradesThisWeek++;
        }

        public void TripCircuitBreaker()
        {
            CircuitBroken = true;
        }

        /// <summary>
        /// Returns true if equity drawdown from start of week meets/exceeds
        /// lossLimitPct. A zero or negative limit disables the check.
        /// </summary>
        public bool ShouldTripCircuitBreaker(double currentEquity, double lossLimitPct)
        {
            if (lossLimitPct <= 0) return false;
            if (StartOfWeekEquity <= 0) return false;
            double lossPct = (StartOfWeekEquity - currentEquity) / StartOfWeekEquity * 100.0;
            return lossPct >= lossLimitPct;
        }
    }
}
