using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  CTA v15 — Asian Box Breakout, Metals (XAUUSD / XAGUSD)
    //  Pearlrock Systematic
    //
    //  Changes from v14:
    //  1.  Overnight box window — BoxStartHour/BoxEndHour are integer
    //      parameters (optimisable). IsInBoxWindow() handles same-day
    //      and overnight windows correctly (e.g. 22:00–05:00).
    //  2.  Session date replaces London calendar date — reset fires at
    //      BoxStartHour, not midnight, so overnight boxes are never split.
    //      GetSessionDate() maps any bar time to the session-start date.
    //  3.  _boxWindowEntered flag — box lock fires exactly once when we
    //      exit the window, preventing spurious re-locks after reset.
    //  4.  Dynamic box height — UseDynamicBoxHeight (default true)
    //      validates box height as MinBoxAtrRatio–MaxBoxAtrRatio × H1 ATR.
    //      Fixed-$ parameters retained as a togglable fallback.
    //  5.  ManageOpenPosition() moved before day-filter — time-based
    //      exits fire even on filtered days so open trades always managed.
    //  6.  London time uses manual BST calculation — reliable in both
    //      backtest and live. TimeZoneInfo.ConvertTimeFromUtc is
    //      unreliable in cTrader backtesting engine.
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class CTA_v15 : Robot
    {
        // ── Label ────────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "CTA_v15", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Session ──────────────────────────────────────────────────
        // Integer hours are optimisable in the cTrader genetic optimiser.
        // Overnight windows supported: BoxStartHour > BoxEndHour
        // e.g. BoxStartHour=22, BoxEndHour=5 → 22:00 to 05:00 next day.
        [Parameter("Box Start Hour (London)", DefaultValue = 22, MinValue = 0, MaxValue = 23, Step = 1, Group = "Session")]
        public int BoxStartHour { get; set; }

        [Parameter("Box End Hour (London)", DefaultValue = 5, MinValue = 0, MaxValue = 23, Step = 1, Group = "Session")]
        public int BoxEndHour { get; set; }

        [Parameter("Trade Start (London)", DefaultValue = "06:00", Group = "Session")]
        public string TradeStart { get; set; }

        [Parameter("Trade End (London)", DefaultValue = "19:00", Group = "Session")]
        public string TradeEnd { get; set; }

        [Parameter("Breakout Buffer ($)", DefaultValue = 2.0, MinValue = 0.0, Step = 0.1, Group = "Session")]
        public double BufferUsd { get; set; }

        // 0 = disabled (TradeEnd is the sole outer boundary).
        // >0 = hard cut-off N minutes after box lock.
        [Parameter("Minutes After Box Lock (0=use TradeEnd only)", DefaultValue = 0, MinValue = 0, MaxValue = 900, Step = 60, Group = "Session")]
        public int MinutesAfterBoxLock { get; set; }

        // ── Direction ────────────────────────────────────────────────
        [Parameter("Allow Longs", DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

        // ── Day Filter ───────────────────────────────────────────────
        // For overnight sessions the filter is applied to the session
        // START date — so a 22:00 Mon session is filtered by TradeMonday
        // even when bars run into Tuesday morning.
        [Parameter("Use Day Filter", DefaultValue = true, Group = "Day Filter")]
        public bool UseDayFilter { get; set; }

        [Parameter("Trade Monday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeMonday { get; set; }

        [Parameter("Trade Tuesday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeTuesday { get; set; }

        [Parameter("Trade Wednesday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeWednesday { get; set; }

        [Parameter("Trade Thursday", DefaultValue = false, Group = "Day Filter")]
        public bool TradeThursday { get; set; }

        [Parameter("Trade Friday", DefaultValue = false, Group = "Day Filter")]
        public bool TradeFriday { get; set; }

        // ── Box Filters ──────────────────────────────────────────────
        // Dynamic mode (default): box height must lie within
        // [MinBoxAtrRatio × H1ATR, MaxBoxAtrRatio × H1ATR]
        // Adapts to expanding/contracting volatility regimes.
        //
        // Fixed mode (UseDynamicBoxHeight = false): uses MinBoxHeightUsd
        // and MaxBoxHeightUsd as in v14.
        [Parameter("Use Dynamic Box Height (ATR-based)", DefaultValue = true, Group = "Box Filters")]
        public bool UseDynamicBoxHeight { get; set; }

        [Parameter("Min Box Height (× H1 ATR)", DefaultValue = 0.15, MinValue = 0.0, Step = 0.05, Group = "Box Filters")]
        public double MinBoxAtrRatio { get; set; }

        [Parameter("Max Box Height (× H1 ATR)", DefaultValue = 0.75, MinValue = 0.05, Step = 0.05, Group = "Box Filters")]
        public double MaxBoxAtrRatio { get; set; }

        // Fixed fallback — only used when UseDynamicBoxHeight = false
        [Parameter("Min Box Height $ (fixed mode)", DefaultValue = 2.0, MinValue = 0.0, Step = 0.1, Group = "Box Filters")]
        public double MinBoxHeightUsd { get; set; }

        [Parameter("Max Box Height $ (fixed mode)", DefaultValue = 20.0, MinValue = 0.1, Step = 0.1, Group = "Box Filters")]
        public double MaxBoxHeightUsd { get; set; }

        // ── Vol Filter ───────────────────────────────────────────────
        [Parameter("Use H1 ATR Filter", DefaultValue = true, Group = "Vol Filter")]
        public bool UseH1AtrFilter { get; set; }

        [Parameter("H1 ATR Period", DefaultValue = 14, MinValue = 1, Group = "Vol Filter")]
        public int H1AtrPeriod { get; set; }

        [Parameter("Min H1 ATR ($)", DefaultValue = 3.0, MinValue = 0.0, Step = 0.1, Group = "Vol Filter")]
        public double MinH1Atr { get; set; }

        [Parameter("Max H1 ATR ($)", DefaultValue = 40.0, MinValue = 0.1, Step = 0.1, Group = "Vol Filter")]
        public double MaxH1Atr { get; set; }

        // ── Risk ─────────────────────────────────────────────────────
        [Parameter("Risk %", DefaultValue = 1.0, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Stop Mult", DefaultValue = 1.2, MinValue = 0.1, Step = 0.1, Group = "Risk")]
        public double AtrStopMult { get; set; }

        [Parameter("TP R Multiple", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1, Group = "Risk")]
        public double TakeProfitR { get; set; }

        [Parameter("Max Trade Duration (Hours, 0=off)", DefaultValue = 8, MinValue = 0, MaxValue = 48, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        // ── EMA Filter ───────────────────────────────────────────────
        [Parameter("Use EMA Filter", DefaultValue = true, Group = "EMA Filter")]
        public bool UseEmaFilter { get; set; }

        [Parameter("EMA Period", DefaultValue = 55, MinValue = 1, Group = "EMA Filter")]
        public int EmaPeriod { get; set; }

        // ── Circuit Breaker ──────────────────────────────────────────
        [Parameter("Daily Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Circuit Breaker")]
        public double DailyLossLimitPct { get; set; }

        // ── Fitness ──────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 65, MinValue = 1, Step = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 20.0, MinValue = 1.0, Step = 1.0, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 15.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        [Parameter("Backtest Years (for fitness calc)", DefaultValue = 8.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }

        // ── Indicators ───────────────────────────────────────────────
        private ExponentialMovingAverage _ema;
        private AverageTrueRange         _atr;
        private Bars                     _m15;
        private Bars                     _h1;
        private AverageTrueRange         _h1Atr;

        // ── Box state ────────────────────────────────────────────────
        private double   _boxHigh;
        private double   _boxLow;
        private bool     _boxLocked;
        private bool     _boxValid;
        private bool     _boxWindowEntered;
        private DateTime _boxLockedLondonTime;

        // ── Gate state ───────────────────────────────────────────────
        private int      _gateDirection; // 1=long, -1=short, 0=none
        private DateTime _gateExpiry;

        // ── Session state ─────────────────────────────────────────────
        private DateTime _currentSessionDate;
        private bool     _tradedToday;
        private DateTime _lastM15;

        // ── Circuit breaker ──────────────────────────────────────────
        private double   _startOfDayEquity;
        private bool     _circuitBroken;

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

            _ema   = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaPeriod);
            _atr   = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _m15   = MarketData.GetBars(TimeFrame.Minute15);
            _h1    = MarketData.GetBars(TimeFrame.Hour);
            _h1Atr = Indicators.AverageTrueRange(_h1, H1AtrPeriod, MovingAverageType.Exponential);

            _lastM15          = _m15.OpenTimes.LastValue;
            _startOfDayEquity = Account.Equity;

            bool overnight = BoxStartHour > BoxEndHour;

            Print($"═══ {BotLabel} started on {SymbolName} M5 ═══");
            Print($"Longs={AllowLongs} Shorts={AllowShorts}");
            Print($"DayFilter={UseDayFilter} | Mon={TradeMonday} Tue={TradeTuesday} Wed={TradeWednesday} Thu={TradeThursday} Fri={TradeFriday}");
            Print($"BoxWindow={BoxStartHour:D2}:00–{BoxEndHour:D2}:00 London | Overnight={overnight}");
            Print($"TradeWindow={TradeStart}–{TradeEnd} London");
            Print($"MinutesAfterBoxLock={(MinutesAfterBoxLock == 0 ? "disabled" : MinutesAfterBoxLock.ToString())}");
            Print($"MaxTradeHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"DynamicBoxHeight={UseDynamicBoxHeight} | " +
                  (UseDynamicBoxHeight
                      ? $"AtrRatio=[{MinBoxAtrRatio:F2}–{MaxBoxAtrRatio:F2}] × H1ATR"
                      : $"Fixed=[{MinBoxHeightUsd}–{MaxBoxHeightUsd}]$"));
            Print($"CircuitBreaker={(DailyLossLimitPct > 0 ? DailyLossLimitPct + "%" : "off")}");
            Print($"Symbol: PipSize={Symbol.PipSize} VolMin={Symbol.VolumeInUnitsMin}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5) return;

            var london = LondonNow();

            ResetSessionState(london);

            // ManageOpenPosition runs BEFORE day filter —
            // time-based exits always fire even on filtered days.
            ManageOpenPosition();

            if (CheckCircuitBreaker())    return;
            if (!PassesDayFilter(london)) return;

            BuildAsianBox(london);
            DetectM15Gate(london);
            CheckEntry(london);
        }

        // ─────────────────────────────────────────────────────────────
        //  SESSION RESET
        //  For overnight windows (BoxStartHour > BoxEndHour), session
        //  resets at BoxStartHour not midnight — overnight boxes are
        //  never wiped mid-accumulation.
        // ─────────────────────────────────────────────────────────────
        private void ResetSessionState(DateTime london)
        {
            var sessionDate = GetSessionDate(london);
            if (sessionDate == _currentSessionDate) return;

            _currentSessionDate  = sessionDate;
            _boxHigh             = 0;
            _boxLow              = 0;
            _boxLocked           = false;
            _boxValid            = false;
            _boxWindowEntered    = false;
            _boxLockedLondonTime = DateTime.MinValue;
            _gateDirection       = 0;
            _tradedToday         = false;
            _startOfDayEquity    = Account.Equity;
            _circuitBroken       = false;

            Print($"NEW SESSION {sessionDate:dd-MMM-yyyy ddd} | Box={BoxStartHour:D2}:00–{BoxEndHour:D2}:00 London");
        }

        // Returns the date on which the current trading session started.
        // Same-day window (BoxStartHour <= BoxEndHour): always current date.
        // Overnight window (BoxStartHour > BoxEndHour):
        //   hour >= BoxStartHour → session started today
        //   hour <  BoxStartHour → session started yesterday
        private DateTime GetSessionDate(DateTime london)
        {
            if (BoxStartHour <= BoxEndHour)
                return london.Date;

            return london.Hour >= BoxStartHour
                ? london.Date
                : london.Date.AddDays(-1);
        }

        // ─────────────────────────────────────────────────────────────
        //  DAY OF WEEK FILTER
        //  Applied to session-start date for correct overnight attribution.
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
                    _gateDirection = 0;

                    var pos = GetMyPosition(TradeType.Buy) ?? GetMyPosition(TradeType.Sell);
                    if (pos != null) ClosePosition(pos);
                }
                return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT — duration exit
        // ─────────────────────────────────────────────────────────────
        private void ManageOpenPosition()
        {
            if (MaxTradeHours <= 0) return;
            var pos = GetMyPosition(TradeType.Buy) ?? GetMyPosition(TradeType.Sell);
            if (pos == null) return;

            var hoursOpen = (Server.Time - pos.EntryTime).TotalHours;
            if (hoursOpen >= MaxTradeHours)
            {
                Print($"MAX DURATION EXIT | Open={hoursOpen:F1}h >= {MaxTradeHours}h | Net={pos.NetProfit:F2}");
                ClosePosition(pos);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  ASIAN BOX — overnight-safe
        //  Accumulates while IsInBoxWindow() is true, then locks exactly
        //  once when we leave the window (_boxWindowEntered=true, !inWindow).
        // ─────────────────────────────────────────────────────────────
        private void BuildAsianBox(DateTime london)
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

            // Lock once — first bar after exiting window having been inside it
            if (!_boxLocked && _boxWindowEntered && !inWindow)
            {
                _boxLocked           = true;
                _boxLockedLondonTime = london;
                double height        = GetBoxHeight();
                _boxValid            = IsBoxHeightValid(height);

                Print($"BOX LOCKED | High={_boxHigh:F2} Low={_boxLow:F2} Height={height:F2} Valid={_boxValid}");
                if (UseDynamicBoxHeight && _h1.Count >= Math.Max(5, H1AtrPeriod + 2))
                {
                    double h1a = _h1Atr.Result.LastValue;
                    Print($"  Dynamic: [{MinBoxAtrRatio:F2}–{MaxBoxAtrRatio:F2}] × H1ATR({h1a:F2}) " +
                          $"= [{MinBoxAtrRatio * h1a:F2}–{MaxBoxAtrRatio * h1a:F2}]$");
                }
            }
        }

        // Returns true if time-of-day falls inside the box window.
        // Handles same-day and overnight windows correctly.
        private bool IsInBoxWindow(TimeSpan t)
        {
            var start = TimeSpan.FromHours(BoxStartHour);
            var end   = TimeSpan.FromHours(BoxEndHour);

            if (BoxStartHour < BoxEndHour)
                return t >= start && t < end;     // same-day e.g. 00:00–06:00
            else if (BoxStartHour > BoxEndHour)
                return t >= start || t < end;     // overnight e.g. 22:00–05:00
            else
                return false;                     // zero-width — never true
        }

        private double GetBoxHeight() =>
            (_boxHigh > 0 && _boxLow > 0) ? _boxHigh - _boxLow : 0;

        private bool IsBoxHeightValid(double height)
        {
            if (height <= 0) return false;

            if (UseDynamicBoxHeight)
            {
                if (_h1.Count < Math.Max(5, H1AtrPeriod + 2)) return false;
                double h1a = _h1Atr.Result.LastValue;
                if (h1a <= 0) return false;
                return height >= MinBoxAtrRatio * h1a && height <= MaxBoxAtrRatio * h1a;
            }
            else
            {
                return height >= MinBoxHeightUsd && height <= MaxBoxHeightUsd;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  H1 VOL FILTER
        // ─────────────────────────────────────────────────────────────
        private bool PassesH1AtrFilter()
        {
            if (!UseH1AtrFilter) return true;
            if (_h1.Count < Math.Max(5, H1AtrPeriod + 2)) return false;

            double h1AtrVal = _h1Atr.Result.LastValue;
            bool   pass     = h1AtrVal >= MinH1Atr && h1AtrVal <= MaxH1Atr;
            Print($"H1 ATR={h1AtrVal:F2} [{MinH1Atr}–{MaxH1Atr}] Pass={pass}");
            return pass;
        }

        // ─────────────────────────────────────────────────────────────
        //  TIME HELPERS
        // ─────────────────────────────────────────────────────────────
        private bool IsInTradeHours(DateTime london)
        {
            var t = london.TimeOfDay;
            return t >= TimeSpan.Parse(TradeStart) && t <= TimeSpan.Parse(TradeEnd);
        }

        private bool IsWithinMinutesAfterBoxLock(DateTime london)
        {
            if (MinutesAfterBoxLock == 0) return true;
            if (!_boxLocked || _boxLockedLondonTime == DateTime.MinValue) return false;
            return london <= _boxLockedLondonTime.AddMinutes(MinutesAfterBoxLock);
        }

        // ─────────────────────────────────────────────────────────────
        //  M15 BREAKOUT GATE
        // ─────────────────────────────────────────────────────────────
        private void DetectM15Gate(DateTime london)
        {
            var m15OpenTime = _m15.OpenTimes.LastValue;
            if (m15OpenTime == _lastM15) return;
            _lastM15 = m15OpenTime;

            if (!_boxLocked || !_boxValid)            return;
            if (!IsInTradeHours(london))               return;
            if (!IsWithinMinutesAfterBoxLock(london))  return;
            if (!PassesH1AtrFilter())                  return;

            double m15Close   = _m15.ClosePrices.Last(1);
            double longLevel  = _boxHigh + BufferUsd;
            double shortLevel = _boxLow  - BufferUsd;

            if (AllowLongs && m15Close > longLevel && _gateDirection != 1)
            {
                _gateDirection = 1;
                _gateExpiry    = london.AddMinutes(15);
                Print($"M15 LONG GATE | M15Close={m15Close:F2} > {longLevel:F2} | Expiry={_gateExpiry:HH:mm}");
                return;
            }

            if (AllowShorts && m15Close < shortLevel && _gateDirection != -1)
            {
                _gateDirection = -1;
                _gateExpiry    = london.AddMinutes(15);
                Print($"M15 SHORT GATE | M15Close={m15Close:F2} < {shortLevel:F2} | Expiry={_gateExpiry:HH:mm}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  ENTRY LOGIC — M5 confirmation within gate window
        // ─────────────────────────────────────────────────────────────
        private void CheckEntry(DateTime london)
        {
            if (_gateDirection == 0) return;

            if (london > _gateExpiry)
            {
                Print($"GATE EXPIRED ({(_gateDirection == 1 ? "LONG" : "SHORT")})");
                _gateDirection = 0;
                return;
            }

            if (_tradedToday) return;

            if (GetMyPosition(TradeType.Buy)  != null ||
                GetMyPosition(TradeType.Sell) != null) return;

            if (!IsWithinMinutesAfterBoxLock(london))
            {
                Print("GATE CANCELLED — outside time window");
                _gateDirection = 0;
                return;
            }

            if (!PassesH1AtrFilter())
            {
                Print("GATE CANCELLED — H1 ATR failed");
                _gateDirection = 0;
                return;
            }

            double m5Close    = Bars.ClosePrices.Last(1);
            double emaVal     = _ema.Result.LastValue;
            double longLevel  = _boxHigh + BufferUsd;
            double shortLevel = _boxLow  - BufferUsd;

            if (_gateDirection == 1)
            {
                if (m5Close <= longLevel) return;
                if (UseEmaFilter && m5Close <= emaVal) return;
                ExecuteTrade(TradeType.Buy);
            }
            else
            {
                if (m5Close >= shortLevel) return;
                if (UseEmaFilter && m5Close >= emaVal) return;
                ExecuteTrade(TradeType.Sell);
            }

            _gateDirection = 0;
        }

        // ─────────────────────────────────────────────────────────────
        //  TRADE EXECUTION
        // ─────────────────────────────────────────────────────────────
        private void ExecuteTrade(TradeType direction)
        {
            double stopDistPrice = _atr.Result.LastValue * AtrStopMult;
            if (stopDistPrice <= 0)
            {
                Print("INVALID STOP DISTANCE — skipping");
                return;
            }

            double stopLossPips   = stopDistPrice / Symbol.PipSize;
            double takeProfitPips = stopLossPips  * TakeProfitR;

            double volumeInUnits = Symbol.VolumeForProportionalRisk(
                ProportionalAmountType.Equity,
                RiskPercent,
                stopLossPips,
                RoundingMode.Down);

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print($"VOLUME TOO SMALL | Calc={volumeInUnits} Min={Symbol.VolumeInUnitsMin}");
                return;
            }

            if (volumeInUnits > Symbol.VolumeInUnitsMax)
                volumeInUnits = Symbol.VolumeInUnitsMax;

            double estimatedRisk = Symbol.AmountRisked(volumeInUnits, stopLossPips);

            var result = ExecuteMarketOrder(
                direction, SymbolName, volumeInUnits, BotLabel,
                stopLossPips, takeProfitPips);

            if (result.IsSuccessful)
            {
                _tradedToday = true;
                Print($"{direction.ToString().ToUpper()} OPEN | " +
                      $"Entry={result.Position.EntryPrice:F2} | " +
                      $"SL={stopLossPips:F1}pips | TP={takeProfitPips:F1}pips | " +
                      $"Vol={volumeInUnits} | EstRisk={estimatedRisk:F2}");
            }
            else
            {
                Print($"ORDER FAILED: {result.Error}");
            }
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

            if (totalTrades   < MinTotalTrades)        return -1000000;
            if (tradesPerYear < MinTradesPerYear)       return -1000000;
            if (netProfit     <= 0)                    return -1000000;
            if (ddPct         > MaxFitnessDrawdownPct) return -1000000;

            double profitScore = Math.Log10(1.0 + netProfit);
            double tradeScore  = Math.Sqrt(totalTrades);
            double ddPenalty   = Math.Pow(ddPct, 1.5);

            return (profitScore * pf * tradeScore) / ddPenalty;
        }

        // ─────────────────────────────────────────────────────────────
        //  LONDON TIME — manual BST calculation
        //  Reliable in both cTrader backtest and live.
        //  TimeZoneInfo.ConvertTimeFromUtc is unreliable in backtest.
        //  BST: last Sunday of March 01:00 UTC to last Sunday of Oct 01:00 UTC
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
        private Position GetMyPosition(TradeType direction)
        {
            return Positions
                .Where(p => p.SymbolName == SymbolName &&
                            p.Label      == BotLabel   &&
                            p.TradeType  == direction)
                .OrderByDescending(p => p.EntryTime)
                .FirstOrDefault();
        }
    }
}
