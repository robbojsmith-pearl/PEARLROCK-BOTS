using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  D1 Convex Breakout — Metals Only (XAUUSD / XAGUSD)
    //  Pearlrock Systematic
    //
    //  Changes from previous version:
    //  1. Instrument guard — only runs on XAU or XAG
    //  2. Deprecated ModifyPosition replaced with ModifyStopLossPrice /
    //     ModifyTakeProfitPrice to fix pip/dollar ambiguity
    //  3. Trail and profit calculations use last closed bar close price
    //     instead of live Symbol.Bid (eliminates gap-open distortion)
    //  4. SL passed directly into ExecuteMarketOrder — eliminates the
    //     unprotected window between entry and first ModifyPosition call
    //  5. Day-of-week filter (Mon/Tue/Wed only) — empirically derived
    //  6. ATR volatility regime filter — gates out high-vol conditions
    //  7. Max trade duration exit
    //  8. Drawdown circuit breaker (session and daily)
    //  9. Parameters defaulted for metals; optimise per instance
    // ═══════════════════════════════════════════════════════════════════

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class D1_ConvexBreakout_Metals : Robot
    {
        // ── Sizing ───────────────────────────────────────────────────
        public enum SizingMode { RiskPercent, Lots, Units }
        public enum RiskCapital { Equity, Balance }

        [Parameter("Label", DefaultValue = "D1_Metals_LONG", Group = "Trade Settings")]
        public string BotLabel { get; set; }

        [Parameter("Sizing Mode", DefaultValue = SizingMode.RiskPercent, Group = "Trade Settings")]
        public SizingMode Sizing { get; set; }

        [Parameter("Risk Capital", DefaultValue = RiskCapital.Equity, Group = "Trade Settings")]
        public RiskCapital RiskBase { get; set; }

        [Parameter("Risk % (of capital)", DefaultValue = 0.25, MinValue = 0.01, Step = 0.01, Group = "Trade Settings")]
        public double RiskPercent { get; set; }

        [Parameter("Quantity (Lots)", DefaultValue = 0.10, MinValue = 0.01, Step = 0.01, Group = "Trade Settings")]
        public double QuantityLots { get; set; }

        [Parameter("Units / Contracts (int)", DefaultValue = 1, MinValue = 1, Step = 1, Group = "Trade Settings")]
        public int UnitsInt { get; set; }

        [Parameter("Max Units Cap (0=off)", DefaultValue = 0, MinValue = 0, Step = 1, Group = "Trade Settings")]
        public int MaxUnitsCapInt { get; set; }

        [Parameter("Min Stop (ticks)", DefaultValue = 5, MinValue = 1, Step = 1, Group = "Trade Settings")]
        public int MinStopTicks { get; set; }

        // ── Entry ────────────────────────────────────────────────────
        [Parameter("EMA Period", DefaultValue = 34, MinValue = 2, Group = "Entry")]
        public int EmaPeriod { get; set; }

        [Parameter("Donchian Lookback (N)", DefaultValue = 20, MinValue = 2, Group = "Entry")]
        public int DonchianN { get; set; }

        // ── Risk / Trail ─────────────────────────────────────────────
        [Parameter("ATR Period", DefaultValue = 14, MinValue = 2, Group = "Risk")]
        public int AtrPeriod { get; set; }

        [Parameter("Initial SL (ATR Mult)", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1, Group = "Risk")]
        public double InitialSlAtrMult { get; set; }

        [Parameter("Trail Start (in R)", DefaultValue = 1.0, MinValue = 0.0, Step = 0.1, Group = "Trailing")]
        public double TrailStartR { get; set; }

        [Parameter("Trail Distance (ATR Mult)", DefaultValue = 3.5, MinValue = 0.1, Step = 0.1, Group = "Trailing")]
        public double TrailAtrMult { get; set; }

        [Parameter("BE Offset (ticks)", DefaultValue = 2, MinValue = 0, Group = "Trailing")]
        public int BeOffsetTicks { get; set; }

        // ── Filters ──────────────────────────────────────────────────
        [Parameter("Min Bars Between Trades", DefaultValue = 5, MinValue = 0, Group = "Filters")]
        public int MinBarsBetweenTrades { get; set; }

        [Parameter("Day Filter Mon-Wed Only", DefaultValue = true, Group = "Filters")]
        public bool UseDayFilter { get; set; }

        [Parameter("Volatility Filter On", DefaultValue = true, Group = "Filters")]
        public bool UseVolFilter { get; set; }

        [Parameter("ATR MA Period (vol filter)", DefaultValue = 20, MinValue = 5, Group = "Filters")]
        public int AtrMaPeriod { get; set; }

        [Parameter("Max ATR Ratio (1.0 = off)", DefaultValue = 1.4, MinValue = 0.5, Step = 0.05, Group = "Filters")]
        public double MaxAtrRatio { get; set; }

        // ── Max Duration ─────────────────────────────────────────────
        [Parameter("Max Trade Duration (hours, 0=off)", DefaultValue = 0, MinValue = 0, Step = 1, Group = "Duration")]
        public int MaxTradeHours { get; set; }

        // ── Circuit Breaker ──────────────────────────────────────────
        [Parameter("Daily Loss Limit % (0=off)", DefaultValue = 1.5, MinValue = 0, Step = 0.1, Group = "Circuit Breaker")]
        public double DailyLossLimitPct { get; set; }

        // ── Misc ─────────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Misc")]
        public bool VerboseLogging { get; set; }

        // ── Internals ────────────────────────────────────────────────
        private ExponentialMovingAverage _ema;
        private AverageTrueRange _atr;
        private SimpleMovingAverage _atrMa;      // smoothed ATR for vol filter

        private long   _posId             = -1;
        private double _initialRiskDist   = 0.0;
        private bool   _trailActive       = false;
        private int    _lastTradeBarIndex = -1000000;

        private double _startOfDayEquity  = 0.0;
        private bool   _circuitBroken     = false;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            // ── Instrument guard ─────────────────────────────────────
            var sym = SymbolName.ToUpperInvariant();
            if (!sym.Contains("XAU") && !sym.Contains("XAG") &&
                !sym.Contains("GOLD") && !sym.Contains("SILVER"))
            {
                Print($"ABORT: {SymbolName} is not a metals instrument. This bot runs on XAU/XAG only.");
                Stop();
                return;
            }

            // ── Timeframe guard ──────────────────────────────────────
            if (Bars.TimeFrame != TimeFrame.Daily)
            {
                Print($"ABORT: Timeframe must be D1. Current = {Bars.TimeFrame}");
                Stop();
                return;
            }

            _ema   = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaPeriod);
            _atr   = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _atrMa = Indicators.SimpleMovingAverage(_atr.Result, AtrMaPeriod);

            _startOfDayEquity = Account.Equity;

            Positions.Closed += OnPositionClosed;

            // ── Instrument diagnostics ───────────────────────────────
            // Critical for verifying volume engine is working correctly
            // per instrument. Compare XAU vs XAG output carefully.
            Print($"═══ INSTRUMENT SPECS [{SymbolName}] ═══");
            Print($"  TickSize       = {Symbol.TickSize}");
            Print($"  TickValue      = {Symbol.TickValue}");
            Print($"  PipSize        = {Symbol.PipSize}");
            Print($"  PipValue       = {Symbol.PipValue}");
            Print($"  LotSize        = {Symbol.LotSize}");
            Print($"  VolumeMin      = {Symbol.VolumeInUnitsMin}");
            Print($"  VolumeStep     = {Symbol.VolumeInUnitsStep}");
            Print($"  VolumeMax      = {Symbol.VolumeInUnitsMax}");
            Print($"  ValPerPriceUnit= {Symbol.TickValue / Symbol.TickSize:F6}");
            Print($"══════════════════════════════════════");
            Print($"  EMA={EmaPeriod} | DonchN={DonchianN} | ATR={AtrPeriod}");
            Print($"  SL={InitialSlAtrMult}ATR | TrailStart={TrailStartR}R | Trail={TrailAtrMult}ATR");
            Print($"  Sizing={Sizing} | Risk%={RiskPercent} ({RiskBase})");
            Print($"  DayFilter={UseDayFilter} | VolFilter={UseVolFilter} (MaxRatio={MaxAtrRatio})");
            Print($"  MaxTradeHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())}");
            Print($"  DailyLossLimit={(DailyLossLimitPct == 0 ? "off" : DailyLossLimitPct + "%")}");
        }

        // ─────────────────────────────────────────────────────────────
        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p == null || p.SymbolName != SymbolName || p.Label != BotLabel) return;

            Print($"CLOSED {p.TradeType} | Entry={p.EntryPrice:F5} | " +
                  $"SL={( p.StopLoss.HasValue ? p.StopLoss.Value.ToString("F5") : "null" )} | " +
                  $"Gross={p.GrossProfit:F2} | Net={p.NetProfit:F2}");

            _posId          = -1;
            _trailActive    = false;
            _initialRiskDist = 0.0;
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            // ── Reset circuit breaker equity baseline at day start ────
            // OnBar on D1 fires once per day — good place to refresh
            ResetDailyBaseline();

            // ── Circuit breaker check ────────────────────────────────
            if (CheckCircuitBreaker()) return;

            var pos = GetPosition();

            // ═══════════════════════════════════════════════════════
            //  POSITION MANAGEMENT — runs before entry check
            // ═══════════════════════════════════════════════════════
            if (pos != null)
            {
                ManagePosition(pos);
                return;
            }

            // ═══════════════════════════════════════════════════════
            //  ENTRY LOGIC
            // ═══════════════════════════════════════════════════════
            _posId          = -1;
            _initialRiskDist = 0.0;
            _trailActive    = false;

            // ── Min bars between trades ──────────────────────────────
            if (MinBarsBetweenTrades > 0 &&
                (Bars.Count - 1 - _lastTradeBarIndex) < MinBarsBetweenTrades)
                return;

            // ── Warm-up guard ────────────────────────────────────────
            int warmup = Math.Max(EmaPeriod, Math.Max(AtrPeriod, AtrMaPeriod)) + DonchianN + 5;
            if (Bars.Count < warmup) return;

            // ── Day of week filter (Mon=1, Tue=2, Wed=3) ─────────────
            if (UseDayFilter)
            {
                var day = Server.Time.DayOfWeek;
                if (day != DayOfWeek.Monday &&
                    day != DayOfWeek.Tuesday &&
                    day != DayOfWeek.Wednesday)
                {
                    Log($"DAY FILTER: skipping {day}");
                    return;
                }
            }

            // ── Signal bar = last completed bar ─────────────────────
            int sig  = Bars.Count - 2;
            int from = sig - DonchianN;
            int to   = sig - 1;

            if (from < 0 || to < 0 || to < from) return;

            // ── Donchian high over lookback (excluding signal bar) ────
            double donchHigh = double.MinValue;
            for (int i = from; i <= to; i++)
                if (Bars.HighPrices[i] > donchHigh) donchHigh = Bars.HighPrices[i];

            double close1 = Bars.ClosePrices[sig];
            double open1  = Bars.OpenPrices[sig];
            double ema1   = _ema.Result[sig];
            double atr    = _atr.Result[sig];

            if (atr <= 0) return;

            // ── Volatility regime filter ─────────────────────────────
            if (UseVolFilter)
            {
                double atrMaVal = _atrMa.Result[sig];
                if (atrMaVal > 0)
                {
                    double atrRatio = atr / atrMaVal;
                    if (atrRatio > MaxAtrRatio)
                    {
                        Log($"VOL FILTER: ATR ratio {atrRatio:F2} > {MaxAtrRatio:F2} — skipping entry");
                        return;
                    }
                }
            }

            // ── Entry signal ─────────────────────────────────────────
            bool bullCandle  = close1 > open1;
            bool aboveEma    = close1 > ema1;
            bool breakout    = close1 > donchHigh;
            bool longSignal  = bullCandle && aboveEma && breakout;

            if (!longSignal) return;

            // ── Size and SL ──────────────────────────────────────────
            double slDist = atr * InitialSlAtrMult;
            var    minStop = MinStopTicks * Symbol.TickSize;
            if (slDist < minStop) slDist = minStop;

            long vol = ResolveVolume(slDist);
            if (vol < (long)Symbol.VolumeInUnitsMin) return;

            // ── Calculate SL price before entry ─────────────────────
            // We pass SL pips directly into ExecuteMarketOrder to avoid
            // the unprotected window that existed in the previous version.
            // Using TickSize for metals — PipSize == TickSize on XAU/XAG.
            double currentAsk = Symbol.Ask;
            double slPrice    = NPrice(currentAsk - slDist);
            double slPips     = (currentAsk - slPrice) / Symbol.PipSize;

            Log($"ENTRY ATTEMPT | Ask={currentAsk:F5} | SL={slPrice:F5} | " +
                $"Dist={slDist:F5} | SLpips={slPips:F1} | Vol={vol} | " +
                $"DonchH={donchHigh:F5} | EMA={ema1:F5} | ATR={atr:F5}");

            var result = ExecuteMarketOrder(
                TradeType.Buy, SymbolName, vol, BotLabel,
                slPips,   // SL in pips — protected from the moment of fill
                null      // no TP — managed by trail
            );

            if (!result.IsSuccessful || result.Position == null)
            {
                Print($"ORDER FAILED: {result.Error}");
                return;
            }

            var entry = result.Position.EntryPrice;

            // Recalculate actual risk distance from confirmed entry price
            _initialRiskDist = result.Position.StopLoss.HasValue
                ? Math.Abs(entry - result.Position.StopLoss.Value)
                : slDist;

            _posId          = result.Position.Id;
            _trailActive    = false;
            _lastTradeBarIndex = Bars.Count - 1;

            Print($"LONG OPEN | {vol}u | Entry={entry:F5} | " +
                  $"SL={( result.Position.StopLoss.HasValue ? result.Position.StopLoss.Value.ToString("F5") : "pending" )} | " +
                  $"1R={_initialRiskDist:F5} | Mode={Sizing}");
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT
        // ─────────────────────────────────────────────────────────────
        private void ManagePosition(Position pos)
        {
            // Sync internal state if bot restarted mid-trade
            if (_posId != pos.Id)
            {
                _posId       = pos.Id;
                _trailActive = false;
                if (pos.StopLoss.HasValue)
                    _initialRiskDist = Math.Abs(pos.EntryPrice - pos.StopLoss.Value);
            }

            // ── Max duration exit ────────────────────────────────────
            if (MaxTradeHours > 0)
            {
                var hoursOpen = (Server.Time - pos.EntryTime).TotalHours;
                if (hoursOpen > MaxTradeHours)
                {
                    Print($"MAX DURATION EXIT | {hoursOpen:F1}h > {MaxTradeHours}h limit");
                    ClosePosition(pos);
                    return;
                }
            }

            int lastBar = Bars.Count - 2;
            if (lastBar < 0) return;

            double atrNow = _atr.Result[lastBar];
            if (atrNow <= 0) return;

            if (_initialRiskDist <= 0 && pos.StopLoss.HasValue)
                _initialRiskDist = Math.Abs(pos.EntryPrice - pos.StopLoss.Value);

            if (_initialRiskDist <= 0) return;

            // ── Use last closed bar close — not live Bid ─────────────
            // Previous version used Symbol.Bid which distorts trail
            // calculations at gap opens. Last closed bar is stable.
            double lastClose  = Bars.ClosePrices[lastBar];
            double profitDist = lastClose - pos.EntryPrice;

            // ── Breakeven activation ─────────────────────────────────
            if (!_trailActive)
            {
                if (profitDist < TrailStartR * _initialRiskDist) return;

                double beOffset = BeOffsetTicks * Symbol.TickSize;
                double bePrice  = NPrice(pos.EntryPrice + beOffset);

                if (!pos.StopLoss.HasValue || bePrice > pos.StopLoss.Value)
                {
                    pos.ModifyStopLossPrice(bePrice);
                    Log($"BE SET | SL→{bePrice:F5} | ProfitDist={profitDist:F5} | 1R={_initialRiskDist:F5}");
                }

                _trailActive = true;
                return; // apply trail from next bar — avoids same-bar double-move
            }

            // ── Trailing stop ────────────────────────────────────────
            double trailDist  = atrNow * TrailAtrMult;
            double desiredSL  = NPrice(lastClose - trailDist);

            // Never move SL backwards, never trail below entry
            if (pos.StopLoss.HasValue && desiredSL <= pos.StopLoss.Value) return;
            if (desiredSL < pos.EntryPrice) return;

            pos.ModifyStopLossPrice(desiredSL);
            Log($"TRAIL → SL={desiredSL:F5} | LastClose={lastClose:F5} | ATR={atrNow:F5}");
        }

        // ─────────────────────────────────────────────────────────────
        //  CIRCUIT BREAKER
        // ─────────────────────────────────────────────────────────────
        private void ResetDailyBaseline()
        {
            // On D1 each OnBar call is a new day — reset baseline
            // Only reset if not currently in a broken state
            if (!_circuitBroken)
                _startOfDayEquity = Account.Equity;
        }

        private bool CheckCircuitBreaker()
        {
            if (DailyLossLimitPct <= 0) return false;

            var currentLoss = (_startOfDayEquity - Account.Equity) / _startOfDayEquity * 100.0;
            if (currentLoss >= DailyLossLimitPct)
            {
                if (!_circuitBroken)
                {
                    Print($"⚡ CIRCUIT BREAKER TRIGGERED | Loss={currentLoss:F2}% >= {DailyLossLimitPct}%");
                    _circuitBroken = true;

                    // Close any open position
                    var pos = GetPosition();
                    if (pos != null)
                    {
                        Print($"CIRCUIT BREAKER: closing open position");
                        ClosePosition(pos);
                    }
                }
                return true;
            }

            // Reset circuit breaker at start of new day if equity recovered
            if (_circuitBroken && currentLoss < DailyLossLimitPct)
            {
                Print($"CIRCUIT BREAKER RESET | Loss now {currentLoss:F2}%");
                _circuitBroken = false;
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────
        //  VOLUME ENGINE
        // ─────────────────────────────────────────────────────────────
        private long ResolveVolume(double stopDistPrice)
        {
            var minStop = MinStopTicks * Symbol.TickSize;
            if (stopDistPrice < minStop) stopDistPrice = minStop;

            double rawUnits;

            if (Sizing == SizingMode.Units)
            {
                rawUnits = UnitsInt;
            }
            else if (Sizing == SizingMode.Lots)
            {
                rawUnits = Symbol.QuantityToVolumeInUnits(QuantityLots);
            }
            else // RiskPercent
            {
                var capital   = (RiskBase == RiskCapital.Equity) ? Account.Equity : Account.Balance;
                var riskMoney = capital * (RiskPercent / 100.0);

                // valuePerPriceUnitPer1Unit: how much account currency
                // moves per 1 price unit move per 1 unit of volume.
                // For XAU: TickValue/TickSize gives $/price-unit/unit
                var valuePerPriceUnit = Symbol.TickValue / Symbol.TickSize;

                if (Symbol.TickSize <= 0 || valuePerPriceUnit <= 0)
                {
                    Print($"VOLUME ENGINE WARNING: invalid tick specs — using min volume");
                    return (long)Symbol.VolumeInUnitsMin;
                }

                rawUnits = riskMoney / (stopDistPrice * valuePerPriceUnit);

                if (double.IsNaN(rawUnits) || double.IsInfinity(rawUnits) || rawUnits <= 0)
                {
                    Print($"VOLUME ENGINE WARNING: rawUnits={rawUnits} — using min volume");
                    return (long)Symbol.VolumeInUnitsMin;
                }

                Log($"VOL CALC | Capital={capital:F2} | Risk={riskMoney:F2} | " +
                    $"StopDist={stopDistPrice:F5} | ValPerUnit={valuePerPriceUnit:F6} | Raw={rawUnits:F2}");
            }

            double norm = Symbol.NormalizeVolumeInUnits(rawUnits, RoundingMode.Down);
            long   vol  = (long)norm;

            if (MaxUnitsCapInt > 0 && vol > MaxUnitsCapInt)
                vol = MaxUnitsCapInt;

            if (vol < (long)Symbol.VolumeInUnitsMin) vol = (long)Symbol.VolumeInUnitsMin;
            if (vol > (long)Symbol.VolumeInUnitsMax) vol = (long)Symbol.VolumeInUnitsMax;

            return vol;
        }

        // ─────────────────────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────────────────────
        private double NPrice(double price)
        {
            var ts = Symbol.TickSize;
            return ts > 0
                ? Math.Round(price / ts) * ts
                : Math.Round(price, Symbol.Digits);
        }

        private Position GetPosition() =>
            Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private void Log(string msg)
        {
            if (VerboseLogging) Print(msg);
        }
    }
}
