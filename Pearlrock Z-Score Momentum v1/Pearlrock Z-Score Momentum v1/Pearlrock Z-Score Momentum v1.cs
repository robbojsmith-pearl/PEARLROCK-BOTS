using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  Pearlrock Z-Score Momentum v1
    //
    //  Strategy logic:
    //    - Computes Z-score of close price over a rolling period
    //    - Enters when ΔZ (bar-to-bar Z acceleration) exceeds EntryDelta
    //    - Optional: require N consecutive bars of momentum before entry
    //    - Exits when Z reaches exhaustion threshold (opposite of entry)
    //    - Optional: exit on momentum reversal (ΔZ flips sign)
    //    - Hard SL = ATR × SlAtrMultiple
    //    - HTF regime filter retained from reversion framework
    //
    //  Pearlrock Systematic
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class Pearlrock_ZScore_Momentum_v1 : Robot
    {
        // ── Identity ──────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "PR_ZSCORE_MOM_V1", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Z-Score ───────────────────────────────────────────────────
        [Parameter("Z-Score Period", DefaultValue = 20, MinValue = 5, MaxValue = 100, Step = 1, Group = "Z-Score")]
        public int ZScorePeriod { get; set; }

        // ── Entry Signal ──────────────────────────────────────────────
        [Parameter("Entry Delta (ΔZ per bar)", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 3.0, Step = 0.1, Group = "Entry Signal")]
        public double EntryDelta { get; set; }

        [Parameter("Require Consecutive Bars", DefaultValue = false, Group = "Entry Signal")]
        public bool RequireConsecutiveBars { get; set; }

        [Parameter("Consecutive Bars Required", DefaultValue = 2, MinValue = 2, MaxValue = 5, Step = 1, Group = "Entry Signal")]
        public int ConsecutiveBarsRequired { get; set; }

        [Parameter("Min Entry Z (0=off)", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 3.0, Step = 0.1, Group = "Entry Signal")]
        public double MinEntryZ { get; set; }

        // ── Exit Signal ───────────────────────────────────────────────
        [Parameter("Exit Z Threshold", DefaultValue = 2.5, MinValue = 0.5, MaxValue = 5.0, Step = 0.1, Group = "Exit Signal")]
        public double ExitZThreshold { get; set; }

        [Parameter("Use Momentum Reversal Exit", DefaultValue = false, Group = "Exit Signal")]
        public bool UseMomentumReversalExit { get; set; }

        [Parameter("Momentum Reversal Delta", DefaultValue = 0.3, MinValue = 0.1, MaxValue = 2.0, Step = 0.1, Group = "Exit Signal")]
        public double MomentumReversalDelta { get; set; }

        // ── HTF Regime ────────────────────────────────────────────────
        [Parameter("Use HTF Direction Filter", DefaultValue = true, Group = "HTF Regime")]
        public bool UseHtfDirectionFilter { get; set; }

        [Parameter("HTF Direction Mode", DefaultValue = "WithTrendMomentum", Group = "HTF Regime")]
        public string HtfDirectionMode { get; set; }
        // Modes: WithTrendMomentum | LongOnlyAbove | ShortOnlyBelow | Off

        [Parameter("HTF TimeFrame", DefaultValue = "H4", Group = "HTF Regime")]
        public string HtfTimeFrame { get; set; }

        [Parameter("HTF EMA Period", DefaultValue = 40, MinValue = 5, MaxValue = 200, Step = 5, Group = "HTF Regime")]
        public int HtfEmaPeriod { get; set; }

        [Parameter("Use HTF EMA Slope Filter", DefaultValue = false, Group = "HTF Regime")]
        public bool UseHtfSlopeFilter { get; set; }

        [Parameter("HTF Slope Lookback Bars", DefaultValue = 3, MinValue = 1, MaxValue = 20, Step = 1, Group = "HTF Regime")]
        public int HtfSlopeLookbackBars { get; set; }

        [Parameter("Block If HTF Neutral", DefaultValue = true, Group = "HTF Regime")]
        public bool BlockIfHtfNeutral { get; set; }

        // ── Risk ──────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, MaxValue = 5.0, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int AtrPeriod { get; set; }

        [Parameter("SL ATR Multiple", DefaultValue = 1.4, MinValue = 0.3, MaxValue = 5.0, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }

        [Parameter("Min SL Points (0=off)", DefaultValue = 0.0, MinValue = 0, Step = 0.5, Group = "Risk")]
        public double MinSlPoints { get; set; }

        [Parameter("Max SL Points (0=off)", DefaultValue = 0.0, MinValue = 0, Step = 0.5, Group = "Risk")]
        public double MaxSlPoints { get; set; }

        [Parameter("Max Trade Duration Hours (0=off)", DefaultValue = 0, MinValue = 0, MaxValue = 240, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        [Parameter("Max Margin Usage %", DefaultValue = 30.0, MinValue = 1.0, MaxValue = 100.0, Step = 1.0, Group = "Risk")]
        public double MaxMarginUsagePct { get; set; }

        [Parameter("Volume Cap Units (0=off)", DefaultValue = 0, MinValue = 0, Step = 1, Group = "Risk")]
        public int VolumeCapUnits { get; set; }

        // ── Direction ─────────────────────────────────────────────────
        [Parameter("Allow Longs", DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

        // ── Trade Control ─────────────────────────────────────────────
        [Parameter("Trade Start HH:mm London", DefaultValue = "00:00", Group = "Trade Control")]
        public string TradeStart { get; set; }

        [Parameter("Trade End HH:mm London", DefaultValue = "23:59", Group = "Trade Control")]
        public string TradeEnd { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 3, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 60, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        // ── Day Filter ────────────────────────────────────────────────
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

        // ── Safety ────────────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = true, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread Points", DefaultValue = 10.0, MinValue = 0, Step = 0.5, Group = "Safety")]
        public double MaxSpreadPoints { get; set; }

        [Parameter("Daily Loss Limit % (0=off)", DefaultValue = 5.0, MinValue = 0, Step = 0.1, Group = "Safety")]
        public double DailyLossLimitPct { get; set; }

        // ── Logging ───────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = false, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        [Parameter("Log Z Values", DefaultValue = false, Group = "Logging")]
        public bool LogZValues { get; set; }

        [Parameter("Log Skip Reasons", DefaultValue = false, Group = "Logging")]
        public bool LogSkipReasons { get; set; }

        [Parameter("Log Sizing Details", DefaultValue = false, Group = "Logging")]
        public bool LogSizingDetails { get; set; }

        [Parameter("Log Exit Reasons", DefaultValue = false, Group = "Logging")]
        public bool LogExitReasons { get; set; }

        // ── Fitness ───────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 40, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 10.0, MinValue = 1.0, Step = 1.0, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 20.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        [Parameter("Backtest Years (0=OOS mode)", DefaultValue = 4.0, MinValue = 0.0, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }
        // Set to 0 to disable all fitness hard gates — use for OOS runs

        // ── Indicators ────────────────────────────────────────────────
        private AverageTrueRange _atr;
        private Bars _htfBars;
        private ExponentialMovingAverage _htfEma;
        private TimeFrame _resolvedHtf;

        // ── State ─────────────────────────────────────────────────────
        private DateTime _lastLondonDay = DateTime.MinValue;
        private int _tradesToday = 0;
        private DateTime _lastExitTime = DateTime.MinValue;
        private double _startOfDayEquity = 0;
        private bool _circuitBroken = false;

        // Momentum tracking
        private int _consecutiveLongBars = 0;
        private int _consecutiveShortBars = 0;

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

            Print($"═══ {BotLabel} started on {SymbolName} ({Bars.TimeFrame}) ═══");
            Print($"ZScore | Period={ZScorePeriod}");
            Print($"Entry | Delta={EntryDelta} ConsecBars={RequireConsecutiveBars} N={ConsecutiveBarsRequired} MinZ={MinEntryZ}");
            Print($"Exit | ExitZ={ExitZThreshold} MomReversal={UseMomentumReversalExit} ReversalDelta={MomentumReversalDelta}");
            Print($"Regime | UseHTF={UseHtfDirectionFilter} Mode={HtfDirectionMode} TF={HtfTimeFrame} EMA={HtfEmaPeriod} Slope={UseHtfSlopeFilter}");
            Print($"Risk | {RiskPercent}% SL={SlAtrMultiple}ATR MaxHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"Session={TradeStart}-{TradeEnd} London | MaxTrades={MaxTradesPerDay} | Friday={TradeFriday}");
            Print($"Spread={(UseSpreadFilter ? $"on max={MaxSpreadPoints}" : "off")} | DailyLoss={(DailyLossLimitPct <= 0 ? "off" : DailyLossLimitPct + "%")}");
            Print($"Fitness | MinTrades={MinTotalTrades} MinPerYear={MinTradesPerYear} MaxDD={MaxFitnessDrawdownPct}% Years={BacktestYears} {(BacktestYears <= 0 ? "(OOS MODE — gates off)" : "")}");
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

            // Check exits on open positions first
            CheckZExits();

            if (_tradesToday >= MaxTradesPerDay) return;
            if (HasOpenPosition()) return;

            CheckMomentumEntry();
        }

        // ─────────────────────────────────────────────────────────────
        //  Z-SCORE CALCULATION
        // ─────────────────────────────────────────────────────────────
        private double CalculateZ(int barIndex)
        {
            if (barIndex < ZScorePeriod + 1) return 0;

            double sma = 0;
            for (int i = barIndex - ZScorePeriod + 1; i <= barIndex; i++)
                sma += Bars.ClosePrices[i];
            sma /= ZScorePeriod;

            double variance = 0;
            for (int i = barIndex - ZScorePeriod + 1; i <= barIndex; i++)
            {
                double diff = Bars.ClosePrices[i] - sma;
                variance += diff * diff;
            }

            double stdDev = Math.Sqrt(variance / ZScorePeriod);
            if (stdDev <= 0) return 0;

            return (Bars.ClosePrices[barIndex] - sma) / stdDev;
        }

        // ─────────────────────────────────────────────────────────────
        //  MOMENTUM ENTRY
        // ─────────────────────────────────────────────────────────────
        private void CheckMomentumEntry()
        {
            int bar = Bars.Count - 2;
            int prevBar = bar - 1;

            if (prevBar < ZScorePeriod + 1) return;

            double zCurr = CalculateZ(bar);
            double zPrev = CalculateZ(prevBar);
            double deltaZ = zCurr - zPrev;

            if (LogZValues)
                Print($"Z | curr={zCurr:F3} prev={zPrev:F3} ΔZ={deltaZ:F3} | consecL={_consecutiveLongBars} consecS={_consecutiveShortBars}");

            // Update consecutive momentum counters
            if (deltaZ > EntryDelta)
            {
                _consecutiveLongBars++;
                _consecutiveShortBars = 0;
            }
            else if (deltaZ < -EntryDelta)
            {
                _consecutiveShortBars++;
                _consecutiveLongBars = 0;
            }
            else
            {
                _consecutiveLongBars = 0;
                _consecutiveShortBars = 0;
            }

            // Check long entry
            if (AllowLongs)
            {
                bool deltaOk = RequireConsecutiveBars
                    ? _consecutiveLongBars >= ConsecutiveBarsRequired
                    : deltaZ > EntryDelta;

                bool minZOk = MinEntryZ <= 0 || zCurr >= MinEntryZ;

                if (deltaOk && minZOk)
                {
                    if (!DirectionAllowed(1, out string reason))
                    {
                        LogSkip($"LONG blocked by regime | {reason}");
                    }
                    else
                    {
                        Log($"LONG MOMENTUM | ΔZ={deltaZ:F3} Z={zCurr:F3} ConsecBars={_consecutiveLongBars}");
                        ExecuteEntry(TradeType.Buy, zCurr, deltaZ);
                        return;
                    }
                }
            }

            // Check short entry
            if (AllowShorts)
            {
                bool deltaOk = RequireConsecutiveBars
                    ? _consecutiveShortBars >= ConsecutiveBarsRequired
                    : deltaZ < -EntryDelta;

                bool minZOk = MinEntryZ <= 0 || zCurr <= -MinEntryZ;

                if (deltaOk && minZOk)
                {
                    if (!DirectionAllowed(-1, out string reason))
                    {
                        LogSkip($"SHORT blocked by regime | {reason}");
                    }
                    else
                    {
                        Log($"SHORT MOMENTUM | ΔZ={deltaZ:F3} Z={zCurr:F3} ConsecBars={_consecutiveShortBars}");
                        ExecuteEntry(TradeType.Sell, zCurr, deltaZ);
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Z-BASED EXIT CHECK
        // ─────────────────────────────────────────────────────────────
        private void CheckZExits()
        {
            var positions = Positions.Where(p => p.SymbolName == SymbolName && p.Label == BotLabel).ToList();
            if (!positions.Any()) return;

            int bar = Bars.Count - 2;
            int prevBar = bar - 1;

            if (bar < ZScorePeriod + 1) return;

            double zCurr = CalculateZ(bar);
            double zPrev = prevBar >= ZScorePeriod + 1 ? CalculateZ(prevBar) : 0;
            double deltaZ = zCurr - zPrev;

            foreach (var pos in positions)
            {
                bool shouldExit = false;
                string exitReason = "";

                if (pos.TradeType == TradeType.Buy)
                {
                    // Long exhaustion: Z pushed too far up
                    if (zCurr >= ExitZThreshold)
                    {
                        shouldExit = true;
                        exitReason = $"Z exhaustion | Z={zCurr:F3} >= {ExitZThreshold}";
                    }
                    // Momentum reversal: Z starts falling hard
                    else if (UseMomentumReversalExit && deltaZ < -MomentumReversalDelta)
                    {
                        shouldExit = true;
                        exitReason = $"Momentum reversal | ΔZ={deltaZ:F3} < -{MomentumReversalDelta}";
                    }
                }
                else if (pos.TradeType == TradeType.Sell)
                {
                    // Short exhaustion: Z pushed too far down
                    if (zCurr <= -ExitZThreshold)
                    {
                        shouldExit = true;
                        exitReason = $"Z exhaustion | Z={zCurr:F3} <= -{ExitZThreshold}";
                    }
                    // Momentum reversal: Z starts rising hard
                    else if (UseMomentumReversalExit && deltaZ > MomentumReversalDelta)
                    {
                        shouldExit = true;
                        exitReason = $"Momentum reversal | ΔZ={deltaZ:F3} > {MomentumReversalDelta}";
                    }
                }

                if (shouldExit)
                {
                    if (LogExitReasons)
                        Print($"[EXIT] {pos.TradeType} | {exitReason} | Net={pos.NetProfit:F2}");

                    ClosePosition(pos);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  EXECUTION
        // ─────────────────────────────────────────────────────────────
        private void ExecuteEntry(TradeType direction, double entryZ, double deltaZ)
        {
            double atrVal = _atr.Result[Bars.Count - 2];
            if (atrVal <= 0) return;

            double slPoints = atrVal * SlAtrMultiple;
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

            double volumeInUnits = CalculateVolumeSafe(direction, slPips);
            if (volumeInUnits <= 0)
            {
                Log($"ENTRY BLOCKED — invalid volume | SL={slPips:F1} pips");
                return;
            }

            // No fixed TP — Z exhaustion exit handles it, but pass 0 for market order
            var result = ExecuteMarketOrder(direction, SymbolName, volumeInUnits, BotLabel, slPips, null);

            if (result.IsSuccessful && result.Position != null)
            {
                _tradesToday++;
                Print($"[{Server.Time:yyyy-MM-dd HH:mm}] {direction.ToString().ToUpper()} OPEN | " +
                      $"Entry={result.Position.EntryPrice:F5} | " +
                      $"SL={slPoints:F2}pts/{slPips:F1}p | " +
                      $"Z={entryZ:F3} ΔZ={deltaZ:F3} | " +
                      $"ExitZ={ExitZThreshold} | Vol={volumeInUnits:F0}");
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
            catch { volume = 0; }

            if (volume <= 0) return 0;

            double volumeBeforeCap = volume;

            if (VolumeCapUnits > 0)
                volume = Math.Min(volume, VolumeCapUnits);

            volume = Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);

            if (volume < Symbol.VolumeInUnitsMin) return 0;
            if (volume > Symbol.VolumeInUnitsMax) volume = Symbol.VolumeInUnitsMax;

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
        //  HTF REGIME FILTER
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
                reason = $"neutral | close={close:F5} ema={ema:F5}";
                return false;
            }

            if (UseHtfSlopeFilter)
            {
                if (direction == 1 && !slopeUp)
                {
                    reason = $"slope not up | ema={ema:F5} emaPrev={emaPrev:F5}";
                    return false;
                }
                if (direction == -1 && !slopeDown)
                {
                    reason = $"slope not down | ema={ema:F5} emaPrev={emaPrev:F5}";
                    return false;
                }
            }

            if (IsMode(HtfDirectionMode, "WithTrendMomentum"))
            {
                bool allowed = (direction == 1 && bull) || (direction == -1 && bear);
                reason = $"WithTrend | dir={(direction == 1 ? "LONG" : "SHORT")} bull={bull} bear={bear}";
                return allowed;
            }

            if (IsMode(HtfDirectionMode, "LongOnlyAbove"))
            {
                bool allowed = direction == 1 && bull;
                reason = $"LongOnlyAbove | bull={bull}";
                return allowed;
            }

            if (IsMode(HtfDirectionMode, "ShortOnlyBelow"))
            {
                bool allowed = direction == -1 && bear;
                reason = $"ShortOnlyBelow | bear={bear}";
                return allowed;
            }

            reason = $"Unknown mode {HtfDirectionMode} — allowing";
            return true;
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
        //  FITNESS FUNCTION
        // ─────────────────────────────────────────────────────────────
        protected override double GetFitness(GetFitnessArgs args)
        {
            double totalTrades = args.TotalTrades;
            double netProfit = args.NetProfit;
            double ddPct = Math.Max(args.MaxEquityDrawdownPercentages, 0.01);
            double pf = Math.Min(args.ProfitFactor, 3.0);
            double winRate = args.WinningTrades / Math.Max(totalTrades, 1);

            // OOS mode — BacktestYears = 0 disables all hard gates
            bool oosMode = BacktestYears <= 0;

            if (!oosMode)
            {
                double testYears = BacktestYears;
                double tradesPerYear = totalTrades / testYears;

                if (totalTrades < MinTotalTrades) return -1000000;
                if (tradesPerYear < MinTradesPerYear) return -1000000;
                if (netProfit <= 0) return -1000000;
                if (ddPct > MaxFitnessDrawdownPct) return -1000000;
            }
            else
            {
                // OOS mode: still penalise zero/negative profit, no other hard gates
                if (netProfit <= 0) return -1000000;
            }

            double profitScore = Math.Log10(1.0 + Math.Abs(netProfit));
            double tradeScore = Math.Sqrt(totalTrades);
            double ddPenalty = Math.Pow(ddPct, 2.0);
            double winBonus = 0.50 + Math.Min(winRate, 1.00);

            return (profitScore * pf * tradeScore * winBonus) / ddPenalty;
        }

        // ─────────────────────────────────────────────────────────────
        //  SAFETY / FILTERS
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

        private bool SafetyOk()
        {
            if (!UseSpreadFilter) return true;

            double spreadPoints = Symbol.Ask - Symbol.Bid;
            bool ok = spreadPoints <= MaxSpreadPoints;

            if (!ok)
                LogSkip($"SKIP — spread {spreadPoints:F2} pts > max {MaxSpreadPoints:F2}");

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
            if (Bars.Count < ZScorePeriod + 10) return false;

            if (UseHtfDirectionFilter && !IsMode(HtfDirectionMode, "Off"))
                return _htfBars != null && _htfEma != null &&
                       _htfBars.Count > HtfEmaPeriod + HtfSlopeLookbackBars + 5;

            return true;
        }

        private bool HasOpenPosition()
        {
            return Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);
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
            _consecutiveLongBars = 0;
            _consecutiveShortBars = 0;

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

            if (x == "m15") return TimeFrame.Minute15;
            if (x == "m30") return TimeFrame.Minute30;
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

            if (LogExitReasons)
                Print($"[CLOSED] {args.Position.TradeType} | Net={args.Position.NetProfit:F2} | Reason={args.Reason}");
        }

        private void Log(string msg)
        {
            if (VerboseLogging) Print(msg);
        }

        private void LogSkip(string msg)
        {
            if (LogSkipReasons) Print(msg);
        }
    }
}
