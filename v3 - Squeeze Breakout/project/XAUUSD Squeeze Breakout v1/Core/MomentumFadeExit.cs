using System;

namespace PearlrockBots.SqueezeBreakout.Core
{
    /// <summary>
    /// Soft exit: leave the position when momentum either flips sign against the
    /// trade or fades by more than (1 − FadeRatio) of its prior strength.
    ///
    /// Direction-aware. The QC original handled this with a sign-sensitive rule
    /// that mis-fired when both prev and curr were negative (long position with
    /// degenerate inputs). This version aligns momentum with trade direction
    /// first, so the same expression works symmetrically:
    ///
    ///   alignedCurr = curr × directionSign  (Long = +1, Short = −1)
    ///   alignedPrev = prev × directionSign
    ///
    ///   Exit if alignedCurr &lt;= 0   (momentum flipped against the trade)
    ///   OR  if alignedPrev &gt; 0
    ///         AND alignedCurr &lt; alignedPrev
    ///         AND alignedCurr &lt; FadeRatio × alignedPrev
    ///
    /// FadeRatio default 0.5 means: exit if you've kept &lt; half of last bar's
    /// aligned momentum strength (and the absolute level has dropped).
    /// </summary>
    public static class MomentumFadeExit
    {
        public static ExitDecision Evaluate(
            Side direction, double currentMomentum, double previousMomentum, double fadeRatio = 0.5)
        {
            if (direction == Side.None)
                throw new ArgumentException("direction must be Long or Short", nameof(direction));
            if (fadeRatio <= 0 || fadeRatio >= 1)
                throw new ArgumentOutOfRangeException(nameof(fadeRatio), "must be in (0, 1)");
            if (!IsFinite(currentMomentum) || !IsFinite(previousMomentum))
                throw new ArgumentException("momentum values must be finite");

            double sign = (int)direction;
            double alignedCurr = currentMomentum * sign;
            double alignedPrev = previousMomentum * sign;

            if (alignedCurr <= 0)
                return ExitDecision.Exit($"momentum_flipped curr={currentMomentum:F5}");

            if (alignedPrev > 0
                && alignedCurr < alignedPrev
                && alignedCurr < fadeRatio * alignedPrev)
            {
                return ExitDecision.Exit(
                    $"momentum_faded curr={currentMomentum:F5} prev={previousMomentum:F5}");
            }

            return ExitDecision.Stay();
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
