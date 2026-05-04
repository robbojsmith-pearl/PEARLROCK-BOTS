// ════════════════════════════════════════════════════════════════════════════
// Weekly Open Mean Reversion v2 — single-file build
// Pearlrock Systematic
//
// This file is the MULTI-FILE v2 project flattened into one .cs you can paste
// directly into cTrader Automate. It compiles to the same cBot — same logic,
// same parameters, same behaviour — but everything lives in one file so the
// editor doesn't need a sub-folder layout.
//
// Sections in order:
//   1. Core/Abstractions.cs      (IClock, IBarSeries, ISymbol)
//   2. Core/Models.cs            (TradeDirection, SignalDecision, EntrySpec, ...)
//   3. Core/SessionClock.cs      (UTC↔London, BST handled by TimeZoneInfo)
//   4. Core/WeeklyState.cs       (Monday open, week equity, circuit breaker)
//   5. Core/SignalEngine.cs      (state machine: Idle / DeviationDetected / Confirming)
//   6. Core/RiskManager.cs       (SL/TP/volume calculator, pure)
//   7. Adapters/CAlgoBarSeries.cs (cAlgo Bars → IBarSeries)
//   8. Adapters/CAlgoClock.cs     (cAlgo Server.Time → IClock)
//   9. Adapters/CAlgoSymbol.cs    (cAlgo Symbol → ISymbol)
//   10. WeeklyOpen_MeanReversion_v2 (the cBot itself)
//
// To unit-test: use the multi-file copy of this project in your workspace —
// the test project links against the Core/ files only, not this single-file.
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using PearlrockBots.WeeklyMeanReversion.Adapters;
using PearlrockBots.WeeklyMeanReversion.Core;

// ════════════════════════════════════════════════════════════════════════════
// 1. Core/Abstractions.cs
// ════════════════════════════════════════════════════════════════════════════
namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>Wall-clock source. Tests inject a fake; production uses Server.Time.</summary>
    public interface IClock
    {
        /// <summary>Current time in UTC.</summary>
        DateTime UtcNow { get; }
    }

    /// <summary>
    /// Read-only view of a bar series. Convention: closed bars only.
    /// <c>Count</c> = number of closed bars accessible.
    /// barsAgo: <c>1</c> = newest closed; <c>Count</c> = oldest accessible.
    /// </summary>
    public interface IBarSeries
    {
        int Count { get; }
        double Open(int barsAgo);
        double High(int barsAgo);
        double Low(int barsAgo);
        double Close(int barsAgo);
        /// <summary>Open time of the bar, in UTC.</summary>
        DateTime OpenTime(int barsAgo);
    }

    /// <summary>Symbol metadata + sizing helpers. Mirrors the bits we need from cAlgo Symbol.</summary>
    public interface ISymbol
    {
        string Name { get; }
        double PipSize { get; }
        double VolumeInUnitsMin { get; }
        double VolumeInUnitsMax { get; }
        double Bid { get; }
        double Ask { get; }
        double Spread { get; }
        double VolumeForRisk(double equity, double riskPercent, double slPips);
        double NormalizeVolume(double units);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 2. Core/Models.cs
// ════════════════════════════════════════════════════════════════════════════
namespace PearlrockBots.WeeklyMeanReversion.Core
{
    public enum TradeDirection
    {
        None = 0,
        Long = 1,
        Short = -1
    }

    public enum SignalPhase
    {
        Idle,
        DeviationDetected,
        Confirming
    }

    public readonly struct SignalDecision
    {
        public TradeDirection Direction { get; }
        public bool ShouldEnter { get; }
        public string Reason { get; }

        private SignalDecision(TradeDirection dir, bool enter, string reason)
        {
            Direction = dir;
            ShouldEnter = enter;
            Reason = reason;
        }

        public static SignalDecision NoOp() =>
            new SignalDecision(TradeDirection.None, false, "no-op");

        public static SignalDecision Wait(string reason) =>
            new SignalDecision(TradeDirection.None, false, reason);

        public static SignalDecision Enter(TradeDirection dir, string reason) =>
            new SignalDecision(dir, true, reason);
    }

    public readonly struct SignalEngineInput
    {
        public double MondayOpen { get; }
        public double LastClose { get; }
        public double PrevClose { get; }
        public double D1Atr { get; }
        public double PipSize { get; }

        public SignalEngineInput(
            double mondayOpen, double lastClose, double prevClose, double d1Atr, double pipSize)
        {
            MondayOpen = mondayOpen;
            LastClose = lastClose;
            PrevClose = prevClose;
            D1Atr = d1Atr;
            PipSize = pipSize;
        }
    }

    public readonly struct EntrySpec
    {
        public TradeDirection Direction { get; }
        public double Volume { get; }
        public double SlPips { get; }
        public double TpPips { get; }
        public string Note { get; }

        public EntrySpec(TradeDirection dir, double volume, double slPips, double tpPips, string note)
        {
            Direction = dir;
            Volume = volume;
            SlPips = slPips;
            TpPips = tpPips;
            Note = note;
        }
    }

    public readonly struct EntryContext
    {
        public TradeDirection Direction { get; }
        public double MondayOpen { get; }
        public double M5Atr { get; }
        public double Equity { get; }
        public double Bid { get; }
        public double Ask { get; }
        public double PipSize { get; }
        public double VolumeInUnitsMin { get; }
        public double VolumeInUnitsMax { get; }
        public Func<double, double, double, double> VolumeForRisk { get; }
        public Func<double, double> NormalizeVolume { get; }

        public EntryContext(
            TradeDirection direction,
            double mondayOpen,
            double m5Atr,
            double equity,
            double bid,
            double ask,
            double pipSize,
            double volumeInUnitsMin,
            double volumeInUnitsMax,
            Func<double, double, double, double> volumeForRisk,
            Func<double, double> normalizeVolume)
        {
            Direction = direction;
            MondayOpen = mondayOpen;
            M5Atr = m5Atr;
            Equity = equity;
            Bid = bid;
            Ask = ask;
            PipSize = pipSize;
            VolumeInUnitsMin = volumeInUnitsMin;
            VolumeInUnitsMax = volumeInUnitsMax;
            VolumeForRisk = volumeForRisk;
            NormalizeVolume = normalizeVolume;
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

// ════════════════════════════════════════════════════════════════════════════
// 3. Core/SessionClock.cs
// ════════════════════════════════════════════════════════════════════════════
namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>
    /// London-time helpers backed by .NET TimeZoneInfo. Replaces v1's hand-rolled
    /// IsBST so DST transitions are handled correctly.
    /// </summary>
    public class SessionClock
    {
        private readonly IClock _clock;
        private readonly TimeZoneInfo _londonTz;

        public SessionClock(IClock clock)
            : this(clock, ResolveLondonTimeZone())
        {
        }

        internal SessionClock(IClock clock, TimeZoneInfo londonTz)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _londonTz = londonTz ?? throw new ArgumentNullException(nameof(londonTz));
        }

        public DateTime LondonNow => ConvertToLondon(_clock.UtcNow);

        public DateTime ConvertToLondon(DateTime utc)
        {
            var asUtc = utc.Kind == DateTimeKind.Utc
                ? utc
                : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(asUtc, _londonTz);
        }

        public DateTime CurrentWeekStartDate() => WeekStartDate(LondonNow);

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
            return t >= start || t < end; // wraps midnight
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
                default:                  return false;
            }
        }

        private static TimeZoneInfo ResolveLondonTimeZone()
        {
            string[] candidates = { "Europe/London", "GMT Standard Time" };
            foreach (var id in candidates)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            throw new InvalidOperationException(
                "Could not find London time zone. Tried 'Europe/London' and 'GMT Standard Time'.");
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 4. Core/WeeklyState.cs
// ════════════════════════════════════════════════════════════════════════════
namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>
    /// Weekly trading state.
    ///
    /// Monday open detection
    /// ─────────────────────
    /// v1 read <c>Bars.OpenPrices.Last(1)</c> on whichever Monday M5 bar OnBar
    /// first fired on, which (a) returned the previous bar's open, and (b)
    /// failed entirely if the bot started mid-week.
    ///
    /// v2 walks the M5 bar history to find the earliest closed bar whose
    /// London time falls on the current week's Monday, and uses that bar's
    /// open. Works correctly when the bot starts mid-week and across DST
    /// boundary weeks.
    /// </summary>
    public class WeeklyState
    {
        public DateTime CurrentWeekStart { get; private set; } = DateTime.MinValue;
        public double MondayOpen { get; private set; }
        public bool MondayOpenSet { get; private set; }
        public int TradesThisWeek { get; private set; }
        public double StartOfWeekEquity { get; private set; }
        public bool CircuitBroken { get; private set; }

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

                if (londonOpen.Date < weekStart.Date) continue;
                if (londonOpen.Date == weekStart.Date)
                {
                    MondayOpen = m5Bars.Open(barsAgo);
                    MondayOpenSet = true;
                    return true;
                }
                // londonOpen.Date > weekStart.Date — no Monday data in history.
                return false;
            }
            return false;
        }

        public void RecordEntry() => TradesThisWeek++;
        public void TripCircuitBreaker() => CircuitBroken = true;

        public bool ShouldTripCircuitBreaker(double currentEquity, double lossLimitPct)
        {
            if (lossLimitPct <= 0) return false;
            if (StartOfWeekEquity <= 0) return false;
            double lossPct = (StartOfWeekEquity - currentEquity) / StartOfWeekEquity * 100.0;
            return lossPct >= lossLimitPct;
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 5. Core/SignalEngine.cs
// ════════════════════════════════════════════════════════════════════════════
namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>
    /// Pure signal state machine.
    ///   Idle              → waiting for deviation
    ///   DeviationDetected → waiting for confirmation bars
    ///   Confirming        → counting consecutive bars closing toward Monday open
    /// </summary>
    public class SignalEngine
    {
        private readonly SignalEngineConfig _cfg;

        public SignalPhase Phase { get; private set; } = SignalPhase.Idle;
        public TradeDirection PendingDirection { get; private set; } = TradeDirection.None;
        public int ConfirmationCount { get; private set; }

        public SignalEngine(SignalEngineConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (cfg.ConfirmationBars < 1)
                throw new ArgumentOutOfRangeException(nameof(cfg.ConfirmationBars), "must be >= 1");
            if (cfg.DeviationAtrMult <= 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.DeviationAtrMult), "must be > 0");
            _cfg = cfg;
        }

        public void Reset()
        {
            Phase = SignalPhase.Idle;
            PendingDirection = TradeDirection.None;
            ConfirmationCount = 0;
        }

        public SignalDecision Evaluate(SignalEngineInput input)
        {
            if (input.D1Atr <= 0) return SignalDecision.Wait("d1_atr_not_ready");
            if (input.PipSize <= 0) return SignalDecision.Wait("pip_size_invalid");

            double deviation = input.LastClose - input.MondayOpen;
            double threshold = _cfg.DeviationAtrMult * input.D1Atr;

            // ── Idle: look for fresh deviation ─────────────────────────
            if (Phase == SignalPhase.Idle)
            {
                if (Math.Abs(deviation) < threshold) return SignalDecision.NoOp();

                PendingDirection = deviation > 0 ? TradeDirection.Short : TradeDirection.Long;
                Phase = SignalPhase.DeviationDetected;
                ConfirmationCount = 0;

                if ((PendingDirection == TradeDirection.Long && !_cfg.AllowLongs) ||
                    (PendingDirection == TradeDirection.Short && !_cfg.AllowShorts))
                {
                    var disabled = PendingDirection;
                    Reset();
                    return SignalDecision.Wait($"{disabled}_disabled");
                }

                return SignalDecision.Wait(
                    $"deviation_reached dev={deviation:F5} thr={threshold:F5} dir={PendingDirection}");
            }

            // ── Phase is DeviationDetected or Confirming ───────────────

            // Reset trigger: closed bar back through Monday open by buffer.
            double resetBuffer = _cfg.ResetBufferPips * input.PipSize;
            bool brokeBackThrough =
                (PendingDirection == TradeDirection.Long && input.LastClose > input.MondayOpen + resetBuffer) ||
                (PendingDirection == TradeDirection.Short && input.LastClose < input.MondayOpen - resetBuffer);
            if (brokeBackThrough)
            {
                Reset();
                return SignalDecision.Wait("reset_close_back_through_monday_open");
            }

            // Direction filter (in case settings changed mid-week)
            if ((PendingDirection == TradeDirection.Long && !_cfg.AllowLongs) ||
                (PendingDirection == TradeDirection.Short && !_cfg.AllowShorts))
            {
                var disabled = PendingDirection;
                Reset();
                return SignalDecision.Wait($"{disabled}_disabled_midstream");
            }

            // Count consecutive bars closing toward Monday open.
            bool closingTowardMean = PendingDirection == TradeDirection.Long
                ? input.LastClose > input.PrevClose
                : input.LastClose < input.PrevClose;

            if (closingTowardMean)
            {
                ConfirmationCount++;
                Phase = SignalPhase.Confirming;
            }
            else
            {
                ConfirmationCount = 0;
                Phase = SignalPhase.DeviationDetected;
                return SignalDecision.Wait("confirm_broken_count_reset");
            }

            // Fire entry?
            if (ConfirmationCount >= _cfg.ConfirmationBars)
            {
                var dir = PendingDirection;
                Reset();
                return SignalDecision.Enter(dir, $"confirmed_{_cfg.ConfirmationBars}_bars");
            }

            return SignalDecision.Wait($"confirming_{ConfirmationCount}_of_{_cfg.ConfirmationBars}");
        }
    }

    public class SignalEngineConfig
    {
        public double DeviationAtrMult { get; set; } = 1.0;
        public int ConfirmationBars { get; set; } = 2;
        public bool AllowLongs { get; set; } = true;
        public bool AllowShorts { get; set; } = true;
        /// <summary>
        /// How many pips past Monday open a closed bar must travel before the engine
        /// concludes the setup is invalidated and resets. v1 effectively used 0.
        /// A small positive value (2–5 pips) suppresses single-bar wick noise.
        /// </summary>
        public double ResetBufferPips { get; set; } = 0;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 6. Core/RiskManager.cs
// ════════════════════════════════════════════════════════════════════════════
namespace PearlrockBots.WeeklyMeanReversion.Core
{
    public class RiskManager
    {
        private readonly RiskManagerConfig _cfg;

        public RiskManager(RiskManagerConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (cfg.RiskPercent <= 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.RiskPercent), "must be > 0");
            if (cfg.SlAtrMultiple <= 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.SlAtrMultiple), "must be > 0");
            if (cfg.MinTpToSlRatio < 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.MinTpToSlRatio), "must be >= 0");
            _cfg = cfg;
        }

        public (EntrySpec? entry, string rejectReason) BuildEntry(EntryContext ctx)
        {
            if (ctx.Direction == TradeDirection.None) return (null, "direction_none");
            if (ctx.M5Atr <= 0) return (null, "m5_atr_not_ready");
            if (ctx.PipSize <= 0) return (null, "pip_size_invalid");
            if (ctx.Equity <= 0) return (null, "equity_invalid");

            double slDistance = ctx.M5Atr * _cfg.SlAtrMultiple;
            double slPips = slDistance / ctx.PipSize;

            if (_cfg.MaxSlPips > 0 && slPips > _cfg.MaxSlPips)
                return (null, $"sl_too_wide_{slPips:F1}p>{_cfg.MaxSlPips}p");

            double currentPrice = ctx.Direction == TradeDirection.Long ? ctx.Ask : ctx.Bid;
            double tpBuffer = _cfg.TpBufferPips * ctx.PipSize;
            double tpPrice = ctx.Direction == TradeDirection.Long
                ? ctx.MondayOpen - tpBuffer
                : ctx.MondayOpen + tpBuffer;
            double tpDistance = Math.Abs(tpPrice - currentPrice);
            double tpPips = tpDistance / ctx.PipSize;

            if (tpPips < slPips * _cfg.MinTpToSlRatio)
                return (null, $"tp_too_small_{tpPips:F1}p<{slPips * _cfg.MinTpToSlRatio:F1}p");

            double rawVolume = ctx.VolumeForRisk(ctx.Equity, _cfg.RiskPercent, slPips);
            double volume = ctx.NormalizeVolume(rawVolume);

            if (volume < ctx.VolumeInUnitsMin)
                return (null, $"volume_below_min_{volume}<{ctx.VolumeInUnitsMin}");
            if (volume > ctx.VolumeInUnitsMax)
                volume = ctx.VolumeInUnitsMax;

            return (new EntrySpec(
                ctx.Direction,
                volume,
                slPips,
                tpPips,
                $"sl={slPips:F1}p tp={tpPips:F1}p vol={volume:F0}"
            ), null);
        }
    }

    public class RiskManagerConfig
    {
        public double RiskPercent { get; set; } = 0.5;
        public double SlAtrMultiple { get; set; } = 2.0;
        public double MaxSlPips { get; set; } = 0;
        public double TpBufferPips { get; set; } = 5;
        public double MinTpToSlRatio { get; set; } = 0.5;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 7. Adapters/CAlgoBarSeries.cs
// ════════════════════════════════════════════════════════════════════════════
namespace PearlrockBots.WeeklyMeanReversion.Adapters
{
    public sealed class CAlgoBarSeries : IBarSeries
    {
        private readonly Bars _bars;

        public CAlgoBarSeries(Bars bars)
        {
            _bars = bars ?? throw new ArgumentNullException(nameof(bars));
        }

        // Subtract 1 from cAlgo's Bars.Count to hide the forming bar.
        public int Count => Math.Max(0, _bars.Count - 1);

        public double Open(int barsAgo)  => _bars.OpenPrices.Last(barsAgo);
        public double High(int barsAgo)  => _bars.HighPrices.Last(barsAgo);
        public double Low(int barsAgo)   => _bars.LowPrices.Last(barsAgo);
        public double Close(int barsAgo) => _bars.ClosePrices.Last(barsAgo);

        public DateTime OpenTime(int barsAgo)
        {
            int idx = _bars.Count - 1 - barsAgo;
            if (idx < 0) idx = 0;
            return DateTime.SpecifyKind(_bars.OpenTimes[idx], DateTimeKind.Utc);
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 8. Adapters/CAlgoClock.cs
// ════════════════════════════════════════════════════════════════════════════
namespace PearlrockBots.WeeklyMeanReversion.Adapters
{
    public sealed class CAlgoClock : IClock
    {
        private readonly Func<DateTime> _serverTimeProvider;

        public CAlgoClock(Func<DateTime> serverTimeProvider)
        {
            _serverTimeProvider = serverTimeProvider
                ?? throw new ArgumentNullException(nameof(serverTimeProvider));
        }

        public DateTime UtcNow
        {
            get
            {
                var t = _serverTimeProvider();
                return t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
            }
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 9. Adapters/CAlgoSymbol.cs
// ════════════════════════════════════════════════════════════════════════════
namespace PearlrockBots.WeeklyMeanReversion.Adapters
{
    public sealed class CAlgoSymbol : ISymbol
    {
        private readonly Symbol _symbol;

        public CAlgoSymbol(Symbol symbol)
        {
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        }

        public string Name              => _symbol.Name;
        public double PipSize           => _symbol.PipSize;
        public double VolumeInUnitsMin  => _symbol.VolumeInUnitsMin;
        public double VolumeInUnitsMax  => _symbol.VolumeInUnitsMax;
        public double Bid               => _symbol.Bid;
        public double Ask               => _symbol.Ask;
        public double Spread            => _symbol.Ask - _symbol.Bid;

        public double VolumeForRisk(double equity, double riskPercent, double slPips)
            => _symbol.VolumeForProportionalRisk(
                ProportionalAmountType.Equity,
                riskPercent,
                slPips,
                RoundingMode.Down);

        public double NormalizeVolume(double units)
            => _symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 10. WeeklyOpen_MeanReversion_v2 — the cBot
// ════════════════════════════════════════════════════════════════════════════
namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class WeeklyOpen_MeanReversion_v2 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "WEEKLY_MR_V2", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Signal ────────────────────────────────────────────────────
        [Parameter("Deviation Threshold (× D1 ATR)", DefaultValue = 1.0, MinValue = 0.25, MaxValue = 4.0, Step = 0.25, Group = "Signal")]
        public double DeviationAtrMult { get; set; }

        [Parameter("D1 ATR Period", DefaultValue = 14, MinValue = 2, Group = "Signal")]
        public int D1AtrPeriod { get; set; }

        [Parameter("Confirmation Bars (consecutive toward mean)", DefaultValue = 2, MinValue = 1, MaxValue = 5, Group = "Signal")]
        public int ConfirmationBars { get; set; }

        [Parameter("Reset Buffer (pips past Monday open)", DefaultValue = 2, MinValue = 0, Step = 1, Group = "Signal")]
        public double ResetBufferPips { get; set; }

        // ── Risk ──────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("M5 ATR Period (for SL)", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int M5AtrPeriod { get; set; }

        [Parameter("SL ATR Multiple (M5)", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }

        [Parameter("TP Buffer (pips from Monday open)", DefaultValue = 5, MinValue = 0, Step = 1, Group = "Risk")]
        public double TpBufferPips { get; set; }

        [Parameter("Max Trade Duration (hours, 0=off)", DefaultValue = 24, MinValue = 0, MaxValue = 120, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        [Parameter("Max SL Pips (0=off)", DefaultValue = 0, MinValue = 0, Step = 5, Group = "Risk")]
        public double MaxSlPips { get; set; }

        [Parameter("Min TP/SL Ratio", DefaultValue = 0.5, MinValue = 0, Step = 0.1, Group = "Risk")]
        public double MinTpToSlRatio { get; set; }

        // ── Direction ─────────────────────────────────────────────────
        [Parameter("Allow Longs",  DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

        // ── Trade Control ─────────────────────────────────────────────
        [Parameter("Trade Start (HH:mm London)", DefaultValue = "00:00", Group = "Trade Control")]
        public string TradeStart { get; set; }

        [Parameter("Trade End (HH:mm London)", DefaultValue = "23:59", Group = "Trade Control")]
        public string TradeEnd { get; set; }

        [Parameter("Max Trades Per Week", DefaultValue = 2, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerWeek { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 60, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        // ── Day Filter ────────────────────────────────────────────────
        [Parameter("Use Day Filter", DefaultValue = true, Group = "Day Filter")]
        public bool UseDayFilter { get; set; }

        [Parameter("Trade Monday",    DefaultValue = false, Group = "Day Filter")] public bool TradeMonday    { get; set; }
        [Parameter("Trade Tuesday",   DefaultValue = true,  Group = "Day Filter")] public bool TradeTuesday   { get; set; }
        [Parameter("Trade Wednesday", DefaultValue = true,  Group = "Day Filter")] public bool TradeWednesday { get; set; }
        [Parameter("Trade Thursday",  DefaultValue = false, Group = "Day Filter")] public bool TradeThursday  { get; set; }
        [Parameter("Trade Friday",    DefaultValue = false, Group = "Day Filter")] public bool TradeFriday    { get; set; }

        // ── Safety / Circuit Breaker ─────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = false, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread", DefaultValue = 0.001, Step = 0.0001, Group = "Safety")]
        public double MaxSpread { get; set; }

        [Parameter("Weekly Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Safety")]
        public double WeeklyLossLimitPct { get; set; }

        // ── Logging ───────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        // ── cAlgo indicators / data ───────────────────────────────────
        private AverageTrueRange _m5Atr;
        private Bars _d1Bars;
        private AverageTrueRange _d1Atr;

        // ── Core ──────────────────────────────────────────────────────
        private SessionClock _clock;
        private WeeklyState _state;
        private SignalEngine _signal;
        private RiskManager _risk;
        private CAlgoBarSeries _m5BarsAdapter;
        private CAlgoSymbol _symbolAdapter;

        // ── Exit tracking ─────────────────────────────────────────────
        private DateTime _lastExitTime = DateTime.MinValue;

        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5)
            {
                Print($"ABORT: Must run on M5. Current = {Bars.TimeFrame}");
                Stop();
                return;
            }

            _m5Atr = Indicators.AverageTrueRange(M5AtrPeriod, MovingAverageType.Exponential);
            _d1Bars = MarketData.GetBars(TimeFrame.Daily);
            _d1Atr = Indicators.AverageTrueRange(_d1Bars, D1AtrPeriod, MovingAverageType.Exponential);

            _clock = new SessionClock(new CAlgoClock(() => Server.Time));
            _state = new WeeklyState();
            _signal = new SignalEngine(new SignalEngineConfig
            {
                DeviationAtrMult = DeviationAtrMult,
                ConfirmationBars = ConfirmationBars,
                AllowLongs = AllowLongs,
                AllowShorts = AllowShorts,
                ResetBufferPips = ResetBufferPips
            });
            _risk = new RiskManager(new RiskManagerConfig
            {
                RiskPercent = RiskPercent,
                SlAtrMultiple = SlAtrMultiple,
                MaxSlPips = MaxSlPips,
                TpBufferPips = TpBufferPips,
                MinTpToSlRatio = MinTpToSlRatio
            });
            _m5BarsAdapter = new CAlgoBarSeries(Bars);
            _symbolAdapter = new CAlgoSymbol(Symbol);

            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} (v2) started on {SymbolName} M5 ═══");
            Print($"Deviation={DeviationAtrMult}×D1ATR | Confirm={ConfirmationBars} | ResetBuffer={ResetBufferPips}p");
            Print($"SL={SlAtrMultiple}×M5ATR (cap {MaxSlPips}p) | TPBuffer={TpBufferPips}p | MinTP/SL={MinTpToSlRatio}");
            Print($"Session={TradeStart}-{TradeEnd} London | DayFilter={UseDayFilter}");
            Print($"MaxTrades/Week={MaxTradesPerWeek} | Cooldown={CooldownMinutes}m");
            Print($"Circuit={(WeeklyLossLimitPct > 0 ? WeeklyLossLimitPct + "%" : "off")}");
        }

        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5) return;

            var london = _clock.LondonNow;
            var weekStart = SessionClock.WeekStartDate(london);

            if (_state.ResetIfNewWeek(weekStart, Account.Equity))
            {
                Log($"NEW WEEK {weekStart:dd-MMM-yyyy}");
                _signal.Reset();
            }

            if (!_state.MondayOpenSet)
            {
                if (_state.TryCaptureMondayOpen(_m5BarsAdapter, _clock, weekStart))
                    Print($"MONDAY OPEN SET | {_state.MondayOpen:F5}");
            }

            ManageOpenPositions();

            if (CheckCircuitBreaker()) return;
            if (!_state.MondayOpenSet) return;
            if (!_clock.IsTradingDayEnabled(london.DayOfWeek, BuildDayFilter())) return;
            if (!IsInTradeHours()) return;
            if (!SpreadOk()) return;
            if (!IndicatorsReady()) return;
            if (_lastExitTime != DateTime.MinValue
                && Server.Time < _lastExitTime.AddMinutes(CooldownMinutes)) return;
            if (_state.TradesThisWeek >= MaxTradesPerWeek) return;
            if (HasOpenPosition()) return;

            int d1LastClosed = _d1Bars.Count - 2;
            if (d1LastClosed < D1AtrPeriod) return;

            var input = new SignalEngineInput(
                mondayOpen: _state.MondayOpen,
                lastClose: Bars.ClosePrices.Last(1),
                prevClose: Bars.ClosePrices.Last(2),
                d1Atr: _d1Atr.Result[d1LastClosed],
                pipSize: Symbol.PipSize);

            var decision = _signal.Evaluate(input);
            if (VerboseLogging && decision.Reason != "no-op")
                Log($"SIGNAL [{_signal.Phase}] {decision.Reason}");

            if (!decision.ShouldEnter) return;

            var ctx = new EntryContext(
                direction: decision.Direction,
                mondayOpen: _state.MondayOpen,
                m5Atr: _m5Atr.Result.LastValue,
                equity: Account.Equity,
                bid: Symbol.Bid,
                ask: Symbol.Ask,
                pipSize: Symbol.PipSize,
                volumeInUnitsMin: Symbol.VolumeInUnitsMin,
                volumeInUnitsMax: Symbol.VolumeInUnitsMax,
                volumeForRisk: _symbolAdapter.VolumeForRisk,
                normalizeVolume: _symbolAdapter.NormalizeVolume);

            var (entry, reject) = _risk.BuildEntry(ctx);
            if (entry == null)
            {
                Log($"ENTRY BLOCKED — {reject}");
                return;
            }

            PlaceOrder(entry.Value);
        }

        private void PlaceOrder(EntrySpec entry)
        {
            var dir = entry.Direction == TradeDirection.Long ? TradeType.Buy : TradeType.Sell;
            var result = ExecuteMarketOrder(
                dir,
                SymbolName,
                entry.Volume,
                BotLabel,
                entry.SlPips,
                entry.TpPips);

            if (result.IsSuccessful)
            {
                _state.RecordEntry();
                Print($"[{Server.Time:HH:mm}] {dir.ToString().ToUpper()} OPEN | " +
                      $"Entry={result.Position.EntryPrice:F5} | {entry.Note} | " +
                      $"MondayOpen={_state.MondayOpen:F5}");
            }
            else
            {
                Print($"ORDER FAILED: {result.Error}");
            }
        }

        private void ManageOpenPositions()
        {
            if (MaxTradeHours <= 0) return;

            foreach (var pos in Positions
                .Where(p => p.SymbolName == SymbolName && p.Label == BotLabel)
                .ToList())
            {
                var hoursOpen = (Server.Time - pos.EntryTime).TotalHours;
                if (hoursOpen >= MaxTradeHours)
                {
                    Print($"MAX DURATION EXIT | {hoursOpen:F1}h | Net={pos.NetProfit:F2}");
                    ClosePosition(pos);
                }
            }
        }

        private bool CheckCircuitBreaker()
        {
            if (!_state.ShouldTripCircuitBreaker(Account.Equity, WeeklyLossLimitPct))
                return _state.CircuitBroken;

            if (!_state.CircuitBroken)
            {
                double lossPct = (_state.StartOfWeekEquity - Account.Equity)
                                 / _state.StartOfWeekEquity * 100.0;
                Print($"⚡ CIRCUIT BREAKER | Loss={lossPct:F2}% >= {WeeklyLossLimitPct}%");
                _state.TripCircuitBreaker();
                foreach (var pos in Positions
                    .Where(p => p.SymbolName == SymbolName && p.Label == BotLabel)
                    .ToList())
                    ClosePosition(pos);
            }
            return true;
        }

        private DayFilter BuildDayFilter() =>
            new DayFilter(UseDayFilter, TradeMonday, TradeTuesday, TradeWednesday,
                          TradeThursday, TradeFriday);

        private bool IsInTradeHours()
        {
            if (string.IsNullOrWhiteSpace(TradeStart) || string.IsNullOrWhiteSpace(TradeEnd))
                return true;
            if (!TimeSpan.TryParse(TradeStart, out var start) ||
                !TimeSpan.TryParse(TradeEnd, out var end))
                return true;
            return _clock.InTradeHours(start, end);
        }

        private bool SpreadOk()
        {
            if (!UseSpreadFilter) return true;
            return _symbolAdapter.Spread <= MaxSpread;
        }

        private bool IndicatorsReady() =>
            _d1Bars.Count > D1AtrPeriod + 5 && Bars.Count > M5AtrPeriod + 5;

        private bool HasOpenPosition() =>
            Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName || args.Position.Label != BotLabel) return;
            _lastExitTime = Server.Time;
        }

        private void Log(string msg)
        {
            if (VerboseLogging) Print(msg);
        }

        // GetFitness omitted — port from v1 if you use the optimizer.
    }
}
