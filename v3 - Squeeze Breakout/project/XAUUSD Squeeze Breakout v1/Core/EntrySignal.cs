using System;

namespace PearlrockBots.SqueezeBreakout.Core
{
    /// <summary>
    /// Pure entry decision combining squeeze-just-released, momentum gate, and
    /// trend gate.
    ///
    /// Returns Enter ONLY when ALL of:
    ///   - SqueezeJustReleased == true
    ///   - TrendGate yields Long or Short (price ≠ SMA)
    ///   - Momentum is aligned with trend direction (curr·sign &gt; 0)
    ///   - Momentum is strengthening (curr·sign &gt; prev·sign)
    ///   - Direction is enabled in config (AllowLongs / AllowShorts)
    ///
    /// Each failed gate returns NoOp with a descriptive reason for the log.
    /// </summary>
    public static class EntrySignal
    {
        public static EntryDecision Evaluate(EntrySignalInput x, EntrySignalConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            if (!x.SqueezeJustReleased) return EntryDecision.NoOp("no_squeeze_release");

            Side trendSide = TrendGate.Evaluate(x.Price, x.TrendSma);
            if (trendSide == Side.None) return EntryDecision.NoOp("trend_neutral");

            double sign = (int)trendSide;
            double alignedCurr = x.MomentumCurrent * sign;
            double alignedPrev = x.MomentumPrevious * sign;

            if (alignedCurr <= 0)
                return EntryDecision.NoOp($"momentum_against_trend curr={x.MomentumCurrent:F5} side={trendSide}");

            if (alignedCurr <= alignedPrev)
                return EntryDecision.NoOp($"momentum_not_strengthening curr={x.MomentumCurrent:F5} prev={x.MomentumPrevious:F5}");

            if (trendSide == Side.Long && !cfg.AllowLongs)
                return EntryDecision.NoOp("longs_disabled");
            if (trendSide == Side.Short && !cfg.AllowShorts)
                return EntryDecision.NoOp("shorts_disabled");

            return EntryDecision.Enter(trendSide, $"squeeze_release_aligned_{trendSide}");
        }
    }

    public readonly struct EntrySignalInput
    {
        public bool SqueezeJustReleased { get; }
        public double Price { get; }
        public double TrendSma { get; }
        public double MomentumCurrent { get; }
        public double MomentumPrevious { get; }

        public EntrySignalInput(bool squeezeJustReleased, double price, double trendSma,
                                double momentumCurrent, double momentumPrevious)
        {
            SqueezeJustReleased = squeezeJustReleased;
            Price = price;
            TrendSma = trendSma;
            MomentumCurrent = momentumCurrent;
            MomentumPrevious = momentumPrevious;
        }
    }

    public class EntrySignalConfig
    {
        public bool AllowLongs { get; set; } = true;
        public bool AllowShorts { get; set; } = true;
    }
}
