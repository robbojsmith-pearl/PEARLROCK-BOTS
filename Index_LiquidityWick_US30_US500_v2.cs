using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  Index Liquidity Wick v2 — US30 / US500
    //  Pearlrock Systematic
    //
    //  CHANGELOG vs v1:
    //  - FIX: Outside-bar two-sided sweeps now skipped (not coin-flipped)
    //  - FIX: Partial close + BE shift split — BE fires even if partial vol too small
    //  - FIX: Soft penalty in fitness function (no cliffs at DD/trade thresholds)
    //  - FIX: Cross-midnight NoTradeFirstMinutes guard handles wrap correctly
    //  - FIX: Session anchored in NY time (not London) — handles US/UK DST mismatch
    //  - FIX: Geometry sanity check at OnStart (catches impossible param combos)
    //  - ADD: Expected-loss verification log on every trade (sizing audit trail)
    //  - ADD: Outside-bar diagnostic logging
    //  - DEFAULTS: Reset to sensible US30-baseline values, NOT optimised
    //
    //  Designed for:
    //  - US30 primarily (US500 secondary — adjust MaxStopPoints/MaxSpreadPoints)
    //  - M5 / M15 / M30 charts
    //  - NY cash session
    //
    //  IMPORTANT — RAW BASELINE WORKFLOW:
    //  1. Run with ALL filters off (UseAdxFilter=false, UseD1TrendFilter=false,
    //     UseDayFilter=false, UsePartialClose=false, UseTrailingStop=false)
    //  2. Verify [SIZE-AUDIT] line shows expected_loss ≈ risk_money for first trade
    //  3. Run M5, M15, M30 separately to see where edge concentrates
    //  4. THEN start single-filter testing
    // ═══════════════════════════════════════════════════════════════════

    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC)]
    public class Index_LiquidityWick_US30_US500_v2 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "IDX_LIQ_WICK_V2", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Direction ────────────────────────────────────────────────
        [Parameter("Allow Longs", DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

        // ── Structure ────────────────────────────────────────────────
        [Parameter("Use Pro Structure Swings", DefaultValue = true, Group = "Structure")]
        public bool UseProStructure { get; set; }

        [Parameter("Swing Left Bars", DefaultValue = 3, MinValue = 1, Group = "Structure")]
        public int SwingLeft { get; set; }

        [Parameter("Swing Right Bars", DefaultValue = 3, MinValue = 1, Group = "Structure")]
        public int SwingRight { get; set; }

        [Parameter("Max Swing Age Bars", DefaultValue = 200, MinValue = 20, Group = "Structure")]
        public int MaxSwingAgeBars { get; set; }

        [Parameter("Min Swing Distance Points", DefaultValue = 0.0, MinValue = 0.0, Step = 0.5, Group = "Structure")]
        public double MinSwingDistancePoints { get; set; }

        [Parameter("Min Swing Distance ATR", DefaultValue = 1.50, MinValue = 0.0, Step = 0.05, Group = "Structure")]
        public double MinSwingDistanceAtr { get; set; }

        [Parameter("Sweep Buffer Points", DefaultValue = 0.0, MinValue = 0.0, Step = 0.5, Group = "Structure")]
        public double SweepBufferPoints { get; set; }

        [Parameter("Sweep Buffer ATR Fraction", DefaultValue = 0.05, MinValue = 0.0, Step = 0.01, Group = "Structure")]
        public double SweepBufferAtrFrac { get; set; }

        // ── Candle / Wick Quality ────────────────────────────────────
        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "Pattern")]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Avg Period", DefaultValue = 50, MinValue = 2, Group = "Pattern")]
        public int AtrAvgPeriod { get; set; }

        [Parameter("Range >= ATR *", DefaultValue = 0.80, MinValue = 0.05, Step = 0.05, Group = "Pattern")]
        public double RangeAtrMultiplier { get; set; }

        [Parameter("ATR >= AvgATR *", DefaultValue = 0.70, MinValue = 0.05, Step = 0.05, Group = "Pattern")]
        public double AtrComparisonMultiplier { get; set; }

        [Parameter("Max Body % of Range", DefaultValue = 0.40, MinValue = 0.05, MaxValue = 0.95, Step = 0.01, Group = "Pattern")]
        public double MaxBodyPctOfRange { get; set; }

        [Parameter("Min Dominant Wick %", DefaultValue = 0.40, MinValue = 0.05, MaxValue = 0.95, Step = 0.01, Group = "Pattern")]
        public double MinDominantWickPctOfRange { get; set; }

        [Parameter("Wick Dominance Ratio", DefaultValue = 1.50, MinValue = 0.1, Step = 0.05, Group = "Pattern")]
        public double WickDominanceRatio { get; set; }

        [Parameter("Close Location Threshold", DefaultValue = 0.60, MinValue = 0.05, MaxValue = 0.95, Step = 0.05, Group = "Pattern")]
        public double CloseLocationThreshold { get; set; }

        [Parameter("Require Close Back Inside", DefaultValue = true, Group = "Pattern")]
        public bool RequireCloseBackInside { get; set; }

        // ── Regime Filter ────────────────────────────────────────────
        [Parameter("Use ADX Filter", DefaultValue = false, Group = "Regime")]
        public bool UseAdxFilter { get; set; }

        [Parameter("ADX Period", DefaultValue = 14, MinValue = 2, Group = "Regime")]
        public int AdxPeriod { get; set; }

        [Parameter("Max ADX to Trade", DefaultValue = 34.0, MinValue = 1.0, Step = 0.5, Group = "Regime")]
        public double MaxAdxToTrade { get; set; }

        // ── Higher Timeframe Trend Filter ────────────────────────────
        [Parameter("Use D1 Trend Filter", DefaultValue = false, Group = "HTF Trend")]
        public bool UseD1TrendFilter { get; set; }

        [Parameter("D1 EMA Period", DefaultValue = 50, MinValue = 5, Group = "HTF Trend")]
        public int D1EmaPeriod { get; set; }

        [Parameter("Trend Mode", DefaultValue = "WithTrend", Group = "HTF Trend")]
        public string TrendMode { get; set; }

        // ── Trade Control / Session ──────────────────────────────────
        // NOTE: Times are now interpreted in NY time (Eastern), NOT London.
        // This avoids US/UK DST mismatch (~2 weeks/year offset bug).
        // NY cash open = 9:30 ET, close = 16:00 ET
        [Parameter("Trade Start HH:mm NY", DefaultValue = "09:30", Group = "Trade Control")]
        public string TradeStart { get; set; }

        [Parameter("Trade End HH:mm NY", DefaultValue = "16:00", Group = "Trade Control")]
        public string TradeEnd { get; set; }

        [Parameter("No Trade First Minutes", DefaultValue = 15, MinValue = 0, MaxValue = 180, Step = 5, Group = "Trade Control")]
        public int NoTradeFirstMinutes { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 3, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 60, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        [Parameter("Min Bars Since Last Signal", DefaultValue = 3, MinValue = 1, Group = "Trade Control")]
        public int MinBarsSinceSignal { get; set; }

        // ── Day Filter ───────────────────────────────────────────────
        [Parameter("Use Day Filter", DefaultValue = false, Group = "Day Filter")]
        public bool UseDayFilter { get; set; }

        [Parameter("Trade Monday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeMonday { get; set; }

        [Parameter("Trade Tuesday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeTuesday { get; set; }

        [Parameter("Trade Wednesday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeWednesday { get; set; }

        [Parameter("Trade Thursday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeThursday { get; set; }

        [Parameter("Trade Friday", DefaultValue = false, Group = "Day Filter")]
        public bool TradeFriday { get; set; }

        // ── Risk ─────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.25, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Stop Loss ATR Mult", DefaultValue = 1.50, MinValue = 0.10, Step = 0.05, Group = "Risk")]
        public double StopLossAtrMult { get; set; }

        [Parameter("Take Profit ATR Mult", DefaultValue = 2.00, MinValue = 0.10, Step = 0.05, Group = "Risk")]
        public double TakeProfitAtrMult { get; set; }

        [Parameter("Min Stop Points 0=off", DefaultValue = 0.0, MinValue = 0.0, Step = 0.5, Group = "Risk")]
        public double MinStopPoints { get; set; }

        // US30 default cap = 150pts (~150 index points).
        // For US500, set this to ~20.
        [Parameter("Max Stop Points 0=off", DefaultValue = 150.0, MinValue = 0.0, Step = 1.0, Group = "Risk")]
        public double MaxStopPoints { get; set; }

        [Parameter("Volume Cap Units 0=off", DefaultValue = 0, MinValue = 0, Step = 1, Group = "Risk")]
        public int VolumeCapUnits { get; set; }

        [Parameter("Skip If Volume Hits Cap", DefaultValue = false, Group = "Risk")]
        public bool SkipIfVolumeHitsCap { get; set; }

        [Parameter("Max Margin Usage %", DefaultValue = 25.0, MinValue = 1.0, Step = 1.0, Group = "Risk")]
        public double MaxMarginUsagePct { get; set; }

        [Parameter("Max Trade Hours 0=off", DefaultValue = 4, MinValue = 0, MaxValue = 48, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        // ── Partial Close ────────────────────────────────────────────
        [Parameter("Use Partial Close at 1R", DefaultValue = false, Group = "Partial Close")]
        public bool UsePartialClose { get; set; }

        [Parameter("Partial Close %", DefaultValue = 50, MinValue = 10, MaxValue = 90, Step = 5, Group = "Partial Close")]
        public int PartialClosePct { get; set; }

        [Parameter("BE Offset Points", DefaultValue = 0.0, MinValue = 0.0, Step = 0.5, Group = "Partial Close")]
        public double BeOffsetPoints { get; set; }

        [Parameter("BE-Only at 1R (no partial)", DefaultValue = false, Group = "Partial Close")]
        public bool BeOnlyAt1R { get; set; }

        // ── Circuit Breaker ──────────────────────────────────────────
        [Parameter("Daily Loss Limit % 0=off", DefaultValue = 2.0, MinValue = 0.0, Step = 0.1, Group = "Circuit Breaker")]
        public double DailyLossLimitPct { get; set; }

        // ── Safety ───────────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = true, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        // US30 default = 8pts. For US500, set to ~1.0.
        [Parameter("Max Spread Points", DefaultValue = 8.0, MinValue = 0.0, Step = 0.5, Group = "Safety")]
        public double MaxSpreadPoints { get; set; }

        // ── Trailing ─────────────────────────────────────────────────
        [Parameter("Use Trailing Stop", DefaultValue = false, Group = "Trailing")]
        public bool UseTrailingStop { get; set; }

        [Parameter("Trail After +1R", DefaultValue = true, Group = "Trailing")]
        public bool TrailAfterOneR { get; set; }

        [Parameter("Trailing ATR Period", DefaultValue = 14, MinValue = 1, Group = "Trailing")]
        public int TrailingAtrPeriod { get; set; }

        [Parameter("Trail Distance ATR Mult", DefaultValue = 1.40, MinValue = 0.10, Step = 0.05, Group = "Trailing")]
        public double TrailingStopAtrMult { get; set; }

        // ── Fitness ──────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 80, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 20, MinValue = 1, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 15.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        // IMPORTANT: Set this to match your actual IS window length.
        // Affects tradesPerYear calc and fitness ranking.
        [Parameter("Backtest Years", DefaultValue = 5.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }

        // ── Logging ──────────────────────────────────────────────────
        [Parameter("Log Signals Only", DefaultValue = false, Group = "Logging")]
        public bool LogSignalsOnly { get; set; }

        [Parameter("Log Sizing Details", DefaultValue = true, Group = "Logging")]
        public bool LogSizingDetails { get; set; }

        [Parameter("Log Skip Reasons", DefaultValue = false, Group = "Logging")]
        public bool LogSkipReasons { get; set; }

        // ── Indicators ───────────────────────────────────────────────
        private AverageTrueRange _atr;
        private AverageTrueRange _atrAvg;
        private AverageTrueRange _atrTrail;
        private DirectionalMovementSystem _dms;
        private Bars _d1Bars;
        private ExponentialMovingAverage _d1Ema;

        // ── State ────────────────────────────────────────────────────
        private DateTime _lastExitTime = DateTime.MinValue;
        private DateTime _lastNyDay = DateTime.MinValue;
        private int _tradesToday = 0;
        private int _lastSignalBarIndex = -1;
        private double _startOfDayEquity = 0;
        private bool _circuitBroken = false;
        private long _partialClosedPosId = -1;
        private long _beShiftedPosId = -1;  // FIX: track BE shift independently of partial
        private bool _firstTradeAudited = false;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            // FIX: Geometry sanity check — catch impossible param combos at startup
            if (MaxBodyPctOfRange + MinDominantWickPctOfRange > 1.0)
            {
                Print($"⚠️  CONFIG WARNING: MaxBodyPctOfRange ({MaxBodyPctOfRange:P0}) + " +
                      $"MinDominantWickPctOfRange ({MinDominantWickPctOfRange:P0}) > 100%. " +
                      $"Geometrically impossible. NO TRADES WILL FIRE. Stop and fix params.");
                Stop();
                return;
            }

            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _atrAvg = Indicators.AverageTrueRange(AtrAvgPeriod, MovingAverageType.Exponential);
            _atrTrail = Indicators.AverageTrueRange(TrailingAtrPeriod, MovingAverageType.Exponential);
            _dms = Indicators.DirectionalMovementSystem(AdxPeriod);

            if (UseD1TrendFilter)
            {
                _d1Bars = MarketData.GetBars(TimeFrame.Daily);
                _d1Ema = Indicators.ExponentialMovingAverage(_d1Bars.ClosePrices, D1EmaPeriod);
            }

            _startOfDayEquity = Account.Equity;
            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} v2 started on {SymbolName} ({Bars.TimeFrame}) ═══");
            Print($"SPECS  | PipSize={Symbol.PipSize} | PipValue={Symbol.PipValue} | LotSize={Symbol.LotSize} | VolMin={Symbol.VolumeInUnitsMin} | VolStep={Symbol.VolumeInUnitsStep} | VolMax={Symbol.VolumeInUnitsMax}");
            Print($"SESSION| {TradeStart}-{TradeEnd} NY (anchored to ET, not London) | NoTradeFirst={NoTradeFirstMinutes}m | MaxTrades={MaxTradesPerDay} | Cooldown={CooldownMinutes}m");
            Print($"PATTERN| RangeATR>={RangeAtrMultiplier} | Body<={MaxBodyPctOfRange:P0} | Wick>={MinDominantWickPctOfRange:P0} | WickDom={WickDominanceRatio:F2} | CloseLoc={CloseLocationThreshold:F2}");
            Print($"RISK   | Risk={RiskPercent}% | SL={StopLossAtrMult}ATR | TP={TakeProfitAtrMult}ATR | MaxStop={MaxStopPoints}pts | MaxMargin={MaxMarginUsagePct}% | SpreadMax={MaxSpreadPoints}pts");
            Print($"FILTERS| ADX={UseAdxFilter} | D1Trend={UseD1TrendFilter}({TrendMode}) | DayFilter={UseDayFilter} | Longs={AllowLongs} Shorts={AllowShorts}");
            Print($"FITNESS| MinTrades={MinTotalTrades} | MinPerYear={MinTradesPerYear} | MaxDD={MaxFitnessDrawdownPct}% | BacktestYears={BacktestYears}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            var ny = NyNow();

            ResetDailyStateIfNeeded(ny);
            ManageOpenPositions();

            if (CheckCircuitBreaker()) return;
            if (!PassesDayFilter(ny)) return;
            if (!InTradeHours(ny)) return;
            if (!SafetyOk()) return;
            if (!IndicatorsReady()) return;

            if (_lastExitTime != DateTime.MinValue && Server.Time < _lastExitTime.AddMinutes(CooldownMinutes))
                return;

            if (_tradesToday >= MaxTradesPerDay) return;
            if (HasOpenPosition()) return;

            int prev = Bars.Count - 2;
            if (prev < 50) return;

            if (_lastSignalBarIndex >= 0 && (Bars.Count - 1 - _lastSignalBarIndex) < MinBarsSinceSignal)
                return;

            if (TryGetSignal(prev, out var dir, out var info))
            {
                _lastSignalBarIndex = Bars.Count - 1;
                Print($"[{Server.Time:yyyy-MM-dd HH:mm}] SIGNAL {dir.ToString().ToUpper()} | {info}");
                ExecuteEntry(dir, prev);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT
        // ─────────────────────────────────────────────────────────────
        private void ManageOpenPositions()
        {
            foreach (var pos in Positions.Where(p => p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
            {
                if (MaxTradeHours > 0)
                {
                    var hoursOpen = (Server.Time - pos.EntryTime).TotalHours;
                    if (hoursOpen >= MaxTradeHours)
                    {
                        Print($"MAX DURATION EXIT | {hoursOpen:F1}h | Net={pos.NetProfit:F2}");
                        ClosePosition(pos);
                        continue;
                    }
                }

                // FIX: Partial close and BE shift now independent
                if ((UsePartialClose || BeOnlyAt1R) && pos.StopLoss.HasValue)
                {
                    double oneR = Math.Abs(pos.EntryPrice - pos.StopLoss.Value);
                    bool hitOneR = pos.TradeType == TradeType.Buy
                        ? Symbol.Bid >= pos.EntryPrice + oneR
                        : Symbol.Ask <= pos.EntryPrice - oneR;

                    if (hitOneR)
                    {
                        // Try partial close (only if requested AND volume large enough)
                        if (UsePartialClose && _partialClosedPosId != pos.Id)
                        {
                            double closeVolume = Symbol.NormalizeVolumeInUnits(
                                pos.VolumeInUnits * (PartialClosePct / 100.0),
                                RoundingMode.Down);

                            if (closeVolume >= Symbol.VolumeInUnitsMin)
                            {
                                ClosePosition(pos, closeVolume);
                                _partialClosedPosId = pos.Id;
                                Print($"PARTIAL CLOSE {PartialClosePct}% on pos {pos.Id} | closeVol={closeVolume:F0}");
                            }
                            else if (LogSizingDetails)
                            {
                                Print($"PARTIAL SKIPPED — vol {closeVolume:F2} < min {Symbol.VolumeInUnitsMin:F2} | BE shift will still attempt");
                            }
                        }

                        // Independently shift to BE (regardless of whether partial fired)
                        if (_beShiftedPosId != pos.Id)
                        {
                            double bePrice = pos.TradeType == TradeType.Buy
                                ? pos.EntryPrice + BeOffsetPoints
                                : pos.EntryPrice - BeOffsetPoints;

                            bool improve = !pos.StopLoss.HasValue ||
                                (pos.TradeType == TradeType.Buy && bePrice > pos.StopLoss.Value) ||
                                (pos.TradeType == TradeType.Sell && bePrice < pos.StopLoss.Value);

                            if (improve)
                            {
                                pos.ModifyStopLossPrice(bePrice);
                                _beShiftedPosId = pos.Id;
                                Print($"BE SHIFT on pos {pos.Id} | SL → {bePrice:F2}");
                            }
                        }
                    }
                }

                if (UseTrailingStop)
                    TrailPosition(pos);
            }
        }

        private void TrailPosition(Position pos)
        {
            if (Bars.Count < TrailingAtrPeriod + 5) return;

            int lastBar = Bars.Count - 2;
            double atrTrail = _atrTrail.Result[lastBar];
            if (atrTrail <= 0) return;

            double trailDist = atrTrail * TrailingStopAtrMult;
            double lastClose = Bars.ClosePrices[lastBar];

            if (TrailAfterOneR && pos.StopLoss.HasValue)
            {
                double oneR = Math.Abs(pos.EntryPrice - pos.StopLoss.Value);
                if (oneR > 0)
                {
                    if (pos.TradeType == TradeType.Buy && (lastClose - pos.EntryPrice) < oneR) return;
                    if (pos.TradeType == TradeType.Sell && (pos.EntryPrice - lastClose) < oneR) return;
                }
            }

            double newSL;
            bool improve;

            if (pos.TradeType == TradeType.Buy)
            {
                newSL = lastClose - trailDist;
                improve = !pos.StopLoss.HasValue || newSL > pos.StopLoss.Value;
            }
            else
            {
                newSL = lastClose + trailDist;
                improve = !pos.StopLoss.HasValue || newSL < pos.StopLoss.Value;
            }

            if (improve)
            {
                pos.ModifyStopLossPrice(newSL);
                if (!LogSignalsOnly)
                    Print($"TRAIL {pos.TradeType} → SL={newSL:F2} | LastClose={lastClose:F2}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  SIGNAL DETECTION
        // ─────────────────────────────────────────────────────────────
        private bool TryGetSignal(int barIndex, out TradeType direction, out string info)
        {
            direction = TradeType.Buy;
            info = "";

            if (UseAdxFilter && _dms.ADX[barIndex] > MaxAdxToTrade)
            {
                Skip($"ADX {_dms.ADX[barIndex]:F1} > {MaxAdxToTrade:F1}");
                return false;
            }

            double high = Bars.HighPrices[barIndex];
            double low = Bars.LowPrices[barIndex];
            double open = Bars.OpenPrices[barIndex];
            double close = Bars.ClosePrices[barIndex];
            double range = high - low;
            if (range <= 0) return false;

            double body = Math.Abs(close - open);
            double bodyPct = body / range;
            double upperWick = high - Math.Max(open, close);
            double lowerWick = Math.Min(open, close) - low;
            double closePos = (close - low) / range;

            bool buyCloseStrong = closePos >= CloseLocationThreshold;
            bool sellCloseStrong = closePos <= (1.0 - CloseLocationThreshold);

            double atr = _atr.Result[barIndex];
            double avgAtr = _atrAvg.Result[barIndex];
            if (atr <= 0 || avgAtr <= 0) return false;

            if (range < atr * RangeAtrMultiplier)
            {
                Skip($"range {range:F2} < ATR*{RangeAtrMultiplier:F2} {(atr * RangeAtrMultiplier):F2}");
                return false;
            }

            if (atr < avgAtr * AtrComparisonMultiplier)
            {
                Skip($"ATR {atr:F2} < AvgATR*{AtrComparisonMultiplier:F2} {(avgAtr * AtrComparisonMultiplier):F2}");
                return false;
            }

            if (bodyPct > MaxBodyPctOfRange)
            {
                Skip($"body {bodyPct:P0} > max {MaxBodyPctOfRange:P0}");
                return false;
            }

            // Wick dominance: dominant wick must be ≥ MinDominantWickPctOfRange of total range
            // AND must be ≥ WickDominanceRatio × the other wick
            bool sellWickDominant =
                (upperWick / range) >= MinDominantWickPctOfRange &&
                upperWick >= lowerWick * WickDominanceRatio;

            bool buyWickDominant =
                (lowerWick / range) >= MinDominantWickPctOfRange &&
                lowerWick >= upperWick * WickDominanceRatio;

            double swingHigh, swingLow;
            int swingHighIndex, swingLowIndex;

            if (UseProStructure)
            {
                if (!TryFindLastSwingHigh(barIndex, out swingHigh, out swingHighIndex))
                {
                    Skip("no swing high");
                    return false;
                }

                if (!TryFindLastSwingLow(barIndex, out swingLow, out swingLowIndex))
                {
                    Skip("no swing low");
                    return false;
                }

                double requiredDist = Math.Max(MinSwingDistancePoints, MinSwingDistanceAtr * atr);
                if ((swingHigh - swingLow) < requiredDist)
                {
                    Skip($"swing range {(swingHigh - swingLow):F2} < required {requiredDist:F2}");
                    return false;
                }
            }
            else
            {
                int earliest = Math.Max(0, barIndex - MaxSwingAgeBars);
                swingHighIndex = barIndex - 1;
                swingLowIndex = barIndex - 1;
                swingHigh = double.MinValue;
                swingLow = double.MaxValue;

                for (int i = earliest; i <= barIndex - 1; i++)
                {
                    if (Bars.HighPrices[i] > swingHigh) { swingHigh = Bars.HighPrices[i]; swingHighIndex = i; }
                    if (Bars.LowPrices[i] < swingLow) { swingLow = Bars.LowPrices[i]; swingLowIndex = i; }
                }
            }

            double buffer = SweepBufferPoints + (SweepBufferAtrFrac * atr);
            bool sweptHigh = high > swingHigh + buffer;
            bool sweptLow = low < swingLow - buffer;

            bool sellRejectionOk = (!RequireCloseBackInside || close < swingHigh) && sellCloseStrong;
            bool buyRejectionOk = (!RequireCloseBackInside || close > swingLow) && buyCloseStrong;

            bool sellSignal = AllowShorts && sweptHigh && sellWickDominant && sellRejectionOk;
            bool buySignal = AllowLongs && sweptLow && buyWickDominant && buyRejectionOk;

            // FIX: Outside-bar two-sided sweep is high-risk noise, not a signal.
            // Skip both rather than coin-flip.
            if (buySignal && sellSignal)
            {
                Print($"[{Server.Time:HH:mm}] OUTSIDE BAR — sweptH AND sweptL with both rejections | SKIPPING (high-vol noise)");
                return false;
            }

            if (!(buySignal || sellSignal))
            {
                Skip($"no signal | sweptH={sweptHigh} sellWick={sellWickDominant} sellReject={sellRejectionOk} | sweptL={sweptLow} buyWick={buyWickDominant} buyReject={buyRejectionOk}");
                return false;
            }

            if (UseD1TrendFilter && _d1Bars != null && _d1Ema != null)
            {
                int d1Last = _d1Bars.Count - 2;
                if (d1Last < D1EmaPeriod + 2) return false;

                double d1Close = _d1Bars.ClosePrices[d1Last];
                double d1EmaVal = _d1Ema.Result[d1Last];
                bool d1Bull = d1Close > d1EmaVal;
                bool d1Bear = d1Close < d1EmaVal;
                bool withTrend = TrendMode == null || !TrendMode.Equals("CounterTrend", StringComparison.OrdinalIgnoreCase);

                if (withTrend)
                {
                    if (buySignal && !d1Bull) return false;
                    if (sellSignal && !d1Bear) return false;
                }
                else
                {
                    if (buySignal && !d1Bear) return false;
                    if (sellSignal && !d1Bull) return false;
                }
            }

            direction = buySignal ? TradeType.Buy : TradeType.Sell;

            info = $"bar={barIndex} " +
                   $"range={range:F2}pts " +
                   $"body%={(bodyPct * 100):F1}% " +
                   $"uw={upperWick:F2}pts " +
                   $"lw={lowerWick:F2}pts " +
                   $"ATR={atr:F2}pts " +
                   (UseAdxFilter ? $"ADX={_dms.ADX[barIndex]:F1} " : "") +
                   (UseD1TrendFilter ? $"D1Trend=✓ " : "") +
                   $"swH={swingHigh:F2}@{swingHighIndex} " +
                   $"swL={swingLow:F2}@{swingLowIndex} " +
                   $"buf={buffer:F2}pts " +
                   $"closePos={(closePos * 100):F0}%";

            return true;
        }

        // ─────────────────────────────────────────────────────────────
        //  SWING DETECTION
        // ─────────────────────────────────────────────────────────────
        private bool TryFindLastSwingHigh(int barIndex, out double swingHigh, out int swingIndex)
        {
            swingHigh = 0;
            swingIndex = -1;
            int latestCandidate = barIndex - SwingRight;
            int earliest = Math.Max(0, barIndex - MaxSwingAgeBars);

            for (int i = latestCandidate; i >= earliest; i--)
            {
                if (IsSwingHigh(i))
                {
                    swingHigh = Bars.HighPrices[i];
                    swingIndex = i;
                    return true;
                }
            }
            return false;
        }

        private bool TryFindLastSwingLow(int barIndex, out double swingLow, out int swingIndex)
        {
            swingLow = 0;
            swingIndex = -1;
            int latestCandidate = barIndex - SwingRight;
            int earliest = Math.Max(0, barIndex - MaxSwingAgeBars);

            for (int i = latestCandidate; i >= earliest; i--)
            {
                if (IsSwingLow(i))
                {
                    swingLow = Bars.LowPrices[i];
                    swingIndex = i;
                    return true;
                }
            }
            return false;
        }

        private bool IsSwingHigh(int i)
        {
            if (i - SwingLeft < 0 || i + SwingRight >= Bars.Count) return false;
            double h = Bars.HighPrices[i];
            for (int k = i - SwingLeft; k <= i + SwingRight; k++)
                if (Bars.HighPrices[k] > h) return false;
            return true;
        }

        private bool IsSwingLow(int i)
        {
            if (i - SwingLeft < 0 || i + SwingRight >= Bars.Count) return false;
            double l = Bars.LowPrices[i];
            for (int k = i - SwingLeft; k <= i + SwingRight; k++)
                if (Bars.LowPrices[k] < l) return false;
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        //  TRADE EXECUTION
        // ─────────────────────────────────────────────────────────────
        private void ExecuteEntry(TradeType tradeType, int signalBarIndex)
        {
            double atr = _atr.Result[signalBarIndex];
            if (atr <= 0) return;

            double slPoints = atr * StopLossAtrMult;
            double tpPoints = atr * TakeProfitAtrMult;

            if (MinStopPoints > 0 && slPoints < MinStopPoints)
            {
                if (!LogSignalsOnly) Print($"SKIP ENTRY — SL {slPoints:F2}pts < min {MinStopPoints:F2}pts");
                return;
            }

            if (MaxStopPoints > 0 && slPoints > MaxStopPoints)
            {
                if (!LogSignalsOnly) Print($"SKIP ENTRY — SL {slPoints:F2}pts > max {MaxStopPoints:F2}pts");
                return;
            }

            double slPips = slPoints / Symbol.PipSize;
            double tpPips = tpPoints / Symbol.PipSize;
            if (slPips <= 0 || tpPips <= 0) return;

            double volume = CalculateVolumeSafe(tradeType, slPips);
            if (volume <= 0)
            {
                if (!LogSignalsOnly) Print($"[{Server.Time:HH:mm}] SKIP — size/margin. SL={slPoints:F2}pts / {slPips:F1}p");
                return;
            }

            var res = ExecuteMarketOrder(tradeType, SymbolName, volume, BotLabel, slPips, tpPips);

            if (res.IsSuccessful && res.Position != null)
            {
                _tradesToday++;
                Print($"[{Server.Time:yyyy-MM-dd HH:mm}] {tradeType.ToString().ToUpper()} OPEN | " +
                      $"Entry={res.Position.EntryPrice:F2} | SL={slPoints:F2}pts | TP={tpPoints:F2}pts | SLpips={slPips:F1} | TPpips={tpPips:F1} | Vol={volume:F0}");

                // FIX: Sizing audit on first trade — verifies risk math is correct on this symbol
                if (!_firstTradeAudited)
                {
                    double expectedLoss = Symbol.PipValue * volume * slPips;
                    double riskMoney = Account.Equity * RiskPercent / 100.0;
                    double diffPct = Math.Abs(expectedLoss - riskMoney) / Math.Max(riskMoney, 0.01) * 100.0;
                    Print($"[SIZE-AUDIT] FIRST TRADE: expectedLoss=${expectedLoss:F2} | targetRisk=${riskMoney:F2} | diff={diffPct:F2}%");
                    if (diffPct > 5.0)
                    {
                        Print($"⚠️  SIZE-AUDIT WARNING: expected loss differs from target risk by {diffPct:F2}%. Verify Symbol.PipValue/PipSize for {SymbolName}.");
                    }
                    _firstTradeAudited = true;
                }
            }
            else
            {
                Print($"[{Server.Time:HH:mm}] Entry failed: {res.Error}");
            }
        }

        private double CalculateVolumeSafe(TradeType tradeType, double stopLossPips)
        {
            if (RiskPercent <= 0 || stopLossPips <= 0 || Account.Equity <= 0) return 0;

            double rawVolume = 0;
            try
            {
                rawVolume = Symbol.VolumeForProportionalRisk(
                    ProportionalAmountType.Equity,
                    RiskPercent,
                    stopLossPips,
                    RoundingMode.Down);
            }
            catch
            {
                rawVolume = 0;
            }

            if (rawVolume <= 0) return 0;

            double volumeBeforeCap = rawVolume;

            if (VolumeCapUnits > 0)
                rawVolume = Math.Min(rawVolume, VolumeCapUnits);

            double volume = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);

            if (volume < Symbol.VolumeInUnitsMin) return 0;
            if (volume > Symbol.VolumeInUnitsMax) volume = Symbol.VolumeInUnitsMax;

            if (SkipIfVolumeHitsCap && VolumeCapUnits > 0 && volumeBeforeCap > VolumeCapUnits * 1.001 && volume >= VolumeCapUnits)
                return 0;

            double estMargin = 0;
            try { estMargin = Symbol.GetEstimatedMargin(tradeType, volume); } catch { }

            if (estMargin > 0 && estMargin > Account.Equity * (MaxMarginUsagePct / 100.0))
                return 0;

            if (LogSizingDetails)
            {
                double riskMoney = Account.Equity * RiskPercent / 100.0;
                double expectedLoss = Symbol.PipValue * volume * stopLossPips;
                Print($"[SIZE] eq={Account.Equity:F2} risk=${riskMoney:F2} SLpips={stopLossPips:F1} rawVol={volumeBeforeCap:F0} finalVol={volume:F0} expLoss=${expectedLoss:F2} margin={(estMargin > 0 ? estMargin.ToString("F2") : "n/a")}");
            }

            return volume;
        }

        // ─────────────────────────────────────────────────────────────
        //  CIRCUIT BREAKER
        // ─────────────────────────────────────────────────────────────
        private bool CheckCircuitBreaker()
        {
            if (DailyLossLimitPct <= 0 || _startOfDayEquity <= 0) return false;

            double lossPercent = (_startOfDayEquity - Account.Equity) / _startOfDayEquity * 100.0;

            if (lossPercent >= DailyLossLimitPct)
            {
                if (!_circuitBroken)
                {
                    Print($"⚡ CIRCUIT BREAKER | Loss={lossPercent:F2}% >= {DailyLossLimitPct}%");
                    _circuitBroken = true;

                    foreach (var pos in Positions.Where(p => p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
                        ClosePosition(pos);
                }
                return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────
        //  DAY FILTER / FITNESS
        // ─────────────────────────────────────────────────────────────
        private bool PassesDayFilter(DateTime ny)
        {
            if (!UseDayFilter) return true;
            switch (ny.DayOfWeek)
            {
                case DayOfWeek.Monday: return TradeMonday;
                case DayOfWeek.Tuesday: return TradeTuesday;
                case DayOfWeek.Wednesday: return TradeWednesday;
                case DayOfWeek.Thursday: return TradeThursday;
                case DayOfWeek.Friday: return TradeFriday;
                default: return false;
            }
        }

        // FIX: Soft penalty instead of -1000000 cliffs.
        // Configs that miss thresholds get heavily penalised but score remains comparable
        // so the optimiser sees a smooth gradient toward the valid region.
        protected override double GetFitness(GetFitnessArgs args)
        {
            double testYears = BacktestYears > 0 ? BacktestYears : 1;
            double totalTrades = args.TotalTrades;
            double netProfit = args.NetProfit;
            double ddPct = Math.Max(args.MaxEquityDrawdownPercentages, 0.01);
            double pf = Math.Min(args.ProfitFactor, 3.0);
            double tradesPerYear = totalTrades / testYears;
            double winRate = args.WinningTrades / Math.Max(totalTrades, 1);

            // Hard rejects only for fundamentally broken configs
            if (totalTrades < 5) return -1000000;
            if (netProfit <= 0) return -1000000;

            double profitScore = Math.Log10(1.0 + netProfit);
            double tradeScore = Math.Sqrt(totalTrades);
            double ddPenalty = Math.Pow(ddPct, 1.45);
            double winBonus = 0.75 + Math.Min(winRate, 0.75);

            double baseScore = (profitScore * pf * tradeScore * winBonus) / ddPenalty;

            // Soft penalties — reduce score smoothly when thresholds are missed
            double tradeShortfall = Math.Max(0, MinTotalTrades - totalTrades);
            double tradeShortfallPenalty = 1.0 + (tradeShortfall / Math.Max(MinTotalTrades, 1)) * 5.0;

            double tpyShortfall = Math.Max(0, MinTradesPerYear - tradesPerYear);
            double tpyShortfallPenalty = 1.0 + (tpyShortfall / Math.Max(MinTradesPerYear, 1)) * 5.0;

            double ddOverage = Math.Max(0, ddPct - MaxFitnessDrawdownPct);
            double ddOveragePenalty = 1.0 + (ddOverage / Math.Max(MaxFitnessDrawdownPct, 1)) * 10.0;

            return baseScore / (tradeShortfallPenalty * tpyShortfallPenalty * ddOveragePenalty);
        }

        // ─────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────
        private void ResetDailyStateIfNeeded(DateTime ny)
        {
            if (_lastNyDay.Date == ny.Date) return;

            _lastNyDay = ny.Date;
            _tradesToday = 0;
            _startOfDayEquity = Account.Equity;
            _circuitBroken = false;
            _partialClosedPosId = -1;
            _beShiftedPosId = -1;

            Print($"NEW NY DAY {ny:dd-MMM-yyyy ddd}");
        }

        // FIX: Anchor session in NY (Eastern) time, not London.
        // US DST: 2nd Sunday March → 1st Sunday November (EDT, UTC-4)
        // Outside DST window: EST, UTC-5
        private DateTime NyNow()
        {
            var utc = Server.Time;
            int offset = IsEdt(utc) ? -4 : -5;
            return utc.AddHours(offset);
        }

        private bool IsEdt(DateTime utc)
        {
            // Outside the possible window
            if (utc.Month < 3 || utc.Month > 11) return false;
            if (utc.Month > 3 && utc.Month < 11) return true;

            // March: EDT starts on the 2nd Sunday at 02:00 local (07:00 UTC)
            if (utc.Month == 3)
            {
                int secondSunday = FindNthSunday(utc.Year, 3, 2);
                if (utc.Day > secondSunday) return true;
                if (utc.Day < secondSunday) return false;
                return utc.Hour >= 7;
            }

            // November: EDT ends on the 1st Sunday at 02:00 local (06:00 UTC)
            if (utc.Month == 11)
            {
                int firstSunday = FindNthSunday(utc.Year, 11, 1);
                if (utc.Day < firstSunday) return true;
                if (utc.Day > firstSunday) return false;
                return utc.Hour < 6;
            }

            return false;
        }

        private int FindNthSunday(int year, int month, int n)
        {
            int count = 0;
            int daysInMonth = DateTime.DaysInMonth(year, month);
            for (int day = 1; day <= daysInMonth; day++)
            {
                if (new DateTime(year, month, day).DayOfWeek == DayOfWeek.Sunday)
                {
                    count++;
                    if (count == n) return day;
                }
            }
            return -1;
        }

        private bool InTradeHours(DateTime ny)
        {
            if (string.IsNullOrWhiteSpace(TradeStart) || string.IsNullOrWhiteSpace(TradeEnd))
                return true;

            if (!TimeSpan.TryParse(TradeStart, out var start) || !TimeSpan.TryParse(TradeEnd, out var end))
                return true;

            var t = ny.TimeOfDay;

            bool inWindow = start <= end
                ? t >= start && t <= end
                : t >= start || t <= end;

            if (!inWindow) return false;

            if (NoTradeFirstMinutes > 0)
            {
                var openGuardEnd = start.Add(TimeSpan.FromMinutes(NoTradeFirstMinutes));

                if (start <= end)
                {
                    // Same-day window: simple comparison
                    if (t >= start && t < openGuardEnd) return false;
                }
                else
                {
                    // FIX: Cross-midnight window: openGuardEnd may itself wrap past 24:00.
                    // Two cases:
                    //   a) openGuardEnd ≤ 24:00 → guard is t ∈ [start, openGuardEnd)
                    //   b) openGuardEnd > 24:00 → guard wraps; t ∈ [start, 24:00) OR [00:00, openGuardEnd-24h)
                    if (openGuardEnd.TotalHours < 24)
                    {
                        if (t >= start && t < openGuardEnd) return false;
                    }
                    else
                    {
                        var wrappedEnd = openGuardEnd.Subtract(TimeSpan.FromHours(24));
                        if (t >= start || t < wrappedEnd) return false;
                    }
                }
            }

            return true;
        }

        private bool IndicatorsReady()
        {
            int need = Math.Max(Math.Max(AtrAvgPeriod, AtrPeriod), Math.Max(TrailingAtrPeriod, AdxPeriod));
            bool mainReady = Bars.Count > need + SwingLeft + SwingRight + 20;

            if (!mainReady) return false;

            if (UseD1TrendFilter)
                return _d1Bars != null && _d1Ema != null && _d1Bars.Count > D1EmaPeriod + 5;

            return true;
        }

        private bool SafetyOk()
        {
            if (!UseSpreadFilter) return true;

            double spreadPoints = Symbol.Ask - Symbol.Bid;
            bool ok = spreadPoints <= MaxSpreadPoints;

            if (!ok && LogSkipReasons)
                Print($"SKIP — spread {spreadPoints:F2}pts > max {MaxSpreadPoints:F2}pts");

            return ok;
        }

        private bool HasOpenPosition()
        {
            return Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName || args.Position.Label != BotLabel)
                return;

            _lastExitTime = Server.Time;
        }

        private void Skip(string reason)
        {
            if (LogSkipReasons && !LogSignalsOnly)
                Print($"SKIP | {reason}");
        }
    }
}
