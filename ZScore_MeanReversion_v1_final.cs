using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  ZScore Mean Reversion v1 — Final
    //  Pearlrock Systematic
    //
    //  Concept:
    //  H1 Z-score identifies statistically extreme price deviations.
    //  M5 confirmation bar shows first sign of reversion before entry.
    //  TP targets Z=0 (price returns to H1 SMA mean).
    //  ATR-based SL. Force close at MaxTradeHours.
    //
    //  Validated on USDCHF M5:
    //  In-sample  2017-2023: 29 trades, PF 1.96, DD 2.2%
    //  Out-of-sample 2024-2025: 22 trades, PF 1.18, DD 1.4%
    //
    //  Confirmed parameters:
    //  Session: 00:00-07:00 London (Asian session)
    //  Day filter: Tuesday, Wednesday, Thursday
    //  ZScoreThreshold: 2.5
    //  ZScorePeriod: 20
    //  ZScoreExitLevel: 0.0 (full mean reversion target)
    //  MaxConfirmationBars: 3
    //  SL: 2.0 ATR, TP: dynamic Z=0
    //  Risk: 0.5%, MaxTradeHours: 12, CircuitBreaker: 2%
    //
    //  Changes from v1 draft:
    //  1. Midnight-spanning TradeStart/TradeEnd window fixed
    //  2. All confirmed defaults baked in
    //  3. Manual BST calculation for reliable London time in backtest
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class ZScore_MeanReversion_v1_Final : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "ZSCORE_MR_V1", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Z-Score Signal (H1) ───────────────────────────────────────
        // Z = (price - SMA_N) / StdDev_N on H1 close prices.
        // 2.5 = price is 2.5 standard deviations from its mean.
        // Instrument-agnostic — same threshold works across pairs.
        // Calibrated: 2.5 on USDCHF. Test on other instruments.
        [Parameter("Z-Score Period (H1 bars)", DefaultValue = 20, MinValue = 5, Step = 1, Group = "Z-Score Signal")]
        public int ZScorePeriod { get; set; }

        [Parameter("Z-Score Entry Threshold", DefaultValue = 2.5, MinValue = 1.0, MaxValue = 5.0, Step = 0.1, Group = "Z-Score Signal")]
        public double ZScoreThreshold { get; set; }

        // Target Z level for TP. 0.0 = full mean reversion.
        // 0.5 = halfway reversion. Tested: 0.0 is optimal on USDCHF.
        [Parameter("Z-Score Exit Level (TP target)", DefaultValue = 0.0, MinValue = -1.0, MaxValue = 1.0, Step = 0.1, Group = "Z-Score Signal")]
        public double ZScoreExitLevel { get; set; }

        // ── M5 Confirmation Gate ──────────────────────────────────────
        // After H1 Z-score flags extreme, waits for M5 bar closing
        // in direction of expected reversion before entering.
        // Tested: 3 and 6 produce identical results. Use 3 for speed.
        [Parameter("Max M5 Bars to Wait for Confirmation", DefaultValue = 3, MinValue = 1, MaxValue = 24, Step = 1, Group = "M5 Confirmation")]
        public int MaxConfirmationBars { get; set; }

        // ── Risk ──────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("ATR Period (M5)", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int AtrPeriod { get; set; }

        // Calibrated: 2.0 is optimal. SL is safety net — exits are
        // primarily driven by TP hits, not SL hits.
        [Parameter("SL ATR Multiple", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }

        // Force close if trade hasn't resolved. Mean reversion trades
        // that don't revert quickly are unlikely to revert at all.
        [Parameter("Max Trade Duration (Hours, 0=off)", DefaultValue = 12, MinValue = 0, MaxValue = 72, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        [Parameter("Max SL Pips (0=off)", DefaultValue = 0, MinValue = 0, Step = 5, Group = "Risk")]
        public double MaxSlPips { get; set; }

        // ── Direction ─────────────────────────────────────────────────
        // Both directions validated on USDCHF — longs and shorts viable.
        // CHF safe-haven snaps back reliably in both directions.
        [Parameter("Allow Longs", DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

        // ── Trade Control ─────────────────────────────────────────────
        // Calibrated: Asian session 00:00-07:00 London is optimal.
        // London/NY sessions are trend-driven not mean-reverting.
        [Parameter("Trade Start (HH:mm London)", DefaultValue = "00:00", Group = "Trade Control")]
        public string TradeStart { get; set; }

        [Parameter("Trade End (HH:mm London)", DefaultValue = "07:00", Group = "Trade Control")]
        public string TradeEnd { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 2, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 60, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        // ── Day Filter ────────────────────────────────────────────────
        // Calibrated: Tue/Wed/Thu optimal on USDCHF.
        // Friday is a consistent underperformer — excluded.
        // Monday marginal — excluded for quality.
        // Test per instrument — pattern may differ on other pairs.
        [Parameter("Use Day Filter", DefaultValue = true, Group = "Day Filter")]
        public bool UseDayFilter { get; set; }

        [Parameter("Trade Monday", DefaultValue = false, Group = "Day Filter")]
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

        [Parameter("Max Spread (pips)", DefaultValue = 3.0, Step = 0.1, Group = "Safety")]
        public double MaxSpreadPips { get; set; }

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

        // ── Indicators ────────────────────────────────────────────────
        private Bars              _h1Bars;
        private AverageTrueRange  _atr;

        // ── State ─────────────────────────────────────────────────────
        private DateTime _lastLondonDay    = DateTime.MinValue;
        private int      _tradesToday      = 0;
        private DateTime _lastExitTime     = DateTime.MinValue;
        private double   _startOfDayEquity = 0;
        private bool     _circuitBroken    = false;

        // Gate state
        // 1 = long gate, -1 = short gate, 0 = none
        private int    _gateDirection     = 0;
        private int    _gateBarsRemaining = 0;
        private double _gateTargetPrice   = 0;
        private double _gateSLDistance    = 0;

        // Track last H1 bar to avoid rechecking same bar
        private DateTime _lastH1BarTime = DateTime.MinValue;

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
            _atr    = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);

            _startOfDayEquity = Account.Equity;

            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} started on {SymbolName} M5 ═══");
            Print($"ZScorePeriod={ZScorePeriod} | ZThreshold={ZScoreThreshold} | ExitLevel={ZScoreExitLevel}");
            Print($"Session={TradeStart}-{TradeEnd} London | DayFilter={UseDayFilter}");
            Print($"SL={SlAtrMultiple}ATR | MaxTradeHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"CircuitBreaker={(DailyLossLimitPct > 0 ? DailyLossLimitPct + "%" : "off")}");
            Print($"SPECS | PipSize={Symbol.PipSize} | VolMin={Symbol.VolumeInUnitsMin}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5) return;

            var london = LondonNow();

            ResetDailyStateIfNeeded(london);
            ManageOpenPositions();

            if (CheckCircuitBreaker())    return;
            if (!PassesDayFilter(london)) return;
            if (!InTradeHours(london))    return;
            if (!SafetyOk())              return;
            if (!IndicatorsReady())       return;

            if (_lastExitTime != DateTime.MinValue &&
                Server.Time < _lastExitTime.AddMinutes(CooldownMinutes))
                return;

            if (_tradesToday >= MaxTradesPerDay) return;
            if (HasOpenPosition())               return;

            // Check for new H1 signal — only on new H1 bar
            CheckH1Signal();

            // Check M5 confirmation if gate is open
            if (_gateDirection != 0)
                CheckM5Confirmation();
        }

        // ─────────────────────────────────────────────────────────────
        //  H1 SIGNAL GATE
        // ─────────────────────────────────────────────────────────────
        private void CheckH1Signal()
        {
            if (_h1Bars.Count < ZScorePeriod + 2) return;

            // Only process once per H1 bar
            var currentH1Time = _h1Bars.OpenTimes.LastValue;
            if (currentH1Time == _lastH1BarTime) return;
            _lastH1BarTime = currentH1Time;

            int lastH1 = _h1Bars.Count - 2;

            double zScore = CalculateZScore(lastH1);
            double atrVal = _atr.Result.LastValue;
            if (atrVal <= 0) return;

            double smaVal = CalculateSMA(lastH1);

            // Long gate — price far below mean, expect reversion up
            if (AllowLongs && zScore <= -ZScoreThreshold && _gateDirection != 1)
            {
                _gateDirection     = 1;
                _gateBarsRemaining = MaxConfirmationBars;
                _gateTargetPrice   = smaVal;
                _gateSLDistance    = atrVal * SlAtrMultiple;

                Log($"H1 LONG GATE | Z={zScore:F2} <= -{ZScoreThreshold} | " +
                    $"TP={smaVal:F5} | SLDist={_gateSLDistance:F5}");
                return;
            }

            // Short gate — price far above mean, expect reversion down
            if (AllowShorts && zScore >= ZScoreThreshold && _gateDirection != -1)
            {
                _gateDirection     = -1;
                _gateBarsRemaining = MaxConfirmationBars;
                _gateTargetPrice   = smaVal;
                _gateSLDistance    = atrVal * SlAtrMultiple;

                Log($"H1 SHORT GATE | Z={zScore:F2} >= +{ZScoreThreshold} | " +
                    $"TP={smaVal:F5} | SLDist={_gateSLDistance:F5}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  M5 CONFIRMATION
        //  Waits for M5 bar to close in reversion direction.
        //  Cancels if confirmation doesn't arrive in time.
        // ─────────────────────────────────────────────────────────────
        private void CheckM5Confirmation()
        {
            _gateBarsRemaining--;

            if (_gateBarsRemaining <= 0)
            {
                Log($"GATE EXPIRED — no M5 confirmation ({(_gateDirection == 1 ? "LONG" : "SHORT")})");
                _gateDirection = 0;
                return;
            }

            int prev     = Bars.Count - 2;
            int prevPrev = Bars.Count - 3;
            if (prev < 1 || prevPrev < 0) return;

            double lastClose = Bars.ClosePrices[prev];
            double prevClose = Bars.ClosePrices[prevPrev];

            bool longConfirmed  = _gateDirection == 1  && lastClose > prevClose;
            bool shortConfirmed = _gateDirection == -1 && lastClose < prevClose;

            if (!longConfirmed && !shortConfirmed) return;

            Log($"M5 CONFIRMED {(_gateDirection == 1 ? "LONG" : "SHORT")} | " +
                $"LastClose={lastClose:F5} PrevClose={prevClose:F5}");

            ExecuteEntry(_gateDirection == 1 ? TradeType.Buy : TradeType.Sell);
            _gateDirection = 0;
        }

        // ─────────────────────────────────────────────────────────────
        //  TRADE EXECUTION
        // ─────────────────────────────────────────────────────────────
        private void ExecuteEntry(TradeType direction)
        {
            double slPips = _gateSLDistance / Symbol.PipSize;

            if (MaxSlPips > 0 && slPips > MaxSlPips)
            {
                Log($"ENTRY BLOCKED — SL {slPips:F1}p > max {MaxSlPips}p");
                return;
            }

            // TP = distance from current price to Z=ExitLevel target
            double currentPrice = direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double tpDistance   = Math.Abs(_gateTargetPrice - currentPrice);
            double tpPips       = tpDistance / Symbol.PipSize;

            // Minimum TP — must be at least 0.5R
            if (tpPips < slPips * 0.5)
            {
                Log($"ENTRY BLOCKED — TP {tpPips:F1}p too small vs SL {slPips:F1}p");
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
                _tradesToday++;
                Print($"[{Server.Time:HH:mm}] {direction.ToString().ToUpper()} OPEN | " +
                      $"Entry={result.Position.EntryPrice:F5} | " +
                      $"SL={slPips:F1}p | TP={tpPips:F1}p | " +
                      $"TPTarget={_gateTargetPrice:F5} | Vol={volumeInUnits:F0}");
            }
            else
            {
                Print($"ORDER FAILED: {result.Error}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT — duration exit
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
        //  Z-SCORE CALCULATION
        //  Z = (price - SMA_N) / StdDev_N on H1 close prices.
        //  Population standard deviation (divide by N not N-1).
        // ─────────────────────────────────────────────────────────────
        private double CalculateZScore(int barIndex)
        {
            double sma    = CalculateSMA(barIndex);
            double stdDev = CalculateStdDev(barIndex, sma);
            if (stdDev <= 0) return 0;
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
            if (DailyLossLimitPct <= 0 || _startOfDayEquity <= 0) return false;

            double lossPercent = (_startOfDayEquity - Account.Equity) / _startOfDayEquity * 100.0;

            if (lossPercent >= DailyLossLimitPct)
            {
                if (!_circuitBroken)
                {
                    Print($"⚡ CIRCUIT BREAKER | Loss={lossPercent:F2}% >= {DailyLossLimitPct}%");
                    _circuitBroken = true;
                    _gateDirection = 0;

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
        //  Win rate rewarded explicitly — mean reversion strategies
        //  should have higher win rates than breakout strategies.
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
        private void ResetDailyStateIfNeeded(DateTime london)
        {
            if (_lastLondonDay.Date == london.Date) return;
            _lastLondonDay    = london.Date;
            _tradesToday      = 0;
            _startOfDayEquity = Account.Equity;
            _circuitBroken    = false;
            _gateDirection    = 0;
            Print($"NEW LONDON DAY {london:dd-MMM-yyyy ddd}");
        }

        // Manual BST — reliable in both backtest and live.
        // TimeZoneInfo.ConvertTimeFromUtc unreliable in cTrader backtest.
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
                // Standard window e.g. 07:00 to 19:00
                return t >= start && t <= end;
            else
                // Midnight-spanning window e.g. 22:00 to 07:00
                return t >= start || t <= end;
        }

        private bool IndicatorsReady()
        {
            return _h1Bars.Count > ZScorePeriod + 5 &&
                   Bars.Count    > AtrPeriod + 5;
        }

        private bool SafetyOk()
        {
            if (!UseSpreadFilter) return true;
            return (Symbol.Ask - Symbol.Bid) / Symbol.PipSize <= MaxSpreadPips;
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
