using System;
using System.Collections.Generic;

namespace PearlrockBots.TrendFollow.Core
{
    /// <summary>
    /// Kaufman-style efficiency ratio: net directional move over a window,
    /// divided by the total path length travelled to get there.
    ///
    ///   efficiency = |close[t] - close[t-N]| / sum(|close[i] - close[i-1]|)
    ///
    /// ~1.0 in a clean, direct trend; ~0.0 in pure chop — independent of
    /// volatility level, which is what makes it a genuine "regime" signal
    /// rather than a vol signal. (Backtesting found GARCH-forecast volatility
    /// had essentially zero correlation with this strategy's monthly P&L,
    /// r=-0.035 p=0.70, while efficiency correlated at r=+0.729 p&lt;0.0001 —
    /// efficiency is the validated regime signal here, not volatility.)
    ///
    /// Used two ways in XAU_TREND_V1:
    ///   - EntrySignal gates new entries on efficiency >= GateThreshold
    ///     (keeps the worst chop out entirely).
    ///   - SizingFactor scales position size continuously for whatever
    ///     survives the gate (leans into cleaner trends, trims noisier ones).
    /// </summary>
    public static class Efficiency
    {
        /// <param name="closesOldestFirst">Window of N+1 closes, oldest at index 0,
        /// most recent (current bar) at the last index.</param>
        public static double Compute(IReadOnlyList<double> closesOldestFirst)
        {
            if (closesOldestFirst == null) throw new ArgumentNullException(nameof(closesOldestFirst));
            int n = closesOldestFirst.Count;
            if (n < 2) throw new ArgumentException("need at least 2 values", nameof(closesOldestFirst));

            double netMove = Math.Abs(closesOldestFirst[n - 1] - closesOldestFirst[0]);
            double pathLen = 0.0;
            for (int i = 1; i < n; i++)
            {
                double v = closesOldestFirst[i];
                if (double.IsNaN(v) || double.IsInfinity(v))
                    throw new ArgumentException($"value at index {i} is not finite");
                pathLen += Math.Abs(v - closesOldestFirst[i - 1]);
            }

            return pathLen > 0 ? netMove / pathLen : 0.0;
        }

        /// <summary>Continuous sizing multiplier: efficiency relative to a fixed
        /// reference level, clipped to a sane band so a trade is never
        /// negligible nor absurdly oversized. RefEfficiency is a fixed,
        /// train-period-derived constant, not re-tuned live.</summary>
        public static double SizingFactor(double efficiency, double refEfficiency, double minFactor, double maxFactor)
        {
            if (refEfficiency <= 0)
                throw new ArgumentOutOfRangeException(nameof(refEfficiency), "must be > 0");
            double factor = efficiency / refEfficiency;
            return Math.Max(minFactor, Math.Min(maxFactor, factor));
        }
    }
}
