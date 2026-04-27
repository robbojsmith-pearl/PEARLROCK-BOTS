using cAlgo.API;
using cAlgo.API.Indicators;
using System;
using System.Linq;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    //  LiquidityWickBot v4 — Multi-pair FX Liquidity Sweep / Wick Rejection
    //  Pearlrock Systematic
    //
    //  Changes from v3:
    //  1.  London time via TimeZoneInfo — DST handled automatically
    //  2.  TrailPositions uses last closed bar price, not live Bid/Ask
    //  3.  ModifyPosition replaced with ModifyStopLossPrice
    //  4.  Circuit breaker — daily loss % limit
    //  5.  Day of week filter — per-day toggleable, default all days on
    //  6.  Higher timeframe trend filter — D1 EMA, toggleable
    //  7.  Session classifier — Asian/London/NY/Overlap filter
    //  8.  Max trade duration exit — force close after N hours
    //  9.  Instrument guard — warns if not FX
    //  10. Partial close at 1R — closes half position, moves SL to BE
    // ═══════════════════════════════════════════════════════════════════

    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC)]
    public class LiquidityWickBot_v4 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "LIQ_WICK_V4", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Structure (Pro Swings) ────────────────────────────────────
        [Parameter("Use Pro Structure (Swings)", DefaultValue = true, Group = "Structure")]
        public bool UseProStructure { get; set; }

        [Parameter("Swing Left Bars", DefaultValue = 3, MinValue = 1, Group = "Structure")]
        public int SwingLeft { get; set; }

        [Parameter("Swing Right Bars", DefaultValue = 3, MinValue = 1, Group = "Structure")]
        public int SwingRight { get; set; }

        [Parameter("Max Swing Age (bars)", DefaultValue = 400, MinValue = 50, Group = "Structure")]
        public int MaxSwingAgeBars { get; set; }

        [Parameter("Min Swing Distance (pips)", DefaultValue = 20, MinValue = 0, Step = 1, Group = "Structure")]
        public double MinSwingDistancePips { get; set; }

        [Parameter("Min Swing Distance (ATR)", DefaultValue = 1.2, MinValue = 0.0, Step = 0.1, Group = "Structure")]
        public double MinSwingDistanceAtr { get; set; }

        [Parameter("Sweep Buffer (pips)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Structure")]
        public double SweepBufferPips { get; set; }

        [Parameter("Sweep Buffer (ATR fraction)", DefaultValue = 0.10, MinValue = 0, Step = 0.05, Group = "Structure")]
        public double SweepBufferAtrFrac { get; set; }

        // ── Candle / Wick Quality ─────────────────────────────────────
        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "Pattern")]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Avg Period", DefaultValue = 50, MinValue = 2, Group = "Pattern")]
        public int AtrAvgPeriod { get; set; }

        [Parameter("Range >= ATR *", DefaultValue = 1.15, Step = 0.05, Group = "Pattern")]
        public double RangeAtrMultiplier { get; set; }

        [Parameter("ATR >= AvgATR *", DefaultValue = 0.95, Step = 0.05, Group = "Pattern")]
        public double AtrComparisonMultiplier { get; set; }

        [Parameter("Max Body % of Range", DefaultValue = 0.20, Step = 0.01, Group = "Pattern")]
        public double MaxBodyPctOfRange { get; set; }

        [Parameter("Min Dominant Wick %", DefaultValue = 0.45, Step = 0.01, Group = "Pattern")]
        public double MinDominantWickPctOfRange { get; set; }

        [Parameter("Wick Dominance Ratio", DefaultValue = 1.8, Step = 0.1, Group = "Pattern")]
        public double WickDominanceRatio { get; set; }

        [Parameter("Close Location Threshold", DefaultValue = 0.70, Step = 0.05, Group = "Pattern")]
        public double CloseLocationThreshold { get; set; }

        [Parameter("Require Close Back Inside", DefaultValue = true, Group = "Pattern")]
        public bool RequireCloseBackInside { get; set; }

        // ── Regime Filter (ADX) ───────────────────────────────────────
        [Parameter("Use ADX Filter", DefaultValue = true, Group = "Regime")]
        public bool UseAdxFilter { get; set; }

        [Parameter("ADX Period", DefaultValue = 14, MinValue = 2, Group = "Regime")]
        public int AdxPeriod { get; set; }

        [Parameter("Max ADX to Trade", DefaultValue = 22.0, Step = 0.5, Group = "Regime")]
        public double MaxAdxToTrade { get; set; }

        // ── Higher Timeframe Trend Filter ─────────────────────────────
        // Only take wick longs when D1 is above EMA, shorts when below.
        // Empirically supported — wick rejections in trend direction
        // have significantly higher follow-through rate.
        [Parameter("Use D1 Trend Filter", DefaultValue = false, Group = "HTF Trend")]
        public bool UseD1TrendFilter { get; set; }

        [Parameter("D1 EMA Period", DefaultValue = 50, MinValue = 5, Group = "HTF Trend")]
        public int D1EmaPeriod { get; set; }

        // ── Session Filter ────────────────────────────────────────────
        // Tag signals by session and allow filtering by session type.
        // London open and London/NY overlap produce the most reliable
        // liquidity sweeps due to institutional order flow.
        [Parameter("Trade Asian Session (00-07 London)", DefaultValue = false, Group = "Session Filter")]
        public bool TradeAsian { get; set; }

        [Parameter("Trade London Session (07-12 London)", DefaultValue = true, Group = "Session Filter")]
        public bool TradeLondon { get; set; }

        [Parameter("Trade London/NY Overlap (12-17 London)", DefaultValue = true, Group = "Session Filter")]
        public bool TradeLondonNyOverlap { get; set; }

        [Parameter("Trade NY Session (17-21 London)", DefaultValue = false, Group = "Session Filter")]
        public bool TradeNy { get; set; }

        // ── Risk ──────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.10, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Stop Loss (ATR mult)", DefaultValue = 1.6, Step = 0.1, Group = "Risk")]
        public double StopLossAtrMult { get; set; }

        [Parameter("Take Profit (ATR mult)", DefaultValue = 2.2, Step = 0.1, Group = "Risk")]
        public double TakeProfitAtrMult { get; set; }

        [Parameter("Max Lots (Hard Cap)", DefaultValue = 0.20, Step = 0.01, Group = "Risk")]
        public double MaxLots { get; set; }

        [Parameter("Volume Cap (units)", DefaultValue = 200000, Step = 1000, Group = "Risk")]
        public int VolumeCap { get; set; }

        [Parameter("Max Margin Usage %", DefaultValue = 30.0, Step = 1.0, Group = "Risk")]
        public double MaxMarginUsagePct { get; set; }

        [Parameter("Skip If Volume Hits Cap", DefaultValue = true, Group = "Risk")]
        public bool SkipIfVolumeHitsCap { get; set; }

        // ── Max Trade Duration ────────────────────────────────────────
        // Force close if trade hasn't resolved within N hours.
        // Wick rejection trades that don't follow through quickly
        // have lower expected value — exit and reset.
        // 0 = disabled.
        [Parameter("Max Trade Duration (Hours, 0=off)", DefaultValue = 8, MinValue = 0, MaxValue = 48, Step = 1, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        // ── Partial Close ─────────────────────────────────────────────
        // Close half position at 1R, move SL to breakeven on remainder.
        // Empirically strong for wick strategies — initial move after
        // genuine sweep is fast and reliable, continuation less certain.
        [Parameter("Use Partial Close at 1R", DefaultValue = false, Group = "Partial Close")]
        public bool UsePartialClose { get; set; }

        [Parameter("Partial Close % of Position", DefaultValue = 50, MinValue = 10, MaxValue = 90, Step = 5, Group = "Partial Close")]
        public int PartialClosePct { get; set; }

        [Parameter("BE Offset After Partial (pips)", DefaultValue = 1, MinValue = 0, Step = 1, Group = "Partial Close")]
        public int BeOffsetPips { get; set; }

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

        // ── Trade Control ─────────────────────────────────────────────
        [Parameter("Trade Start (HH:mm London)", DefaultValue = "06:00", Group = "Trade Control")]
        public string TradeStart { get; set; }

        [Parameter("Trade End (HH:mm London)", DefaultValue = "21:00", Group = "Trade Control")]
        public string TradeEnd { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 2, MinValue = 1, Group = "Trade Control")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Cooldown Minutes After Exit", DefaultValue = 120, MinValue = 0, Group = "Trade Control")]
        public int CooldownMinutes { get; set; }

        [Parameter("Min Bars Since Last Signal", DefaultValue = 6, MinValue = 1, Group = "Trade Control")]
        public int MinBarsSinceSignal { get; set; }

        // ── Circuit Breaker ───────────────────────────────────────────
        [Parameter("Daily Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Circuit Breaker")]
        public double DailyLossLimitPct { get; set; }

        // ── Safety ────────────────────────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = true, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread (pips)", DefaultValue = 3.0, Step = 0.1, Group = "Safety")]
        public double MaxSpreadPips { get; set; }

        // ── Trailing ──────────────────────────────────────────────────
        [Parameter("Use Trailing Stop", DefaultValue = false, Group = "Trailing")]
        public bool UseTrailingStop { get; set; }

        [Parameter("Trail After +1R", DefaultValue = true, Group = "Trailing")]
        public bool TrailAfterOneR { get; set; }

        [Parameter("Trailing ATR Period", DefaultValue = 14, MinValue = 1, Group = "Trailing")]
        public int TrailingAtrPeriod { get; set; }

        [Parameter("Trail Distance (ATR mult)", DefaultValue = 1.8, Step = 0.1, Group = "Trailing")]
        public double TrailingStopAtrMult { get; set; }

        // ── Logging ───────────────────────────────────────────────────
        [Parameter("Log Signals Only", DefaultValue = true, Group = "Logging")]
        public bool LogSignalsOnly { get; set; }

        [Parameter("Log Sizing Details", DefaultValue = true, Group = "Logging")]
        public bool LogSizingDetails { get; set; }

        // ── Indicators ────────────────────────────────────────────────
        private AverageTrueRange          _atr;
        private AverageTrueRange          _atrAvg;
        private AverageTrueRange          _atrTrail;
        private DirectionalMovementSystem _dms;

        // D1 trend filter
        private Bars                      _d1Bars;
        private ExponentialMovingAverage  _d1Ema;

        // ── London timezone ───────────────────────────────────────────
        private static readonly TimeZoneInfo LondonTz =
            TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

        // ── State ─────────────────────────────────────────────────────
        private DateTime _lastExitTime      = DateTime.MinValue;
        private DateTime _lastLondonDay     = DateTime.MinValue;
        private int      _tradesToday       = 0;
        private int      _lastSignalBarIndex = -1;

        // Circuit breaker
        private double   _startOfDayEquity  = 0;
        private bool     _circuitBroken     = false;

        // Partial close tracking — one partial per position
        private long     _partialClosedPosId = -1;

        // ─────────────────────────────────────────────────────────────
        protected override void OnStart()
        {
            // Instrument warning — non-blocking since this runs on multiple pairs
            var sym = SymbolName.ToUpperInvariant();
            bool isFx = sym.Length == 6 ||
                        sym.Contains("USD") || sym.Contains("EUR") ||
                        sym.Contains("GBP") || sym.Contains("JPY") ||
                        sym.Contains("CHF") || sym.Contains("AUD") ||
                        sym.Contains("NZD") || sym.Contains("CAD");

            if (!isFx)
                Print($"WARNING: {SymbolName} may not be an FX pair. Verify parameters are appropriate.");

            _atr      = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _atrAvg   = Indicators.AverageTrueRange(AtrAvgPeriod, MovingAverageType.Exponential);
            _atrTrail = Indicators.AverageTrueRange(TrailingAtrPeriod, MovingAverageType.Exponential);
            _dms      = Indicators.DirectionalMovementSystem(AdxPeriod);

            // D1 trend filter setup
            if (UseD1TrendFilter)
            {
                _d1Bars = MarketData.GetBars(TimeFrame.Daily);
                _d1Ema  = Indicators.ExponentialMovingAverage(_d1Bars.ClosePrices, D1EmaPeriod);
            }

            _startOfDayEquity = Account.Equity;

            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} started on {SymbolName} ({Bars.TimeFrame}) ═══");
            Print($"SPECS | LotSize={Symbol.LotSize} | PipSize={Symbol.PipSize} | PipValue={Symbol.PipValue} | VolMin={Symbol.VolumeInUnitsMin} | VolStep={Symbol.VolumeInUnitsStep}");
            Print($"D1TrendFilter={UseD1TrendFilter} | ADXFilter={UseAdxFilter} | DayFilter={UseDayFilter}");
            Print($"Sessions: Asian={TradeAsian} London={TradeLondon} Overlap={TradeLondonNyOverlap} NY={TradeNy}");
            Print($"MaxTradeHours={(MaxTradeHours == 0 ? "off" : MaxTradeHours.ToString())} | PartialClose={UsePartialClose}");
            Print($"CircuitBreaker={(DailyLossLimitPct > 0 ? DailyLossLimitPct + "%" : "off")}");
        }

        // ─────────────────────────────────────────────────────────────
        protected override void OnBar()
        {
            var london = LondonNow();

            ResetDailyStateIfNeeded(london);

            // Manage open positions first — duration exit, partial close, trailing
            ManageOpenPositions();

            if (CheckCircuitBreaker()) return;
            if (!PassesDayFilter(london)) return;
            if (!InTradeHours(london)) return;
            if (!PassesSessionFilter(london)) return;
            if (!SafetyOk()) return;
            if (!IndicatorsReady()) return;

            if (_lastExitTime != DateTime.MinValue &&
                Server.Time < _lastExitTime.AddMinutes(CooldownMinutes))
                return;

            if (_tradesToday >= MaxTradesPerDay) return;
            if (HasOpenPosition()) return;

            int prev = Bars.Count - 2;
            if (prev < 50) return;

            if (_lastSignalBarIndex >= 0 &&
                (Bars.Count - 1 - _lastSignalBarIndex) < MinBarsSinceSignal)
                return;

            if (TryGetSignal(prev, out var dir, out var info))
            {
                _lastSignalBarIndex = Bars.Count - 1;
                Print($"[{Server.Time:HH:mm}] SIGNAL {dir} | {info}");
                ExecuteEntry(dir, prev);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  POSITION MANAGEMENT
        //  Runs every bar before entry logic.
        // ─────────────────────────────────────────────────────────────
        private void ManageOpenPositions()
        {
            foreach (var pos in Positions.Where(p =>
                p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
            {
                // ── Max duration exit ────────────────────────────────
                if (MaxTradeHours > 0)
                {
                    var hoursOpen = (Server.Time - pos.EntryTime).TotalHours;
                    if (hoursOpen >= MaxTradeHours)
                    {
                        Print($"MAX DURATION EXIT | {hoursOpen:F1}h | Net={pos.NetProfit:F2}");
                        ClosePosition(pos);
                        continue;
                    }
                }

                // ── Partial close at 1R ──────────────────────────────
                if (UsePartialClose && _partialClosedPosId != pos.Id && pos.StopLoss.HasValue)
                {
                    double oneR = Math.Abs(pos.EntryPrice - pos.StopLoss.Value);
                    bool hitOneR = pos.TradeType == TradeType.Buy
                        ? Symbol.Bid >= pos.EntryPrice + oneR
                        : Symbol.Ask <= pos.EntryPrice - oneR;

                    if (hitOneR)
                    {
                        double closeVolume = Symbol.NormalizeVolumeInUnits(
                            pos.VolumeInUnits * (PartialClosePct / 100.0),
                            RoundingMode.Down);

                        if (closeVolume >= Symbol.VolumeInUnitsMin)
                        {
                            ClosePosition(pos, closeVolume);
                            _partialClosedPosId = pos.Id;

                            // Move SL to breakeven + offset on remainder
                            double beOffset = BeOffsetPips * Symbol.PipSize;
                            double bePrice = pos.TradeType == TradeType.Buy
                                ? pos.EntryPrice + beOffset
                                : pos.EntryPrice - beOffset;

                            if (!pos.StopLoss.HasValue ||
                                (pos.TradeType == TradeType.Buy && bePrice > pos.StopLoss.Value) ||
                                (pos.TradeType == TradeType.Sell && bePrice < pos.StopLoss.Value))
                            {
                                pos.ModifyStopLossPrice(bePrice);
                                Print($"PARTIAL CLOSE {PartialClosePct}% | BE set @ {bePrice:F5}");
                            }
                        }
                    }
                }

                // ── Trailing stop ────────────────────────────────────
                if (UseTrailingStop)
                    TrailPosition(pos);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  TRAILING STOP
        //  Uses last closed bar price — not live Bid/Ask.
        // ─────────────────────────────────────────────────────────────
        private void TrailPosition(Position pos)
        {
            if (Bars.Count < TrailingAtrPeriod + 5) return;

            int lastBar  = Bars.Count - 2;
            double atrTrail = _atrTrail.Result[lastBar];
            if (atrTrail <= 0) return;

            double trailDist  = atrTrail * TrailingStopAtrMult;
            double lastClose  = Bars.ClosePrices[lastBar];

            if (TrailAfterOneR && pos.StopLoss.HasValue)
            {
                double oneR = Math.Abs(pos.EntryPrice - pos.StopLoss.Value);
                if (oneR > 0)
                {
                    if (pos.TradeType == TradeType.Buy &&
                        (lastClose - pos.EntryPrice) < oneR) return;
                    if (pos.TradeType == TradeType.Sell &&
                        (pos.EntryPrice - lastClose) < oneR) return;
                }
            }

            double newSL;
            bool   improve;

            if (pos.TradeType == TradeType.Buy)
            {
                newSL   = lastClose - trailDist;
                improve = !pos.StopLoss.HasValue || newSL > pos.StopLoss.Value;
            }
            else
            {
                newSL   = lastClose + trailDist;
                improve = !pos.StopLoss.HasValue || newSL < pos.StopLoss.Value;
            }

            if (improve)
            {
                pos.ModifyStopLossPrice(newSL);
                if (!LogSignalsOnly)
                    Print($"TRAIL {pos.TradeType} → SL={newSL:F5} | LastClose={lastClose:F5}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  SIGNAL DETECTION
        // ─────────────────────────────────────────────────────────────
        private bool TryGetSignal(int barIndex, out TradeType direction, out string info)
        {
            direction = TradeType.Buy;
            info      = "";

            // ADX regime filter
            if (UseAdxFilter)
            {
                double adx = _dms.ADX[barIndex];
                if (adx > MaxAdxToTrade) return false;
            }

            double high  = Bars.HighPrices[barIndex];
            double low   = Bars.LowPrices[barIndex];
            double open  = Bars.OpenPrices[barIndex];
            double close = Bars.ClosePrices[barIndex];

            double range = high - low;
            if (range <= 0) return false;

            double body    = Math.Abs(close - open);
            double bodyPct = body / range;

            double upperWick = high - Math.Max(open, close);
            double lowerWick = Math.Min(open, close) - low;
            double closePos  = (close - low) / range;

            bool buyCloseStrong  = closePos >= CloseLocationThreshold;
            bool sellCloseStrong = closePos <= (1.0 - CloseLocationThreshold);

            double atr    = _atr.Result[barIndex];
            double avgAtr = _atrAvg.Result[barIndex];
            if (atr <= 0 || avgAtr <= 0) return false;

            if (!(range >= atr * RangeAtrMultiplier))            return false;
            if (!(atr   >= avgAtr * AtrComparisonMultiplier))    return false;
            if (!(bodyPct <= MaxBodyPctOfRange))                  return false;

            bool sellWickDominant =
                (upperWick / range) >= MinDominantWickPctOfRange &&
                upperWick >= lowerWick * WickDominanceRatio;

            bool buyWickDominant =
                (lowerWick / range) >= MinDominantWickPctOfRange &&
                lowerWick >= upperWick * WickDominanceRatio;

            // Swing levels
            double swingHigh, swingLow;
            int    swingHighIndex, swingLowIndex;

            if (UseProStructure)
            {
                if (!TryFindLastSwingHigh(barIndex, out swingHigh, out swingHighIndex)) return false;
                if (!TryFindLastSwingLow(barIndex,  out swingLow,  out swingLowIndex))  return false;

                double requiredDistancePrice = Math.Max(
                    MinSwingDistancePips * Symbol.PipSize,
                    MinSwingDistanceAtr  * atr);

                if ((swingHigh - swingLow) < requiredDistancePrice) return false;
            }
            else
            {
                int earliest    = Math.Max(0, barIndex - MaxSwingAgeBars);
                swingHighIndex  = barIndex - 1;
                swingLowIndex   = barIndex - 1;
                swingHigh       = double.MinValue;
                swingLow        = double.MaxValue;
                for (int i = earliest; i <= barIndex - 1; i++)
                {
                    swingHigh = Math.Max(swingHigh, Bars.HighPrices[i]);
                    swingLow  = Math.Min(swingLow,  Bars.LowPrices[i]);
                }
            }

            double bufferPrice   = (SweepBufferPips * Symbol.PipSize) + (SweepBufferAtrFrac * atr);
            bool   sweptHigh     = high > swingHigh + bufferPrice;
            bool   sweptLow      = low  < swingLow  - bufferPrice;

            bool sellRejectionOk = (!RequireCloseBackInside || close < swingHigh) && sellCloseStrong;
            bool buyRejectionOk  = (!RequireCloseBackInside || close > swingLow)  && buyCloseStrong;

            bool sellSignal = sweptHigh && sellWickDominant && sellRejectionOk;
            bool buySignal  = sweptLow  && buyWickDominant  && buyRejectionOk;

            if (!(buySignal || sellSignal)) return false;

            // D1 trend filter — only trade in direction of D1 EMA
            if (UseD1TrendFilter && _d1Bars != null && _d1Ema != null)
            {
                int    d1Last   = _d1Bars.Count - 2;
                double d1Close  = _d1Bars.ClosePrices[d1Last];
                double d1EmaVal = _d1Ema.Result[d1Last];

                bool d1Bullish = d1Close > d1EmaVal;
                bool d1Bearish = d1Close < d1EmaVal;

                if (buySignal  && !d1Bullish) return false;
                if (sellSignal && !d1Bearish) return false;
            }

            direction = buySignal ? TradeType.Buy : TradeType.Sell;

            double reqPips = Math.Max(MinSwingDistancePips,
                (MinSwingDistanceAtr * atr) / Symbol.PipSize);

            info = $"bar={barIndex} " +
                   $"range={(range / Symbol.PipSize):F1}p " +
                   $"body%={(bodyPct * 100.0):F1}% " +
                   $"uw={(upperWick / Symbol.PipSize):F1}p " +
                   $"lw={(lowerWick / Symbol.PipSize):F1}p " +
                   $"ATR={(atr / Symbol.PipSize):F1}p " +
                   $"AvgATR={(avgAtr / Symbol.PipSize):F1}p " +
                   (UseAdxFilter ? $"ADX={_dms.ADX[barIndex]:F1} " : "") +
                   (UseD1TrendFilter ? $"D1={'▲' } " : "") +
                   $"swingHi={swingHigh:F5}@{swingHighIndex} " +
                   $"swingLo={swingLow:F5}@{swingLowIndex} " +
                   $"sweepHi={sweptHigh} sweepLo={sweptLow} " +
                   $"reqSwing~{reqPips:F1}p " +
                   $"closePos={(closePos * 100.0):F0}%";

            return true;
        }

        // ─────────────────────────────────────────────────────────────
        //  SWING DETECTION
        // ─────────────────────────────────────────────────────────────
        private bool TryFindLastSwingHigh(int barIndex, out double swingHigh, out int swingIndex)
        {
            swingHigh = 0;
            swingIndex = -1;

            int latestCandidate = barIndex - SwingRight;
            int earliest        = Math.Max(0, barIndex - MaxSwingAgeBars);

            for (int i = latestCandidate; i >= earliest; i--)
            {
                if (IsSwingHigh(i))
                {
                    swingHigh  = Bars.HighPrices[i];
                    swingIndex = i;
                    return true;
                }
            }
            return false;
        }

        private bool TryFindLastSwingLow(int barIndex, out double swingLow, out int swingIndex)
        {
            swingLow   = 0;
            swingIndex = -1;

            int latestCandidate = barIndex - SwingRight;
            int earliest        = Math.Max(0, barIndex - MaxSwingAgeBars);

            for (int i = latestCandidate; i >= earliest; i--)
            {
                if (IsSwingLow(i))
                {
                    swingLow   = Bars.LowPrices[i];
                    swingIndex = i;
                    return true;
                }
            }
            return false;
        }

        private bool IsSwingHigh(int i)
        {
            if (i - SwingLeft < 0 || i + SwingRight >= Bars.Count) return false;
            double h = Bars.HighPrices[i];
            for (int k = i - SwingLeft; k <= i + SwingRight; k++)
                if (Bars.HighPrices[k] > h) return false;
            return true;
        }

        private bool IsSwingLow(int i)
        {
            if (i - SwingLeft < 0 || i + SwingRight >= Bars.Count) return false;
            double l = Bars.LowPrices[i];
            for (int k = i - SwingLeft; k <= i + SwingRight; k++)
                if (Bars.LowPrices[k] < l) return false;
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        //  TRADE EXECUTION
        // ─────────────────────────────────────────────────────────────
        private void ExecuteEntry(TradeType tradeType, int signalBarIndex)
        {
            double atr = _atr.Result[signalBarIndex];
            if (atr <= 0) return;

            double slPips = (atr * StopLossAtrMult)    / Symbol.PipSize;
            double tpPips = (atr * TakeProfitAtrMult)  / Symbol.PipSize;
            if (slPips <= 0 || tpPips <= 0) return;

            double volume = CalculateVolumeSafe(tradeType, slPips);
            if (volume <= 0)
            {
                if (!LogSignalsOnly)
                    Print($"[{Server.Time:HH:mm}] SKIP — size/margin check failed. SL={slPips:F1}p");
                return;
            }

            var res = ExecuteMarketOrder(tradeType, SymbolName, volume, BotLabel, slPips, tpPips);

            if (res.IsSuccessful && res.Position != null)
            {
                _tradesToday++;
                Print($"[{Server.Time:HH:mm}] {tradeType.ToString().ToUpper()} OPEN | " +
                      $"vol={volume:F0} SL={slPips:F1}p TP={tpPips:F1}p | " +
                      $"Entry={res.Position.EntryPrice:F5}");
            }
            else
            {
                Print($"[{Server.Time:HH:mm}] Entry failed: {res.Error}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  VOLUME CALCULATION
        // ─────────────────────────────────────────────────────────────
        private double CalculateVolumeSafe(TradeType tradeType, double stopLossPips)
        {
            if (RiskPercent <= 0 || stopLossPips <= 0) return 0;

            double equity = Account.Equity;
            if (equity <= 0) return 0;

            double riskMoney  = equity * (RiskPercent / 100.0);
            double riskPerLot = stopLossPips * Symbol.PipValue;
            if (riskPerLot <= 0) return 0;

            double lotsRaw  = riskMoney / riskPerLot;
            double lots     = Math.Min(lotsRaw, MaxLots);
            double unitsRaw = lots * Symbol.LotSize;

            double unitsBeforeCap = unitsRaw;
            double unitsCapped    = Math.Min(unitsRaw, VolumeCap);
            double unitsFinal     = Symbol.NormalizeVolumeInUnits(unitsCapped, RoundingMode.Down);

            if (unitsFinal < Symbol.VolumeInUnitsMin) return 0;

            if (SkipIfVolumeHitsCap && VolumeCap > 0 &&
                unitsBeforeCap > VolumeCap * 1.001 &&
                unitsFinal >= VolumeCap)
                return 0;

            double estMargin = 0;
            try { estMargin = Symbol.GetEstimatedMargin(tradeType, unitsFinal); } catch { }

            if (estMargin > 0)
            {
                double maxAllowed = equity * (MaxMarginUsagePct / 100.0);
                if (estMargin > maxAllowed) return 0;
            }

            if (LogSizingDetails)
            {
                double lotsUsed = unitsFinal / Symbol.LotSize;
                Print($"[SIZE] eq={equity:F2} risk$={riskMoney:F2} SL={stopLossPips:F1}p " +
                      $"pipVal={Symbol.PipValue:F4} lotsRaw={lotsRaw:F3} " +
                      $"lotsUsed={lotsUsed:F3} unitsFinal={unitsFinal:F0} " +
                      $"margin={(estMargin > 0 ? estMargin.ToString("F2") : "n/a")}");
            }

            return unitsFinal;
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

                    foreach (var pos in Positions.Where(p =>
                        p.SymbolName == SymbolName && p.Label == BotLabel).ToList())
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
        //  SESSION CLASSIFIER
        //  London time boundaries:
        //  Asian:          00:00 - 07:00
        //  London:         07:00 - 12:00
        //  London/NY:      12:00 - 17:00
        //  NY:             17:00 - 21:00
        // ─────────────────────────────────────────────────────────────
        private bool PassesSessionFilter(DateTime london)
        {
            var t = london.TimeOfDay;

            var asianStart   = TimeSpan.FromHours(0);
            var londonStart  = TimeSpan.FromHours(7);
            var overlapStart = TimeSpan.FromHours(12);
            var nyStart      = TimeSpan.FromHours(17);
            var nyEnd        = TimeSpan.FromHours(21);

            if (t >= asianStart   && t < londonStart)  return TradeAsian;
            if (t >= londonStart  && t < overlapStart) return TradeLondon;
            if (t >= overlapStart && t < nyStart)      return TradeLondonNyOverlap;
            if (t >= nyStart      && t < nyEnd)        return TradeNy;

            return false; // Outside all sessions
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

            Print($"NEW LONDON DAY {london:dd-MMM-yyyy ddd}");
        }

        private DateTime LondonNow() =>
            TimeZoneInfo.ConvertTimeFromUtc(Server.Time, LondonTz);

        private bool InTradeHours(DateTime london)
        {
            if (string.IsNullOrWhiteSpace(TradeStart) ||
                string.IsNullOrWhiteSpace(TradeEnd)) return true;

            if (!TimeSpan.TryParse(TradeStart, out var start) ||
                !TimeSpan.TryParse(TradeEnd,   out var end))   return true;

            var t = london.TimeOfDay;
            return t >= start && t <= end;
        }

        private bool IndicatorsReady()
        {
            int need = Math.Max(Math.Max(AtrAvgPeriod, AtrPeriod),
                       Math.Max(TrailingAtrPeriod, AdxPeriod));
            int swingNeed = SwingLeft + SwingRight + 10;
            return Bars.Count > need + swingNeed + 10;
        }

        private bool SafetyOk()
        {
            if (!UseSpreadFilter) return true;
            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            return spreadPips <= MaxSpreadPips;
        }

        private bool HasOpenPosition() =>
            Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName) return;
            if (args.Position.Label      != BotLabel)   return;
            _lastExitTime = Server.Time;
        }
    }
}
