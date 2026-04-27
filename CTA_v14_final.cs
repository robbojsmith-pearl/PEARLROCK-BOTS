using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  CTA v14 — Asian Box Breakout, Metals (XAUUSD / XAGUSD)
    //  Pearlrock Systematic
    //
    //  Changes from v13:
    //  1.  Short side added — box low breakout mirrors long logic
    //  2.  Day of week filter — per-day toggleable, defaults Mon/Tue/Wed
    //  3.  Circuit breaker — daily loss % limit, closes position, halts
    //  4.  London time via TimeZoneInfo — DST handled automatically
    //  5.  GetFitness TestYears calculated dynamically from backtest window
    //  6.  Gate direction stored explicitly (1=long, -1=short, 0=none)
    //  7.  Max trade duration exit — optimisable, 0=off
    //  8.  MinutesAfterBoxLock now optimisable, 0=disabled (TradeEnd only)
    //  9.  Instrument + timeframe guard on OnStart
    //  10. ManageOpenPosition runs before entry logic each bar
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class CTA_v14 : Robot
    {
        // ── Label ────────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "CTA_v14", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Session ──────────────────────────────────────────────────
        [Parameter("Box Start (London)", DefaultValue = "00:00", Group = "Session")]
        public string BoxStart { get; set; }

        [Parameter("Box End (London)", DefaultValue = "06:00", Group = "Session")]
        public string BoxEnd { get; set; }

        [Parameter("Trade Start (London)", DefaultValue = "06:00", Group = "Session")]
        public string TradeStart { get; set; }

        [Parameter("Trade End (London)", DefaultValue = "19:00", Group = "Session")]
        public string TradeEnd { get; set; }

        [Parameter("Breakout Buffer ($)", DefaultValue = 2.0, MinValue = 0.0, Step = 0.1, Group = "Session")]
        public double BufferUsd { get; set; }

        // 0 = disabled, TradeEnd is the only outer time boundary.
        // >0 = hard cut-off N minutes after box lock.
        // Optimise over 0, 120, 180, 240, 300, 360, 420, 480
        // to discover whether London-only or NY extension wins.
        [Parameter("Minutes After Box Lock (0=use TradeEnd only)", DefaultValue = 0, MinValue = 0, MaxValue = 900, Step = 60, Group = "Session")]
        public int MinutesAfterBoxLock { get; set; }

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

        [Parameter("Trade Thursday", DefaultValue = false, Group = "Day Filter")]
        public bool TradeThursday { get; set; }

        [Parameter("Trade Friday", DefaultValue = false, Group = "Day Filter")]
        public bool TradeFriday { get; set; }

        // ── Box Filters ──────────────────────────────────────────────
        [Parameter("Min Box Height ($)", DefaultValue = 2.0, MinValue = 0.0, Step = 0.1, Group = "Box Filters")]
        public double MinBoxHeight { get; set; }

        [Parameter("Max Box Height ($)", DefaultValue = 20.0, MinValue = 0.1, Step = 0.1, Group = "Box Filters")]
        public double MaxBoxHeight { get; set; }

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

        // 0 = off. Optimise over 4-12 step 1 to find sweet spot.
        // Empirical live data suggests 4-8 hours is the productive window.
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

        // ── Fitness Filters ──────────────────────────────────────────
        [Parameter("Min Total Trades", DefaultValue = 65, MinValue = 1, Step = 1, Group = "Fitness")]
        public int MinTotalTrades { get; set; }

        [Parameter("Min Trades Per Year", DefaultValue = 20.0, MinValue = 1.0, Step = 1.0, Group = "Fitness")]
        public double MinTradesPerYear { get; set; }

        [Parameter("Max Fitness DD %", DefaultValue = 15.0, MinValue = 1.0, Step = 0.5, Group = "Fitness")]
        public double MaxFitnessDrawdownPct { get; set; }

        // ── Indicators ───────────────────────────────────────────────
        private ExponentialMovingAverage _ema;
        private AverageTrueRange         _atr;
        private Bars                     _m15;
        private Bars                     _h1;
        private AverageTrueRange         _h1Atr;

        // ── London timezone — DST handled automatically ───────────────
        private static readonly TimeZoneInfo LondonTz =
            TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

        // ── Box state ────────────────────────────────────────────────
        private double   _boxHigh;
        private double   _boxLow;
        private bool     _boxLocked;
        private bool     _boxValid;
        private DateTime _boxLockedLondonTime;

        // ── Gate state ───────────────────────────────────────────────
        // 1 = long gate open, -1 = short gate open, 0 = no gate
        private int      _gateDirection;
        private DateTime _gateExpiry;

        // ── Daily state ──────────────────────────────────────────────
        private DateTime _currentLondonDay;
        private bool     _tradedToday;
        private DateTime _lastM15;

        // ── Circuit breaker ──────────────────────────────────────────
        private double _startOfDayEquity;
        private bool   _circuitBroken;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            // Instrument guard
            var sym = SymbolName.ToUpperInvariant();
            if (!sym.Contains("XAU") && !sym.Contains("XAG") &&
                !sym.Contains("GOLD") && !sym.Contains("SILVER"))
            {
                Print($"ABORT: {SymbolName} is not a metals instrument. Runs on XAU/XAG only.");
                Stop();
                return;
            }

            // Timeframe guard
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

            Print($"═══ {BotLabel} started on {SymbolName} M5 ═══");
            Print($"Longs={AllowLongs} Shorts={AllowShorts}");
            Print($"DayFilter={UseDayFilter} | Mon={TradeMonday} Tue={TradeTuesday} Wed={TradeWednesday} Thu={TradeThursday} Fri={TradeFriday}");
            Print($"BoxWindow={BoxStart}-{BoxEnd} London | TradeWindow={TradeStart}-{TradeEnd} London");
            Print($"MinutesAfterBoxLock={(MinutesAfterBoxLock == 0 ? "disabled (TradeEnd only)" : MinutesAfterBoxLock.ToString())}");
            Print($"MaxTradeHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"CircuitBreaker={(DailyLossLimitPct > 0 ? DailyLossLimitPct + "%" : "off")}");
            Print($"Symbol: PipSize={Symbol.PipSize} VolMin={Symbol.VolumeInUnitsMin} VolStep={Symbol.VolumeInUnitsStep}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5) return;

            var london = TimeZoneInfo.ConvertTimeFromUtc(Server.Time, LondonTz);

            ResetDailyState(london);

            if (CheckCircuitBreaker()) return;
            if (!PassesDayFilter(london)) return;

            ManageOpenPosition();

            BuildAsianBox(london);
            DetectM15Gate(london);
            CheckEntry(london);
        }

        // ─────────────────────────────────────────────────────────────
        //  DAILY RESET
        // ─────────────────────────────────────────────────────────────
        private void ResetDailyState(DateTime london)
        {
            if (london.Date == _currentLondonDay) return;

            _currentLondonDay    = london.Date;
            _boxHigh             = 0;
            _boxLow              = 0;
            _boxLocked           = false;
            _boxValid            = false;
            _boxLockedLondonTime = DateTime.MinValue;
            _gateDirection       = 0;
            _tradedToday         = false;
            _startOfDayEquity    = Account.Equity;
            _circuitBroken       = false;

            Print($"NEW LONDON DAY {london:dd-MMM-yyyy ddd}");
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
                default:                  return false; // Weekend
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  CIRCUIT BREAKER
        // ─────────────────────────────────────────────────────────────
        private bool CheckCircuitBreaker()
        {
            if (DailyLossLimitPct <= 0) return false;
            if (_startOfDayEquity  <= 0) return false;

            var lossPercent = (_startOfDayEquity - Account.Equity) / _startOfDayEquity * 100.0;

            if (lossPercent >= DailyLossLimitPct)
            {
                if (!_circuitBroken)
                {
                    Print($"⚡ CIRCUIT BREAKER | Loss={lossPercent:F2}% >= {DailyLossLimitPct}%");
                    _circuitBroken = true;
                    _gateDirection = 0;

                    var pos = GetMyPosition(TradeType.Buy) ?? GetMyPosition(TradeType.Sell);
                    if (pos != null)
                    {
                        Print("CIRCUIT BREAKER: closing position");
                        ClosePosition(pos);
                    }
                }
                return true;
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT — runs every bar before entry logic
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
        //  ASIAN BOX
        // ─────────────────────────────────────────────────────────────
        private void BuildAsianBox(DateTime london)
        {
            var t        = london.TimeOfDay;
            var boxStart = TimeSpan.Parse(BoxStart);
            var boxEnd   = TimeSpan.Parse(BoxEnd);

            // Accumulate box during Asian session window
            if (t >= boxStart && t < boxEnd)
            {
                double high = Bars.HighPrices.Last(1);
                double low  = Bars.LowPrices.Last(1);

                if (_boxHigh == 0 || high > _boxHigh) _boxHigh = high;
                if (_boxLow  == 0 || low  < _boxLow)  _boxLow  = low;
            }

            // Lock the box once at box end
            if (!_boxLocked && t >= boxEnd)
            {
                _boxLocked           = true;
                _boxLockedLondonTime = london;

                double height = GetBoxHeight();
                _boxValid = height >= MinBoxHeight && height <= MaxBoxHeight;

                Print($"BOX LOCKED | High={_boxHigh:F2} Low={_boxLow:F2} Height={height:F2} Valid={_boxValid}");
            }
        }

        private double GetBoxHeight() =>
            (_boxHigh > 0 && _boxLow > 0) ? _boxHigh - _boxLow : 0;

        // ─────────────────────────────────────────────────────────────
        //  H1 VOL FILTER
        // ─────────────────────────────────────────────────────────────
        private bool PassesH1AtrFilter()
        {
            if (!UseH1AtrFilter) return true;
            if (_h1.Count < Math.Max(5, H1AtrPeriod + 2)) return false;

            double h1AtrVal = _h1Atr.Result.LastValue;
            bool   pass     = h1AtrVal >= MinH1Atr && h1AtrVal <= MaxH1Atr;

            Print($"H1 ATR={h1AtrVal:F2} [{MinH1Atr}-{MaxH1Atr}] Pass={pass}");
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
            // 0 = disabled, TradeEnd controls the outer boundary instead
            if (MinutesAfterBoxLock == 0) return true;

            if (!_boxLocked || _boxLockedLondonTime == DateTime.MinValue) return false;
            return london <= _boxLockedLondonTime.AddMinutes(MinutesAfterBoxLock);
        }

        // ─────────────────────────────────────────────────────────────
        //  M15 BREAKOUT GATE
        //  Fires once per M15 bar close.
        //  Opens long gate if M15 closes above box high + buffer.
        //  Opens short gate if M15 closes below box low - buffer.
        // ─────────────────────────────────────────────────────────────
        private void DetectM15Gate(DateTime london)
        {
            var m15OpenTime = _m15.OpenTimes.LastValue;
            if (m15OpenTime == _lastM15) return;
            _lastM15 = m15OpenTime;

            if (!_boxLocked || !_boxValid)           return;
            if (!IsInTradeHours(london))              return;
            if (!IsWithinMinutesAfterBoxLock(london)) return;
            if (!PassesH1AtrFilter())                 return;

            double m15Close   = _m15.ClosePrices.Last(1);
            double longLevel  = _boxHigh + BufferUsd;
            double shortLevel = _boxLow  - BufferUsd;

            // Long gate
            if (AllowLongs && m15Close > longLevel && _gateDirection != 1)
            {
                _gateDirection = 1;
                _gateExpiry    = london.AddMinutes(15);
                Print($"M15 LONG GATE | M15Close={m15Close:F2} > {longLevel:F2} | Expiry={_gateExpiry:HH:mm}");
                return; // one gate per M15 bar
            }

            // Short gate
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

            // Gate expired
            if (london > _gateExpiry)
            {
                Print($"GATE EXPIRED ({(_gateDirection == 1 ? "LONG" : "SHORT")})");
                _gateDirection = 0;
                return;
            }

            if (_tradedToday) return;

            // Already have an open position
            if (GetMyPosition(TradeType.Buy) != null ||
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
            double takeProfitPips = stopLossPips * TakeProfitR;

            double volumeInUnits = Symbol.VolumeForProportionalRisk(
                ProportionalAmountType.Equity,
                RiskPercent,
                stopLossPips,
                RoundingMode.Down
            );

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
                direction,
                SymbolName,
                volumeInUnits,
                BotLabel,
                stopLossPips,
                takeProfitPips
            );

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
        //  CUSTOM FITNESS FUNCTION
        //  TestYears calculated dynamically — accurate for any
        //  backtest window, no hardcoded constants.
        // ─────────────────────────────────────────────────────────────
        protected override double GetFitness(GetFitnessArgs args)
        {
            double testYears = (args.EndTime - args.StartTime).TotalDays / 365.25;
            if (testYears <= 0) testYears = 1;

            double totalTrades   = args.TotalTrades;
            double netProfit     = args.NetProfit;
            double ddPct         = Math.Max(args.MaxEquityDrawdownPercentages, 0.01);
            double pf            = Math.Min(args.ProfitFactor, 3.0);
            double tradesPerYear = totalTrades / testYears;

            // Hard disqualifiers
            if (totalTrades   < MinTotalTrades)        return -1000000;
            if (tradesPerYear < MinTradesPerYear)       return -1000000;
            if (netProfit     <= 0)                    return -1000000;
            if (ddPct         > MaxFitnessDrawdownPct) return -1000000;

            // Score: reward profit and trade frequency, penalise drawdown
            double profitScore = Math.Log10(1.0 + netProfit);
            double tradeScore  = Math.Sqrt(totalTrades);
            double ddPenalty   = Math.Pow(ddPct, 1.5);

            return (profitScore * pf * tradeScore) / ddPenalty;
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
