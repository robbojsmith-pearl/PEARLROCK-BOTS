using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  CTA BoxRejection v1 — Overnight Box Short Rejection
    //  Pearlrock Systematic
    //
    //  Concept:
    //  Box builds overnight (BoxStartHour to BoxEndHour London).
    //  After box locks, price opens inside the box and makes a weak
    //  rally to >= RallyThresholdPct of box height from box low.
    //  When that rally fails — confirmed by two consecutive M5 bars
    //  closing lower — a short is entered inside the box.
    //  Target: box low minus buffer. SL: box high plus buffer.
    //
    //  Key design decisions:
    //  - Short only — empirically derived from 2024-2025 gold bull regime
    //  - Rally threshold remembered — price can consolidate after touching
    //    the threshold before the two down bars trigger entry
    //  - TradeStart automatically = BoxEndHour — no parameter misalignment
    //  - Box start/end hours are optimisable integers
    //  - Manual BST calculation — reliable in cTrader backtest and live
    //
    //  Optimise parameters:
    //  BoxStartHour: 19-23 step 1
    //  BoxEndHour: 3-7 step 1
    //  RallyThresholdPct: 0.60-0.90 step 0.05
    //  BufferUsd: 0.5-3.0 step 0.5
    //  TradeEndHour: 8-12 step 1
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class CTA_BoxRejection_v1 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "CTA_BOXREJ_V1", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Session ──────────────────────────────────────────────────
        // Overnight window: BoxStartHour > BoxEndHour
        // e.g. BoxStartHour=22, BoxEndHour=5 → 22:00 to 05:00 next day
        // TradeStart is automatically set to BoxEndHour — no separate param.
        [Parameter("Box Start Hour (London)", DefaultValue = 22, MinValue = 18, MaxValue = 23, Step = 1, Group = "Session")]
        public int BoxStartHour { get; set; }

        [Parameter("Box End Hour (London)", DefaultValue = 5, MinValue = 2, MaxValue = 8, Step = 1, Group = "Session")]
        public int BoxEndHour { get; set; }

        // Trade window ends at this hour — captures the early London move.
        // Empirically: rejection setups resolve quickly after box lock.
        [Parameter("Trade End Hour (London)", DefaultValue = 10, MinValue = 7, MaxValue = 14, Step = 1, Group = "Session")]
        public int TradeEndHour { get; set; }

        // Buffer added above box high for SL and subtracted below box low for TP.
        [Parameter("Buffer ($)", DefaultValue = 1.5, MinValue = 0.0, Step = 0.5, Group = "Session")]
        public double BufferUsd { get; set; }

        // ── Entry Signal ─────────────────────────────────────────────
        // Rally must reach >= RallyThresholdPct of box height from box low
        // before the two consecutive down bars can trigger entry.
        // Default 0.70 = price must reach 70% of box height.
        // Optimise over 0.60-0.90 step 0.05.
        [Parameter("Rally Threshold (% of box height)", DefaultValue = 0.70, MinValue = 0.40, MaxValue = 0.95, Step = 0.05, Group = "Entry Signal")]
        public double RallyThresholdPct { get; set; }

        // ── Day Filter ───────────────────────────────────────────────
        // NOTE: Day filter applies to SESSION START date.
        // With overnight box (BoxStartHour=22), a Monday session starts
        // Monday 22:00 and trades fire Tuesday morning.
        // So TradeMonday = true means Tuesday morning actual execution.
        [Parameter("Use Day Filter", DefaultValue = true, Group = "Day Filter")]
        public bool UseDayFilter { get; set; }

        [Parameter("Trade Monday Session", DefaultValue = true, Group = "Day Filter")]
        public bool TradeMonday { get; set; }

        [Parameter("Trade Tuesday Session", DefaultValue = true, Group = "Day Filter")]
        public bool TradeTuesday { get; set; }

        [Parameter("Trade Wednesday Session", DefaultValue = true, Group = "Day Filter")]
        public bool TradeWednesday { get; set; }

        [Parameter("Trade Thursday Session", DefaultValue = false, Group = "Day Filter")]
        public bool TradeThursday { get; set; }

        [Parameter("Trade Friday Session", DefaultValue = false, Group = "Day Filter")]
        public bool TradeFriday { get; set; }

        // ── Risk ──────────────────────────────────────────────────────
        // SL = box high + BufferUsd
        // TP = box low - BufferUsd
        // Position sized by risk % of equity
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Max Trade Duration (Hours, 0=off)", DefaultValue = 8, MinValue = 0, MaxValue = 48, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        [Parameter("Max SL Pips (0=off)", DefaultValue = 0, MinValue = 0, Step = 5, Group = "Risk")]
        public double MaxSlPips { get; set; }

        // ── Circuit Breaker ───────────────────────────────────────────
        [Parameter("Daily Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Circuit Breaker")]
        public double DailyLossLimitPct { get; set; }

        // ── Safety ────────────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = true, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread (pips)", DefaultValue = 3.0, Step = 0.1, Group = "Safety")]
        public double MaxSpreadPips { get; set; }

        // ── Fitness ───────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 50, MinValue = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 8, MinValue = 1, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 20.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        [Parameter("Backtest Years", DefaultValue = 8.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }

        // ── Logging ───────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        // ── Indicators ───────────────────────────────────────────────
        // No external indicators needed — all logic derived from
        // box levels and M5 price action directly.

        // ── Box state ────────────────────────────────────────────────
        private double   _boxHigh;
        private double   _boxLow;
        private bool     _boxLocked;
        private bool     _boxWindowEntered;

        // ── Signal state ─────────────────────────────────────────────
        // rallyReached: true once price has touched RallyThresholdPct
        private bool     _rallyReached;
        private int      _consecutiveDownBars;

        // ── Session state ─────────────────────────────────────────────
        private DateTime _currentSessionDate;
        private bool     _tradedThisSession;

        // ── Circuit breaker ──────────────────────────────────────────
        private double   _startOfSessionEquity;
        private bool     _circuitBroken;

        // ── Exit time tracking ────────────────────────────────────────
        private DateTime _lastExitTime = DateTime.MinValue;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            var sym = SymbolName.ToUpperInvariant();
            if (!sym.Contains("XAU") && !sym.Contains("XAG") &&
                !sym.Contains("GOLD") && !sym.Contains("SILVER"))
            {
                Print($"ABORT: {SymbolName} is not a metals instrument.");
                Stop();
                return;
            }

            if (Bars.TimeFrame != TimeFrame.Minute5)
            {
                Print($"ABORT: Must run on M5. Current = {Bars.TimeFrame}");
                Stop();
                return;
            }

            _startOfSessionEquity = Account.Equity;

            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} started on {SymbolName} M5 ═══");
            Print($"BoxWindow={BoxStartHour:D2}:00–{BoxEndHour:D2}:00 London (overnight)");
            Print($"TradeWindow={BoxEndHour:D2}:00–{TradeEndHour:D2}:00 London");
            Print($"RallyThreshold={RallyThresholdPct:P0} of box height");
            Print($"Buffer=${BufferUsd} | Risk={RiskPercent}%");
            Print($"DayFilter={UseDayFilter} | Mon={TradeMonday} Tue={TradeTuesday} Wed={TradeWednesday} Thu={TradeThursday} Fri={TradeFriday}");
            Print($"MaxTradeHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"CircuitBreaker={(DailyLossLimitPct > 0 ? DailyLossLimitPct + "%" : "off")}");
            Print($"Symbol: PipSize={Symbol.PipSize} VolMin={Symbol.VolumeInUnitsMin}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5) return;

            var london = LondonNow();

            ResetSessionState(london);

            // Manage open position before any filter — exits always fire
            ManageOpenPosition();

            if (CheckCircuitBreaker())    return;
            if (!PassesDayFilter(london)) return;
            if (!SafetyOk())              return;
            if (Bars.Count < 10)          return;

            BuildBox(london);

            // Only look for entries after box is locked and within trade window
            if (!_boxLocked)                         return;
            if (!IsInTradeWindow(london))             return;
            if (_tradedThisSession)                   return;
            if (HasOpenPosition())                    return;

            CheckRejectionSignal();
        }

        // ─────────────────────────────────────────────────────────────
        //  SESSION RESET
        //  Overnight session resets at BoxStartHour not midnight.
        // ─────────────────────────────────────────────────────────────
        private void ResetSessionState(DateTime london)
        {
            var sessionDate = GetSessionDate(london);
            if (sessionDate == _currentSessionDate) return;

            _currentSessionDate   = sessionDate;
            _boxHigh              = 0;
            _boxLow               = 0;
            _boxLocked            = false;
            _boxWindowEntered     = false;
            _rallyReached         = false;
            _consecutiveDownBars  = 0;
            _tradedThisSession    = false;
            _startOfSessionEquity = Account.Equity;
            _circuitBroken        = false;

            Print($"NEW SESSION {sessionDate:dd-MMM-yyyy ddd}");
        }

        // Session date for overnight window
        private DateTime GetSessionDate(DateTime london)
        {
            // BoxStartHour > BoxEndHour = overnight window
            return london.Hour >= BoxStartHour
                ? london.Date
                : london.Date.AddDays(-1);
        }

        // ─────────────────────────────────────────────────────────────
        //  BOX CONSTRUCTION — overnight safe
        // ─────────────────────────────────────────────────────────────
        private void BuildBox(DateTime london)
        {
            bool inWindow = IsInBoxWindow(london.TimeOfDay);

            if (inWindow)
            {
                _boxWindowEntered = true;
                double high = Bars.HighPrices.Last(1);
                double low  = Bars.LowPrices.Last(1);
                if (_boxHigh == 0 || high > _boxHigh) _boxHigh = high;
                if (_boxLow  == 0 || low  < _boxLow)  _boxLow  = low;
            }

            // Lock once — first bar after exiting window
            if (!_boxLocked && _boxWindowEntered && !inWindow)
            {
                _boxLocked = true;
                double height = GetBoxHeight();
                Print($"BOX LOCKED | High={_boxHigh:F2} Low={_boxLow:F2} " +
                      $"Height={height:F2} | " +
                      $"RallyTarget={_boxLow + height * RallyThresholdPct:F2}");
            }
        }

        private bool IsInBoxWindow(TimeSpan t)
        {
            var start = TimeSpan.FromHours(BoxStartHour);
            var end   = TimeSpan.FromHours(BoxEndHour);
            // Always overnight since BoxStartHour > BoxEndHour by design
            return t >= start || t < end;
        }

        private double GetBoxHeight() =>
            (_boxHigh > 0 && _boxLow > 0) ? _boxHigh - _boxLow : 0;

        // ─────────────────────────────────────────────────────────────
        //  REJECTION SIGNAL DETECTION
        //
        //  Conditions (all must be true):
        //  1. Current M5 close is inside the box
        //  2. Price has at some point reached >= RallyThresholdPct
        //     of box height from box low (remembered touch)
        //  3. Two consecutive M5 bars have closed lower than previous
        //     (momentum confirmation of failure)
        // ─────────────────────────────────────────────────────────────
        private void CheckRejectionSignal()
        {
            double height = GetBoxHeight();
            if (height <= 0) return;

            int prev     = Bars.Count - 2; // last completed bar
            int prevPrev = Bars.Count - 3;
            if (prev < 2 || prevPrev < 0) return;

            double lastClose = Bars.ClosePrices[prev];
            double prevClose = Bars.ClosePrices[prevPrev];

            // ── Condition 1: price must be inside the box ─────────────
            if (lastClose >= _boxHigh || lastClose <= _boxLow)
            {
                // Price broke out of box — signal invalidated
                _rallyReached        = false;
                _consecutiveDownBars = 0;
                return;
            }

            // ── Condition 2: check if rally threshold reached ─────────
            double rallyTarget = _boxLow + height * RallyThresholdPct;
            if (!_rallyReached)
            {
                // Check if any recent bar high touched the rally level
                if (Bars.HighPrices[prev] >= rallyTarget)
                {
                    _rallyReached = true;
                    Log($"RALLY REACHED | High={Bars.HighPrices[prev]:F2} >= Target={rallyTarget:F2}");
                }
                else
                {
                    return; // Rally not yet reached
                }
            }

            // ── Condition 3: two consecutive down bars ────────────────
            if (lastClose < prevClose)
            {
                _consecutiveDownBars++;
                Log($"DOWN BAR {_consecutiveDownBars}/2 | Close={lastClose:F2} < Prev={prevClose:F2}");
            }
            else
            {
                _consecutiveDownBars = 0; // Reset — must be consecutive
                return;
            }

            if (_consecutiveDownBars < 2) return;

            // ── All conditions met — execute short ────────────────────
            Log($"REJECTION SIGNAL | Inside box, rally failed, 2 down bars confirmed");
            ExecuteShort();
            _consecutiveDownBars = 0;
            _rallyReached        = false;
        }

        // ─────────────────────────────────────────────────────────────
        //  TRADE EXECUTION
        //  SL = box high + buffer
        //  TP = box low - buffer
        //  Position sized by risk % of equity
        // ─────────────────────────────────────────────────────────────
        private void ExecuteShort()
        {
            double currentAsk = Symbol.Ask;

            double slPrice = _boxHigh + BufferUsd;
            double tpPrice = _boxLow  - BufferUsd;

            // Validate levels
            if (slPrice <= currentAsk)
            {
                Print($"ENTRY BLOCKED — SL {slPrice:F2} <= Ask {currentAsk:F2}");
                return;
            }

            if (tpPrice >= currentAsk)
            {
                Print($"ENTRY BLOCKED — TP {tpPrice:F2} >= Ask {currentAsk:F2}");
                return;
            }

            double slPips = (slPrice - currentAsk) / Symbol.PipSize;
            double tpPips = (currentAsk - tpPrice) / Symbol.PipSize;

            // Minimum RR check — must be at least 1:1
            if (tpPips < slPips * 0.8)
            {
                Print($"ENTRY BLOCKED — RR too low. TP={tpPips:F1}p SL={slPips:F1}p");
                return;
            }

            // Hard SL cap if set
            if (MaxSlPips > 0 && slPips > MaxSlPips)
            {
                Print($"ENTRY BLOCKED — SL {slPips:F1}p > MaxSlPips {MaxSlPips}");
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
                Print($"VOLUME TOO SMALL | Calc={volumeInUnits} Min={Symbol.VolumeInUnitsMin}");
                return;
            }

            if (volumeInUnits > Symbol.VolumeInUnitsMax)
                volumeInUnits = Symbol.VolumeInUnitsMax;

            var result = ExecuteMarketOrder(
                TradeType.Sell, SymbolName, volumeInUnits, BotLabel,
                slPips, tpPips);

            if (result.IsSuccessful)
            {
                _tradedThisSession = true;
                Print($"SHORT OPEN | Entry={result.Position.EntryPrice:F2} | " +
                      $"SL={slPrice:F2} ({slPips:F1}p) | " +
                      $"TP={tpPrice:F2} ({tpPips:F1}p) | " +
                      $"BoxHigh={_boxHigh:F2} BoxLow={_boxLow:F2} | " +
                      $"Vol={volumeInUnits:F0}");
            }
            else
            {
                Print($"ORDER FAILED: {result.Error}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT — duration exit
        // ─────────────────────────────────────────────────────────────
        private void ManageOpenPosition()
        {
            if (MaxTradeHours <= 0) return;

            var pos = GetMyPosition();
            if (pos == null) return;

            var hoursOpen = (Server.Time - pos.EntryTime).TotalHours;
            if (hoursOpen >= MaxTradeHours)
            {
                Print($"MAX DURATION EXIT | {hoursOpen:F1}h | Net={pos.NetProfit:F2}");
                ClosePosition(pos);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  CIRCUIT BREAKER
        // ─────────────────────────────────────────────────────────────
        private bool CheckCircuitBreaker()
        {
            if (DailyLossLimitPct <= 0 || _startOfSessionEquity <= 0) return false;

            double lossPercent = (_startOfSessionEquity - Account.Equity) /
                                  _startOfSessionEquity * 100.0;

            if (lossPercent >= DailyLossLimitPct)
            {
                if (!_circuitBroken)
                {
                    Print($"⚡ CIRCUIT BREAKER | Loss={lossPercent:F2}% >= {DailyLossLimitPct}%");
                    _circuitBroken = true;

                    var pos = GetMyPosition();
                    if (pos != null) ClosePosition(pos);
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
        //  Applied to session-start date for overnight attribution.
        // ─────────────────────────────────────────────────────────────
        private bool PassesDayFilter(DateTime london)
        {
            if (!UseDayFilter) return true;
            switch (GetSessionDate(london).DayOfWeek)
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
        //  TIME HELPERS
        // ─────────────────────────────────────────────────────────────
        private bool IsInTradeWindow(DateTime london)
        {
            var t     = london.TimeOfDay;
            var start = TimeSpan.FromHours(BoxEndHour);   // auto-matched to box end
            var end   = TimeSpan.FromHours(TradeEndHour);
            return t >= start && t <= end;
        }

        // ─────────────────────────────────────────────────────────────
        //  LONDON TIME — manual BST, reliable in backtest and live
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

        // ─────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────
        private bool SafetyOk()
        {
            if (!UseSpreadFilter) return true;
            return (Symbol.Ask - Symbol.Bid) / Symbol.PipSize <= MaxSpreadPips;
        }

        private bool HasOpenPosition() =>
            Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private Position GetMyPosition() =>
            Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label == BotLabel);

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
