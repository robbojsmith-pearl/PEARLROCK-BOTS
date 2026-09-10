using System;

namespace PearlrockBots.TrendFollow.Core
{
    /// <summary>
    /// Direction filter based on price vs a simple moving average. Pure function.
    /// XAU_TREND_V1 only acts on the Long case (see EntrySignal) — validated
    /// backtesting (2013-2026) found the short side weak/inconsistent at every
    /// SMA period tested on this instrument, consistent with gold's largely
    /// upward-biased history over the period. Kept generic (returns Short too)
    /// so the gate itself stays a reusable, honest primitive.
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
