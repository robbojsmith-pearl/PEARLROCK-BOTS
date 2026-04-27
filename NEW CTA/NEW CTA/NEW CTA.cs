using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  CTA v16 — Universal Session Box Breakout
    //  Pearlrock Systematic
    //
    //  Asset-agnostic version of CTA v15.
    //
    //  Core idea:
    //  - Build an optimisable session box in London time
    //  - Supports same-day and overnight box windows
    //  - Lock the box once the window closes
    //  - Confirm breakout on completed M15 close
    //  - Enter on M5 confirmation within the gate window
    //  - Optional EMA trend filter
    //  - Optional H1 ATR volatility filter
    //  - Optional ATR-relative box-height filter
    //  - ATR stop, TP in R
    //
    //  Main upgrades from v15:
    //  1. Runs on any asset, not only XAU/XAG.
    //  2. Box start/end are optimisable by hour + minute.
    //  3. Trade start/end are optimisable by hour + minute.
    //  4. Overnight-safe logic reused for both box and trade windows.
    //  5. London timezone works on Windows and Mac/Linux.
    //  6. Trade end is exclusive: t < end.
    //  7. Added Max Trades Per Session.
    //  8. Added optional spread filter in pips.
    //  9. Added Verbose Logging to reduce log spam.
    // 10. Generic price formatting using Symbol.Digits.
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class CTA_v16_UniversalBoxBreakout : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "CTA_v16_UNIVERSAL", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Box Session ──────────────────────────────────────────────
        [Parameter("Box Start Hour (London)", DefaultValue = 22, MinValue = 0, MaxValue = 23, Step = 1, Group = "Box Session")]
        public int BoxStartHour { get; set; }

        [Parameter("Box Start Minute", DefaultValue = 0, MinValue = 0, MaxValue = 45, Step = 15, Group = "Box Session")]
        public int BoxStartMinute { get; set; }

        [Parameter("Box End Hour (London)", DefaultValue = 5, MinValue = 0, MaxValue = 23, Step = 1, Group = "Box Session")]
        public int BoxEndHour { get; set; }

        [Parameter("Box End Minute", DefaultValue = 0, MinValue = 0, MaxValue = 45, Step = 15, Group = "Box Session")]
        public int BoxEndMinute { get; set; }

        // ── Trade Session ────────────────────────────────────────────
        [Parameter("Trade Start Hour (London)", DefaultValue = 6, MinValue = 0, MaxValue = 23, Step = 1, Group = "Trade Session")]
        public int TradeStartHour { get; set; }

        [Parameter("Trade Start Minute", DefaultValue = 0, MinValue = 0, MaxValue = 45, Step = 15, Group = "Trade Session")]
        public int TradeStartMinute { get; set; }

        [Parameter("Trade End Hour (London)", DefaultValue = 19, MinValue = 0, MaxValue = 23, Step = 1, Group = "Trade Session")]
        public int TradeEndHour { get; set; }

        [Parameter("Trade End Minute", DefaultValue = 0, MinValue = 0, MaxValue = 45, Step = 15, Group = "Trade Session")]
        public int TradeEndMinute { get; set; }

        // 0 = disabled. >0 = only allow gates/entries N minutes after box lock.
        [Parameter("Minutes After Box Lock (0=off)", DefaultValue = 0, MinValue = 0, MaxValue = 1440, Step = 60, Group = "Trade Session")]
        public int MinutesAfterBoxLock { get; set; }

        [Parameter("Breakout Buffer (price units)", DefaultValue = 2.0, MinValue = 0.0, Step = 0.1, Group = "Trade Session")]
        public double BreakoutBuffer { get; set; }

        [Parameter("Max Trades Per Session", DefaultValue = 1, MinValue = 1, MaxValue = 10, Step = 1, Group = "Trade Session")]
        public int MaxTradesPerSession { get; set; }

        // ── Direction ────────────────────────────────────────────────
        [Parameter("Allow Longs", DefaultValue = true, Group = "Direction")]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Shorts", DefaultValue = true, Group = "Direction")]
        public bool AllowShorts { get; set; }

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

        [Parameter("Trade Friday", DefaultValue = true, Group = "Day Filter")]
        public bool TradeFriday { get; set; }

        // ── Box Filters ──────────────────────────────────────────────
        [Parameter("Use Dynamic Box Height", DefaultValue = true, Group = "Box Filters")]
        public bool UseDynamicBoxHeight { get; set; }

        [Parameter("Min Box Height (× H1 ATR)", DefaultValue = 0.15, MinValue = 0.0, Step = 0.05, Group = "Box Filters")]
        public double MinBoxAtrRatio { get; set; }

        [Parameter("Max Box Height (× H1 ATR)", DefaultValue = 0.75, MinValue = 0.05, Step = 0.05, Group = "Box Filters")]
        public double MaxBoxAtrRatio { get; set; }

        [Parameter("Min Box Height (price units)", DefaultValue = 2.0, MinValue = 0.0, Step = 0.1, Group = "Box Filters")]
        public double MinBoxHeight { get; set; }

        [Parameter("Max Box Height (price units)", DefaultValue = 20.0, MinValue = 0.1, Step = 0.1, Group = "Box Filters")]
        public double MaxBoxHeight { get; set; }

        // ── Vol Filter ───────────────────────────────────────────────
        [Parameter("Use H1 ATR Filter", DefaultValue = true, Group = "Vol Filter")]
        public bool UseH1AtrFilter { get; set; }

        [Parameter("H1 ATR Period", DefaultValue = 14, MinValue = 1, Group = "Vol Filter")]
        public int H1AtrPeriod { get; set; }

        [Parameter("Min H1 ATR", DefaultValue = 0.0, MinValue = 0.0, Step = 0.1, Group = "Vol Filter")]
        public double MinH1Atr { get; set; }

        [Parameter("Max H1 ATR", DefaultValue = 999999.0, MinValue = 0.1, Step = 0.1, Group = "Vol Filter")]
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

        [Parameter("Max Trade Duration Hours (0=off)", DefaultValue = 8, MinValue = 0, MaxValue = 72, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        // ── EMA Filter ───────────────────────────────────────────────
        [Parameter("Use EMA Filter", DefaultValue = true, Group = "EMA Filter")]
        public bool UseEmaFilter { get; set; }

        [Parameter("EMA Period", DefaultValue = 55, MinValue = 1, Group = "EMA Filter")]
        public int EmaPeriod { get; set; }

        // ── Spread Filter ────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = false, Group = "Spread Filter")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread Pips", DefaultValue = 5.0, MinValue = 0.1, Step = 0.1, Group = "Spread Filter")]
        public double MaxSpreadPips { get; set; }

        // ── Circuit Breaker ──────────────────────────────────────────
        [Parameter("Session Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Circuit Breaker")]
        public double SessionLossLimitPct { get; set; }

        // ── Fitness ──────────────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 65, MinValue = 1, Step = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 20.0, MinValue = 1.0, Step = 1.0, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 15.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        [Parameter("Backtest Years", DefaultValue = 8.0, MinValue = 0.5, Step = 0.5, Group = "Fitness")]
        public double BacktestYears { get; set; }

        // ── Logging ──────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = false, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        // ── Indicators ───────────────────────────────────────────────
        private ExponentialMovingAverage _ema;
        private AverageTrueRange _atr;
        private Bars _m15;
        private Bars _h1;
        private AverageTrueRange _h1Atr;

        // ── Timezone ─────────────────────────────────────────────────
        private static readonly TimeZoneInfo LondonTz =
            TimeZoneInfo.FindSystemTimeZoneById(
                Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? "GMT Standard Time"
                    : "Europe/London"
            );

        // ── Box state ────────────────────────────────────────────────
        private double _boxHigh;
        private double _boxLow;
        private bool _boxLocked;
        private bool _boxValid;
        private bool _boxWindowEntered;
        private DateTime _boxLockedLondonTime;

        // ── Gate state ───────────────────────────────────────────────
        private int _gateDirection; // 1=long, -1=short, 0=none
        private DateTime _gateExpiryLondon;

        // ── Session state ────────────────────────────────────────────
        private DateTime _currentSessionDate = DateTime.MinValue;
        private int _tradesThisSession;
        private DateTime _lastM15OpenTime = DateTime.MinValue;

        // ── Circuit breaker ──────────────────────────────────────────
        private double _sessionStartEquity;
        private bool _circuitBroken;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5)
            {
                Print($"ABORT: Must run on M5. Current timeframe = {Bars.TimeFrame}");
                Stop();
                return;
            }

            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaPeriod);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _m15 = MarketData.GetBars(TimeFrame.Minute15);
            _h1 = MarketData.GetBars(TimeFrame.Hour);
            _h1Atr = Indicators.AverageTrueRange(_h1, H1AtrPeriod, MovingAverageType.Exponential);

            if (_m15.Count > 0)
                _lastM15OpenTime = _m15.OpenTimes.LastValue;

            _sessionStartEquity = Account.Equity;

            Print($"═══ {BotLabel} started on {SymbolName} M5 ═══");
            Print($"Universal asset mode: ON");
            Print($"Longs={AllowLongs} | Shorts={AllowShorts}");
            Print($"BoxWindow={FormatTime(GetBoxStart())}–{FormatTime(GetBoxEnd())} London | Overnight={IsOvernightWindow(GetBoxStart(), GetBoxEnd())}");
            Print($"TradeWindow={FormatTime(GetTradeStart())}–{FormatTime(GetTradeEnd())} London | Overnight={IsOvernightWindow(GetTradeStart(), GetTradeEnd())}");
            Print($"MinutesAfterBoxLock={(MinutesAfterBoxLock == 0 ? "off" : MinutesAfterBoxLock.ToString())}");
            Print($"MaxTradesPerSession={MaxTradesPerSession}");
            Print($"BreakoutBuffer={FormatPrice(BreakoutBuffer)} price units");
            Print($"DynamicBoxHeight={UseDynamicBoxHeight} | " +
                  (UseDynamicBoxHeight
                      ? $"ATR ratio [{MinBoxAtrRatio:F2}–{MaxBoxAtrRatio:F2}] × H1 ATR"
                      : $"Fixed [{FormatPrice(MinBoxHeight)}–{FormatPrice(MaxBoxHeight)}] price units"));
            Print($"H1AtrFilter={UseH1AtrFilter} | Range=[{FormatPrice(MinH1Atr)}–{FormatPrice(MaxH1Atr)}]");
            Print($"EMAFilter={UseEmaFilter} | EMA={EmaPeriod}");
            Print($"ATRStop={AtrStopMult:F2} × ATR({AtrPeriod}) | TP={TakeProfitR:F2}R | MaxTradeHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"SpreadFilter={(UseSpreadFilter ? $"on <= {MaxSpreadPips:F1} pips" : "off")}");
            Print($"CircuitBreaker={(SessionLossLimitPct > 0 ? SessionLossLimitPct + "% session loss" : "off")}");
            Print($"Symbol specs | Digits={Symbol.Digits} | PipSize={Symbol.PipSize} | TickSize={Symbol.TickSize} | VolMin={Symbol.VolumeInUnitsMin} | VolStep={Symbol.VolumeInUnitsStep}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5)
                return;

            DateTime london = TimeZoneInfo.ConvertTimeFromUtc(Server.Time, LondonTz);

            ResetSessionStateIfNeeded(london);

            // Always manage open trades first, even on filtered days.
            ManageOpenPositions();

            if (CheckCircuitBreaker())
                return;

            if (!PassesDayFilter(london))
                return;

            BuildBox(london);
            DetectM15Gate(london);
            CheckEntry(london);
        }

        // ─────────────────────────────────────────────────────────────
        //  SESSION RESET
        // ─────────────────────────────────────────────────────────────
        private void ResetSessionStateIfNeeded(DateTime london)
        {
            DateTime sessionDate = GetSessionDate(london);

            if (sessionDate == _currentSessionDate)
                return;

            _currentSessionDate = sessionDate;

            _boxHigh = 0;
            _boxLow = 0;
            _boxLocked = false;
            _boxValid = false;
            _boxWindowEntered = false;
            _boxLockedLondonTime = DateTime.MinValue;

            _gateDirection = 0;
            _gateExpiryLondon = DateTime.MinValue;

            _tradesThisSession = 0;

            _sessionStartEquity = Account.Equity;
            _circuitBroken = false;

            Print($"NEW SESSION {sessionDate:dd-MMM-yyyy ddd} | Box={FormatTime(GetBoxStart())}–{FormatTime(GetBoxEnd())} London");
        }

        private DateTime GetSessionDate(DateTime london)
        {
            TimeSpan start = GetBoxStart();
            TimeSpan end = GetBoxEnd();
            TimeSpan t = london.TimeOfDay;

            if (!IsOvernightWindow(start, end))
                return london.Date;

            return t >= start ? london.Date : london.Date.AddDays(-1);
        }

        // ─────────────────────────────────────────────────────────────
        //  DAY FILTER
        // ─────────────────────────────────────────────────────────────
        private bool PassesDayFilter(DateTime london)
        {
            if (!UseDayFilter)
                return true;

            switch (GetSessionDate(london).DayOfWeek)
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
        //  BOX BUILDING
        // ─────────────────────────────────────────────────────────────
        private void BuildBox(DateTime london)
        {
            bool inBoxWindow = IsInWindow(london.TimeOfDay, GetBoxStart(), GetBoxEnd());

            if (inBoxWindow)
            {
                _boxWindowEntered = true;

                double high = Bars.HighPrices.Last(1);
                double low = Bars.LowPrices.Last(1);

                if (_boxHigh == 0 || high > _boxHigh)
                    _boxHigh = high;

                if (_boxLow == 0 || low < _boxLow)
                    _boxLow = low;
            }

            if (!_boxLocked && _boxWindowEntered && !inBoxWindow)
            {
                _boxLocked = true;
                _boxLockedLondonTime = london;

                double height = GetBoxHeight();
                _boxValid = IsBoxHeightValid(height);

                Print($"BOX LOCKED | High={FormatPrice(_boxHigh)} Low={FormatPrice(_boxLow)} Height={FormatPrice(height)} Valid={_boxValid}");

                if (UseDynamicBoxHeight && H1AtrReady())
                {
                    double h1Atr = _h1Atr.Result.LastValue;
                    Print($"BOX DYNAMIC RANGE | H1ATR={FormatPrice(h1Atr)} | " +
                          $"Min={FormatPrice(MinBoxAtrRatio * h1Atr)} Max={FormatPrice(MaxBoxAtrRatio * h1Atr)}");
                }
            }
        }

        private double GetBoxHeight()
        {
            if (_boxHigh <= 0 || _boxLow <= 0)
                return 0;

            return _boxHigh - _boxLow;
        }

        private bool IsBoxHeightValid(double height)
        {
            if (height <= 0)
                return false;

            if (!UseDynamicBoxHeight)
                return height >= MinBoxHeight && height <= MaxBoxHeight;

            if (!H1AtrReady())
                return false;

            double h1Atr = _h1Atr.Result.LastValue;

            if (h1Atr <= 0)
                return false;

            double minHeight = MinBoxAtrRatio * h1Atr;
            double maxHeight = MaxBoxAtrRatio * h1Atr;

            return height >= minHeight && height <= maxHeight;
        }

        // ─────────────────────────────────────────────────────────────
        //  M15 BREAKOUT GATE
        // ─────────────────────────────────────────────────────────────
        private void DetectM15Gate(DateTime london)
        {
            if (_m15.Count < 3)
                return;

            DateTime m15OpenTime = _m15.OpenTimes.LastValue;

            if (m15OpenTime == _lastM15OpenTime)
                return;

            _lastM15OpenTime = m15OpenTime;

            if (!_boxLocked || !_boxValid)
                return;

            if (!IsInTradeHours(london))
                return;

            if (!IsWithinMinutesAfterBoxLock(london))
                return;

            if (!SafetyOk())
                return;

            if (!PassesH1AtrFilter())
                return;

            if (_tradesThisSession >= MaxTradesPerSession)
                return;

            if (HasOpenPosition())
                return;

            double m15Close = _m15.ClosePrices.Last(1);
            double longLevel = _boxHigh + BreakoutBuffer;
            double shortLevel = _boxLow - BreakoutBuffer;

            if (AllowLongs && m15Close > longLevel && _gateDirection != 1)
            {
                _gateDirection = 1;
                _gateExpiryLondon = london.AddMinutes(15);

                Print($"M15 LONG GATE | Close={FormatPrice(m15Close)} > Level={FormatPrice(longLevel)} | Expiry={_gateExpiryLondon:HH:mm}");
                return;
            }

            if (AllowShorts && m15Close < shortLevel && _gateDirection != -1)
            {
                _gateDirection = -1;
                _gateExpiryLondon = london.AddMinutes(15);

                Print($"M15 SHORT GATE | Close={FormatPrice(m15Close)} < Level={FormatPrice(shortLevel)} | Expiry={_gateExpiryLondon:HH:mm}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  M5 ENTRY CONFIRMATION
        // ─────────────────────────────────────────────────────────────
        private void CheckEntry(DateTime london)
        {
            if (_gateDirection == 0)
                return;

            if (london > _gateExpiryLondon)
            {
                Log($"GATE EXPIRED | Direction={(_gateDirection == 1 ? "LONG" : "SHORT")}");
                _gateDirection = 0;
                return;
            }

            if (_tradesThisSession >= MaxTradesPerSession)
                return;

            if (HasOpenPosition())
                return;

            if (!IsInTradeHours(london))
            {
                Log("GATE CANCELLED — outside trade window");
                _gateDirection = 0;
                return;
            }

            if (!IsWithinMinutesAfterBoxLock(london))
            {
                Log("GATE CANCELLED — outside minutes-after-box-lock window");
                _gateDirection = 0;
                return;
            }

            if (!SafetyOk())
            {
                Log("GATE CANCELLED — safety filter failed");
                _gateDirection = 0;
                return;
            }

            if (!PassesH1AtrFilter())
            {
                Log("GATE CANCELLED — H1 ATR filter failed");
                _gateDirection = 0;
                return;
            }

            double m5Close = Bars.ClosePrices.Last(1);
            double emaVal = _ema.Result.LastValue;
            double longLevel = _boxHigh + BreakoutBuffer;
            double shortLevel = _boxLow - BreakoutBuffer;

            if (_gateDirection == 1)
            {
                if (m5Close <= longLevel)
                    return;

                if (UseEmaFilter && m5Close <= emaVal)
                    return;

                ExecuteTrade(TradeType.Buy);
            }
            else if (_gateDirection == -1)
            {
                if (m5Close >= shortLevel)
                    return;

                if (UseEmaFilter && m5Close >= emaVal)
                    return;

                ExecuteTrade(TradeType.Sell);
            }

            _gateDirection = 0;
        }

        // ─────────────────────────────────────────────────────────────
        //  EXECUTION
        // ─────────────────────────────────────────────────────────────
        private void ExecuteTrade(TradeType direction)
        {
            double atrValue = _atr.Result.LastValue;

            if (atrValue <= 0)
            {
                Print("ENTRY BLOCKED — ATR invalid");
                return;
            }

            double stopDistPrice = atrValue * AtrStopMult;

            if (stopDistPrice <= 0)
            {
                Print("ENTRY BLOCKED — stop distance invalid");
                return;
            }

            double stopLossPips = stopDistPrice / Symbol.PipSize;
            double takeProfitPips = stopLossPips * TakeProfitR;

            if (stopLossPips <= 0 || takeProfitPips <= 0)
            {
                Print($"ENTRY BLOCKED — invalid SL/TP pips | SL={stopLossPips:F1} TP={takeProfitPips:F1}");
                return;
            }

            double volumeInUnits = Symbol.VolumeForProportionalRisk(
                ProportionalAmountType.Equity,
                RiskPercent,
                stopLossPips,
                RoundingMode.Down
            );

            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print($"ENTRY BLOCKED — volume too small | Calc={volumeInUnits} Min={Symbol.VolumeInUnitsMin}");
                return;
            }

            if (volumeInUnits > Symbol.VolumeInUnitsMax)
                volumeInUnits = Symbol.VolumeInUnitsMax;

            double estimatedRisk = Symbol.AmountRisked(volumeInUnits, stopLossPips);

            var result = ExecuteMarketOrder(
                direction,
                SymbolName,
                volumeInUnits,
                BotLabel,
                stopLossPips,
                takeProfitPips
            );

            if (result.IsSuccessful)
            {
                _tradesThisSession++;

                Print($"{direction.ToString().ToUpper()} OPEN | " +
                      $"Entry={FormatPrice(result.Position.EntryPrice)} | " +
                      $"SL={stopLossPips:F1}p | TP={takeProfitPips:F1}p | " +
                      $"ATR={FormatPrice(atrValue)} | Vol={volumeInUnits:F0} | EstRisk={estimatedRisk:F2} | " +
                      $"TradesThisSession={_tradesThisSession}/{MaxTradesPerSession}");
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
                double hoursOpen = (Server.Time - pos.EntryTime).TotalHours;

                if (hoursOpen >= MaxTradeHours)
                {
                    Print($"MAX DURATION EXIT | {hoursOpen:F1}h >= {MaxTradeHours}h | {pos.TradeType} | Net={pos.NetProfit:F2}");
                    ClosePosition(pos);
                }
            }
        }

        private bool HasOpenPosition()
        {
            return Positions.Any(p =>
                p.SymbolName == SymbolName &&
                p.Label == BotLabel);
        }

        // ─────────────────────────────────────────────────────────────
        //  FILTERS
        // ─────────────────────────────────────────────────────────────
        private bool PassesH1AtrFilter()
        {
            if (!UseH1AtrFilter)
                return true;

            if (!H1AtrReady())
                return false;

            double h1Atr = _h1Atr.Result.LastValue;
            bool pass = h1Atr >= MinH1Atr && h1Atr <= MaxH1Atr;

            if (!pass)
                Log($"H1 ATR BLOCK | H1ATR={FormatPrice(h1Atr)} Range=[{FormatPrice(MinH1Atr)}–{FormatPrice(MaxH1Atr)}]");

            return pass;
        }

        private bool SafetyOk()
        {
            if (!UseSpreadFilter)
                return true;

            double spreadPips = Symbol.Spread / Symbol.PipSize;

            if (spreadPips > MaxSpreadPips)
            {
                Log($"SPREAD BLOCK | Spread={spreadPips:F1}p > Max={MaxSpreadPips:F1}p");
                return false;
            }

            return true;
        }

        private bool CheckCircuitBreaker()
        {
            if (SessionLossLimitPct <= 0)
                return false;

            if (_sessionStartEquity <= 0)
                return false;

            double lossPct = (_sessionStartEquity - Account.Equity) / _sessionStartEquity * 100.0;

            if (lossPct >= SessionLossLimitPct)
            {
                if (!_circuitBroken)
                {
                    Print($"⚡ SESSION CIRCUIT BREAKER | Loss={lossPct:F2}% >= {SessionLossLimitPct:F2}%");

                    _circuitBroken = true;
                    _gateDirection = 0;

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

        private bool H1AtrReady()
        {
            return _h1 != null &&
                   _h1Atr != null &&
                   _h1.Count >= Math.Max(5, H1AtrPeriod + 2) &&
                   _h1Atr.Result.Count > H1AtrPeriod + 2;
        }

        // ─────────────────────────────────────────────────────────────
        //  TIME HELPERS
        // ─────────────────────────────────────────────────────────────
        private TimeSpan GetBoxStart()
        {
            return new TimeSpan(BoxStartHour, BoxStartMinute, 0);
        }

        private TimeSpan GetBoxEnd()
        {
            return new TimeSpan(BoxEndHour, BoxEndMinute, 0);
        }

        private TimeSpan GetTradeStart()
        {
            return new TimeSpan(TradeStartHour, TradeStartMinute, 0);
        }

        private TimeSpan GetTradeEnd()
        {
            return new TimeSpan(TradeEndHour, TradeEndMinute, 0);
        }

        private bool IsInTradeHours(DateTime london)
        {
            return IsInWindow(london.TimeOfDay, GetTradeStart(), GetTradeEnd());
        }

        private bool IsWithinMinutesAfterBoxLock(DateTime london)
        {
            if (MinutesAfterBoxLock == 0)
                return true;

            if (!_boxLocked || _boxLockedLondonTime == DateTime.MinValue)
                return false;

            return london <= _boxLockedLondonTime.AddMinutes(MinutesAfterBoxLock);
        }

        private bool IsInWindow(TimeSpan t, TimeSpan start, TimeSpan end)
        {
            if (start < end)
                return t >= start && t < end;

            if (start > end)
                return t >= start || t < end;

            return false;
        }

        private bool IsOvernightWindow(TimeSpan start, TimeSpan end)
        {
            return start > end;
        }

        private string FormatTime(TimeSpan t)
        {
            return $"{t.Hours:D2}:{t.Minutes:D2}";
        }

        private string FormatPrice(double price)
        {
            return price.ToString("F" + Symbol.Digits);
        }

        private void Log(string message)
        {
            if (VerboseLogging)
                Print(message);
        }

        // ─────────────────────────────────────────────────────────────
        //  CUSTOM FITNESS
        // ─────────────────────────────────────────────────────────────
        protected override double GetFitness(GetFitnessArgs args)
        {
            double testYears = BacktestYears > 0 ? BacktestYears : 1.0;
            double totalTrades = args.TotalTrades;
            double netProfit = args.NetProfit;
            double ddPct = Math.Max(args.MaxEquityDrawdownPercentages, 0.01);
            double pf = Math.Min(args.ProfitFactor, 3.0);
            double tradesPerYear = totalTrades / testYears;

            if (totalTrades < MinTotalTrades)
                return -1000000;

            if (tradesPerYear < MinTradesPerYear)
                return -1000000;

            if (netProfit <= 0)
                return -1000000;

            if (ddPct > MaxFitnessDrawdownPct)
                return -1000000;

            double profitScore = Math.Log10(1.0 + netProfit);
            double tradeScore = Math.Sqrt(totalTrades);
            double ddPenalty = Math.Pow(ddPct, 1.5);

            return (profitScore * pf * tradeScore) / ddPenalty;
        }
    }
}