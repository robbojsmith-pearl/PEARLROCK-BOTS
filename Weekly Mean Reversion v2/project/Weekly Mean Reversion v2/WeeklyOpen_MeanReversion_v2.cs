using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using PearlrockBots.WeeklyMeanReversion.Adapters;
using PearlrockBots.WeeklyMeanReversion.Core;

namespace cAlgo.Robots
{
    // ═══════════════════════════════════════════════════════════════════
    // Weekly Open Mean Reversion v2 — orchestrator
    // Pearlrock Systematic
    //
    // Thin shell over Core. Strategy logic, time math, Monday-open detection
    // and risk sizing live in PearlrockBots.WeeklyMeanReversion.Core, which
    // is unit-tested separately.
    //
    // Concept (unchanged from v1):
    //   Reference = Monday open (first M5 bar after 00:00 London).
    //   Signal fires when price deviates >= DeviationAtrMult × D1 ATR
    //   from Monday open AND ConfirmationBars consecutive M5 bars close
    //   back toward Monday open.
    //   TP = Monday open price ± TpBufferPips. SL = M5 ATR-based.
    //
    // What's different from v1:
    //   - Monday open captured by scanning M5 history for the true first
    //     Monday bar, not just whichever bar OnBar happened to fire on.
    //   - DST/BST handled by .NET TimeZoneInfo, not hand-rolled logic.
    //   - Signal state is an explicit machine; reset uses a buffer to
    //     suppress wick noise.
    //   - All strategy logic is in unit-tested classes outside the cBot.
    // ═══════════════════════════════════════════════════════════════════
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class WeeklyOpen_MeanReversion_v2 : Robot
    {
        // ── Identity ─────────────────────────────────────────────────
        [Parameter("Bot Label", DefaultValue = "WEEKLY_MR_V2", Group = "Identity")]
        public string BotLabel { get; set; }

        // ── Signal ────────────────────────────────────────────────────
        [Parameter("Deviation Threshold (× D1 ATR)", DefaultValue = 1.0, MinValue = 0.25, MaxValue = 4.0, Step = 0.25, Group = "Signal")]
        public double DeviationAtrMult { get; set; }

        [Parameter("D1 ATR Period", DefaultValue = 14, MinValue = 2, Group = "Signal")]
        public int D1AtrPeriod { get; set; }

        [Parameter("Confirmation Bars (consecutive toward mean)", DefaultValue = 2, MinValue = 1, MaxValue = 5, Group = "Signal")]
        public int ConfirmationBars { get; set; }

        [Parameter("Reset Buffer (pips past Monday open)", DefaultValue = 2, MinValue = 0, Step = 1, Group = "Signal")]
        public double ResetBufferPips { get; set; }

        // ── Risk ──────────────────────────────────────────────────────
        [Parameter("Risk % per Trade", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("M5 ATR Period (for SL)", DefaultValue = 14, MinValue = 1, Group = "Risk")]
        public int M5AtrPeriod { get; set; }

        [Parameter("SL ATR Multiple (M5)", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1, Group = "Risk")]
        public double SlAtrMultiple { get; set; }

        [Parameter("TP Buffer (pips from Monday open)", DefaultValue = 5, MinValue = 0, Step = 1, Group = "Risk")]
        public double TpBufferPips { get; set; }

        [Parameter("Max Trade Duration (hours, 0=off)", DefaultValue = 24, MinValue = 0, MaxValue = 120, Group = "Risk")]
        public int MaxTradeHours { get; set; }

        [Parameter("Max SL Pips (0=off)", DefaultValue = 0, MinValue = 0, Step = 5, Group = "Risk")]
        public double MaxSlPips { get; set; }

        [Parameter("Min TP/SL Ratio", DefaultValue = 0.5, MinValue = 0, Step = 0.1, Group = "Risk")]
        public double MinTpToSlRatio { get; set; }

        [Parameter("TP Fraction (0.05..1, of entry->Monday open distance)", DefaultValue = 1.0, MinValue = 0.05, MaxValue = 1.0, Step = 0.05, Group = "Risk")]
        public double TpFraction { get; set; }

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

        [Parameter("Trade Monday",    DefaultValue = false, Group = "Day Filter")] public bool TradeMonday    { get; set; }
        [Parameter("Trade Tuesday",   DefaultValue = true,  Group = "Day Filter")] public bool TradeTuesday   { get; set; }
        [Parameter("Trade Wednesday", DefaultValue = true,  Group = "Day Filter")] public bool TradeWednesday { get; set; }
        [Parameter("Trade Thursday",  DefaultValue = false, Group = "Day Filter")] public bool TradeThursday  { get; set; }
        [Parameter("Trade Friday",    DefaultValue = false, Group = "Day Filter")] public bool TradeFriday    { get; set; }

        // ── Safety / Circuit Breaker ─────────────────────────────────
        [Parameter("Use Spread Filter", DefaultValue = false, Group = "Safety")]
        public bool UseSpreadFilter { get; set; }

        [Parameter("Max Spread", DefaultValue = 0.001, Step = 0.0001, Group = "Safety")]
        public double MaxSpread { get; set; }

        [Parameter("Weekly Loss Limit % (0=off)", DefaultValue = 2.0, MinValue = 0, Step = 0.1, Group = "Safety")]
        public double WeeklyLossLimitPct { get; set; }

        // ── Logging ───────────────────────────────────────────────────
        [Parameter("Verbose Logging", DefaultValue = true, Group = "Logging")]
        public bool VerboseLogging { get; set; }

        // ── cAlgo indicators / data ───────────────────────────────────
        private AverageTrueRange _m5Atr;
        private Bars _d1Bars;
        private AverageTrueRange _d1Atr;

        // ── Core ──────────────────────────────────────────────────────
        private SessionClock _clock;
        private WeeklyState _state;
        private SignalEngine _signal;
        private RiskManager _risk;
        private CAlgoBarSeries _m5BarsAdapter;
        private CAlgoSymbol _symbolAdapter;

        // ── Exit tracking ─────────────────────────────────────────────
        private DateTime _lastExitTime = DateTime.MinValue;

        protected override void OnStart()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5)
            {
                Print($"ABORT: Must run on M5. Current = {Bars.TimeFrame}");
                Stop();
                return;
            }

            _m5Atr = Indicators.AverageTrueRange(M5AtrPeriod, MovingAverageType.Exponential);
            _d1Bars = MarketData.GetBars(TimeFrame.Daily);
            _d1Atr = Indicators.AverageTrueRange(_d1Bars, D1AtrPeriod, MovingAverageType.Exponential);

            _clock = new SessionClock(new CAlgoClock(() => Server.Time));
            _state = new WeeklyState();
            _signal = new SignalEngine(new SignalEngineConfig
            {
                DeviationAtrMult = DeviationAtrMult,
                ConfirmationBars = ConfirmationBars,
                AllowLongs = AllowLongs,
                AllowShorts = AllowShorts,
                ResetBufferPips = ResetBufferPips
            });
            _risk = new RiskManager(new RiskManagerConfig
            {
                RiskPercent = RiskPercent,
                SlAtrMultiple = SlAtrMultiple,
                MaxSlPips = MaxSlPips,
                TpBufferPips = TpBufferPips,
                MinTpToSlRatio = MinTpToSlRatio,
                TpFraction = TpFraction
            });
            _m5BarsAdapter = new CAlgoBarSeries(Bars);
            _symbolAdapter = new CAlgoSymbol(Symbol);

            Positions.Closed += OnPositionClosed;

            Print($"═══ {BotLabel} (v2) started on {SymbolName} M5 ═══");
            Print($"Deviation={DeviationAtrMult}×D1ATR | Confirm={ConfirmationBars} | ResetBuffer={ResetBufferPips}p");
            Print($"SL={SlAtrMultiple}×M5ATR (cap {MaxSlPips}p) | TPBuffer={TpBufferPips}p | MinTP/SL={MinTpToSlRatio}");
            Print($"Session={TradeStart}-{TradeEnd} London | DayFilter={UseDayFilter}");
            Print($"MaxTrades/Week={MaxTradesPerWeek} | Cooldown={CooldownMinutes}m");
            Print($"Circuit={(WeeklyLossLimitPct > 0 ? WeeklyLossLimitPct + "%" : "off")}");
        }

        protected override void OnBar()
        {
            if (Bars.TimeFrame != TimeFrame.Minute5) return;

            var london = _clock.LondonNow;
            var weekStart = SessionClock.WeekStartDate(london);

            // ── Weekly state ─────────────────────────────────────────
            if (_state.ResetIfNewWeek(weekStart, Account.Equity))
            {
                Log($"NEW WEEK {weekStart:dd-MMM-yyyy}");
                _signal.Reset();
            }

            // Try to capture Monday open. May fail until Monday data is in history.
            if (!_state.MondayOpenSet)
            {
                if (_state.TryCaptureMondayOpen(_m5BarsAdapter, _clock, weekStart))
                    Print($"MONDAY OPEN SET | {_state.MondayOpen:F5}");
            }

            // ── Position management ──────────────────────────────────
            ManageOpenPositions();

            // ── Gating ────────────────────────────────────────────────
            if (CheckCircuitBreaker()) return;
            if (!_state.MondayOpenSet) return;
            if (!_clock.IsTradingDayEnabled(london.DayOfWeek, BuildDayFilter())) return;
            if (!IsInTradeHours()) return;
            if (!SpreadOk()) return;
            if (!IndicatorsReady()) return;
            if (_lastExitTime != DateTime.MinValue
                && Server.Time < _lastExitTime.AddMinutes(CooldownMinutes)) return;
            if (_state.TradesThisWeek >= MaxTradesPerWeek) return;
            if (HasOpenPosition()) return;

            // ── Build signal input ───────────────────────────────────
            // Use the most recent CLOSED M5 bar — barsAgo=1 in our adapter — and
            // the last CLOSED D1 ATR value (Count - 2 = last closed daily bar).
            int d1LastClosed = _d1Bars.Count - 2;
            if (d1LastClosed < D1AtrPeriod) return;

            var input = new SignalEngineInput(
                mondayOpen: _state.MondayOpen,
                lastClose: Bars.ClosePrices.Last(1),
                prevClose: Bars.ClosePrices.Last(2),
                d1Atr: _d1Atr.Result[d1LastClosed],
                pipSize: Symbol.PipSize);

            var decision = _signal.Evaluate(input);
            if (VerboseLogging && decision.Reason != "no-op")
                Log($"SIGNAL [{_signal.Phase}] {decision.Reason}");

            if (!decision.ShouldEnter) return;

            // ── Build and place order ────────────────────────────────
            var ctx = new EntryContext(
                direction: decision.Direction,
                mondayOpen: _state.MondayOpen,
                m5Atr: _m5Atr.Result.LastValue,
                equity: Account.Equity,
                bid: Symbol.Bid,
                ask: Symbol.Ask,
                pipSize: Symbol.PipSize,
                volumeInUnitsMin: Symbol.VolumeInUnitsMin,
                volumeInUnitsMax: Symbol.VolumeInUnitsMax,
                volumeForRisk: _symbolAdapter.VolumeForRisk,
                normalizeVolume: _symbolAdapter.NormalizeVolume);

            var (entry, reject) = _risk.BuildEntry(ctx);
            if (entry == null)
            {
                Log($"ENTRY BLOCKED — {reject}");
                return;
            }

            PlaceOrder(entry.Value);
        }

        // ─────────────────────────────────────────────────────────────
        private void PlaceOrder(EntrySpec entry)
        {
            var dir = entry.Direction == TradeDirection.Long ? TradeType.Buy : TradeType.Sell;
            var result = ExecuteMarketOrder(
                dir,
                SymbolName,
                entry.Volume,
                BotLabel,
                entry.SlPips,
                entry.TpPips);

            if (result.IsSuccessful)
            {
                _state.RecordEntry();
                Print($"[{Server.Time:HH:mm}] {dir.ToString().ToUpper()} OPEN | " +
                      $"Entry={result.Position.EntryPrice:F5} | {entry.Note} | " +
                      $"MondayOpen={_state.MondayOpen:F5}");
            }
            else
            {
                Print($"ORDER FAILED: {result.Error}");
            }
        }

        private void ManageOpenPositions()
        {
            if (MaxTradeHours <= 0) return;

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

        private bool CheckCircuitBreaker()
        {
            if (!_state.ShouldTripCircuitBreaker(Account.Equity, WeeklyLossLimitPct))
                return _state.CircuitBroken;

            if (!_state.CircuitBroken)
            {
                double lossPct = (_state.StartOfWeekEquity - Account.Equity)
                                 / _state.StartOfWeekEquity * 100.0;
                Print($"⚡ CIRCUIT BREAKER | Loss={lossPct:F2}% >= {WeeklyLossLimitPct}%");
                _state.TripCircuitBreaker();
                foreach (var pos in Positions
                    .Where(p => p.SymbolName == SymbolName && p.Label == BotLabel)
                    .ToList())
                    ClosePosition(pos);
            }
            return true;
        }

        private DayFilter BuildDayFilter() =>
            new DayFilter(UseDayFilter, TradeMonday, TradeTuesday, TradeWednesday,
                          TradeThursday, TradeFriday);

        private bool IsInTradeHours()
        {
            if (string.IsNullOrWhiteSpace(TradeStart) || string.IsNullOrWhiteSpace(TradeEnd))
                return true;
            if (!TimeSpan.TryParse(TradeStart, out var start) ||
                !TimeSpan.TryParse(TradeEnd, out var end))
                return true;
            return _clock.InTradeHours(start, end);
        }

        private bool SpreadOk()
        {
            if (!UseSpreadFilter) return true;
            return _symbolAdapter.Spread <= MaxSpread;
        }

        private bool IndicatorsReady() =>
            _d1Bars.Count > D1AtrPeriod + 5 && Bars.Count > M5AtrPeriod + 5;

        private bool HasOpenPosition() =>
            Positions.Any(p => p.SymbolName == SymbolName && p.Label == BotLabel);

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.SymbolName != SymbolName || args.Position.Label != BotLabel) return;
            _lastExitTime = Server.Time;
        }

        private void Log(string msg)
        {
            if (VerboseLogging) Print(msg);
        }

        // Note: GetFitness omitted for brevity — port from v1 if you use the optimizer.
        // Keep it on the cBot side so Robot.GetFitnessArgs is reachable.
    }
}
