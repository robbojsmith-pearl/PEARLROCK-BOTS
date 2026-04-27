using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  ZScore Mean Reversion v2.2 — Option B: M5 Reclaim Confirmation
    //  Pearlrock Systematic
    //
    //  Based on v2 / v2.1 structural fixes.
    //
    //  Core idea:
    //  - H1 z-score identifies statistical stretch
    //  - M5 confirmation waits for a small reclaim / structure break
    //  - TP targets H1 SMA
    //  - SL uses M5 ATR
    //
    //  Changes from v2.1:
    //  1. M5 confirmation changed from:
    //       Long:  last close > previous close
    //       Short: last close < previous close
    //
    //     To:
    //       Long:  last close > previous M5 high
    //       Short: last close < previous M5 low
    //
    //  2. No new optimisable parameters added.
    //
    //  Structural fixes retained:
    //  - H1 dedupe uses completed H1 OpenTime
    //  - H1 dedupe is not reset on new London day
    //  - TP target must be on profitable side of entry
    //  - Hardcoded 0.50R minimum TP sanity check
    //
    //  Original v2 reference loaded from user file. 
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class ZScore_MeanReversion_v2_2_OptionB : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "ZSCORE_MR_V2_2B", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Z-Score Signal (H1) ───────────────────────────────────────
        [Parameter("Z-Score Period (H1 bars)", DefaultValue = 20, MinValue = 5, Step = 1, Group = "Z-Score Signal")]
        public int ZScorePeriod { get; set; }

        [Parameter("Z-Score Entry Threshold", DefaultValue = 2.5, MinValue = 1.0, MaxValue = 5.0, Step = 0.1, Group = "Z-Score Signal")]
        public double ZScoreThreshold { get; set; }

        [Parameter("Z-Score Exit Level (TP target)", DefaultValue = 0.0, MinValue = -1.0, MaxValue = 1.0, Step = 0.1, Group = "Z-Score Signal")]
        public double ZScoreExitLevel { get; set; }

        // ── M5 Confirmation Gate ──────────────────────────────────────
        [Parameter("Max M5 Bars to Wait for Confirmation", DefaultValue = 3, MinValue = 1, MaxValue = 24, Step = 1, Group = "M5 Confirmation")]
        public int MaxConfirmationBars { get; set; }

        // ── Risk ──────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("ATR Period (M5)", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int AtrPeriod { get; set; }

        [Parameter("SL ATR Multiple", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }

        [Parameter("Max Trade Duration (Hours, 0=off)", DefaultValue = 12, MinValue = 0, MaxValue = 72, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        [Parameter("Max SL Pips (0=off)", DefaultValue = 0, MinValue = 0, Step = 5, Group = "Risk")]
        public double MaxSlPips { get; set; }

        // ── Direction ─────────────────────────────────────────────────
        [Parameter("Allow Longs", DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

        // ── Trade Control ─────────────────────────────────────────────
        [Parameter("Trade Start (HH:mm London)", DefaultValue = "00:00", Group = "Trade Control")]
        public string TradeStart { get; set; }

        [Parameter("Trade End (HH:mm London)", DefaultValue = "07:00", Group = "Trade Control")]
        public string TradeEnd { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 2, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 60, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

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

        // ── Safety ────────────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = false, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread ($ / price units)", DefaultValue = 1.0, Step = 0.1, Group = "Safety")]
        public double MaxSpreadUsd { get; set; }

        // ── Circuit Breaker ───────────────────────────────────────────
        [Parameter("Daily Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Circuit Breaker")]
        public double DailyLossLimitPct { get; set; }

        // ── Fitness ───────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 20, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 4, MinValue = 1, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 20.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        [Parameter("Backtest Years", DefaultValue = 7.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }

        // ── Logging ───────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        // ── Structural constants ──────────────────────────────────────
        private const double MinRewardToRisk = 0.50;

        // ── Indicators ────────────────────────────────────────────────
        private Bars _h1Bars;
        private AverageTrueRange _atr;

        // ── Daily state ───────────────────────────────────────────────
        private DateTime _lastLondonDay = DateTime.MinValue;
        private int _tradesToday = 0;
        private DateTime _lastExitTime = DateTime.MinValue;
        private double _startOfDayEquity = 0;
        private bool _circuitBroken = false;

        // ── Gate state ────────────────────────────────────────────────
        private int _gateDirection = 0;
        private int _gateBarsRemaining = 0;
        private double _gateTargetPrice = 0;
        private double _gateSLDistance = 0;

        // H1 deduplication.
        // Identity is the OpenTime of the last completed H1 candle.
        private DateTime _lastProcessedCompletedH1OpenTime = DateTime.MinValue;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5)
            {
                Print($"ABORT: Must run on M5. Current = {Bars.TimeFrame}");
                Stop();
                return;
            }

            _h1Bars = MarketData.GetBars(TimeFrame.Hour);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);

            _startOfDayEquity = Account.Equity;

            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} started on {SymbolName} M5 ═══");
            Print("Version=v2.2 Option B — M5 Reclaim Confirmation");
            Print($"ZScorePeriod={ZScorePeriod} | ZThreshold={ZScoreThreshold} | ExitLevel={ZScoreExitLevel}");
            Print($"Session={TradeStart}-{TradeEnd} London | DayFilter={UseDayFilter}");
            Print($"M5Confirm=Close above previous high for long / below previous low for short");
            Print($"SL={SlAtrMultiple}ATR | MaxTradeHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"MinRewardToRisk={MinRewardToRisk:F2}R");
            Print($"SpreadFilter={(UseSpreadFilter ? $"on max={MaxSpreadUsd}" : "off")}");
            Print($"CircuitBreaker={(DailyLossLimitPct > 0 ? DailyLossLimitPct + "%" : "off")}");
            Print($"SPECS | PipSize={Symbol.PipSize} | VolMin={Symbol.VolumeInUnitsMin} | VolMax={Symbol.VolumeInUnitsMax}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5)
                return;

            var london = LondonNow();

            ResetDailyStateIfNeeded(london);
            ManageOpenPositions();

            if (CheckCircuitBreaker()) return;
            if (!PassesDayFilter(london)) return;
            if (!InTradeHours(london)) return;
            if (!SafetyOk()) return;
            if (!IndicatorsReady()) return;

            if (_lastExitTime != DateTime.MinValue &&
                Server.Time < _lastExitTime.AddMinutes(CooldownMinutes))
                return;

            if (_tradesToday >= MaxTradesPerDay) return;
            if (HasOpenPosition()) return;

            CheckH1Signal();

            if (_gateDirection != 0)
                CheckM5Confirmation();
        }

        // ─────────────────────────────────────────────────────────────
        //  H1 SIGNAL GATE
        // ─────────────────────────────────────────────────────────────
        private void CheckH1Signal()
        {
            if (_h1Bars.Count < ZScorePeriod + 2)
                return;

            // Last completed H1 bar.
            // Count - 1 is normally the currently forming H1 bar.
            int lastH1 = _h1Bars.Count - 2;

            if (lastH1 < ZScorePeriod)
                return;

            DateTime completedH1OpenTime = _h1Bars.OpenTimes[lastH1];

            if (completedH1OpenTime == _lastProcessedCompletedH1OpenTime)
                return;

            _lastProcessedCompletedH1OpenTime = completedH1OpenTime;

            double zScore = CalculateZScore(lastH1);
            double atrVal = _atr.Result.LastValue;

            if (atrVal <= 0)
                return;

            double smaVal = CalculateSMA(lastH1);

            Log($"H1 CHECK | CompletedH1={completedH1OpenTime:yyyy-MM-dd HH:mm} | Z={zScore:F2} | Threshold={ZScoreThreshold}");

            if (AllowLongs && zScore <= -ZScoreThreshold)
            {
                _gateDirection = 1;
                _gateBarsRemaining = MaxConfirmationBars;
                _gateTargetPrice = smaVal;
                _gateSLDistance = atrVal * SlAtrMultiple;

                Log($"H1 LONG GATE | Z={zScore:F2} <= -{ZScoreThreshold} | " +
                    $"TPTarget={smaVal:F5} | SLDist={_gateSLDistance:F5} | WaitBars={MaxConfirmationBars}");

                return;
            }

            if (AllowShorts && zScore >= ZScoreThreshold)
            {
                _gateDirection = -1;
                _gateBarsRemaining = MaxConfirmationBars;
                _gateTargetPrice = smaVal;
                _gateSLDistance = atrVal * SlAtrMultiple;

                Log($"H1 SHORT GATE | Z={zScore:F2} >= +{ZScoreThreshold} | " +
                    $"TPTarget={smaVal:F5} | SLDist={_gateSLDistance:F5} | WaitBars={MaxConfirmationBars}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  M5 CONFIRMATION — OPTION B
        //
        //  Long:
        //      Last completed M5 close must reclaim above the previous
        //      completed M5 high.
        //
        //  Short:
        //      Last completed M5 close must break below the previous
        //      completed M5 low.
        //
        //  This is stricter than close-vs-close and asks for a small
        //  structure break before entering.
        // ─────────────────────────────────────────────────────────────
        private void CheckM5Confirmation()
        {
            _gateBarsRemaining--;

            if (_gateBarsRemaining <= 0)
            {
                Log($"GATE EXPIRED | Direction={(_gateDirection == 1 ? "LONG" : "SHORT")}");
                ResetGate();
                return;
            }

            int lastCompleted = Bars.Count - 2;
            int previousCompleted = Bars.Count - 3;

            if (lastCompleted < 1 || previousCompleted < 0)
                return;

            double lastClose = Bars.ClosePrices[lastCompleted];
            double prevHigh = Bars.HighPrices[previousCompleted];
            double prevLow = Bars.LowPrices[previousCompleted];

            bool longConfirmed = _gateDirection == 1 && lastClose > prevHigh;
            bool shortConfirmed = _gateDirection == -1 && lastClose < prevLow;

            Log($"M5 CHECK | Dir={(_gateDirection == 1 ? "LONG" : "SHORT")} | " +
                $"BarsLeft={_gateBarsRemaining} | LastClose={lastClose:F5} | " +
                $"PrevHigh={prevHigh:F5} | PrevLow={prevLow:F5}");

            if (!longConfirmed && !shortConfirmed)
                return;

            Log($"M5 RECLAIM CONFIRMED {(_gateDirection == 1 ? "LONG" : "SHORT")} | " +
                $"LastClose={lastClose:F5} | PrevHigh={prevHigh:F5} | PrevLow={prevLow:F5}");

            ExecuteEntry(_gateDirection == 1 ? TradeType.Buy : TradeType.Sell);
            ResetGate();
        }

        // ─────────────────────────────────────────────────────────────
        //  TRADE EXECUTION
        // ─────────────────────────────────────────────────────────────
        private void ExecuteEntry(TradeType direction)
        {
            if (_gateSLDistance <= 0)
            {
                Log("ENTRY BLOCKED — invalid SL distance");
                return;
            }

            double slPips = _gateSLDistance / Symbol.PipSize;

            if (slPips <= 0)
            {
                Log($"ENTRY BLOCKED — invalid SL pips {slPips:F2}");
                return;
            }

            if (MaxSlPips > 0 && slPips > MaxSlPips)
            {
                Log($"ENTRY BLOCKED — SL {slPips:F1}p > max {MaxSlPips}p");
                return;
            }

            double currentPrice = direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;

            // Mean-reversion target must be on the profitable side of entry.
            // Do not use Math.Abs before this check.
            if (direction == TradeType.Buy && _gateTargetPrice <= currentPrice)
            {
                Log($"ENTRY BLOCKED — LONG target not above entry | Target={_gateTargetPrice:F5} | Entry={currentPrice:F5}");
                return;
            }

            if (direction == TradeType.Sell && _gateTargetPrice >= currentPrice)
            {
                Log($"ENTRY BLOCKED — SHORT target not below entry | Target={_gateTargetPrice:F5} | Entry={currentPrice:F5}");
                return;
            }

            double tpDistance = Math.Abs(_gateTargetPrice - currentPrice);
            double tpPips = tpDistance / Symbol.PipSize;

            if (tpPips < slPips * MinRewardToRisk)
            {
                Log($"ENTRY BLOCKED — TP {tpPips:F1}p too small vs SL {slPips:F1}p | MinRR={MinRewardToRisk:F2}");
                return;
            }

            double volumeInUnits = Symbol.VolumeForProportionalRisk(
                ProportionalAmountType.Equity,
                RiskPercent,
                slPips,
                RoundingMode.Down);

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Log($"VOLUME TOO SMALL | Calc={volumeInUnits} | Min={Symbol.VolumeInUnitsMin}");
                return;
            }

            if (volumeInUnits > Symbol.VolumeInUnitsMax)
                volumeInUnits = Symbol.VolumeInUnitsMax;

            var result = ExecuteMarketOrder(
                direction,
                SymbolName,
                volumeInUnits,
                BotLabel,
                slPips,
                tpPips);

            if (result.IsSuccessful)
            {
                _tradesToday++;

                Print($"[{Server.Time:yyyy-MM-dd HH:mm}] {direction.ToString().ToUpper()} OPEN | " +
                      $"Entry={result.Position.EntryPrice:F5} | " +
                      $"SL={slPips:F1}p | TP={tpPips:F1}p | " +
                      $"TPTarget={_gateTargetPrice:F5} | Vol={volumeInUnits:F0}");
            }
            else
            {
                Print($"ORDER FAILED | {result.Error}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT
        // ─────────────────────────────────────────────────────────────
        private void ManageOpenPositions()
        {
            if (MaxTradeHours <= 0)
                return;

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

        // ─────────────────────────────────────────────────────────────
        //  Z-SCORE CALCULATION
        // ─────────────────────────────────────────────────────────────
        private double CalculateZScore(int barIndex)
        {
            double sma = CalculateSMA(barIndex);
            double stdDev = CalculateStdDev(barIndex, sma);

            if (stdDev <= 0)
                return 0;

            return (_h1Bars.ClosePrices[barIndex] - sma) / stdDev;
        }

        private double CalculateSMA(int barIndex)
        {
            double sum = 0;

            for (int i = barIndex - ZScorePeriod + 1; i <= barIndex; i++)
                sum += _h1Bars.ClosePrices[i];

            return sum / ZScorePeriod;
        }

        private double CalculateStdDev(int barIndex, double sma)
        {
            double sumSqDiff = 0;

            for (int i = barIndex - ZScorePeriod + 1; i <= barIndex; i++)
            {
                double diff = _h1Bars.ClosePrices[i] - sma;
                sumSqDiff += diff * diff;
            }

            return Math.Sqrt(sumSqDiff / ZScorePeriod);
        }

        // ─────────────────────────────────────────────────────────────
        //  CIRCUIT BREAKER
        // ─────────────────────────────────────────────────────────────
        private bool CheckCircuitBreaker()
        {
            if (DailyLossLimitPct <= 0 || _startOfDayEquity <= 0)
                return false;

            double lossPercent = (_startOfDayEquity - Account.Equity) / _startOfDayEquity * 100.0;

            if (lossPercent >= DailyLossLimitPct)
            {
                if (!_circuitBroken)
                {
                    Print($"⚡ CIRCUIT BREAKER | Loss={lossPercent:F2}% >= {DailyLossLimitPct}%");

                    _circuitBroken = true;
                    ResetGate();

                    foreach (var pos in Positions
                        .Where(p => p.SymbolName == SymbolName && p.Label == BotLabel)
                        .ToList())
                    {
                        ClosePosition(pos);
                    }
                }

                return true;
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────
        //  CUSTOM FITNESS
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
            double ddPenalty = Math.Pow(ddPct, 1.5);
            double winBonus = Math.Pow(winRate, 2);

            return (profitScore * pf * tradeScore * winBonus) / ddPenalty;
        }

        // ─────────────────────────────────────────────────────────────
        //  DAY OF WEEK FILTER
        // ─────────────────────────────────────────────────────────────
        private bool PassesDayFilter(DateTime london)
        {
            if (!UseDayFilter)
                return true;

            switch (london.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    return TradeMonday;

                case DayOfWeek.Tuesday:
                    return TradeTuesday;

                case DayOfWeek.Wednesday:
                    return TradeWednesday;

                case DayOfWeek.Thursday:
                    return TradeThursday;

                case DayOfWeek.Friday:
                    return TradeFriday;

                default:
                    return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────
        private void ResetDailyStateIfNeeded(DateTime london)
        {
            if (_lastLondonDay.Date == london.Date)
                return;

            _lastLondonDay = london.Date;
            _tradesToday = 0;
            _startOfDayEquity = Account.Equity;
            _circuitBroken = false;
            ResetGate();

            Print($"NEW LONDON DAY {london:dd-MMM-yyyy ddd}");
        }

        private void ResetGate()
        {
            _gateDirection = 0;
            _gateBarsRemaining = 0;
            _gateTargetPrice = 0;
            _gateSLDistance = 0;
        }

        private DateTime LondonNow()
        {
            var utc = Server.Time;
            int offset = IsBST(utc) ? 1 : 0;
            return utc.AddHours(offset);
        }

        private bool IsBST(DateTime utc)
        {
            if (utc.Month < 3 || utc.Month > 10)
                return false;

            if (utc.Month > 3 && utc.Month < 10)
                return true;

            int daysInMonth = DateTime.DaysInMonth(utc.Year, utc.Month);
            int lastSunday = daysInMonth;

            while (new DateTime(utc.Year, utc.Month, lastSunday).DayOfWeek != DayOfWeek.Sunday)
                lastSunday--;

            if (utc.Month == 3)
            {
                return utc.Day > lastSunday ||
                       utc.Day == lastSunday && utc.Hour >= 1;
            }

            return utc.Day < lastSunday ||
                   utc.Day == lastSunday && utc.Hour < 1;
        }

        private bool InTradeHours(DateTime london)
        {
            if (string.IsNullOrWhiteSpace(TradeStart) ||
                string.IsNullOrWhiteSpace(TradeEnd))
                return true;

            if (!TimeSpan.TryParse(TradeStart, out var start) ||
                !TimeSpan.TryParse(TradeEnd, out var end))
                return true;

            var t = london.TimeOfDay;

            if (start <= end)
                return t >= start && t <= end;

            return t >= start || t <= end;
        }

        private bool IndicatorsReady()
        {
            return _h1Bars.Count > ZScorePeriod + 5 &&
                   Bars.Count > AtrPeriod + 5;
        }

        private bool SafetyOk()
        {
            if (!UseSpreadFilter)
                return true;

            double spread = Symbol.Ask - Symbol.Bid;

            if (spread > MaxSpreadUsd)
            {
                Log($"SPREAD BLOCK | Spread={spread:F5} > Max={MaxSpreadUsd:F5}");
                return false;
            }

            return true;
        }

        private bool HasOpenPosition()
        {
            return Positions.Any(p =>
                p.SymbolName == SymbolName &&
                p.Label == BotLabel);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName ||
                args.Position.Label != BotLabel)
                return;

            _lastExitTime = Server.Time;
        }

        private void Log(string msg)
        {
            if (VerboseLogging)
                Print(msg);
        }
    }
}