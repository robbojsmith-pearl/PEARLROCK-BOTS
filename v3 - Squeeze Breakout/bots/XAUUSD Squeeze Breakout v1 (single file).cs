// ════════════════════════════════════════════════════════════════════════════
// XAUUSD Squeeze Breakout v1 — single-file build
// Pearlrock Systematic
//
// Long/short volatility-compression breakout. Trades open the bar AFTER a
// Bollinger-Band-inside-Keltner-Channel squeeze releases, when:
//   - price aligns with the 200H SMA trend gate
//   - LR-projected momentum is aligned with the trend
//   - LR-projected momentum is strengthening vs prior bar
//
// Stop = 3×ATR. TP = 6×ATR. Trail = 3×ATR ratchet. Plus momentum-fade soft exit.
// One position at a time. Risk-based sizing (0.5% default), leverage-capped.
//
// This file is the multi-file project flattened. Strategy logic in the Core
// namespace is xUnit-tested separately (49 tests). Compiles to the same cBot.
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using PearlrockBots.SqueezeBreakout.Core;

namespace PearlrockBots.SqueezeBreakout.Core
{
    // ── Abstractions ─────────────────────────────────────────────────
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
        double NormalizeVolume(double units);
    }

    // ── Models ───────────────────────────────────────────────────────
    public enum Side { None = 0, Long = 1, Short = -1 }

    public readonly struct EntryDecision
    {
        public Side Direction { get; }
        public bool ShouldEnter { get; }
        public string Reason { get; }
        private EntryDecision(Side d, bool e, string r) { Direction = d; ShouldEnter = e; Reason = r; }
        public static EntryDecision NoOp(string reason = "no-op") => new EntryDecision(Side.None, false, reason);
        public static EntryDecision Enter(Side d, string r) => new EntryDecision(d, true, r);
    }

    public readonly struct ExitDecision
    {
        public bool ShouldExit { get; }
        public string Reason { get; }
        private ExitDecision(bool e, string r) { ShouldExit = e; Reason = r; }
        public static ExitDecision Stay() => new ExitDecision(false, "stay");
        public static ExitDecision Exit(string r) => new ExitDecision(true, r);
    }

    // ── Fitness ──────────────────────────────────────────────────────
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
            double avgTrade = x.TotalTrades > 0 ? x.NetProfit / x.TotalTrades : 0;

            if (x.TotalTrades < cfg.MinTotalTrades) return cfg.RejectionScore;
            if (tradesPerYear < cfg.MinTradesPerYear) return cfg.RejectionScore;
            if (x.NetProfit <= 0) return cfg.RejectionScore;
            if (ddPct > cfg.MaxDrawdownPct) return cfg.RejectionScore;
            if (avgTrade < cfg.MinAverageTradeProfit) return cfg.RejectionScore;

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
        public FitnessInput(double t, double w, double n, double d, double p, double y)
        { TotalTrades = t; WinningTrades = w; NetProfit = n; MaxEquityDrawdownPct = d; ProfitFactor = p; BacktestYears = y; }
    }

    public class FitnessConfig
    {
        public int MinTotalTrades { get; set; } = 40;
        public double MinTradesPerYear { get; set; } = 6;
        public double MaxDrawdownPct { get; set; } = 30.0;
        public double PfCap { get; set; } = 3.0;
        public double MinAverageTradeProfit { get; set; } = 10.0;
        public double RejectionScore { get; set; } = -1_000_000;
    }

    // ── SqueezeDetector ──────────────────────────────────────────────
    public class SqueezeDetector
    {
        private bool? _previousIsOn;
        public bool IsOn { get; private set; }
        public bool JustReleased { get; private set; }
        public bool HasHistory => _previousIsOn.HasValue;

        public void Update(double bbUpper, double bbLower, double kcUpper, double kcLower)
        {
            if (!IsFinite(bbUpper) || !IsFinite(bbLower) || !IsFinite(kcUpper) || !IsFinite(kcLower))
                throw new ArgumentException("All band inputs must be finite numbers.");
            if (bbUpper < bbLower) throw new ArgumentException("bbUpper must be >= bbLower.");
            if (kcUpper < kcLower) throw new ArgumentException("kcUpper must be >= kcLower.");

            bool nowOn = bbLower > kcLower && bbUpper < kcUpper;
            JustReleased = _previousIsOn == true && !nowOn;
            IsOn = nowOn;
            _previousIsOn = nowOn;
        }

        public void Reset() { IsOn = false; JustReleased = false; _previousIsOn = null; }
        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }

    // ── TrendGate ────────────────────────────────────────────────────
    public static class TrendGate
    {
        public static Side Evaluate(double price, double sma)
        {
            if (double.IsNaN(price) || double.IsInfinity(price))
                throw new ArgumentException("price must be finite", nameof(price));
            if (double.IsNaN(sma) || double.IsInfinity(sma))
                throw new ArgumentException("sma must be finite", nameof(sma));
            if (price > sma) return Side.Long;
            if (price < sma) return Side.Short;
            return Side.None;
        }
    }

    // ── MomentumProjector ────────────────────────────────────────────
    public class MomentumProjector
    {
        private readonly Queue<double> _window;
        private readonly int _windowSize;

        public MomentumProjector(int windowSize)
        {
            if (windowSize < 2) throw new ArgumentOutOfRangeException(nameof(windowSize), "must be >= 2");
            _windowSize = windowSize;
            _window = new Queue<double>(windowSize);
        }

        public int WindowSize => _windowSize;
        public int Count => _window.Count;
        public bool IsReady => _window.Count == _windowSize;

        public void Add(double momSource)
        {
            if (double.IsNaN(momSource) || double.IsInfinity(momSource))
                throw new ArgumentException("momSource must be finite", nameof(momSource));
            _window.Enqueue(momSource);
            if (_window.Count > _windowSize) _window.Dequeue();
        }

        public void Reset() => _window.Clear();

        public double Project()
        {
            if (!IsReady) throw new InvalidOperationException(
                $"Window not ready ({_window.Count}/{_windowSize}).");
            return ComputeLrProjection(_window.ToArray());
        }

        public static double ComputeMidline(double periodHigh, double periodLow, double periodSma)
            => ((periodHigh + periodLow) / 2.0 + periodSma) / 2.0;

        public static double ComputeLrProjection(IReadOnlyList<double> oldestFirstValues)
        {
            if (oldestFirstValues == null) throw new ArgumentNullException(nameof(oldestFirstValues));
            int n = oldestFirstValues.Count;
            if (n < 2) throw new ArgumentException("need at least 2 values", nameof(oldestFirstValues));

            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            for (int i = 0; i < n; i++)
            {
                double y = oldestFirstValues[i];
                if (double.IsNaN(y) || double.IsInfinity(y))
                    throw new ArgumentException($"value at index {i} is not finite");
                sumX += i;
                sumY += y;
                sumXY += (double)i * y;
                sumXX += (double)i * i;
            }
            double denom = n * sumXX - sumX * sumX;
            if (Math.Abs(denom) < 1e-12)
                throw new ArgumentException("LR denominator zero (degenerate x values)");
            double slope = (n * sumXY - sumX * sumY) / denom;
            double intercept = (sumY - slope * sumX) / n;
            return slope * (n - 1) + intercept;
        }
    }

    // ── AtrTrailingStop ──────────────────────────────────────────────
    public class AtrTrailingStop
    {
        public Side Direction { get; }
        public double Multiplier { get; }
        public double TrailLevel { get; private set; }

        public AtrTrailingStop(Side direction, double initialTrailLevel, double multiplier)
        {
            if (direction == Side.None) throw new ArgumentException("Direction must be Long or Short", nameof(direction));
            if (multiplier <= 0) throw new ArgumentOutOfRangeException(nameof(multiplier), "must be > 0");
            if (!IsFinite(initialTrailLevel)) throw new ArgumentException("initialTrailLevel must be finite", nameof(initialTrailLevel));
            Direction = direction; Multiplier = multiplier; TrailLevel = initialTrailLevel;
        }

        public ExitDecision Update(double currentPrice, double currentAtr)
        {
            if (!IsFinite(currentPrice) || currentPrice <= 0)
                throw new ArgumentException("currentPrice must be finite and > 0", nameof(currentPrice));
            if (!IsFinite(currentAtr) || currentAtr <= 0)
                throw new ArgumentException("currentAtr must be finite and > 0", nameof(currentAtr));

            double atrBuffer = currentAtr * Multiplier;
            if (Direction == Side.Long)
            {
                double candidate = currentPrice - atrBuffer;
                if (candidate > TrailLevel) TrailLevel = candidate;
                if (currentPrice < TrailLevel) return ExitDecision.Exit($"trail_hit_long@{TrailLevel:F5}");
            }
            else
            {
                double candidate = currentPrice + atrBuffer;
                if (candidate < TrailLevel) TrailLevel = candidate;
                if (currentPrice > TrailLevel) return ExitDecision.Exit($"trail_hit_short@{TrailLevel:F5}");
            }
            return ExitDecision.Stay();
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }

    // ── MomentumFadeExit ─────────────────────────────────────────────
    public static class MomentumFadeExit
    {
        public static ExitDecision Evaluate(Side direction, double currentMomentum, double previousMomentum, double fadeRatio = 0.5)
        {
            if (direction == Side.None) throw new ArgumentException("direction must be Long or Short", nameof(direction));
            if (fadeRatio <= 0 || fadeRatio >= 1) throw new ArgumentOutOfRangeException(nameof(fadeRatio), "must be in (0, 1)");
            if (double.IsNaN(currentMomentum) || double.IsInfinity(currentMomentum) ||
                double.IsNaN(previousMomentum) || double.IsInfinity(previousMomentum))
                throw new ArgumentException("momentum values must be finite");

            double sign = (int)direction;
            double alignedCurr = currentMomentum * sign;
            double alignedPrev = previousMomentum * sign;

            if (alignedCurr <= 0)
                return ExitDecision.Exit($"momentum_flipped curr={currentMomentum:F5}");
            if (alignedPrev > 0 && alignedCurr < alignedPrev && alignedCurr < fadeRatio * alignedPrev)
                return ExitDecision.Exit($"momentum_faded curr={currentMomentum:F5} prev={previousMomentum:F5}");
            return ExitDecision.Stay();
        }
    }

    // ── EntrySignal ──────────────────────────────────────────────────
    public static class EntrySignal
    {
        public static EntryDecision Evaluate(EntrySignalInput x, EntrySignalConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            if (!x.SqueezeJustReleased) return EntryDecision.NoOp("no_squeeze_release");
            Side trendSide = TrendGate.Evaluate(x.Price, x.TrendSma);
            if (trendSide == Side.None) return EntryDecision.NoOp("trend_neutral");

            double sign = (int)trendSide;
            double alignedCurr = x.MomentumCurrent * sign;
            double alignedPrev = x.MomentumPrevious * sign;

            if (alignedCurr <= 0)
                return EntryDecision.NoOp($"momentum_against_trend curr={x.MomentumCurrent:F5} side={trendSide}");
            if (alignedCurr <= alignedPrev)
                return EntryDecision.NoOp($"momentum_not_strengthening curr={x.MomentumCurrent:F5} prev={x.MomentumPrevious:F5}");

            if (trendSide == Side.Long && !cfg.AllowLongs) return EntryDecision.NoOp("longs_disabled");
            if (trendSide == Side.Short && !cfg.AllowShorts) return EntryDecision.NoOp("shorts_disabled");

            return EntryDecision.Enter(trendSide, $"squeeze_release_aligned_{trendSide}");
        }
    }

    public readonly struct EntrySignalInput
    {
        public bool SqueezeJustReleased { get; }
        public double Price { get; }
        public double TrendSma { get; }
        public double MomentumCurrent { get; }
        public double MomentumPrevious { get; }
        public EntrySignalInput(bool sqr, double price, double sma, double curr, double prev)
        { SqueezeJustReleased = sqr; Price = price; TrendSma = sma; MomentumCurrent = curr; MomentumPrevious = prev; }
    }

    public class EntrySignalConfig
    {
        public bool AllowLongs { get; set; } = true;
        public bool AllowShorts { get; set; } = true;
    }

    // ── RiskSizer ────────────────────────────────────────────────────
    public class RiskSizer
    {
        private readonly RiskSizerConfig _cfg;

        public RiskSizer(RiskSizerConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (cfg.RiskPercent <= 0) throw new ArgumentOutOfRangeException(nameof(cfg.RiskPercent));
            if (cfg.MaxLeverage <= 0) throw new ArgumentOutOfRangeException(nameof(cfg.MaxLeverage));
            if (cfg.LeverageBuffer < 0) throw new ArgumentOutOfRangeException(nameof(cfg.LeverageBuffer));
            _cfg = cfg;
        }

        public (double? volume, string rejectReason) Calculate(SizingInput x)
        {
            if (x.StopDistance <= 0) return (null, "stop_distance_invalid");
            if (x.Equity <= 0) return (null, "equity_invalid");
            if (x.CurrentPrice <= 0) return (null, "price_invalid");
            if (x.NormalizeVolume == null) return (null, "normalizer_missing");

            double riskDollars = x.Equity * (_cfg.RiskPercent / 100.0);
            double rawVolume = riskDollars / x.StopDistance;
            double effectiveLeverage = Math.Max(0, _cfg.MaxLeverage - _cfg.LeverageBuffer);
            double maxLeverageVolume = (x.Equity * effectiveLeverage) / x.CurrentPrice;
            double cappedVolume = Math.Min(rawVolume, maxLeverageVolume);
            double normalized = x.NormalizeVolume(cappedVolume);

            if (normalized < x.MinVolume) return (null, $"volume_below_min_{normalized}<{x.MinVolume}");
            if (normalized > x.MaxVolume) normalized = x.MaxVolume;
            return (normalized, null);
        }
    }

    public readonly struct SizingInput
    {
        public double Equity { get; }
        public double StopDistance { get; }
        public double CurrentPrice { get; }
        public double MinVolume { get; }
        public double MaxVolume { get; }
        public Func<double, double> NormalizeVolume { get; }
        public SizingInput(double e, double sd, double p, double minV, double maxV, Func<double, double> nv)
        { Equity = e; StopDistance = sd; CurrentPrice = p; MinVolume = minV; MaxVolume = maxV; NormalizeVolume = nv; }
    }

    public class RiskSizerConfig
    {
        public double RiskPercent { get; set; } = 0.5;
        public double MaxLeverage { get; set; } = 10.0;
        public double LeverageBuffer { get; set; } = 0.2;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// cBot
// ════════════════════════════════════════════════════════════════════════════
namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class XAUUSD_SqueezeBreakout_v1 : Robot
    {
        // Identity
        [Parameter("Bot Label", DefaultValue = "XAU_SQZ_V1", Group = "Identity")]
        public string BotLabel { get; set; }

        // Squeeze
        [Parameter("BB Period", DefaultValue = 20, MinValue = 5, Group = "Squeeze")]
        public int BBPeriod { get; set; }
        [Parameter("BB StdDev", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Squeeze")]
        public double BBStdDev { get; set; }
        [Parameter("KC Period", DefaultValue = 20, MinValue = 5, Group = "Squeeze")]
        public int KCPeriod { get; set; }
        [Parameter("KC Multiplier", DefaultValue = 1.5, MinValue = 0.5, Step = 0.1, Group = "Squeeze")]
        public double KCMultiplier { get; set; }

        // Trend
        [Parameter("Trend SMA Period", DefaultValue = 200, MinValue = 20, Group = "Trend")]
        public int TrendSmaPeriod { get; set; }

        // Momentum
        [Parameter("Momentum Window", DefaultValue = 20, MinValue = 5, Group = "Momentum")]
        public int MomentumWindow { get; set; }
        [Parameter("Momentum Fade Ratio (0..1)", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 0.95, Step = 0.05, Group = "Momentum")]
        public double MomentumFadeRatio { get; set; }

        // ATR / Risk
        [Parameter("ATR Period", DefaultValue = 14, MinValue = 2, Group = "Risk")]
        public int AtrPeriod { get; set; }
        [Parameter("SL ATR Multiple", DefaultValue = 3.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }
        [Parameter("TP ATR Multiple", DefaultValue = 6.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double TpAtrMultiple { get; set; }
        [Parameter("Trail ATR Multiple", DefaultValue = 3.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double TrailAtrMultiple { get; set; }
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }
        [Parameter("Max Leverage", DefaultValue = 10.0, MinValue = 1.0, Step = 0.5, Group = "Risk")]
        public double MaxLeverage { get; set; }
        [Parameter("Leverage Buffer", DefaultValue = 0.2, MinValue = 0.0, Step = 0.05, Group = "Risk")]
        public double LeverageBuffer { get; set; }

        // Direction
        [Parameter("Allow Longs",  DefaultValue = true,  Group = "Direction")]
        public bool AllowLongs { get; set; }
        [Parameter("Allow Shorts", DefaultValue = true,  Group = "Direction")]
        public bool AllowShorts { get; set; }

        // Trade Control
        [Parameter("Trade Start (HH:mm UTC)", DefaultValue = "00:00", Group = "Trade Control")]
        public string TradeStart { get; set; }
        [Parameter("Trade End (HH:mm UTC)", DefaultValue = "23:59", Group = "Trade Control")]
        public string TradeEnd { get; set; }
        [Parameter("Max Trade Duration (hours, 0=off)", DefaultValue = 0, MinValue = 0, MaxValue = 240, Group = "Trade Control")]
        public int MaxTradeHours { get; set; }
        [Parameter("Cooldown Minutes After Exit", DefaultValue = 0, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        // Day Filter
        [Parameter("Use Day Filter", DefaultValue = false, Group = "Day Filter")]
        public bool UseDayFilter { get; set; }
        [Parameter("Trade Monday",    DefaultValue = true, Group = "Day Filter")] public bool TradeMonday    { get; set; }
        [Parameter("Trade Tuesday",   DefaultValue = true, Group = "Day Filter")] public bool TradeTuesday   { get; set; }
        [Parameter("Trade Wednesday", DefaultValue = true, Group = "Day Filter")] public bool TradeWednesday { get; set; }
        [Parameter("Trade Thursday",  DefaultValue = true, Group = "Day Filter")] public bool TradeThursday  { get; set; }
        [Parameter("Trade Friday",    DefaultValue = true, Group = "Day Filter")] public bool TradeFriday    { get; set; }

        // Safety
        [Parameter("Use Spread Filter", DefaultValue = false, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }
        [Parameter("Max Spread", DefaultValue = 0.5, Step = 0.05, Group = "Safety")]
        public double MaxSpread { get; set; }

        // Fitness
        [Parameter("Min Total Trades", DefaultValue = 40, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }
        [Parameter("Min Trades Per Year", DefaultValue = 6.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }
        [Parameter("Max Fitness DD %", DefaultValue = 30.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }
        [Parameter("Backtest Years", DefaultValue = 14.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }
        [Parameter("Fitness PF Cap", DefaultValue = 3.0, MinValue = 1.0, Step = 0.1, Group = "Fitness")]
        public double FitnessPfCap { get; set; }
        [Parameter("Min Average Trade ($)", DefaultValue = 10.0, MinValue = 0, Step = 1, Group = "Fitness")]
        public double MinAverageTradeProfit { get; set; }

        // Logging
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        // cAlgo indicators
        private BollingerBands _bb;
        private KeltnerChannels _kc;
        private SimpleMovingAverage _trendSma;
        private AverageTrueRange _atr;
        private SimpleMovingAverage _midlineSma;

        // Core
        private SqueezeDetector _squeeze;
        private MomentumProjector _momentum;
        private EntrySignalConfig _entryCfg;
        private RiskSizer _riskSizer;
        private AtrTrailingStop _trail;
        private double _takeProfitPrice;
        private double _previousMomentum;
        private bool _hasPreviousMomentum;
        private DateTime _lastExitTime = DateTime.MinValue;

        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Hour)
            {
                Print($"ABORT: Must run on H1. Current = {Bars.TimeFrame}");
                Stop(); return;
            }

            _bb = Indicators.BollingerBands(Bars.ClosePrices, BBPeriod, BBStdDev, MovingAverageType.Simple);
            _kc = Indicators.KeltnerChannels(KCPeriod, MovingAverageType.Simple,
                                             KCPeriod, MovingAverageType.Simple,
                                             KCMultiplier);
            _trendSma = Indicators.SimpleMovingAverage(Bars.ClosePrices, TrendSmaPeriod);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            _midlineSma = Indicators.SimpleMovingAverage(Bars.ClosePrices, MomentumWindow);

            _squeeze = new SqueezeDetector();
            _momentum = new MomentumProjector(MomentumWindow);
            _entryCfg = new EntrySignalConfig { AllowLongs = AllowLongs, AllowShorts = AllowShorts };
            _riskSizer = new RiskSizer(new RiskSizerConfig
            {
                RiskPercent = RiskPercent,
                MaxLeverage = MaxLeverage,
                LeverageBuffer = LeverageBuffer
            });

            Positions.Closed += OnPositionClosed;

            Print($"=== {BotLabel} (v3 squeeze breakout) on {SymbolName} H1 ===");
            Print($"BB={BBPeriod}/{BBStdDev:F1}σ  KC={KCPeriod}/{KCMultiplier:F1}  Trend SMA={TrendSmaPeriod}");
            Print($"Mom window={MomentumWindow}  fadeRatio={MomentumFadeRatio}");
            Print($"ATR={AtrPeriod}  SL={SlAtrMultiple}×  TP={TpAtrMultiple}×  Trail={TrailAtrMultiple}×");
            Print($"Risk={RiskPercent}%  MaxLev={MaxLeverage}x  LevBuf={LeverageBuffer}");
            Print($"Allow Longs={AllowLongs}  Shorts={AllowShorts}");
        }

        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Hour) return;
            if (!IndicatorsReady()) return;

            double bbUpper = _bb.Top.Last(1);
            double bbLower = _bb.Bottom.Last(1);
            double kcUpper = _kc.Top.Last(1);
            double kcLower = _kc.Bottom.Last(1);
            _squeeze.Update(bbUpper, bbLower, kcUpper, kcLower);

            double price = Bars.ClosePrices.Last(1);
            double midline = MomentumProjector.ComputeMidline(
                HighestHighOverWindow(), LowestLowOverWindow(), _midlineSma.Result.Last(1));
            _momentum.Add(price - midline);
            if (!_momentum.IsReady) return;
            double momCurr = _momentum.Project();

            var open = OpenPosition();
            if (open != null)
            {
                ManageOpenPosition(open, price, momCurr);
                _previousMomentum = momCurr;
                _hasPreviousMomentum = true;
                return;
            }

            if (CheckCooldown() || !PassesDayFilter() || !IsInTradeHours() || !SpreadOk())
            {
                _previousMomentum = momCurr; _hasPreviousMomentum = true; return;
            }

            if (!_hasPreviousMomentum)
            {
                _previousMomentum = momCurr; _hasPreviousMomentum = true; return;
            }

            var entryInput = new EntrySignalInput(
                _squeeze.JustReleased, price, _trendSma.Result.Last(1),
                momCurr, _previousMomentum);
            var decision = EntrySignal.Evaluate(entryInput, _entryCfg);
            if (VerboseLogging && _squeeze.JustReleased)
                Log($"SIGNAL release ({decision.Direction}) — {decision.Reason}");

            if (decision.ShouldEnter) PlaceEntry(decision.Direction);

            _previousMomentum = momCurr;
            _hasPreviousMomentum = true;
        }

        private void ManageOpenPosition(Position pos, double price, double currMomentum)
        {
            if ((pos.TradeType == TradeType.Buy && price >= _takeProfitPrice) ||
                (pos.TradeType == TradeType.Sell && price <= _takeProfitPrice))
            {
                Print($"TP HIT @ {price:F2} | net={pos.NetProfit:F2}");
                ClosePosition(pos); return;
            }

            double atrNow = _atr.Result.LastValue;
            if (_trail != null && atrNow > 0)
            {
                var trailDecision = _trail.Update(price, atrNow);
                if (trailDecision.ShouldExit)
                {
                    Print($"TRAIL @ {price:F2} | {trailDecision.Reason} | net={pos.NetProfit:F2}");
                    ClosePosition(pos); return;
                }
            }

            if (_hasPreviousMomentum)
            {
                var side = pos.TradeType == TradeType.Buy ? Side.Long : Side.Short;
                var fadeDecision = MomentumFadeExit.Evaluate(side, currMomentum, _previousMomentum, MomentumFadeRatio);
                if (fadeDecision.ShouldExit)
                {
                    Print($"FADE | {fadeDecision.Reason} | net={pos.NetProfit:F2}");
                    ClosePosition(pos); return;
                }
            }

            if (MaxTradeHours > 0)
            {
                var hoursOpen = (Server.Time - pos.EntryTime).TotalHours;
                if (hoursOpen >= MaxTradeHours)
                {
                    Print($"MAX DURATION EXIT | {hoursOpen:F1}h | net={pos.NetProfit:F2}");
                    ClosePosition(pos);
                }
            }
        }

        private void PlaceEntry(Side direction)
        {
            double atrNow = _atr.Result.LastValue;
            if (atrNow <= 0) { Log("ENTRY BLOCKED — atr_not_ready"); return; }

            double stopDist = atrNow * SlAtrMultiple;
            double tpDist = atrNow * TpAtrMultiple;
            double currentPrice = direction == Side.Long ? Symbol.Ask : Symbol.Bid;

            var sizing = new SizingInput(
                Account.Equity, stopDist, currentPrice,
                Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsMax,
                u => Symbol.NormalizeVolumeInUnits(u, RoundingMode.Down));

            var (volume, reject) = _riskSizer.Calculate(sizing);
            if (volume == null) { Log($"ENTRY BLOCKED — {reject}"); return; }

            double slPips = stopDist / Symbol.PipSize;
            double tpPips = tpDist / Symbol.PipSize;
            var tradeType = direction == Side.Long ? TradeType.Buy : TradeType.Sell;

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume.Value, BotLabel, slPips, tpPips);
            if (!result.IsSuccessful) { Print($"ORDER FAILED: {result.Error}"); return; }

            double entry = result.Position.EntryPrice;
            _takeProfitPrice = direction == Side.Long ? entry + tpDist : entry - tpDist;
            double initialTrail = direction == Side.Long
                ? entry - atrNow * TrailAtrMultiple
                : entry + atrNow * TrailAtrMultiple;
            _trail = new AtrTrailingStop(direction, initialTrail, TrailAtrMultiple);

            Print($"[{Server.Time:yyyy-MM-dd HH:mm}] {direction} OPEN @ {entry:F2} | " +
                  $"vol={volume:F2} sl={slPips:F1}p tp={tpPips:F1}p trail={initialTrail:F2}");
        }

        private bool IndicatorsReady() =>
            _bb.Top.Count > BBPeriod + 5 &&
            _kc.Top.Count > KCPeriod + 5 &&
            _trendSma.Result.Count > TrendSmaPeriod + 5 &&
            _atr.Result.Count > AtrPeriod + 5 &&
            _midlineSma.Result.Count > MomentumWindow + 5 &&
            Bars.Count > MomentumWindow + 5;

        private double HighestHighOverWindow()
        {
            double max = double.MinValue;
            for (int i = 1; i <= MomentumWindow; i++)
            {
                double h = Bars.HighPrices.Last(i);
                if (h > max) max = h;
            }
            return max;
        }

        private double LowestLowOverWindow()
        {
            double min = double.MaxValue;
            for (int i = 1; i <= MomentumWindow; i++)
            {
                double l = Bars.LowPrices.Last(i);
                if (l < min) min = l;
            }
            return min;
        }

        private Position OpenPosition() =>
            Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private bool CheckCooldown() =>
            CooldownMinutes > 0 && _lastExitTime != DateTime.MinValue &&
            Server.Time < _lastExitTime.AddMinutes(CooldownMinutes);

        private bool PassesDayFilter()
        {
            if (!UseDayFilter) return true;
            switch (Server.Time.DayOfWeek)
            {
                case DayOfWeek.Monday: return TradeMonday;
                case DayOfWeek.Tuesday: return TradeTuesday;
                case DayOfWeek.Wednesday: return TradeWednesday;
                case DayOfWeek.Thursday: return TradeThursday;
                case DayOfWeek.Friday: return TradeFriday;
                default: return false;
            }
        }

        private bool IsInTradeHours()
        {
            if (string.IsNullOrWhiteSpace(TradeStart) || string.IsNullOrWhiteSpace(TradeEnd)) return true;
            if (!TimeSpan.TryParse(TradeStart, out var s) || !TimeSpan.TryParse(TradeEnd, out var e)) return true;
            var t = Server.Time.TimeOfDay;
            if (s <= e) return t >= s && t < e;
            return t >= s || t < e;
        }

        private bool SpreadOk() => !UseSpreadFilter || (Symbol.Ask - Symbol.Bid) <= MaxSpread;

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName || args.Position.Label != BotLabel) return;
            _lastExitTime = Server.Time;
            _trail = null;
            _takeProfitPrice = 0;
        }

        private void Log(string msg) { if (VerboseLogging) Print(msg); }

        protected override double GetFitness(GetFitnessArgs args)
        {
            return Fitness.Compute(
                new FitnessInput(
                    args.TotalTrades, args.WinningTrades, args.NetProfit,
                    args.MaxEquityDrawdownPercentages, args.ProfitFactor, BacktestYears),
                new FitnessConfig
                {
                    MinTotalTrades = MinTotalTrades,
                    MinTradesPerYear = MinTradesPerYear,
                    MaxDrawdownPct = MaxFitnessDrawdownPct,
                    PfCap = FitnessPfCap,
                    MinAverageTradeProfit = MinAverageTradeProfit
                });
        }
    }
}
