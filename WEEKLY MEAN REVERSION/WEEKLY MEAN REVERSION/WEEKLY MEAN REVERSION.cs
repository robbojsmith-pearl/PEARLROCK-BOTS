using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  Weekly Open Mean Reversion v1
    //  Pearlrock Systematic
    //
    //  Concept:
    //  Reference level = Monday open (first M5 bar after 00:00 London).
    //  Fixed for the entire week — does not drift like a moving average.
    //  Signal fires when price deviates >= DeviationAtrMult × D1 ATR
    //  from the Monday open AND ConfirmationBars consecutive M5 bars
    //  close back toward the Monday open (momentum confirmation).
    //  TP = Monday open price ± TpBufferPips.
    //  SL = M5 ATR-based beyond entry.
    //
    //  Design decisions:
    //  - D1 ATR for deviation measurement (weekly-scale volatility)
    //  - M5 ATR for SL sizing (intraday risk management)
    //  - Monday open = first M5 bar open after 00:00 London Monday
    //  - Trade Tuesday and Wednesday only by default
    //  - Weekly circuit breaker and trade count cap
    //  - Manual BST — reliable in cTrader backtest and live
    //
    //  Bug fixes from initial version:
    //  - entryDirection captured before state reset to avoid
    //    always entering with direction=0
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class WeeklyOpen_MeanReversion_v1 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "WEEKLY_MR_V1", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Signal ────────────────────────────────────────────────────
        [Parameter("Deviation Threshold (× D1 ATR)", DefaultValue = 1.0, MinValue = 0.25, MaxValue = 4.0, Step = 0.25, Group = "Signal")]
        public double DeviationAtrMult { get; set; }

        [Parameter("D1 ATR Period", DefaultValue = 14, MinValue = 2, Group = "Signal")]
        public int D1AtrPeriod { get; set; }

        // ── Confirmation ──────────────────────────────────────────────
        [Parameter("Confirmation Bars (consecutive toward mean)", DefaultValue = 2, MinValue = 1, MaxValue = 5, Step = 1, Group = "Confirmation")]
        public int ConfirmationBars { get; set; }

        // ── Risk ──────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("ATR Period M5 (for SL)", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int M5AtrPeriod { get; set; }

        [Parameter("SL ATR Multiple (M5)", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }

        [Parameter("TP Buffer (pips from Monday open)", DefaultValue = 5, MinValue = 0, Step = 1, Group = "Risk")]
        public double TpBufferPips { get; set; }

        [Parameter("Max Trade Duration (Hours, 0=off)", DefaultValue = 24, MinValue = 0, MaxValue = 120, Step = 1, Group = "Risk")]
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

        [Parameter("Trade End (HH:mm London)", DefaultValue = "23:59", Group = "Trade Control")]
        public string TradeEnd { get; set; }

        [Parameter("Max Trades Per Week", DefaultValue = 2, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerWeek { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 60, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        // ── Day Filter ────────────────────────────────────────────────
        [Parameter("Use Day Filter", DefaultValue = true, Group = "Day Filter")]
        public bool UseDayFilter { get; set; }

        [Parameter("Trade Monday", DefaultValue = false, Group = "Day Filter")]
        public bool TradeMonday { get; set; }

        [Parameter("Trade Tuesday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeTuesday { get; set; }

        [Parameter("Trade Wednesday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeWednesday { get; set; }

        [Parameter("Trade Thursday", DefaultValue = false, Group = "Day Filter")]
        public bool TradeThursday { get; set; }

        [Parameter("Trade Friday", DefaultValue = false, Group = "Day Filter")]
        public bool TradeFriday { get; set; }

        // ── Safety ────────────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = false, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread ($)", DefaultValue = 0.001, Step = 0.0001, Group = "Safety")]
        public double MaxSpreadUsd { get; set; }

        // ── Circuit Breaker ───────────────────────────────────────────
        [Parameter("Weekly Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Circuit Breaker")]
        public double WeeklyLossLimitPct { get; set; }

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

        // ── Indicators ────────────────────────────────────────────────
        private AverageTrueRange _m5Atr;
        private Bars             _d1Bars;
        private AverageTrueRange _d1Atr;

        // ── Weekly state ──────────────────────────────────────────────
        private double   _mondayOpen       = 0;
        private bool     _mondayOpenSet    = false;
        private DateTime _currentWeekStart = DateTime.MinValue;
        private int      _tradesThisWeek   = 0;

        // ── Signal state ─────────────────────────────────────────────
        private int  _signalDirection   = 0;
        private int  _confirmationCount = 0;
        private bool _deviationReached  = false;

        // ── Circuit breaker ──────────────────────────────────────────
        private double _startOfWeekEquity = 0;
        private bool   _circuitBroken     = false;

        // ── Exit tracking ─────────────────────────────────────────────
        private DateTime _lastExitTime = DateTime.MinValue;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5)
            {
                Print($"ABORT: Must run on M5. Current = {Bars.TimeFrame}");
                Stop();
                return;
            }

            _m5Atr  = Indicators.AverageTrueRange(M5AtrPeriod, MovingAverageType.Exponential);
            _d1Bars = MarketData.GetBars(TimeFrame.Daily);
            _d1Atr  = Indicators.AverageTrueRange(_d1Bars, D1AtrPeriod, MovingAverageType.Exponential);

            _startOfWeekEquity = Account.Equity;

            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} started on {SymbolName} M5 ═══");
            Print($"Deviation={DeviationAtrMult}×D1ATR | ConfirmBars={ConfirmationBars}");
            Print($"SL={SlAtrMultiple}×M5ATR | TPBuffer={TpBufferPips}p | MaxTradeHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"Session={TradeStart}-{TradeEnd} London | DayFilter={UseDayFilter}");
            Print($"MaxTradesPerWeek={MaxTradesPerWeek}");
            Print($"CircuitBreaker={(WeeklyLossLimitPct > 0 ? WeeklyLossLimitPct + "% weekly" : "off")}");
            Print($"SPECS | PipSize={Symbol.PipSize} | VolMin={Symbol.VolumeInUnitsMin}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5) return;

            var london = LondonNow();

            ResetWeeklyStateIfNeeded(london);
            SetMondayOpenIfNeeded(london);
            ManageOpenPositions();

            if (CheckCircuitBreaker())    return;
            if (!_mondayOpenSet)          return;
            if (!PassesDayFilter(london)) return;
            if (!InTradeHours(london))    return;
            if (!SafetyOk())              return;
            if (!IndicatorsReady())       return;

            if (_lastExitTime != DateTime.MinValue &&
                Server.Time < _lastExitTime.AddMinutes(CooldownMinutes))
                return;

            if (_tradesThisWeek >= MaxTradesPerWeek) return;
            if (HasOpenPosition())                   return;

            CheckDeviationSignal();
        }

        // ─────────────────────────────────────────────────────────────
        //  WEEKLY STATE RESET
        // ─────────────────────────────────────────────────────────────
        private void ResetWeeklyStateIfNeeded(DateTime london)
        {
            var weekStart = GetWeekStart(london);
            if (weekStart == _currentWeekStart) return;

            _currentWeekStart  = weekStart;
            _mondayOpen        = 0;
            _mondayOpenSet     = false;
            _tradesThisWeek    = 0;
            _signalDirection   = 0;
            _confirmationCount = 0;
            _deviationReached  = false;
            _startOfWeekEquity = Account.Equity;
            _circuitBroken     = false;

            Print($"NEW WEEK {weekStart:dd-MMM-yyyy}");
        }

        private DateTime GetWeekStart(DateTime london)
        {
            var date = london.Date;
            int daysFromMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return date.AddDays(-daysFromMonday);
        }

        // ─────────────────────────────────────────────────────────────
        //  MONDAY OPEN DETECTION
        // ─────────────────────────────────────────────────────────────
        private void SetMondayOpenIfNeeded(DateTime london)
        {
            if (_mondayOpenSet) return;
            if (london.DayOfWeek != DayOfWeek.Monday) return;

            _mondayOpen    = Bars.OpenPrices.Last(1);
            _mondayOpenSet = true;

            Print($"MONDAY OPEN SET | {_mondayOpen:F5} | {london:dd-MMM-yyyy HH:mm}");
        }

        // ─────────────────────────────────────────────────────────────
        //  DEVIATION SIGNAL DETECTION
        // ─────────────────────────────────────────────────────────────
        private void CheckDeviationSignal()
        {
            if (_d1Bars.Count < D1AtrPeriod + 2) return;

            int    d1Last   = _d1Bars.Count - 2;
            double d1AtrVal = _d1Atr.Result[d1Last];
            if (d1AtrVal <= 0) return;

            double threshold    = DeviationAtrMult * d1AtrVal;
            double currentClose = Bars.ClosePrices.Last(1);
            double prevClose    = Bars.ClosePrices.Last(2);
            double deviation    = currentClose - _mondayOpen;

            // Step 1 — check deviation threshold (remembered once hit)
            if (!_deviationReached)
            {
                if (Math.Abs(deviation) >= threshold)
                {
                    _deviationReached  = true;
                    _signalDirection   = deviation > 0 ? -1 : 1;
                    _confirmationCount = 0;

                    Log($"DEVIATION REACHED | Close={currentClose:F5} MondayOpen={_mondayOpen:F5} " +
                        $"Dev={deviation:F5} Threshold={threshold:F5} " +
                        $"Dir={(_signalDirection == 1 ? "LONG" : "SHORT")}");
                }
                else
                {
                    return;
                }
            }

            // Reset if price crosses Monday open — reversion completed or reversed
            if (_signalDirection == 1 && currentClose > _mondayOpen)
            {
                Log($"SIGNAL RESET — price crossed above Monday open");
                _deviationReached  = false;
                _signalDirection   = 0;
                _confirmationCount = 0;
                return;
            }
            if (_signalDirection == -1 && currentClose < _mondayOpen)
            {
                Log($"SIGNAL RESET — price crossed below Monday open");
                _deviationReached  = false;
                _signalDirection   = 0;
                _confirmationCount = 0;
                return;
            }

            // Direction filter
            if (_signalDirection == 1  && !AllowLongs)  return;
            if (_signalDirection == -1 && !AllowShorts) return;

            // Step 2 — count consecutive bars closing toward Monday open
            bool closingTowardMean = _signalDirection == 1
                ? currentClose > prevClose
                : currentClose < prevClose;

            if (closingTowardMean)
            {
                _confirmationCount++;
                Log($"CONFIRM {_confirmationCount}/{ConfirmationBars} | " +
                    $"Close={currentClose:F5} toward MondayOpen={_mondayOpen:F5}");
            }
            else
            {
                _confirmationCount = 0;
                return;
            }

            // Step 3 — fire entry after ConfirmationBars
            if (_confirmationCount >= ConfirmationBars)
            {
                // Capture direction before reset
                var entryDirection = _signalDirection;
                _confirmationCount = 0;
                _deviationReached  = false;
                _signalDirection   = 0;

                ExecuteEntry(entryDirection == 1 ? TradeType.Buy : TradeType.Sell);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  TRADE EXECUTION
        // ─────────────────────────────────────────────────────────────
        private void ExecuteEntry(TradeType direction)
        {
            double m5AtrVal = _m5Atr.Result.LastValue;
            if (m5AtrVal <= 0) return;

            double slDistance = m5AtrVal * SlAtrMultiple;
            double slPips     = slDistance / Symbol.PipSize;

            if (MaxSlPips > 0 && slPips > MaxSlPips)
            {
                Log($"ENTRY BLOCKED — SL {slPips:F1}p > max {MaxSlPips}p");
                return;
            }

            double currentPrice = direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double tpBuffer     = TpBufferPips * Symbol.PipSize;

            double tpPrice = direction == TradeType.Buy
                ? _mondayOpen - tpBuffer
                : _mondayOpen + tpBuffer;

            double tpDistance = Math.Abs(tpPrice - currentPrice);
            double tpPips     = tpDistance / Symbol.PipSize;

            if (tpPips < slPips * 0.5)
            {
                Log($"ENTRY BLOCKED — TP {tpPips:F1}p too small vs SL {slPips:F1}p. " +
                    $"Price too close to Monday open already.");
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
                Log($"VOLUME TOO SMALL | Calc={volumeInUnits} Min={Symbol.VolumeInUnitsMin}");
                return;
            }

            if (volumeInUnits > Symbol.VolumeInUnitsMax)
                volumeInUnits = Symbol.VolumeInUnitsMax;

            var result = ExecuteMarketOrder(
                direction, SymbolName, volumeInUnits, BotLabel,
                slPips, tpPips);

            if (result.IsSuccessful)
            {
                _tradesThisWeek++;
                Print($"[{Server.Time:HH:mm}] {direction.ToString().ToUpper()} OPEN | " +
                      $"Entry={result.Position.EntryPrice:F5} | " +
                      $"SL={slPips:F1}p | TP={tpPips:F1}p | " +
                      $"MondayOpen={_mondayOpen:F5} | Vol={volumeInUnits:F0}");
            }
            else
            {
                Print($"ORDER FAILED: {result.Error}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT
        // ─────────────────────────────────────────────────────────────
        private void ManageOpenPositions()
        {
            if (MaxTradeHours <= 0) return;

            foreach (var pos in Positions.Where(p =>
                p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
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
        //  CIRCUIT BREAKER — weekly based
        // ─────────────────────────────────────────────────────────────
        private bool CheckCircuitBreaker()
        {
            if (WeeklyLossLimitPct <= 0 || _startOfWeekEquity <= 0) return false;

            double lossPercent = (_startOfWeekEquity - Account.Equity) /
                                  _startOfWeekEquity * 100.0;

            if (lossPercent >= WeeklyLossLimitPct)
            {
                if (!_circuitBroken)
                {
                    Print($"⚡ CIRCUIT BREAKER | Loss={lossPercent:F2}% >= {WeeklyLossLimitPct}%");
                    _circuitBroken = true;

                    foreach (var pos in Positions.Where(p =>
                        p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
                        ClosePosition(pos);
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
            double testYears     = BacktestYears > 0 ? BacktestYears : 1;
            double totalTrades   = args.TotalTrades;
            double netProfit     = args.NetProfit;
            double ddPct         = Math.Max(args.MaxEquityDrawdownPercentages, 0.01);
            double pf            = Math.Min(args.ProfitFactor, 3.0);
            double tradesPerYear = totalTrades / testYears;
            double winRate       = args.WinningTrades / Math.Max(totalTrades, 1);

            if (totalTrades   < MinTotalTrades)        return -1000000;
            if (tradesPerYear < MinTradesPerYear)       return -1000000;
            if (netProfit     <= 0)                    return -1000000;
            if (ddPct         > MaxFitnessDrawdownPct) return -1000000;

            double profitScore = Math.Log10(1.0 + netProfit);
            double tradeScore  = Math.Sqrt(totalTrades);
            double ddPenalty   = Math.Pow(ddPct, 1.5);
            double winBonus    = Math.Pow(winRate, 2);

            return (profitScore * pf * tradeScore * winBonus) / ddPenalty;
        }

        // ─────────────────────────────────────────────────────────────
        //  DAY OF WEEK FILTER
        // ─────────────────────────────────────────────────────────────
        private bool PassesDayFilter(DateTime london)
        {
            if (!UseDayFilter) return true;
            switch (london.DayOfWeek)
            {
                case DayOfWeek.Monday:    return TradeMonday;
                case DayOfWeek.Tuesday:   return TradeTuesday;
                case DayOfWeek.Wednesday: return TradeWednesday;
                case DayOfWeek.Thursday:  return TradeThursday;
                case DayOfWeek.Friday:    return TradeFriday;
                default:                  return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────
        private DateTime LondonNow()
        {
            var utc    = Server.Time;
            int offset = IsBST(utc) ? 1 : 0;
            return utc.AddHours(offset);
        }

        private bool IsBST(DateTime utc)
        {
            if (utc.Month < 3 || utc.Month > 10) return false;
            if (utc.Month > 3 && utc.Month < 10) return true;

            int daysInMonth = DateTime.DaysInMonth(utc.Year, utc.Month);
            int lastSunday  = daysInMonth;
            while (new DateTime(utc.Year, utc.Month, lastSunday).DayOfWeek != DayOfWeek.Sunday)
                lastSunday--;

            if (utc.Month == 3)
                return utc.Day > lastSunday ||
                       (utc.Day == lastSunday && utc.Hour >= 1);
            else
                return utc.Day < lastSunday ||
                       (utc.Day == lastSunday && utc.Hour < 1);
        }

        private bool InTradeHours(DateTime london)
        {
            if (string.IsNullOrWhiteSpace(TradeStart) ||
                string.IsNullOrWhiteSpace(TradeEnd)) return true;

            if (!TimeSpan.TryParse(TradeStart, out var start) ||
                !TimeSpan.TryParse(TradeEnd,   out var end))   return true;

            var t = london.TimeOfDay;
            if (start <= end)
                return t >= start && t <= end;
            else
                return t >= start || t <= end;
        }

        private bool IndicatorsReady()
        {
            return _d1Bars.Count > D1AtrPeriod + 5 &&
                   Bars.Count    > M5AtrPeriod + 5;
        }

        private bool SafetyOk()
        {
            if (!UseSpreadFilter) return true;
            return (Symbol.Ask - Symbol.Bid) <= MaxSpreadUsd;
        }

        private bool HasOpenPosition() =>
            Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName ||
                args.Position.Label      != BotLabel) return;
            _lastExitTime = Server.Time;
        }

        private void Log(string msg)
        {
            if (VerboseLogging) Print(msg);
        }
    }
}