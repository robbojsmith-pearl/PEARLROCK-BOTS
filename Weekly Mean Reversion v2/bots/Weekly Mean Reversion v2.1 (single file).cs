// ════════════════════════════════════════════════════════════════════════════
// Weekly Open Mean Reversion v2.1 — single-file build (TP Fraction)
// Pearlrock Systematic
//
// Same as v2 except:
//   - New parameter `TpFraction` (default 1.0 = full reversion to Monday open
//     i.e. v2.0 behaviour). 0.5 = halfway back to Monday open.
//   - TP target is now distance-from-entry × TpFraction, minus TpBufferPips.
//   - Reject reasons:
//       price_already_at_or_past_mo
//       tp_distance_non_positive_after_buffer
//
// Class name `WeeklyOpen_MeanReversion_v2_1` so this can live as a SEPARATE
// cBot in cTrader alongside the original v2.
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using PearlrockBots.WeeklyMeanReversion.V21.Adapters;
using PearlrockBots.WeeklyMeanReversion.V21.Core;

namespace PearlrockBots.WeeklyMeanReversion.V21.Core
{
    public interface IClock { DateTime UtcNow { get; } }

    public interface IBarSeries
    {
        int Count { get; }
        double Open(int barsAgo);
        double High(int barsAgo);
        double Low(int barsAgo);
        double Close(int barsAgo);
        DateTime OpenTime(int barsAgo);
    }

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

    public enum TradeDirection { None = 0, Long = 1, Short = -1 }

    public enum SignalPhase { Idle, DeviationDetected, Confirming }

    public readonly struct SignalDecision
    {
        public TradeDirection Direction { get; }
        public bool ShouldEnter { get; }
        public string Reason { get; }
        private SignalDecision(TradeDirection d, bool e, string r)
        { Direction = d; ShouldEnter = e; Reason = r; }
        public static SignalDecision NoOp() => new SignalDecision(TradeDirection.None, false, "no-op");
        public static SignalDecision Wait(string reason) => new SignalDecision(TradeDirection.None, false, reason);
        public static SignalDecision Enter(TradeDirection d, string reason) => new SignalDecision(d, true, reason);
    }

    public readonly struct SignalEngineInput
    {
        public double MondayOpen { get; }
        public double LastClose { get; }
        public double PrevClose { get; }
        public double D1Atr { get; }
        public double PipSize { get; }
        public SignalEngineInput(double mo, double lc, double pc, double d1, double ps)
        { MondayOpen = mo; LastClose = lc; PrevClose = pc; D1Atr = d1; PipSize = ps; }
    }

    public readonly struct EntrySpec
    {
        public TradeDirection Direction { get; }
        public double Volume { get; }
        public double SlPips { get; }
        public double TpPips { get; }
        public string Note { get; }
        public EntrySpec(TradeDirection d, double v, double sl, double tp, string n)
        { Direction = d; Volume = v; SlPips = sl; TpPips = tp; Note = n; }
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

        public EntryContext(TradeDirection direction, double mondayOpen, double m5Atr,
            double equity, double bid, double ask, double pipSize,
            double volumeInUnitsMin, double volumeInUnitsMax,
            Func<double, double, double, double> volumeForRisk,
            Func<double, double> normalizeVolume)
        {
            Direction = direction; MondayOpen = mondayOpen; M5Atr = m5Atr;
            Equity = equity; Bid = bid; Ask = ask; PipSize = pipSize;
            VolumeInUnitsMin = volumeInUnitsMin; VolumeInUnitsMax = volumeInUnitsMax;
            VolumeForRisk = volumeForRisk; NormalizeVolume = normalizeVolume;
        }
    }

    public readonly struct DayFilter
    {
        public bool Enabled { get; }
        public bool Monday { get; } public bool Tuesday { get; } public bool Wednesday { get; }
        public bool Thursday { get; } public bool Friday { get; }
        public DayFilter(bool e, bool mo, bool tu, bool we, bool th, bool fr)
        { Enabled = e; Monday = mo; Tuesday = tu; Wednesday = we; Thursday = th; Friday = fr; }
    }

    public class SessionClock
    {
        private readonly IClock _clock;
        private readonly TimeZoneInfo _londonTz;
        public SessionClock(IClock clock) : this(clock, ResolveLondonTimeZone()) {}
        internal SessionClock(IClock clock, TimeZoneInfo londonTz)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _londonTz = londonTz ?? throw new ArgumentNullException(nameof(londonTz));
        }
        public DateTime LondonNow => ConvertToLondon(_clock.UtcNow);
        public DateTime ConvertToLondon(DateTime utc)
        {
            var asUtc = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(asUtc, _londonTz);
        }
        public DateTime CurrentWeekStartDate() => WeekStartDate(LondonNow);
        public static DateTime WeekStartDate(DateTime londonTime)
        {
            var d = londonTime.Date;
            int dfm = ((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return d.AddDays(-dfm);
        }
        public bool InTradeHours(TimeSpan start, TimeSpan end)
        {
            var t = LondonNow.TimeOfDay;
            if (start <= end) return t >= start && t < end;
            return t >= start || t < end;
        }
        public bool IsTradingDayEnabled(DayOfWeek day, DayFilter f)
        {
            if (!f.Enabled) return true;
            switch (day)
            {
                case DayOfWeek.Monday: return f.Monday;
                case DayOfWeek.Tuesday: return f.Tuesday;
                case DayOfWeek.Wednesday: return f.Wednesday;
                case DayOfWeek.Thursday: return f.Thursday;
                case DayOfWeek.Friday: return f.Friday;
                default: return false;
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
            throw new InvalidOperationException("Could not find London time zone.");
        }
    }

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

            double resetBuffer = _cfg.ResetBufferPips * input.PipSize;
            bool brokeBackThrough =
                (PendingDirection == TradeDirection.Long && input.LastClose > input.MondayOpen + resetBuffer) ||
                (PendingDirection == TradeDirection.Short && input.LastClose < input.MondayOpen - resetBuffer);
            if (brokeBackThrough)
            {
                Reset();
                return SignalDecision.Wait("reset_close_back_through_monday_open");
            }

            if ((PendingDirection == TradeDirection.Long && !_cfg.AllowLongs) ||
                (PendingDirection == TradeDirection.Short && !_cfg.AllowShorts))
            {
                var disabled = PendingDirection;
                Reset();
                return SignalDecision.Wait($"{disabled}_disabled_midstream");
            }

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
        public double ResetBufferPips { get; set; } = 0;
    }

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
            if (cfg.TpFraction <= 0 || cfg.TpFraction > 1)
                throw new ArgumentOutOfRangeException(nameof(cfg.TpFraction), "must be in (0, 1]");
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

            double distanceToMO = ctx.Direction == TradeDirection.Long
                ? ctx.MondayOpen - currentPrice
                : currentPrice - ctx.MondayOpen;
            if (distanceToMO <= 0)
                return (null, $"price_already_at_or_past_mo_dist={distanceToMO:F5}");

            double tpBuffer = _cfg.TpBufferPips * ctx.PipSize;
            double tpDistance = (distanceToMO * _cfg.TpFraction) - tpBuffer;
            if (tpDistance <= 0)
                return (null, $"tp_distance_non_positive_after_buffer_{tpDistance:F5}");

            double tpPips = tpDistance / ctx.PipSize;
            if (tpPips < slPips * _cfg.MinTpToSlRatio)
                return (null, $"tp_too_small_{tpPips:F1}p<{slPips * _cfg.MinTpToSlRatio:F1}p");

            double rawVolume = ctx.VolumeForRisk(ctx.Equity, _cfg.RiskPercent, slPips);
            double volume = ctx.NormalizeVolume(rawVolume);
            if (volume < ctx.VolumeInUnitsMin)
                return (null, $"volume_below_min_{volume}<{ctx.VolumeInUnitsMin}");
            if (volume > ctx.VolumeInUnitsMax)
                volume = ctx.VolumeInUnitsMax;

            return (new EntrySpec(ctx.Direction, volume, slPips, tpPips,
                $"sl={slPips:F1}p tp={tpPips:F1}p frac={_cfg.TpFraction:F2} vol={volume:F0}"), null);
        }
    }

    public class RiskManagerConfig
    {
        public double RiskPercent { get; set; } = 0.5;
        public double SlAtrMultiple { get; set; } = 2.0;
        public double MaxSlPips { get; set; } = 0;
        public double TpBufferPips { get; set; } = 5;
        public double MinTpToSlRatio { get; set; } = 0.5;
        public double TpFraction { get; set; } = 1.0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Custom optimizer fitness — hard-rejects under-trading curve-fits at the
    // optimizer level so the results grid is pre-filtered.
    //
    // Reject if:
    //   total trades < MinTotalTrades, or
    //   trades / year < MinTradesPerYear, or
    //   net profit ≤ 0, or
    //   max equity DD% > MaxDrawdownPct
    //
    // Otherwise:
    //   score = (log10(1 + net) × min(PF, PfCap) × √trades × winRate²) / DD%^1.5
    //
    // PF capped at PfCap (default 3.0) so absurd PFs from overfit setups don't
    // dominate the score.
    // ─────────────────────────────────────────────────────────────────────────
    public static class Fitness
    {
        public static double Compute(FitnessInput x, FitnessConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            double years = x.BacktestYears > 0 ? x.BacktestYears : 1;
            double tradesPerYear = x.TotalTrades / years;
            double winRate = x.TotalTrades > 0 ? x.WinningTrades / x.TotalTrades : 0;
            double ddPct = Math.Max(x.MaxEquityDrawdownPct, 0.01);
            double pf = Math.Min(x.ProfitFactor, cfg.PfCap);

            if (x.TotalTrades < cfg.MinTotalTrades) return cfg.RejectionScore;
            if (tradesPerYear < cfg.MinTradesPerYear) return cfg.RejectionScore;
            if (x.NetProfit <= 0) return cfg.RejectionScore;
            if (ddPct > cfg.MaxDrawdownPct) return cfg.RejectionScore;

            double profitScore = Math.Log10(1.0 + x.NetProfit);
            double tradeScore = Math.Sqrt(x.TotalTrades);
            double ddPenalty = Math.Pow(ddPct, 1.5);
            double winBonus = Math.Pow(winRate, 2);

            return (profitScore * pf * tradeScore * winBonus) / ddPenalty;
        }
    }

    public readonly struct FitnessInput
    {
        public double TotalTrades { get; }
        public double WinningTrades { get; }
        public double NetProfit { get; }
        public double MaxEquityDrawdownPct { get; }
        public double ProfitFactor { get; }
        public double BacktestYears { get; }

        public FitnessInput(double totalTrades, double winningTrades, double netProfit,
                            double maxEquityDrawdownPct, double profitFactor, double backtestYears)
        {
            TotalTrades = totalTrades;
            WinningTrades = winningTrades;
            NetProfit = netProfit;
            MaxEquityDrawdownPct = maxEquityDrawdownPct;
            ProfitFactor = profitFactor;
            BacktestYears = backtestYears;
        }
    }

    public class FitnessConfig
    {
        public int MinTotalTrades { get; set; } = 40;
        public double MinTradesPerYear { get; set; } = 6;
        public double MaxDrawdownPct { get; set; } = 20.0;
        public double PfCap { get; set; } = 3.0;
        public double RejectionScore { get; set; } = -1_000_000;
    }
}

namespace PearlrockBots.WeeklyMeanReversion.V21.Adapters
{
    public sealed class CAlgoBarSeries : IBarSeries
    {
        private readonly Bars _bars;
        public CAlgoBarSeries(Bars bars)
        { _bars = bars ?? throw new ArgumentNullException(nameof(bars)); }
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

    public sealed class CAlgoClock : IClock
    {
        private readonly Func<DateTime> _serverTimeProvider;
        public CAlgoClock(Func<DateTime> p)
        { _serverTimeProvider = p ?? throw new ArgumentNullException(nameof(p)); }
        public DateTime UtcNow
        {
            get
            {
                var t = _serverTimeProvider();
                return t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
            }
        }
    }

    public sealed class CAlgoSymbol : ISymbol
    {
        private readonly Symbol _symbol;
        public CAlgoSymbol(Symbol s) { _symbol = s ?? throw new ArgumentNullException(nameof(s)); }
        public string Name => _symbol.Name;
        public double PipSize => _symbol.PipSize;
        public double VolumeInUnitsMin => _symbol.VolumeInUnitsMin;
        public double VolumeInUnitsMax => _symbol.VolumeInUnitsMax;
        public double Bid => _symbol.Bid;
        public double Ask => _symbol.Ask;
        public double Spread => _symbol.Ask - _symbol.Bid;
        public double VolumeForRisk(double equity, double riskPercent, double slPips)
            => _symbol.VolumeForProportionalRisk(ProportionalAmountType.Equity, riskPercent, slPips, RoundingMode.Down);
        public double NormalizeVolume(double units)
            => _symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
    }
}

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class WeeklyOpen_MeanReversion_v2_1 : Robot
    {
        // Identity
        [Parameter("Bot Label", DefaultValue = "WEEKLY_MR_V2_1", Group = "Identity")]
        public string BotLabel { get; set; }

        // Signal
        [Parameter("Deviation Threshold (× D1 ATR)", DefaultValue = 2.0, MinValue = 0.25, MaxValue = 4.0, Step = 0.25, Group = "Signal")]
        public double DeviationAtrMult { get; set; }
        [Parameter("D1 ATR Period", DefaultValue = 14, MinValue = 2, Group = "Signal")]
        public int D1AtrPeriod { get; set; }
        [Parameter("Confirmation Bars (consecutive toward mean)", DefaultValue = 2, MinValue = 1, MaxValue = 5, Group = "Signal")]
        public int ConfirmationBars { get; set; }
        [Parameter("Reset Buffer (pips past Monday open)", DefaultValue = 2, MinValue = 0, Step = 1, Group = "Signal")]
        public double ResetBufferPips { get; set; }

        // Risk
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }
        [Parameter("M5 ATR Period (for SL)", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int M5AtrPeriod { get; set; }
        [Parameter("SL ATR Multiple (M5)", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }
        [Parameter("TP Buffer (pips)", DefaultValue = 5, MinValue = 0, Step = 1, Group = "Risk")]
        public double TpBufferPips { get; set; }
        [Parameter("Max Trade Duration (hours, 0=off)", DefaultValue = 48, MinValue = 0, MaxValue = 120, Group = "Risk")]
        public int MaxTradeHours { get; set; }
        [Parameter("Max SL Pips (0=off)", DefaultValue = 0, MinValue = 0, Step = 5, Group = "Risk")]
        public double MaxSlPips { get; set; }
        [Parameter("Min TP/SL Ratio", DefaultValue = 0.5, MinValue = 0, Step = 0.1, Group = "Risk")]
        public double MinTpToSlRatio { get; set; }
        [Parameter("TP Fraction (0.05..1, of entry->Monday open distance)", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 1.0, Step = 0.05, Group = "Risk")]
        public double TpFraction { get; set; }

        // Direction
        [Parameter("Allow Longs",  DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }
        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

        // Trade Control
        [Parameter("Trade Start (HH:mm London)", DefaultValue = "00:00", Group = "Trade Control")]
        public string TradeStart { get; set; }
        [Parameter("Trade End (HH:mm London)", DefaultValue = "23:59", Group = "Trade Control")]
        public string TradeEnd { get; set; }
        [Parameter("Max Trades Per Week", DefaultValue = 3, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerWeek { get; set; }
        [Parameter("Cooldown Minutes After Exit", DefaultValue = 60, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        // Day Filter
        [Parameter("Use Day Filter", DefaultValue = true, Group = "Day Filter")]
        public bool UseDayFilter { get; set; }
        [Parameter("Trade Monday",    DefaultValue = false, Group = "Day Filter")] public bool TradeMonday    { get; set; }
        [Parameter("Trade Tuesday",   DefaultValue = true,  Group = "Day Filter")] public bool TradeTuesday   { get; set; }
        [Parameter("Trade Wednesday", DefaultValue = true,  Group = "Day Filter")] public bool TradeWednesday { get; set; }
        [Parameter("Trade Thursday",  DefaultValue = false, Group = "Day Filter")] public bool TradeThursday  { get; set; }
        [Parameter("Trade Friday",    DefaultValue = false, Group = "Day Filter")] public bool TradeFriday    { get; set; }

        // Safety / Circuit Breaker
        [Parameter("Use Spread Filter", DefaultValue = false, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }
        [Parameter("Max Spread", DefaultValue = 0.001, Step = 0.0001, Group = "Safety")]
        public double MaxSpread { get; set; }
        [Parameter("Weekly Loss Limit % (0=off)", DefaultValue = 3.0, MinValue = 0, Step = 0.1, Group = "Safety")]
        public double WeeklyLossLimitPct { get; set; }

        // Fitness (custom optimizer scoring)
        [Parameter("Min Total Trades", DefaultValue = 40, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }
        [Parameter("Min Trades Per Year", DefaultValue = 6.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }
        [Parameter("Max Fitness DD %", DefaultValue = 20.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }
        [Parameter("Backtest Years", DefaultValue = 8.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }
        [Parameter("Fitness PF Cap", DefaultValue = 3.0, MinValue = 1.0, Step = 0.1, Group = "Fitness")]
        public double FitnessPfCap { get; set; }

        // Logging
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        // cAlgo data
        private AverageTrueRange _m5Atr;
        private Bars _d1Bars;
        private AverageTrueRange _d1Atr;

        // Core
        private SessionClock _clock;
        private WeeklyState _state;
        private SignalEngine _signal;
        private RiskManager _risk;
        private CAlgoBarSeries _m5BarsAdapter;
        private CAlgoSymbol _symbolAdapter;
        private DateTime _lastExitTime = DateTime.MinValue;

        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5)
            {
                Print($"ABORT: Must run on M5. Current = {Bars.TimeFrame}");
                Stop(); return;
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
                MinTpToSlRatio = MinTpToSlRatio,
                TpFraction = TpFraction
            });
            _m5BarsAdapter = new CAlgoBarSeries(Bars);
            _symbolAdapter = new CAlgoSymbol(Symbol);

            Positions.Closed += OnPositionClosed;
            Print($"=== {BotLabel} (v2.1) on {SymbolName} M5 ===");
            Print($"Deviation={DeviationAtrMult}xD1ATR Confirm={ConfirmationBars} ResetBuf={ResetBufferPips}p");
            Print($"SL={SlAtrMultiple}xM5ATR(cap={MaxSlPips}p) TPBuf={TpBufferPips}p TPFrac={TpFraction} MinTP/SL={MinTpToSlRatio}");
            Print($"Session={TradeStart}-{TradeEnd} London DayFilter={UseDayFilter}");
            Print($"MaxTrades/Week={MaxTradesPerWeek} Cooldown={CooldownMinutes}m");
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
                _state.MondayOpen,
                Bars.ClosePrices.Last(1),
                Bars.ClosePrices.Last(2),
                _d1Atr.Result[d1LastClosed],
                Symbol.PipSize);

            var decision = _signal.Evaluate(input);
            if (VerboseLogging && decision.Reason != "no-op")
                Log($"SIGNAL [{_signal.Phase}] {decision.Reason}");
            if (!decision.ShouldEnter) return;

            var ctx = new EntryContext(
                decision.Direction,
                _state.MondayOpen,
                _m5Atr.Result.LastValue,
                Account.Equity,
                Symbol.Bid, Symbol.Ask, Symbol.PipSize,
                Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsMax,
                _symbolAdapter.VolumeForRisk,
                _symbolAdapter.NormalizeVolume);

            var (entry, reject) = _risk.BuildEntry(ctx);
            if (entry == null) { Log($"ENTRY BLOCKED -- {reject}"); return; }
            PlaceOrder(entry.Value);
        }

        private void PlaceOrder(EntrySpec entry)
        {
            var dir = entry.Direction == TradeDirection.Long ? TradeType.Buy : TradeType.Sell;
            var result = ExecuteMarketOrder(dir, SymbolName, entry.Volume, BotLabel, entry.SlPips, entry.TpPips);
            if (result.IsSuccessful)
            {
                _state.RecordEntry();
                Print($"[{Server.Time:HH:mm}] {dir.ToString().ToUpper()} OPEN | " +
                      $"Entry={result.Position.EntryPrice:F5} | {entry.Note} | MO={_state.MondayOpen:F5}");
            }
            else { Print($"ORDER FAILED: {result.Error}"); }
        }

        private void ManageOpenPositions()
        {
            if (MaxTradeHours <= 0) return;
            foreach (var pos in Positions.Where(p => p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
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
                double lossPct = (_state.StartOfWeekEquity - Account.Equity) / _state.StartOfWeekEquity * 100.0;
                Print($"CIRCUIT BREAKER | Loss={lossPct:F2}% >= {WeeklyLossLimitPct}%");
                _state.TripCircuitBreaker();
                foreach (var pos in Positions.Where(p => p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
                    ClosePosition(pos);
            }
            return true;
        }

        private DayFilter BuildDayFilter()
            => new DayFilter(UseDayFilter, TradeMonday, TradeTuesday, TradeWednesday, TradeThursday, TradeFriday);

        private bool IsInTradeHours()
        {
            if (string.IsNullOrWhiteSpace(TradeStart) || string.IsNullOrWhiteSpace(TradeEnd)) return true;
            if (!TimeSpan.TryParse(TradeStart, out var start) || !TimeSpan.TryParse(TradeEnd, out var end)) return true;
            return _clock.InTradeHours(start, end);
        }

        private bool SpreadOk() => !UseSpreadFilter || _symbolAdapter.Spread <= MaxSpread;

        private bool IndicatorsReady()
            => _d1Bars.Count > D1AtrPeriod + 5 && Bars.Count > M5AtrPeriod + 5;

        private bool HasOpenPosition()
            => Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName || args.Position.Label != BotLabel) return;
            _lastExitTime = Server.Time;
        }

        private void Log(string msg) { if (VerboseLogging) Print(msg); }

        // Custom optimizer fitness — see Fitness class for math.
        protected override double GetFitness(GetFitnessArgs args)
        {
            return Fitness.Compute(
                new FitnessInput(
                    totalTrades:          args.TotalTrades,
                    winningTrades:        args.WinningTrades,
                    netProfit:            args.NetProfit,
                    maxEquityDrawdownPct: args.MaxEquityDrawdownPercentages,
                    profitFactor:         args.ProfitFactor,
                    backtestYears:        BacktestYears),
                new FitnessConfig
                {
                    MinTotalTrades   = MinTotalTrades,
                    MinTradesPerYear = MinTradesPerYear,
                    MaxDrawdownPct   = MaxFitnessDrawdownPct,
                    PfCap            = FitnessPfCap
                });
        }
    }
}
