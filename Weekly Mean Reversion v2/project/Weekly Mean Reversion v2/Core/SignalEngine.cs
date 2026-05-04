using System;

namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>
    /// Signal state machine. Pure: no I/O, no globals, no time queries.
    ///
    /// Phases:
    ///   Idle             — waiting for a deviation from Monday open by
    ///                      DeviationAtrMult × D1 ATR.
    ///   DeviationDetected— threshold met; remembers direction, waits for
    ///                      consecutive bars closing toward the mean.
    ///   Confirming       — at least one bar has closed toward the mean;
    ///                      counting up to ConfirmationBars.
    ///
    /// Differences from v1:
    ///   - Phase is an explicit enum (not three boolean/int fields).
    ///   - Reset on a closed bar past Monday open uses a buffer
    ///     (ResetBufferPips) rather than a single-tick crossover, so noise
    ///     wicks don't kill a valid setup.
    ///   - Direction-disabled cases reset cleanly instead of silently lingering.
    /// </summary>
    public class SignalEngine
    {
        private readonly SignalEngineConfig _cfg;

        public SignalPhase Phase { get; private set; } = SignalPhase.Idle;
        public TradeDirection PendingDirection { get; private set; } = TradeDirection.None;
        public int ConfirmationCount { get; private set; }

        public SignalEngine(SignalEngineConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (cfg.ConfirmationBars < 1)
                throw new ArgumentOutOfRangeException(nameof(cfg.ConfirmationBars), "must be >= 1");
            if (cfg.DeviationAtrMult <= 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.DeviationAtrMult), "must be > 0");
            _cfg = cfg;
        }

        public void Reset()
        {
            Phase = SignalPhase.Idle;
            PendingDirection = TradeDirection.None;
            ConfirmationCount = 0;
        }

        public SignalDecision Evaluate(SignalEngineInput input)
        {
            if (input.D1Atr <= 0) return SignalDecision.Wait("d1_atr_not_ready");
            if (input.PipSize <= 0) return SignalDecision.Wait("pip_size_invalid");

            double deviation = input.LastClose - input.MondayOpen;
            double threshold = _cfg.DeviationAtrMult * input.D1Atr;

            // ── Idle: look for fresh deviation ─────────────────────────
            if (Phase == SignalPhase.Idle)
            {
                if (Math.Abs(deviation) < threshold) return SignalDecision.NoOp();

                PendingDirection = deviation > 0 ? TradeDirection.Short : TradeDirection.Long;
                Phase = SignalPhase.DeviationDetected;
                ConfirmationCount = 0;

                if ((PendingDirection == TradeDirection.Long && !_cfg.AllowLongs) ||
                    (PendingDirection == TradeDirection.Short && !_cfg.AllowShorts))
                {
                    var disabled = PendingDirection;
                    Reset();
                    return SignalDecision.Wait($"{disabled}_disabled");
                }

                return SignalDecision.Wait(
                    $"deviation_reached dev={deviation:F5} thr={threshold:F5} dir={PendingDirection}");
            }

            // ── Phase is DeviationDetected or Confirming ───────────────

            // Reset trigger: closed bar back through Monday open by buffer.
            // (v1 reset on any cross of MO; v2 requires the close to be past MO
            //  by ResetBufferPips, which suppresses single-bar wick noise.)
            double resetBuffer = _cfg.ResetBufferPips * input.PipSize;
            bool brokeBackThrough =
                (PendingDirection == TradeDirection.Long && input.LastClose > input.MondayOpen + resetBuffer) ||
                (PendingDirection == TradeDirection.Short && input.LastClose < input.MondayOpen - resetBuffer);
            if (brokeBackThrough)
            {
                Reset();
                return SignalDecision.Wait("reset_close_back_through_monday_open");
            }

            // Direction filter (re-check in case settings changed mid-week)
            if ((PendingDirection == TradeDirection.Long && !_cfg.AllowLongs) ||
                (PendingDirection == TradeDirection.Short && !_cfg.AllowShorts))
            {
                var disabled = PendingDirection;
                Reset();
                return SignalDecision.Wait($"{disabled}_disabled_midstream");
            }

            // Count consecutive bars closing toward Monday open.
            bool closingTowardMean = PendingDirection == TradeDirection.Long
                ? input.LastClose > input.PrevClose
                : input.LastClose < input.PrevClose;

            if (closingTowardMean)
            {
                ConfirmationCount++;
                Phase = SignalPhase.Confirming;
            }
            else
            {
                ConfirmationCount = 0;
                Phase = SignalPhase.DeviationDetected;
                return SignalDecision.Wait("confirm_broken_count_reset");
            }

            // Fire entry?
            if (ConfirmationCount >= _cfg.ConfirmationBars)
            {
                var dir = PendingDirection;
                Reset();
                return SignalDecision.Enter(dir, $"confirmed_{_cfg.ConfirmationBars}_bars");
            }

            return SignalDecision.Wait($"confirming_{ConfirmationCount}_of_{_cfg.ConfirmationBars}");
        }
    }

    public class SignalEngineConfig
    {
        public double DeviationAtrMult { get; set; } = 1.0;
        public int ConfirmationBars { get; set; } = 2;
        public bool AllowLongs { get; set; } = true;
        public bool AllowShorts { get; set; } = true;
        /// <summary>
        /// How many pips past Monday open a closed bar must travel before the engine
        /// concludes the setup is invalidated and resets. v1 effectively used 0.
        /// A small positive value (e.g. 2–5 pips) suppresses single-bar wick noise.
        /// </summary>
        public double ResetBufferPips { get; set; } = 0;
    }
}
