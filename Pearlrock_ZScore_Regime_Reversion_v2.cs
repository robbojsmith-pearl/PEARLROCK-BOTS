using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  Pearlrock Z-Score Regime Reversion v2 — US30 / DE40 H2
    //
    //  Improvements from v1:
    //
    //  - TP Target now configurable: SMA-based or ATR-based
    //  - MinTpToSlRatio enforced (no negative R:R trades)
    //  - Longer confirmation window by default (5 bars)
    //  - RequireCloseDirection disabled by default (less filtering)
    //  - HTF regime mode defaults to WithTrendReversion (balanced)
    //  - Daily loss limit increased to 5% (prevents circuit breaker churn)
    //  - Tighter Z-score threshold (2.2) for better signal quality
    //
    //  TP Target Modes:
    //    "SMA"        → TP = SMA (original, risky if entry is past SMA)
    //    "ATRBased"   → TP = SMA ± (ATR × TpAtrMultiple)
    //                   LONG: TP = SMA + (ATR × TpAtrMultiple)
    //                   SHORT: TP = SMA - (ATR × TpAtrMultiple)
    //
    //  Pearlrock Systematic
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class Pearlrock_ZScore_Regime_Reversion_v2 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "PR_ZSCORE_REGIME_V2", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Z-Score Signal ───────────────────────────────────────────
        [Parameter("Z-Score Period", DefaultValue = 20, MinValue = 5, Step = 1, Group = "Z-Score Signal")]
        public int ZScorePeriod { get; set; }

        [Parameter("Z-Score Entry Threshold", DefaultValue = 2.2, MinValue = 1.0, MaxValue = 5.0, Step = 0.1, Group = "Z-Score Signal")]
        public double ZScoreThreshold { get; set; }

        [Parameter("Use High/Low Wick Trigger", DefaultValue = false, Group = "Z-Score Signal")]
        public bool UseWickTrigger { get; set; }

        [Parameter("Reset Opposite Gate", DefaultValue = false, Group = "Z-Score Signal")]
        public bool ResetOppositeGate { get; set; }

        // ── TP Target ────────────────────────────────────────────────
        [Parameter("TP Target Mode", DefaultValue = "ATRBased", Group = "TP Target")]
        public string TpTargetMode { get; set; }

        [Parameter("TP ATR Multiple (if ATRBased)", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 5.0, Step = 0.1, Group = "TP Target")]
        public double TpAtrMultiple { get; set; }

        // ── HTF Direction Regime ─────────────────────────────────────
        [Parameter("Use HTF Direction Filter", DefaultValue = true, Group = "HTF Regime")]
        public bool UseHtfDirectionFilter { get; set; }

        [Parameter("HTF Direction Mode", DefaultValue = "WithTrendReversion", Group = "HTF Regime")]
        public string HtfDirectionMode { get; set; }

        [Parameter("HTF TimeFrame", DefaultValue = "H4", Group = "HTF Regime")]
        public string HtfTimeFrame { get; set; }

        [Parameter("HTF EMA Period", DefaultValue = 50, MinValue = 5, Step = 5, Group = "HTF Regime")]
        public int HtfEmaPeriod { get; set; }

        [Parameter("Use HTF EMA Slope Filter", DefaultValue = false, Group = "HTF Regime")]
        public bool UseHtfSlopeFilter { get; set; }

        [Parameter("HTF Slope Lookback Bars", DefaultValue = 3, MinValue = 1, MaxValue = 20, Step = 1, Group = "HTF Regime")]
        public int HtfSlopeLookbackBars { get; set; }

        [Parameter("Block If HTF Neutral", DefaultValue = true, Group = "HTF Regime")]
        public bool BlockIfHtfNeutral { get; set; }

        // ── Confirmation Gate ────────────────────────────────────────
        [Parameter("Max Bars to Wait", DefaultValue = 5, MinValue = 1, MaxValue = 24, Step = 1, Group = "Confirmation")]
        public int MaxConfirmationBars { get; set; }

        [Parameter("Require Close Toward SMA", DefaultValue = true, Group = "Confirmation")]
        public bool RequireCloseTowardSma { get; set; }

        [Parameter("Require Close Direction", DefaultValue = false, Group = "Confirmation")]
        public bool RequireCloseDirection { get; set; }

        // ── Risk ─────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.50, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int AtrPeriod { get; set; }

        [Parameter("SL ATR Multiple", DefaultValue = 1.2, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }

        [Parameter("Min SL Points (0=off)", DefaultValue = 0.0, MinValue = 0, Step = 0.5, Group = "Risk")]
        public double MinSlPoints { get; set; }

        [Parameter("Max SL Points (0=off)", DefaultValue = 0.0, MinValue = 0, Step = 0.5, Group = "Risk")]
        public double MaxSlPoints { get; set; }

        [Parameter("Min TP / SL Ratio", DefaultValue = 1.5, MinValue = 0.0, MaxValue = 5.0, Step = 0.05, Group = "Risk")]
        public double MinTpToSlRatio { get; set; }

        [Parameter("Max Trade Duration Hours (0=off)", DefaultValue = 0, MinValue = 0, MaxValue = 120, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        [Parameter("Volume Cap Units (0=off)", DefaultValue = 0, MinValue = 0, Step = 1, Group = "Risk")]
        public int VolumeCapUnits { get; set; }

        [Parameter("Skip If Volume Hits Cap", DefaultValue = false, Group = "Risk")]
        public bool SkipIfVolumeHitsCap { get; set; }

        [Parameter("Max Margin Usage %", DefaultValue = 30.0, MinValue = 1.0, Step = 1.0, Group = "Risk")]
        public double MaxMarginUsagePct { get; set; }

        // ── Direction ────────────────────────────────────────────────
        [Parameter("Allow Longs", DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

        // ── Trade Control ────────────────────────────────────────────
        [Parameter("Trade Start HH:mm London", DefaultValue = "00:00", Group = "Trade Control")]
        public string TradeStart { get; set; }

        [Parameter("Trade End HH:mm London", DefaultValue = "23:59", Group = "Trade Control")]
        public string TradeEnd { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 2, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 60, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        // ── Day Filter ───────────────────────────────────────────────
        [Parameter("Use Day Filter", DefaultValue = true, Group = "Day Filter")]
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

        // ── Safety ───────────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = true, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread Points", DefaultValue = 10.0, MinValue = 0, Step = 0.05, Group = "Safety")]
        public double MaxSpreadPoints { get; set; }

        [Parameter("Daily Loss Limit % (0=off)", DefaultValue = 5.0, MinValue = 0, Step = 0.1, Group = "Safety")]
        public double DailyLossLimitPct { get; set; }

        // ── Logging ──────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = false, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        [Parameter("Log Skip Reasons", DefaultValue = false, Group = "Logging")]
        public bool LogSkipReasons { get; set; }

        [Parameter("Log Sizing Details", DefaultValue = false, Group = "Logging")]
        public bool LogSizingDetails { get; set; }

        // ── Fitness ──────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 40, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 4.0, MinValue = 1, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 20.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        [Parameter("Backtest Years", DefaultValue = 3.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }

        // ── Indicators / Bars ────────────────────────────────────────
        private AverageTrueRange _atr;
        private Bars _htfBars;
        private ExponentialMovingAverage _htfEma;
        private TimeFrame _resolvedHtf;

        // ── State ────────────────────────────────────────────────────
        private DateTime _lastLondonDay = DateTime.MinValue;
        private int _tradesToday = 0;
        private DateTime _lastExitTime = DateTime.MinValue;
        private double _startOfDayEquity = 0;
        private bool _circuitBroken = false;

        private int _gateDirection = 0;       // 1 = long, -1 = short, 0 = none
        private int _gateBarsRemaining = 0;
        private double _gateSmaTp = 0;        // SMA target
        private double _gateTpDistance = 0;   // TP distance from entry
        private double _gateSLDistance = 0;   // SL distance
        private double _gateZScore = 0;
        private DateTime _gateTime = DateTime.MinValue;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);

            if (UseHtfDirectionFilter && !IsMode(HtfDirectionMode, "Off"))
            {
                _resolvedHtf = ResolveTimeFrame(HtfTimeFrame);
                _htfBars = MarketData.GetBars(_resolvedHtf);
                _htfEma = Indicators.ExponentialMovingAverage(_htfBars.ClosePrices, HtfEmaPeriod);
            }

            _startOfDayEquity = Account.Equity;
            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} v2 started on {SymbolName} ({Bars.TimeFrame}) ═══");
            Print($"ZScore | Period={ZScorePeriod} Threshold={ZScoreThreshold:F2} WickTrigger={UseWickTrigger}");
            Print($"TP Target | Mode={TpTargetMode} {(IsMode(TpTargetMode, "ATRBased") ? $"ATRMult={TpAtrMultiple:F1}" : "")}");
            Print($"Regime | UseHTF={UseHtfDirectionFilter} Mode={HtfDirectionMode} TF={HtfTimeFrame} EMA={HtfEmaPeriod} Slope={UseHtfSlopeFilter}");
            Print($"Confirm | MaxBars={MaxConfirmationBars} TowardSMA={RequireCloseTowardSma} Direction={RequireCloseDirection}");
            Print($"Risk | {RiskPercent}% SL={SlAtrMultiple}ATR MinTP/SL={MinTpToSlRatio:F2} MaxHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"Session={TradeStart}-{TradeEnd} London | MaxTrades={MaxTradesPerDay} | Friday={TradeFriday}");
            Print($"SpreadFilter={(UseSpreadFilter ? $"on max={MaxSpreadPoints:F2} points" : "off")} | DailyLoss={(DailyLossLimitPct <= 0 ? "off" : DailyLossLimitPct.ToString("F2") + "%")}");
            Print($"SPECS | PipSize={Symbol.PipSize} | PipValue={Symbol.PipValue} | LotSize={Symbol.LotSize} | VolMin={Symbol.VolumeInUnitsMin} | VolStep={Symbol.VolumeInUnitsStep} | VolMax={Symbol.VolumeInUnitsMax}");
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
            if (!SafetyOk()) return;
            if (!IndicatorsReady()) return;

            if (_lastExitTime != DateTime.MinValue && Server.Time < _lastExitTime.AddMinutes(CooldownMinutes))
                return;

            if (_tradesToday >= MaxTradesPerDay) return;
            if (HasOpenPosition()) return;

            CheckSignalGate();

            if (_gateDirection != 0)
                CheckConfirmation();
        }

        // ─────────────────────────────────────────────────────────────
        //  SIGNAL GATE — chart-timeframe native
        // ─────────────────────────────────────────────────────────────
        private void CheckSignalGate()
        {
            int bar = Bars.Count - 2;
            if (bar < ZScorePeriod + 2) return;

            double sma = CalculateSMA(bar);
            double stdDev = CalculateStdDev(bar, sma);
            if (stdDev <= 0) return;

            double closeZ = (Bars.ClosePrices[bar] - sma) / stdDev;
            double highZ = (Bars.HighPrices[bar] - sma) / stdDev;
            double lowZ = (Bars.LowPrices[bar] - sma) / stdDev;

            double shortZ = UseWickTrigger ? highZ : closeZ;
            double longZ = UseWickTrigger ? lowZ : closeZ;

            double atrVal = _atr.Result[bar];
            if (atrVal <= 0) return;

            Log($"Z check | CloseZ={closeZ:F2} HighZ={highZ:F2} LowZ={lowZ:F2} | Threshold={ZScoreThreshold:F2}");

            if (AllowLongs && longZ <= -ZScoreThreshold)
            {
                if (!DirectionAllowed(1, out string reason))
                {
                    Skip($"LONG blocked by regime | {reason}");
                    return;
                }

                if (_gateDirection == -1 && ResetOppositeGate)
                    ResetGate("opposite LONG signal");

                if (_gateDirection != 1)
                {
                    _gateDirection = 1;
                    _gateBarsRemaining = MaxConfirmationBars;
                    _gateSmaTp = sma;
                    _gateSLDistance = atrVal * SlAtrMultiple;
                    _gateTpDistance = CalculateTpDistance(1, sma, atrVal);
                    _gateZScore = longZ;
                    _gateTime = Server.Time;

                    Log($"LONG GATE | Z={longZ:F2} <= -{ZScoreThreshold:F2} | SMA={sma:F2} | TP={_gateTpDistance:F2} SL={_gateSLDistance:F2}");
                }

                return;
            }

            if (AllowShorts && shortZ >= ZScoreThreshold)
            {
                if (!DirectionAllowed(-1, out string reason))
                {
                    Skip($"SHORT blocked by regime | {reason}");
                    return;
                }

                if (_gateDirection == 1 && ResetOppositeGate)
                    ResetGate("opposite SHORT signal");

                if (_gateDirection != -1)
                {
                    _gateDirection = -1;
                    _gateBarsRemaining = MaxConfirmationBars;
                    _gateSmaTp = sma;
                    _gateSLDistance = atrVal * SlAtrMultiple;
                    _gateTpDistance = CalculateTpDistance(-1, sma, atrVal);
                    _gateZScore = shortZ;
                    _gateTime = Server.Time;

                    Log($"SHORT GATE | Z={shortZ:F2} >= +{ZScoreThreshold:F2} | SMA={sma:F2} | TP={_gateTpDistance:F2} SL={_gateSLDistance:F2}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  TP Distance Calculation (new logic)
        // ─────────────────────────────────────────────────────────────
        private double CalculateTpDistance(int direction, double sma, double atrVal)
        {
            if (IsMode(TpTargetMode, "SMA"))
            {
                // Original mode: TP target is the SMA itself
                return sma;  // Return SMA, not distance
            }

            if (IsMode(TpTargetMode, "ATRBased"))
            {
                // New mode: TP is SMA ± (ATR × multiple)
                // Distance is measured from SMA
                return atrVal * TpAtrMultiple;
            }

            // Default to SMA
            return sma;
        }

        // ─────────────────────────────────────────────────────────────
        //  HTF REGIME DIRECTION FILTER
        // ─────────────────────────────────────────────────────────────
        private bool DirectionAllowed(int direction, out string reason)
        {
            reason = "HTF filter off";

            if (!UseHtfDirectionFilter || IsMode(HtfDirectionMode, "Off"))
                return true;

            if (_htfBars == null || _htfEma == null || _htfBars.Count < HtfEmaPeriod + HtfSlopeLookbackBars + 5)
            {
                reason = "HTF not ready";
                return false;
            }

            int last = _htfBars.Count - 2;
            int slopeIndex = Math.Max(0, last - HtfSlopeLookbackBars);

            double close = _htfBars.ClosePrices[last];
            double ema = _htfEma.Result[last];
            double emaPrev = _htfEma.Result[slopeIndex];

            bool bull = close > ema;
            bool bear = close < ema;
            bool slopeUp = ema > emaPrev;
            bool slopeDown = ema < emaPrev;

            if (BlockIfHtfNeutral && !bull && !bear)
            {
                reason = $"neutral | close={close:F2} ema={ema:F2}";
                return false;
            }

            if (UseHtfSlopeFilter)
            {
                if (direction == 1 && bull && !slopeUp)
                {
                    reason = $"bull but EMA slope not up | close={close:F2} ema={ema:F2} emaPrev={emaPrev:F2}";
                    return false;
                }

                if (direction == -1 && bear && !slopeDown)
                {
                    reason = $"bear but EMA slope not down | close={close:F2} ema={ema:F2} emaPrev={emaPrev:F2}";
                    return false;
                }
            }

            bool allowed;

            if (IsMode(HtfDirectionMode, "WithTrendReversion"))
            {
                allowed = (direction == 1 && bull) || (direction == -1 && bear);
                reason = $"WithTrend | dir={(direction == 1 ? "LONG" : "SHORT")} close={close:F2} ema={ema:F2} bull={bull} bear={bear}";
                return allowed;
            }

            if (IsMode(HtfDirectionMode, "CounterTrendReversion"))
            {
                allowed = (direction == 1 && bear) || (direction == -1 && bull);
                reason = $"CounterTrend | dir={(direction == 1 ? "LONG" : "SHORT")} close={close:F2} ema={ema:F2} bull={bull} bear={bear}";
                return allowed;
            }

            if (IsMode(HtfDirectionMode, "ShortOnlyBelow"))
            {
                allowed = direction == -1 && bear;
                reason = $"ShortOnlyBelow | close={close:F2} ema={ema:F2} bear={bear}";
                return allowed;
            }

            if (IsMode(HtfDirectionMode, "LongOnlyAbove"))
            {
                allowed = direction == 1 && bull;
                reason = $"LongOnlyAbove | close={close:F2} ema={ema:F2} bull={bull}";
                return allowed;
            }

            reason = $"Unknown mode {HtfDirectionMode}; allowing direction";
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        //  CONFIRMATION
        // ─────────────────────────────────────────────────────────────
        private void CheckConfirmation()
        {
            int prev = Bars.Count - 2;
            int prevPrev = Bars.Count - 3;
            if (prev < 1 || prevPrev < 0) return;

            if (!DirectionAllowed(_gateDirection, out string regimeReason))
            {
                Log($"GATE CANCELLED — regime changed | {regimeReason}");
                ResetGate("regime changed");
                return;
            }

            double lastClose = Bars.ClosePrices[prev];
            double prevClose = Bars.ClosePrices[prevPrev];

            bool directionOk = true;
            if (RequireCloseDirection)
            {
                directionOk =
                    (_gateDirection == 1 && lastClose > prevClose) ||
                    (_gateDirection == -1 && lastClose < prevClose);
            }

            bool towardSmaOk = true;
            if (RequireCloseTowardSma)
            {
                towardSmaOk =
                    (_gateDirection == 1 && lastClose < _gateSmaTp) ||
                    (_gateDirection == -1 && lastClose > _gateSmaTp);
            }

            if (directionOk && towardSmaOk)
            {
                Log($"CONFIRMED {(_gateDirection == 1 ? "LONG" : "SHORT")} | LastClose={lastClose:F2} PrevClose={prevClose:F2} SMA={_gateSmaTp:F2}");
                ExecuteEntry(_gateDirection == 1 ? TradeType.Buy : TradeType.Sell);
                ResetGate("entry attempted");
                return;
            }

            _gateBarsRemaining--;

            if (_gateBarsRemaining <= 0)
            {
                Log($"GATE EXPIRED {(_gateDirection == 1 ? "LONG" : "SHORT")} | GateZ={_gateZScore:F2} | GateTime={_gateTime:yyyy-MM-dd HH:mm}");
                ResetGate("expired");
            }
            else if (LogSkipReasons)
            {
                Log($"WAIT CONFIRM | DirOk={directionOk} TowardSMA={towardSmaOk} | BarsLeft={_gateBarsRemaining}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  EXECUTION
        // ─────────────────────────────────────────────────────────────
        private void ExecuteEntry(TradeType direction)
        {
            if (_gateSLDistance <= 0) return;

            double slPoints = _gateSLDistance;
            double slPips = slPoints / Symbol.PipSize;

            if (MinSlPoints > 0 && slPoints < MinSlPoints)
            {
                Log($"ENTRY BLOCKED — SL {slPoints:F2} pts < min {MinSlPoints:F2}");
                return;
            }

            if (MaxSlPoints > 0 && slPoints > MaxSlPoints)
            {
                Log($"ENTRY BLOCKED — SL {slPoints:F2} pts > max {MaxSlPoints:F2}");
                return;
            }

            double currentPrice = direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;

            // Calculate TP based on mode
            double tpPoints;
            if (IsMode(TpTargetMode, "SMA"))
            {
                // SMA mode: TP is absolute price
                tpPoints = Math.Abs(_gateSmaTp - currentPrice);
            }
            else
            {
                // ATRBased mode: TP is distance from SMA
                tpPoints = _gateTpDistance;
            }

            double tpPips = tpPoints / Symbol.PipSize;

            if (tpPips <= 0)
            {
                Log("ENTRY BLOCKED — TP distance <= 0");
                return;
            }

            double tpToSl = tpPips / slPips;
            if (tpToSl < MinTpToSlRatio)
            {
                Log($"ENTRY BLOCKED — TP/SL {tpToSl:F2} < min {MinTpToSlRatio:F2} | TP={tpPoints:F2}pts SL={slPoints:F2}pts");
                return;
            }

            double volumeInUnits = CalculateVolumeSafe(direction, slPips);
            if (volumeInUnits <= 0)
            {
                Log($"ENTRY BLOCKED — invalid volume | SL={slPips:F1} pips");
                return;
            }

            var result = ExecuteMarketOrder(direction, SymbolName, volumeInUnits, BotLabel, slPips, tpPips);

            if (result.IsSuccessful && result.Position != null)
            {
                _tradesToday++;
                Print($"[{Server.Time:yyyy-MM-dd HH:mm}] {direction.ToString().ToUpper()} OPEN | " +
                      $"Entry={result.Position.EntryPrice:F2} | SL={slPoints:F2}pts/{slPips:F1}p | " +
                      $"TP={tpPoints:F2}pts/{tpPips:F1}p | TargetSMA={_gateSmaTp:F2} | " +
                      $"TP/SL={tpToSl:F2} | Vol={volumeInUnits:F0}");
            }
            else
            {
                Print($"ORDER FAILED: {result.Error}");
            }
        }

        private double CalculateVolumeSafe(TradeType tradeType, double stopLossPips)
        {
            if (RiskPercent <= 0 || stopLossPips <= 0 || Account.Equity <= 0) return 0;

            double volume = 0;
            try
            {
                volume = Symbol.VolumeForProportionalRisk(
                    ProportionalAmountType.Equity,
                    RiskPercent,
                    stopLossPips,
                    RoundingMode.Down);
            }
            catch
            {
                volume = 0;
            }

            if (volume <= 0) return 0;

            double volumeBeforeCap = volume;

            if (VolumeCapUnits > 0)
                volume = Math.Min(volume, VolumeCapUnits);

            volume = Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);

            if (volume < Symbol.VolumeInUnitsMin) return 0;
            if (volume > Symbol.VolumeInUnitsMax) volume = Symbol.VolumeInUnitsMax;

            if (SkipIfVolumeHitsCap && VolumeCapUnits > 0 &&
                volumeBeforeCap > VolumeCapUnits * 1.001 &&
                volume >= VolumeCapUnits)
                return 0;

            double estMargin = 0;
            try { estMargin = Symbol.GetEstimatedMargin(tradeType, volume); } catch { }

            if (estMargin > 0 && estMargin > Account.Equity * (MaxMarginUsagePct / 100.0))
            {
                Log($"ENTRY BLOCKED — margin {estMargin:F2} > allowed {(Account.Equity * MaxMarginUsagePct / 100.0):F2}");
                return 0;
            }

            if (LogSizingDetails)
            {
                double riskMoney = Account.Equity * RiskPercent / 100.0;
                Print($"[SIZE] eq={Account.Equity:F2} risk={riskMoney:F2} SLpips={stopLossPips:F1} " +
                      $"rawVol={volumeBeforeCap:F0} finalVol={volume:F0} margin={(estMargin > 0 ? estMargin.ToString("F2") : "n/a")}");
            }

            return volume;
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT
        // ─────────────────────────────────────────────────────────────
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

        // ─────────────────────────────────────────────────────────────
        //  STATS
        // ─────────────────────────────────────────────────────────────
        private double CalculateSMA(int barIndex)
        {
            double sum = 0;
            int start = barIndex - ZScorePeriod + 1;
            for (int i = start; i <= barIndex; i++)
                sum += Bars.ClosePrices[i];

            return sum / ZScorePeriod;
        }

        private double CalculateStdDev(int barIndex, double sma)
        {
            double sumSqDiff = 0;
            int start = barIndex - ZScorePeriod + 1;
            for (int i = start; i <= barIndex; i++)
            {
                double diff = Bars.ClosePrices[i] - sma;
                sumSqDiff += diff * diff;
            }

            return Math.Sqrt(sumSqDiff / ZScorePeriod);
        }

        // ─────────────────────────────────────────────────────────────
        //  CIRCUIT BREAKER / FILTERS
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
                    ResetGate("circuit breaker");

                    foreach (var pos in Positions.Where(p => p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
                        ClosePosition(pos);
                }
                return true;
            }

            return false;
        }

        private bool SafetyOk()
        {
            if (!UseSpreadFilter) return true;

            double spreadPoints = Symbol.Ask - Symbol.Bid;
            bool ok = spreadPoints <= MaxSpreadPoints;

            if (!ok && LogSkipReasons)
                Log($"SKIP — spread {spreadPoints:F2} pts > max {MaxSpreadPoints:F2}");

            return ok;
        }

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

        private bool InTradeHours(DateTime london)
        {
            if (string.IsNullOrWhiteSpace(TradeStart) || string.IsNullOrWhiteSpace(TradeEnd))
                return true;

            if (!TimeSpan.TryParse(TradeStart, out var start) || !TimeSpan.TryParse(TradeEnd, out var end))
                return true;

            var t = london.TimeOfDay;

            if (start <= end)
                return t >= start && t <= end;

            return t >= start || t <= end;
        }

        private bool IndicatorsReady()
        {
            bool baseReady = Bars.Count > Math.Max(ZScorePeriod + 5, AtrPeriod + 5);

            if (!baseReady) return false;

            if (UseHtfDirectionFilter && !IsMode(HtfDirectionMode, "Off"))
                return _htfBars != null && _htfEma != null && _htfBars.Count > HtfEmaPeriod + HtfSlopeLookbackBars + 5;

            return true;
        }

        private bool HasOpenPosition()
        {
            return Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);
        }

        // ─────────────────────────────────────────────────────────────
        //  FITNESS
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

            if (totalTrades < MinTotalTrades) return -1000000;
            if (tradesPerYear < MinTradesPerYear) return -1000000;
            if (netProfit <= 0) return -1000000;
            if (ddPct > MaxFitnessDrawdownPct) return -1000000;

            double profitScore = Math.Log10(1.0 + netProfit);
            double tradeScore = Math.Sqrt(totalTrades);
            double ddPenalty = Math.Pow(ddPct, 1.45);
            double winBonus = 0.75 + Math.Min(winRate, 0.75);

            return (profitScore * pf * tradeScore * winBonus) / ddPenalty;
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
            ResetGate("new day");

            Print($"NEW LONDON DAY {london:dd-MMM-yyyy ddd}");
        }

        private DateTime LondonNow()
        {
            var utc = Server.Time;
            int offset = IsBST(utc) ? 1 : 0;
            return utc.AddHours(offset);
        }

        private bool IsBST(DateTime utc)
        {
            if (utc.Month < 3 || utc.Month > 10) return false;
            if (utc.Month > 3 && utc.Month < 10) return true;

            int daysInMonth = DateTime.DaysInMonth(utc.Year, utc.Month);
            int lastSunday = daysInMonth;

            while (new DateTime(utc.Year, utc.Month, lastSunday).DayOfWeek != DayOfWeek.Sunday)
                lastSunday--;

            if (utc.Month == 3)
                return utc.Day > lastSunday || (utc.Day == lastSunday && utc.Hour >= 1);

            return utc.Day < lastSunday || (utc.Day == lastSunday && utc.Hour < 1);
        }

        private TimeFrame ResolveTimeFrame(string tf)
        {
            if (tf == null) return TimeFrame.Daily;

            string x = tf.Trim().ToLowerInvariant();

            if (x == "m30" || x == "minute30" || x == "30m") return TimeFrame.Minute30;
            if (x == "h1" || x == "hour" || x == "hour1" || x == "1h") return TimeFrame.Hour;
            if (x == "h2" || x == "hour2" || x == "2h") return TimeFrame.Hour2;
            if (x == "h3" || x == "hour3" || x == "3h") return TimeFrame.Hour3;
            if (x == "h4" || x == "hour4" || x == "4h") return TimeFrame.Hour4;
            if (x == "h6" || x == "hour6" || x == "6h") return TimeFrame.Hour6;
            if (x == "h8" || x == "hour8" || x == "8h") return TimeFrame.Hour8;
            if (x == "h12" || x == "hour12" || x == "12h") return TimeFrame.Hour12;
            if (x == "d1" || x == "daily" || x == "day") return TimeFrame.Daily;

            return TimeFrame.Daily;
        }

        private bool IsMode(string source, string value)
        {
            return source != null && source.Equals(value, StringComparison.OrdinalIgnoreCase);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName || args.Position.Label != BotLabel)
                return;

            _lastExitTime = Server.Time;
        }

        private void ResetGate(string reason)
        {
            _gateDirection = 0;
            _gateBarsRemaining = 0;
            _gateSmaTp = 0;
            _gateTpDistance = 0;
            _gateSLDistance = 0;
            _gateZScore = 0;
            _gateTime = DateTime.MinValue;
        }

        private void Log(string msg)
        {
            if (VerboseLogging)
                Print(msg);
        }

        private void Skip(string msg)
        {
            if (VerboseLogging && LogSkipReasons)
                Print(msg);
        }
    }
}
