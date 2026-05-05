using System;

namespace PearlrockBots.SqueezeBreakout.Core
{
    /// <summary>
    /// Direction filter based on price vs a simple moving average. Pure function.
    ///
    /// Returns Side.Long when price &gt; SMA, Side.Short when price &lt; SMA,
    /// Side.None when exactly equal (rare but well-defined).
    /// </summary>
    public static class TrendGate
    {
        public static Side Evaluate(double price, double sma)
        {
            if (double.IsNaN(price) || double.IsInfinity(price))
                throw new ArgumentException("price must be finite", nameof(price));
            if (double.IsNaN(sma) || double.IsInfinity(sma))
                throw new ArgumentException("sma must be finite", nameof(sma));

            if (price > sma) return Side.Long;
            if (price < sma) return Side.Short;
            return Side.None;
        }
    }
}
