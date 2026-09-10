using System;

namespace PearlrockBots.TrendFollow.Core
{
    /// <summary>
    /// ATR-based trailing stop that ratchets in the favourable direction only.
    /// Unchanged from XAU_SQZ_V1's version (Core/AtrTrailingStop.cs) — this
    /// piece was already validated design (adapts to live, post-entry ATR
    /// rather than freezing at entry-time ATR) and backtesting found no
    /// reason to touch it for this strategy.
    ///
    /// LONG  -> trail level only moves UP   (max of prior, currentPrice - N*ATR).
    ///          Breached if currentPrice &lt; trail.
    /// SHORT -> trail level only moves DOWN (min of prior, currentPrice + N*ATR).
    ///          Breached if currentPrice &gt; trail. (Kept for completeness;
    ///          XAU_TREND_V1 itself is long-only.)
    /// </summary>
    public class AtrTrailingStop
    {
        public Side Direction { get; }
        public double Multiplier { get; }
        public double TrailLevel { get; private set; }

        public AtrTrailingStop(Side direction, double initialTrailLevel, double multiplier)
        {
            if (direction == Side.None)
                throw new ArgumentException("Direction must be Long or Short", nameof(direction));
            if (multiplier <= 0)
                throw new ArgumentOutOfRangeException(nameof(multiplier), "must be > 0");
            if (!IsFinite(initialTrailLevel))
                throw new ArgumentException("initialTrailLevel must be finite", nameof(initialTrailLevel));

            Direction = direction;
            Multiplier = multiplier;
            TrailLevel = initialTrailLevel;
        }

        public ExitDecision Update(double currentPrice, double currentAtr)
        {
            if (!IsFinite(currentPrice) || currentPrice <= 0)
                throw new ArgumentException("currentPrice must be finite and > 0", nameof(currentPrice));
            if (!IsFinite(currentAtr) || currentAtr <= 0)
                throw new ArgumentException("currentAtr must be finite and > 0", nameof(currentAtr));

            double atrBuffer = currentAtr * Multiplier;

            if (Direction == Side.Long)
            {
                double candidate = currentPrice - atrBuffer;
                if (candidate > TrailLevel) TrailLevel = candidate;
                if (currentPrice < TrailLevel)
                    return ExitDecision.Exit($"trail_hit_long@{TrailLevel:F5}");
            }
            else // Short
            {
                double candidate = currentPrice + atrBuffer;
                if (candidate < TrailLevel) TrailLevel = candidate;
                if (currentPrice > TrailLevel)
                    return ExitDecision.Exit($"trail_hit_short@{TrailLevel:F5}");
            }

            return ExitDecision.Stay();
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
