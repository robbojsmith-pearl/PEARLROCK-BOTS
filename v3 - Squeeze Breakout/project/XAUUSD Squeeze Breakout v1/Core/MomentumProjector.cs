using System;
using System.Collections.Generic;

namespace PearlrockBots.SqueezeBreakout.Core
{
    /// <summary>
    /// Computes the QC LinearAlphaKCSqueeze momentum:
    ///
    ///   midline = ((20H high + 20H low) / 2 + 20H SMA) / 2
    ///   mom_source[t] = price[t] − midline[t]
    ///   { mom_source values over a rolling window of N bars, oldest-first }
    ///   y = slope·x + intercept, fit via OLS over x = 0..N−1
    ///   projection = slope·(N−1) + intercept   ← regression-smoothed
    ///                                            estimate of the most recent point
    ///
    /// This class:
    ///   - manages the rolling window of mom_source values
    ///   - exposes static helpers for midline + LR projection (both pure)
    ///   - returns the latest projection on demand once the window is full
    /// </summary>
    public class MomentumProjector
    {
        private readonly Queue<double> _window;
        private readonly int _windowSize;

        public MomentumProjector(int windowSize)
        {
            if (windowSize < 2)
                throw new ArgumentOutOfRangeException(nameof(windowSize), "must be >= 2");
            _windowSize = windowSize;
            _window = new Queue<double>(windowSize);
        }

        public int WindowSize => _windowSize;
        public int Count => _window.Count;
        public bool IsReady => _window.Count == _windowSize;

        public void Add(double momSource)
        {
            if (double.IsNaN(momSource) || double.IsInfinity(momSource))
                throw new ArgumentException("momSource must be finite", nameof(momSource));
            _window.Enqueue(momSource);
            if (_window.Count > _windowSize) _window.Dequeue();
        }

        public void Reset() => _window.Clear();

        public double Project()
        {
            if (!IsReady)
                throw new InvalidOperationException(
                    $"Window not ready ({_window.Count}/{_windowSize}). Add more values before Project().");
            return ComputeLrProjection(_window.ToArray());
        }

        // ── Static helpers (pure functions) ─────────────────────────────

        /// <summary>
        /// QC midline: average of (HHHL midpoint) and (period SMA).
        /// </summary>
        public static double ComputeMidline(double periodHigh, double periodLow, double periodSma)
        {
            return ((periodHigh + periodLow) / 2.0 + periodSma) / 2.0;
        }

        /// <summary>
        /// Fit y = slope·x + intercept over x = 0..N−1 using ordinary least squares,
        /// then return the value of the regression line at the END of the window
        /// (x = N−1). This is a regression-smoothed estimate of the most recent
        /// observation, NOT a forward-looking forecast.
        /// </summary>
        /// <param name="oldestFirstValues">Window values, oldest at index 0.</param>
        public static double ComputeLrProjection(IReadOnlyList<double> oldestFirstValues)
        {
            if (oldestFirstValues == null) throw new ArgumentNullException(nameof(oldestFirstValues));
            int n = oldestFirstValues.Count;
            if (n < 2) throw new ArgumentException("need at least 2 values", nameof(oldestFirstValues));

            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            for (int i = 0; i < n; i++)
            {
                double y = oldestFirstValues[i];
                if (double.IsNaN(y) || double.IsInfinity(y))
                    throw new ArgumentException($"value at index {i} is not finite");
                sumX  += i;
                sumY  += y;
                sumXY += (double)i * y;
                sumXX += (double)i * i;
            }
            double denom = n * sumXX - sumX * sumX;
            if (Math.Abs(denom) < 1e-12)
                throw new ArgumentException("LR denominator zero (degenerate x values)");

            double slope = (n * sumXY - sumX * sumY) / denom;
            double intercept = (sumY - slope * sumX) / n;
            return slope * (n - 1) + intercept;
        }
    }
}
