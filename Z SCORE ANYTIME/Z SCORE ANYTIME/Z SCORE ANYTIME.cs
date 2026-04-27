using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  Pearlrock H2 Z-Score Mean Reversion v1 — XAU / XAG
    //
    //  Designed for:
    //  - XAUUSD H2
    //  - XAGUSD H2
    //
    //  Core idea:
    //  1. Use the chart timeframe as the signal timeframe.
    //     Attach this bot to H2 if you want H2-native logic.
    //  2. Detect statistical stretch using close-to-SMA Z-score.
    //  3. Open a gate when price is stretched:
    //       +Z = short candidate
    //       -Z = long candidate
    //  4. Enter only after confirmation: closed bar moves back toward mean.
    //  5. TP targets the SMA/mean area, with optional TP-vs-SL sanity check.
    //  6. SL uses chart-timeframe ATR.
    //
    //  Why this version exists:
    //  The older version used H1 signal bars but chart-timeframe confirmation.
    //  On H2 this created a hybrid H1/H2 engine and caused confusing missed trades.
    //  This version is H2-native when attached to an H2 chart.
    //
    //  Suggested first tests:
    //  XAUUSD H2:
    //    ZPeriod 30, ZThreshold 2.5, SL 2.0 ATR, MaxConfirm 3, MaxHours 18
    //
    //  XAGUSD H2:
    //    ZPeriod 30-40, ZThreshold 2.2-2.8, SL 2.2 ATR, MaxConfirm 3-5
    //
    //  Attach to: H2 chart.
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class Pearlrock_H2_ZScore_MR_XAU_XAG_v1 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "PR_H2_ZSCORE_MR_V1", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Z-Score Signal ───────────────────────────────────────────
        [Parameter("Z-Score Period", DefaultValue = 30, MinValue = 5, Step = 1, Group = "Z-Score Signal")]
        public int ZScorePeriod { get; set; }

        [Parameter("Z-Score Entry Threshold", DefaultValue = 2.5, MinValue = 1.0, MaxValue = 5.0, Step = 0.1, Group = "Z-Score Signal")]
        public double ZScoreThreshold { get; set; }

        [Parameter("Use High/Low Wick Trigger", DefaultValue = false, Group = "Z-Score Signal")]
        public bool UseWickTrigger { get; set; }

        [Parameter("Reset Opposite Gate", DefaultValue = true, Group = "Z-Score Signal")]
        public bool ResetOppositeGate { get; set; }

        // ── Confirmation Gate ────────────────────────────────────────
        [Parameter("Max Bars to Wait", DefaultValue = 3, MinValue = 1, MaxValue = 24, Step = 1, Group = "Confirmation")]
        public int MaxConfirmationBars { get; set; }

        [Parameter("Require Close Toward SMA", DefaultValue = true, Group = "Confirmation")]
        public bool RequireCloseTowardSma { get; set; }

        [Parameter("Require Close Direction", DefaultValue = true, Group = "Confirmation")]
        public bool RequireCloseDirection { get; set; }

        // ── Risk ─────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.20, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int AtrPeriod { get; set; }

        [Parameter("SL ATR Multiple", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }

        [Parameter("Min SL Points (0=off)", DefaultValue = 0.0, MinValue = 0, Step = 0.5, Group = "Risk")]
        public double MinSlPoints { get; set; }

        [Parameter("Max SL Points (0=off)", DefaultValue = 0.0, MinValue = 0, Step = 0.5, Group = "Risk")]
        public double MaxSlPoints { get; set; }

        [Parameter("Min TP / SL Ratio", DefaultValue = 0.50, MinValue = 0.0, MaxValue = 5.0, Step = 0.05, Group = "Risk")]
        public double MinTpToSlRatio { get; set; }

        [Parameter("Max Trade Duration Hours (0=off)", DefaultValue = 18, MinValue = 0, MaxValue = 120, Step = 1, Group = "Risk")]
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

        [Parameter("Max Trades Per Day", DefaultValue = 1, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 120, MinValue = 0, Group = "Trade Control")]
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

        [Parameter("Max Spread Points", DefaultValue = 1.00, MinValue = 0, Step = 0.05, Group = "Safety")]
        public double MaxSpreadPoints { get; set; }

        [Parameter("Daily Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Safety")]
        public double DailyLossLimitPct { get; set; }

        // ── Logging ──────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        [Parameter("Log Skip Reasons", DefaultValue = true, Group = "Logging")]
        public bool LogSkipReasons { get; set; }

        [Parameter("Log Sizing Details", DefaultValue = true, Group = "Logging")]
        public bool LogSizingDetails { get; set; }

        // ── Fitness ──────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 30, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 4, MinValue = 1, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 20.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        [Parameter("Backtest Years", DefaultValue = 7.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }

        // ── Indicators ───────────────────────────────────────────────
        private AverageTrueRange _atr;

        // ── State ────────────────────────────────────────────────────
        private DateTime _lastLondonDay = DateTime.MinValue;
        private int _tradesToday = 0;
        private DateTime _lastExitTime = DateTime.MinValue;
        private double _startOfDayEquity = 0;
        private bool _circuitBroken = false;

        private int _gateDirection = 0;       // 1 = long, -1 = short, 0 = none
        private int _gateBarsRemaining = 0;
        private double _gateTargetPrice = 0;
        private double _gateSLDistance = 0;
        private double _gateZScore = 0;
        private DateTime _gateTime = DateTime.MinValue;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _startOfDayEquity = Account.Equity;

            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} started on {SymbolName} ({Bars.TimeFrame}) ═══");
            Print($"IMPORTANT | This bot is chart-timeframe native. Attach to H2 for H2 logic.");
            Print($"ZScorePeriod={ZScorePeriod} | Threshold={ZScoreThreshold} | WickTrigger={UseWickTrigger}");
            Print($"Confirmation | MaxBars={MaxConfirmationBars} | TowardSMA={RequireCloseTowardSma} | Direction={RequireCloseDirection}");
            Print($"Risk | {RiskPercent}% | SL={SlAtrMultiple} ATR | MinTP/SL={MinTpToSlRatio:F2} | MaxHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"Session={TradeStart}-{TradeEnd} London | DayFilter={UseDayFilter} | MaxTrades={MaxTradesPerDay}");
            Print($"SpreadFilter={(UseSpreadFilter ? $"on max={MaxSpreadPoints:F2} points" : "off")}");
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

            Log($"H2 check | CloseZ={closeZ:F2} HighZ={highZ:F2} LowZ={lowZ:F2} | Threshold={ZScoreThreshold:F2}");

            if (AllowLongs && longZ <= -ZScoreThreshold)
            {
                if (_gateDirection == -1 && ResetOppositeGate)
                    ResetGate("opposite LONG signal");

                if (_gateDirection != 1)
                {
                    _gateDirection = 1;
                    _gateBarsRemaining = MaxConfirmationBars;
                    _gateTargetPrice = sma;
                    _gateSLDistance = atrVal * SlAtrMultiple;
                    _gateZScore = longZ;
                    _gateTime = Server.Time;

                    Log($"LONG GATE | Z={longZ:F2} <= -{ZScoreThreshold:F2} | SMA={sma:F3} | SLDist={_gateSLDistance:F3}");
                }
                return;
            }

            if (AllowShorts && shortZ >= ZScoreThreshold)
            {
                if (_gateDirection == 1 && ResetOppositeGate)
                    ResetGate("opposite SHORT signal");

                if (_gateDirection != -1)
                {
                    _gateDirection = -1;
                    _gateBarsRemaining = MaxConfirmationBars;
                    _gateTargetPrice = sma;
                    _gateSLDistance = atrVal * SlAtrMultiple;
                    _gateZScore = shortZ;
                    _gateTime = Server.Time;

                    Log($"SHORT GATE | Z={shortZ:F2} >= +{ZScoreThreshold:F2} | SMA={sma:F3} | SLDist={_gateSLDistance:F3}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  CONFIRMATION
        // ─────────────────────────────────────────────────────────────
        private void CheckConfirmation()
        {
            int prev = Bars.Count - 2;
            int prevPrev = Bars.Count - 3;
            if (prev < 1 || prevPrev < 0) return;

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
                    (_gateDirection == 1 && lastClose < _gateTargetPrice) ||
                    (_gateDirection == -1 && lastClose > _gateTargetPrice);
            }

            if (directionOk && towardSmaOk)
            {
                Log($"CONFIRMED {(_gateDirection == 1 ? "LONG" : "SHORT")} | LastClose={lastClose:F3} PrevClose={prevClose:F3} SMA={_gateTargetPrice:F3}");
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
                Log($"ENTRY BLOCKED — SL {slPoints:F3} pts < min {MinSlPoints:F3}");
                return;
            }

            if (MaxSlPoints > 0 && slPoints > MaxSlPoints)
            {
                Log($"ENTRY BLOCKED — SL {slPoints:F3} pts > max {MaxSlPoints:F3}");
                return;
            }

            double currentPrice = direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double tpDistance = Math.Abs(_gateTargetPrice - currentPrice);
            double tpPips = tpDistance / Symbol.PipSize;

            if (tpPips <= 0)
            {
                Log("ENTRY BLOCKED — TP distance <= 0");
                return;
            }

            double tpToSl = tpPips / slPips;
            if (tpToSl < MinTpToSlRatio)
            {
                Log($"ENTRY BLOCKED — TP/SL {tpToSl:F2} < min {MinTpToSlRatio:F2} | TP={tpDistance:F3}pts SL={slPoints:F3}pts");
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
                      $"Entry={result.Position.EntryPrice:F3} | SL={slPoints:F3}pts/{slPips:F1}p | " +
                      $"TP={tpDistance:F3}pts/{tpPips:F1}p | TargetSMA={_gateTargetPrice:F3} | " +
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
                    ResetGate("circuit breaker");

                    foreach (var pos in Positions.Where(p => p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
                        ClosePosition(pos);
                }
                return true;
            }

            return false;
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
            return Bars.Count > Math.Max(ZScorePeriod + 5, AtrPeriod + 5);
        }

        private bool SafetyOk()
        {
            if (!UseSpreadFilter) return true;

            double spreadPoints = Symbol.Ask - Symbol.Bid;
            bool ok = spreadPoints <= MaxSpreadPoints;

            if (!ok && LogSkipReasons)
                Log($"SKIP — spread {spreadPoints:F3} pts > max {MaxSpreadPoints:F3}");

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

        private void ResetGate(string reason)
        {
            _gateDirection = 0;
            _gateBarsRemaining = 0;
            _gateTargetPrice = 0;
            _gateSLDistance = 0;
            _gateZScore = 0;
            _gateTime = DateTime.MinValue;
        }

        private void Log(string msg)
        {
            if (VerboseLogging)
                Print(msg);
        }
    }
}
