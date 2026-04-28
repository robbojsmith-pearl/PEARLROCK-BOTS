using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  LiquidityWickBot v5 — Multi-pair FX Liquidity Sweep / Wick Rejection
    //  Pearlrock Systematic
    //
    //  CHANGELOG vs v4:
    //  - FIX: Volume rounding Down → ToNearest (was systematically under-risking)
    //  - FIX: Outside-bar two-sided sweeps now skipped (was silently picking buy)
    //  - FIX: Geometry sanity check at OnStart (catches impossible param combos)
    //  - FIX: MaxLots and VolumeCap unified — consistent skip-or-cap behaviour, no silent under-risk
    //  - FIX: Partial close + BE shift split — BE fires even if partial vol too small
    //  - ADD: First-trade sizing audit (verifies Symbol.PipValue math on each pair)
    //  - ADD: Outside-bar diagnostic logging
    //  - ADD: GetFitness with soft penalties (no cliffs at threshold boundaries)
    //  - ADD: Cap-triggered logging so silent under-risking is visible
    //
    //  Designed for:
    //  - JPY FX pairs (GBPJPY, AUDJPY, EURJPY, NZDJPY) primarily
    //  - Other major FX pairs secondary
    //  - M15 / M30 / H1 charts
    //  - Asian / London / Overlap sessions
    //
    //  IMPORTANT — RUN ONCE BEFORE OPTIMISING:
    //  1. Verify [SIZE-AUDIT] line shows expected_loss ≈ risk_money on first trade
    //  2. Verify no [CAP-HIT] warnings flooding the log (means MaxLots/VolumeCap too tight)
    //  3. Run raw baseline before optimisation to confirm signal exists
    // ═══════════════════════════════════════════════════════════════════

    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC)]
    public class LiquidityWickBot_v5 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "LIQ_WICK_V5", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Direction ────────────────────────────────────────────────
        [Parameter("Allow Longs", DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

        // ── Structure (Pro Swings) ────────────────────────────────────
        [Parameter("Use Pro Structure (Swings)", DefaultValue = true, Group = "Structure")]
        public bool UseProStructure { get; set; }

        [Parameter("Swing Left Bars", DefaultValue = 3, MinValue = 1, Group = "Structure")]
        public int SwingLeft { get; set; }

        [Parameter("Swing Right Bars", DefaultValue = 3, MinValue = 1, Group = "Structure")]
        public int SwingRight { get; set; }

        [Parameter("Max Swing Age (bars)", DefaultValue = 400, MinValue = 50, Group = "Structure")]
        public int MaxSwingAgeBars { get; set; }

        [Parameter("Min Swing Distance (pips)", DefaultValue = 20, MinValue = 0, Step = 1, Group = "Structure")]
        public double MinSwingDistancePips { get; set; }

        [Parameter("Min Swing Distance (ATR)", DefaultValue = 1.2, MinValue = 0.0, Step = 0.1, Group = "Structure")]
        public double MinSwingDistanceAtr { get; set; }

        [Parameter("Sweep Buffer (pips)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Structure")]
        public double SweepBufferPips { get; set; }

        [Parameter("Sweep Buffer (ATR fraction)", DefaultValue = 0.10, MinValue = 0, Step = 0.05, Group = "Structure")]
        public double SweepBufferAtrFrac { get; set; }

        // ── Candle / Wick Quality ─────────────────────────────────────
        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "Pattern")]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Avg Period", DefaultValue = 50, MinValue = 2, Group = "Pattern")]
        public int AtrAvgPeriod { get; set; }

        [Parameter("Range >= ATR *", DefaultValue = 1.15, Step = 0.05, Group = "Pattern")]
        public double RangeAtrMultiplier { get; set; }

        [Parameter("ATR >= AvgATR *", DefaultValue = 0.95, Step = 0.05, Group = "Pattern")]
        public double AtrComparisonMultiplier { get; set; }

        [Parameter("Max Body % of Range", DefaultValue = 0.20, Step = 0.01, Group = "Pattern")]
        public double MaxBodyPctOfRange { get; set; }

        [Parameter("Min Dominant Wick %", DefaultValue = 0.45, Step = 0.01, Group = "Pattern")]
        public double MinDominantWickPctOfRange { get; set; }

        [Parameter("Wick Dominance Ratio", DefaultValue = 1.8, Step = 0.1, Group = "Pattern")]
        public double WickDominanceRatio { get; set; }

        [Parameter("Close Location Threshold", DefaultValue = 0.70, Step = 0.05, Group = "Pattern")]
        public double CloseLocationThreshold { get; set; }

        [Parameter("Require Close Back Inside", DefaultValue = true, Group = "Pattern")]
        public bool RequireCloseBackInside { get; set; }

        // ── Regime Filter (ADX) ───────────────────────────────────────
        [Parameter("Use ADX Filter", DefaultValue = true, Group = "Regime")]
        public bool UseAdxFilter { get; set; }

        [Parameter("ADX Period", DefaultValue = 14, MinValue = 2, Group = "Regime")]
        public int AdxPeriod { get; set; }

        [Parameter("Max ADX to Trade", DefaultValue = 22.0, Step = 0.5, Group = "Regime")]
        public double MaxAdxToTrade { get; set; }

        // ── Higher Timeframe Trend Filter ─────────────────────────────
        [Parameter("Use D1 Trend Filter", DefaultValue = false, Group = "HTF Trend")]
        public bool UseD1TrendFilter { get; set; }

        [Parameter("D1 EMA Period", DefaultValue = 50, MinValue = 5, Group = "HTF Trend")]
        public int D1EmaPeriod { get; set; }

        [Parameter("Trend Mode", DefaultValue = "WithTrend", Group = "HTF Trend")]
        public string TrendMode { get; set; }

        // ── Session Filter ────────────────────────────────────────────
        [Parameter("Trade Asian Session (00-07 London)", DefaultValue = false, Group = "Session Filter")]
        public bool TradeAsian { get; set; }

        [Parameter("Trade London Session (07-12 London)", DefaultValue = true, Group = "Session Filter")]
        public bool TradeLondon { get; set; }

        [Parameter("Trade London/NY Overlap (12-17 London)", DefaultValue = true, Group = "Session Filter")]
        public bool TradeLondonNyOverlap { get; set; }

        [Parameter("Trade NY Session (17-21 London)", DefaultValue = false, Group = "Session Filter")]
        public bool TradeNy { get; set; }

        // ── Risk ──────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.10, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Stop Loss (ATR mult)", DefaultValue = 1.6, Step = 0.1, Group = "Risk")]
        public double StopLossAtrMult { get; set; }

        [Parameter("Take Profit (ATR mult)", DefaultValue = 2.2, Step = 0.1, Group = "Risk")]
        public double TakeProfitAtrMult { get; set; }

        // FIX: Unified cap. Set in lots — converted to units internally.
        // 0 = no cap. If position would exceed cap, behaviour controlled by SkipIfCapHit.
        [Parameter("Max Lots Cap (0=off)", DefaultValue = 0.50, MinValue = 0, Step = 0.01, Group = "Risk")]
        public double MaxLotsCap { get; set; }

        [Parameter("Skip If Cap Hit", DefaultValue = false, Group = "Risk")]
        public bool SkipIfCapHit { get; set; }

        [Parameter("Min Stop Pips 0=off", DefaultValue = 0.0, MinValue = 0.0, Step = 1.0, Group = "Risk")]
        public double MinStopPips { get; set; }

        [Parameter("Max Stop Pips 0=off", DefaultValue = 0.0, MinValue = 0.0, Step = 1.0, Group = "Risk")]
        public double MaxStopPips { get; set; }

        [Parameter("Max Margin Usage %", DefaultValue = 30.0, Step = 1.0, Group = "Risk")]
        public double MaxMarginUsagePct { get; set; }

        [Parameter("Max Trade Duration (Hours, 0=off)", DefaultValue = 8, MinValue = 0, MaxValue = 48, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        // ── Partial Close ─────────────────────────────────────────────
        [Parameter("Use Partial Close at 1R", DefaultValue = false, Group = "Partial Close")]
        public bool UsePartialClose { get; set; }

        [Parameter("Partial Close % of Position", DefaultValue = 50, MinValue = 10, MaxValue = 90, Step = 5, Group = "Partial Close")]
        public int PartialClosePct { get; set; }

        [Parameter("BE Offset After Partial (pips)", DefaultValue = 1, MinValue = 0, Step = 1, Group = "Partial Close")]
        public int BeOffsetPips { get; set; }

        [Parameter("BE-Only at 1R (no partial)", DefaultValue = false, Group = "Partial Close")]
        public bool BeOnlyAt1R { get; set; }

        // ── Day Filter ────────────────────────────────────────────────
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

        [Parameter("Trade Friday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeFriday { get; set; }

        // ── Trade Control ─────────────────────────────────────────────
        [Parameter("Trade Start (HH:mm London)", DefaultValue = "06:00", Group = "Trade Control")]
        public string TradeStart { get; set; }

        [Parameter("Trade End (HH:mm London)", DefaultValue = "21:00", Group = "Trade Control")]
        public string TradeEnd { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 2, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 120, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        [Parameter("Min Bars Since Last Signal", DefaultValue = 6, MinValue = 1, Group = "Trade Control")]
        public int MinBarsSinceSignal { get; set; }

        // ── Circuit Breaker ───────────────────────────────────────────
        [Parameter("Daily Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Circuit Breaker")]
        public double DailyLossLimitPct { get; set; }

        // ── Safety ────────────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = true, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread (pips)", DefaultValue = 3.0, Step = 0.1, Group = "Safety")]
        public double MaxSpreadPips { get; set; }

        // ── Trailing ──────────────────────────────────────────────────
        [Parameter("Use Trailing Stop", DefaultValue = false, Group = "Trailing")]
        public bool UseTrailingStop { get; set; }

        [Parameter("Trail After +1R", DefaultValue = true, Group = "Trailing")]
        public bool TrailAfterOneR { get; set; }

        [Parameter("Trailing ATR Period", DefaultValue = 14, MinValue = 1, Group = "Trailing")]
        public int TrailingAtrPeriod { get; set; }

        [Parameter("Trail Distance (ATR mult)", DefaultValue = 1.8, Step = 0.1, Group = "Trailing")]
        public double TrailingStopAtrMult { get; set; }

        // ── Fitness ──────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 80, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 15, MinValue = 1, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 10.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        [Parameter("Backtest Years", DefaultValue = 5.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }

        // ── Logging ───────────────────────────────────────────────────
        [Parameter("Log Signals Only", DefaultValue = false, Group = "Logging")]
        public bool LogSignalsOnly { get; set; }

        [Parameter("Log Sizing Details", DefaultValue = true, Group = "Logging")]
        public bool LogSizingDetails { get; set; }

        [Parameter("Log Skip Reasons", DefaultValue = false, Group = "Logging")]
        public bool LogSkipReasons { get; set; }

        // ── Indicators ────────────────────────────────────────────────
        private AverageTrueRange _atr;
        private AverageTrueRange _atrAvg;
        private AverageTrueRange _atrTrail;
        private DirectionalMovementSystem _dms;
        private Bars _d1Bars;
        private ExponentialMovingAverage _d1Ema;

        // ── London timezone ───────────────────────────────────────────
        private static readonly TimeZoneInfo LondonTz =
            TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

        // ── State ─────────────────────────────────────────────────────
        private DateTime _lastExitTime = DateTime.MinValue;
        private DateTime _lastLondonDay = DateTime.MinValue;
        private int _tradesToday = 0;
        private int _lastSignalBarIndex = -1;
        private double _startOfDayEquity = 0;
        private bool _circuitBroken = false;
        private long _partialClosedPosId = -1;
        private long _beShiftedPosId = -1;
        private bool _firstTradeAudited = false;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            // FIX: Geometry sanity check
            if (MaxBodyPctOfRange + MinDominantWickPctOfRange > 1.0)
            {
                Print($"⚠️  CONFIG WARNING: MaxBodyPctOfRange ({MaxBodyPctOfRange:P0}) + " +
                      $"MinDominantWickPctOfRange ({MinDominantWickPctOfRange:P0}) > 100%. " +
                      $"Geometrically impossible. NO TRADES WILL FIRE. Stop and fix params.");
                Stop();
                return;
            }

            // Instrument warning
            var sym = SymbolName.ToUpperInvariant();
            bool isFx = sym.Length == 6 ||
                        sym.Contains("USD") || sym.Contains("EUR") ||
                        sym.Contains("GBP") || sym.Contains("JPY") ||
                        sym.Contains("CHF") || sym.Contains("AUD") ||
                        sym.Contains("NZD") || sym.Contains("CAD");

            if (!isFx)
                Print($"⚠️  WARNING: {SymbolName} may not be an FX pair. Verify parameters are appropriate.");

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

            Print($"═══ {BotLabel} v5 started on {SymbolName} ({Bars.TimeFrame}) ═══");
            Print($"SPECS  | LotSize={Symbol.LotSize} | PipSize={Symbol.PipSize} | PipValue={Symbol.PipValue} | VolMin={Symbol.VolumeInUnitsMin} | VolStep={Symbol.VolumeInUnitsStep}");
            Print($"SESSION| {TradeStart}-{TradeEnd} London | Asian={TradeAsian} London={TradeLondon} Overlap={TradeLondonNyOverlap} NY={TradeNy}");
            Print($"PATTERN| RangeATR>={RangeAtrMultiplier} | Body<={MaxBodyPctOfRange:P0} | Wick>={MinDominantWickPctOfRange:P0} | WickDom={WickDominanceRatio:F2} | CloseLoc={CloseLocationThreshold:F2}");
            Print($"RISK   | Risk={RiskPercent}% | SL={StopLossAtrMult}ATR | TP={TakeProfitAtrMult}ATR | MaxLotsCap={MaxLotsCap} | MaxMargin={MaxMarginUsagePct}% | SpreadMax={MaxSpreadPips}p");
            Print($"FILTERS| ADX={UseAdxFilter}({MaxAdxToTrade}) | D1Trend={UseD1TrendFilter}({TrendMode}) | DayFilter={UseDayFilter} | Longs={AllowLongs} Shorts={AllowShorts}");
            Print($"FITNESS| MinTrades={MinTotalTrades} | MinPerYear={MinTradesPerYear} | MaxDD={MaxFitnessDrawdownPct}% | BacktestYears={BacktestYears}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            var london = LondonNow();

            ResetDailyStateIfNeeded(london);
            ManageOpenPositions();

            if (CheckCircuitBreaker()) return;
            if (!PassesDayFilter(london)) return;
            if (!InTradeHours(london)) return;
            if (!PassesSessionFilter(london)) return;
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
                            double beOffset = BeOffsetPips * Symbol.PipSize;
                            double bePrice = pos.TradeType == TradeType.Buy
                                ? pos.EntryPrice + beOffset
                                : pos.EntryPrice - beOffset;

                            bool improve = !pos.StopLoss.HasValue ||
                                (pos.TradeType == TradeType.Buy && bePrice > pos.StopLoss.Value) ||
                                (pos.TradeType == TradeType.Sell && bePrice < pos.StopLoss.Value);

                            if (improve)
                            {
                                pos.ModifyStopLossPrice(bePrice);
                                _beShiftedPosId = pos.Id;
                                Print($"BE SHIFT on pos {pos.Id} | SL → {bePrice:F5}");
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
                    Print($"TRAIL {pos.TradeType} → SL={newSL:F5} | LastClose={lastClose:F5}");
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
                Skip($"range {(range / Symbol.PipSize):F1}p < ATR*{RangeAtrMultiplier:F2}");
                return false;
            }

            if (atr < avgAtr * AtrComparisonMultiplier)
            {
                Skip($"ATR {(atr / Symbol.PipSize):F1}p < AvgATR*{AtrComparisonMultiplier:F2}");
                return false;
            }

            if (bodyPct > MaxBodyPctOfRange)
            {
                Skip($"body {bodyPct:P0} > max {MaxBodyPctOfRange:P0}");
                return false;
            }

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

                double requiredDistancePrice = Math.Max(
                    MinSwingDistancePips * Symbol.PipSize,
                    MinSwingDistanceAtr * atr);

                if ((swingHigh - swingLow) < requiredDistancePrice)
                {
                    Skip($"swing range {((swingHigh - swingLow) / Symbol.PipSize):F1}p < required");
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

            double bufferPrice = (SweepBufferPips * Symbol.PipSize) + (SweepBufferAtrFrac * atr);
            bool sweptHigh = high > swingHigh + bufferPrice;
            bool sweptLow = low < swingLow - bufferPrice;

            bool sellRejectionOk = (!RequireCloseBackInside || close < swingHigh) && sellCloseStrong;
            bool buyRejectionOk = (!RequireCloseBackInside || close > swingLow) && buyCloseStrong;

            bool sellSignal = AllowShorts && sweptHigh && sellWickDominant && sellRejectionOk;
            bool buySignal = AllowLongs && sweptLow && buyWickDominant && buyRejectionOk;

            // FIX: Outside-bar two-sided sweep is high-risk noise, not a signal
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
                   $"range={(range / Symbol.PipSize):F1}p " +
                   $"body%={(bodyPct * 100):F1}% " +
                   $"uw={(upperWick / Symbol.PipSize):F1}p " +
                   $"lw={(lowerWick / Symbol.PipSize):F1}p " +
                   $"ATR={(atr / Symbol.PipSize):F1}p " +
                   (UseAdxFilter ? $"ADX={_dms.ADX[barIndex]:F1} " : "") +
                   (UseD1TrendFilter ? $"D1Trend=✓ " : "") +
                   $"swH={swingHigh:F5}@{swingHighIndex} " +
                   $"swL={swingLow:F5}@{swingLowIndex} " +
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

            double slPips = (atr * StopLossAtrMult) / Symbol.PipSize;
            double tpPips = (atr * TakeProfitAtrMult) / Symbol.PipSize;

            if (MinStopPips > 0 && slPips < MinStopPips)
            {
                if (!LogSignalsOnly) Print($"SKIP ENTRY — SL {slPips:F1}p < min {MinStopPips:F1}p");
                return;
            }

            if (MaxStopPips > 0 && slPips > MaxStopPips)
            {
                if (!LogSignalsOnly) Print($"SKIP ENTRY — SL {slPips:F1}p > max {MaxStopPips:F1}p");
                return;
            }

            if (slPips <= 0 || tpPips <= 0) return;

            double volume = CalculateVolumeSafe(tradeType, slPips);
            if (volume <= 0)
            {
                if (!LogSignalsOnly) Print($"[{Server.Time:HH:mm}] SKIP — size/margin. SL={slPips:F1}p");
                return;
            }

            var res = ExecuteMarketOrder(tradeType, SymbolName, volume, BotLabel, slPips, tpPips);

            if (res.IsSuccessful && res.Position != null)
            {
                _tradesToday++;
                Print($"[{Server.Time:yyyy-MM-dd HH:mm}] {tradeType.ToString().ToUpper()} OPEN | " +
                      $"vol={volume:F0} SL={slPips:F1}p TP={tpPips:F1}p | Entry={res.Position.EntryPrice:F5}");

                // FIX: First-trade sizing audit
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

        // ─────────────────────────────────────────────────────────────
        //  VOLUME CALCULATION
        //  FIX: Unified MaxLotsCap handles both old MaxLots and VolumeCap.
        //  Cap-hit behaviour is explicit (skip-or-cap) and logged.
        //  FIX: ToNearest rounding instead of Down — prevents systematic under-risk.
        // ─────────────────────────────────────────────────────────────
        private double CalculateVolumeSafe(TradeType tradeType, double stopLossPips)
        {
            if (RiskPercent <= 0 || stopLossPips <= 0) return 0;

            double equity = Account.Equity;
            if (equity <= 0) return 0;

            double riskMoney = equity * (RiskPercent / 100.0);
            double riskPerLot = stopLossPips * Symbol.PipValue;
            if (riskPerLot <= 0) return 0;

            double lotsRaw = riskMoney / riskPerLot;
            double lotsBeforeCap = lotsRaw;
            bool capHit = false;

            if (MaxLotsCap > 0 && lotsRaw > MaxLotsCap)
            {
                if (SkipIfCapHit)
                {
                    if (LogSizingDetails)
                        Print($"[CAP-HIT] lotsRaw={lotsRaw:F3} > MaxLotsCap={MaxLotsCap:F3} | SkipIfCapHit=true → skipping");
                    return 0;
                }
                lotsRaw = MaxLotsCap;
                capHit = true;
            }

            double unitsRaw = lotsRaw * Symbol.LotSize;
            // FIX: ToNearest instead of Down — prevents systematic under-risking on volume step
            double unitsFinal = Symbol.NormalizeVolumeInUnits(unitsRaw, RoundingMode.ToNearest);

            if (unitsFinal < Symbol.VolumeInUnitsMin) return 0;
            if (unitsFinal > Symbol.VolumeInUnitsMax) unitsFinal = Symbol.VolumeInUnitsMax;

            double estMargin = 0;
            try { estMargin = Symbol.GetEstimatedMargin(tradeType, unitsFinal); } catch { }

            if (estMargin > 0 && estMargin > equity * (MaxMarginUsagePct / 100.0))
            {
                if (LogSizingDetails)
                    Print($"[MARGIN-SKIP] estMargin={estMargin:F2} > maxAllowed={(equity * MaxMarginUsagePct / 100.0):F2}");
                return 0;
            }

            if (LogSizingDetails)
            {
                double lotsUsed = unitsFinal / Symbol.LotSize;
                double expectedLoss = Symbol.PipValue * unitsFinal * stopLossPips;
                string capFlag = capHit ? " [CAPPED]" : "";
                Print($"[SIZE]{capFlag} eq={equity:F2} risk=${riskMoney:F2} SL={stopLossPips:F1}p " +
                      $"pipVal={Symbol.PipValue:F4} lotsRaw={lotsBeforeCap:F3} lotsUsed={lotsUsed:F3} " +
                      $"unitsFinal={unitsFinal:F0} expLoss=${expectedLoss:F2} margin={(estMargin > 0 ? estMargin.ToString("F2") : "n/a")}");
            }

            return unitsFinal;
        }

        // ─────────────────────────────────────────────────────────────
        //  CIRCUIT BREAKER
        // ─────────────────────────────────────────────────────────────
        private bool CheckCircuitBreaker()
        {
            if (DailyLossLimitPct <= 0 || _startOfDayEquity <= 0) return false;

            var lossPercent = (_startOfDayEquity - Account.Equity) / _startOfDayEquity * 100.0;

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
        //  SESSION CLASSIFIER
        // ─────────────────────────────────────────────────────────────
        private bool PassesSessionFilter(DateTime london)
        {
            var t = london.TimeOfDay;
            var asianStart = TimeSpan.FromHours(0);
            var londonStart = TimeSpan.FromHours(7);
            var overlapStart = TimeSpan.FromHours(12);
            var nyStart = TimeSpan.FromHours(17);
            var nyEnd = TimeSpan.FromHours(21);

            if (t >= asianStart && t < londonStart) return TradeAsian;
            if (t >= londonStart && t < overlapStart) return TradeLondon;
            if (t >= overlapStart && t < nyStart) return TradeLondonNyOverlap;
            if (t >= nyStart && t < nyEnd) return TradeNy;

            return false;
        }

        // ─────────────────────────────────────────────────────────────
        //  DAY FILTER
        // ─────────────────────────────────────────────────────────────
        private bool PassesDayFilter(DateTime london)
        {
            if (!UseDayFilter) return true;

            switch (london.DayOfWeek)
            {
                case DayOfWeek.Monday: return TradeMonday;
                case DayOfWeek.Tuesday: return TradeTuesday;
                case DayOfWeek.Wednesday: return TradeWednesday;
                case DayOfWeek.Thursday: return TradeThursday;
                case DayOfWeek.Friday: return TradeFriday;
                default: return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  FITNESS — soft penalties, no cliffs
        // ─────────────────────────────────────────────────────────────
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

            // Soft penalties
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
        private void ResetDailyStateIfNeeded(DateTime london)
        {
            if (_lastLondonDay.Date == london.Date) return;

            _lastLondonDay = london.Date;
            _tradesToday = 0;
            _startOfDayEquity = Account.Equity;
            _circuitBroken = false;
            _partialClosedPosId = -1;
            _beShiftedPosId = -1;

            Print($"NEW LONDON DAY {london:dd-MMM-yyyy ddd}");
        }

        private DateTime LondonNow() =>
            TimeZoneInfo.ConvertTimeFromUtc(Server.Time, LondonTz);

        private bool InTradeHours(DateTime london)
        {
            if (string.IsNullOrWhiteSpace(TradeStart) || string.IsNullOrWhiteSpace(TradeEnd))
                return true;

            if (!TimeSpan.TryParse(TradeStart, out var start) || !TimeSpan.TryParse(TradeEnd, out var end))
                return true;

            var t = london.TimeOfDay;
            return t >= start && t <= end;
        }

        private bool IndicatorsReady()
        {
            int need = Math.Max(Math.Max(AtrAvgPeriod, AtrPeriod), Math.Max(TrailingAtrPeriod, AdxPeriod));
            int swingNeed = SwingLeft + SwingRight + 10;
            bool mainReady = Bars.Count > need + swingNeed + 10;

            if (!mainReady) return false;

            if (UseD1TrendFilter)
                return _d1Bars != null && _d1Ema != null && _d1Bars.Count > D1EmaPeriod + 5;

            return true;
        }

        private bool SafetyOk()
        {
            if (!UseSpreadFilter) return true;
            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            bool ok = spreadPips <= MaxSpreadPips;
            if (!ok && LogSkipReasons)
                Print($"SKIP — spread {spreadPips:F2}p > max {MaxSpreadPips:F2}p");
            return ok;
        }

        private bool HasOpenPosition() =>
            Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName) return;
            if (args.Position.Label != BotLabel) return;
            _lastExitTime = Server.Time;
        }

        private void Skip(string reason)
        {
            if (LogSkipReasons && !LogSignalsOnly)
                Print($"SKIP | {reason}");
        }
    }
}
