using System;

namespace PearlrockBots.SqueezeBreakout.Core
{
    /// <summary>
    /// Tracks Bollinger-Bands-inside-Keltner-Channels squeeze state across bars.
    ///
    /// "Squeeze on" = both Bollinger Bands sit inside the Keltner Channel
    /// (BB lower &gt; KC lower AND BB upper &lt; KC upper). This is a
    /// volatility-compression regime.
    ///
    /// "Just released" = previous bar was on, current bar is off. The trading
    /// signal in the QC original fires on the bar AFTER this transition is
    /// observed — i.e. one bar after the squeeze ended. We model the same.
    ///
    /// Usage: call <see cref="Update"/> exactly once per closed bar with the
    /// latest BB and KC band values.
    /// </summary>
    public class SqueezeDetector
    {
        private bool? _previousIsOn;
        public bool IsOn { get; private set; }
        public bool JustReleased { get; private set; }
        public bool HasHistory => _previousIsOn.HasValue;

        public void Update(double bbUpper, double bbLower, double kcUpper, double kcLower)
        {
            if (!IsFinite(bbUpper) || !IsFinite(bbLower) ||
                !IsFinite(kcUpper) || !IsFinite(kcLower))
                throw new ArgumentException("All band inputs must be finite numbers.");
            if (bbUpper < bbLower)
                throw new ArgumentException("bbUpper must be >= bbLower.");
            if (kcUpper < kcLower)
                throw new ArgumentException("kcUpper must be >= kcLower.");

            bool nowOn = bbLower > kcLower && bbUpper < kcUpper;
            JustReleased = _previousIsOn == true && !nowOn;
            IsOn = nowOn;
            _previousIsOn = nowOn;
        }

        public void Reset()
        {
            IsOn = false;
            JustReleased = false;
            _previousIsOn = null;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
