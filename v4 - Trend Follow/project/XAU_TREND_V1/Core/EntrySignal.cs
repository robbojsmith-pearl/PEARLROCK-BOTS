using System;

namespace PearlrockBots.TrendFollow.Core
{
    /// <summary>
    /// Pure entry decision: long-only trend-follow, gated by trend-efficiency.
    ///
    /// Enter Long when:
    ///   - price > TrendSma (200H, validated — see trend_period_sweep.py)
    ///   - efficiency(200H) >= GateThreshold (keeps the worst chop out; see
    ///     Core/Efficiency.cs for why efficiency was chosen over GARCH vol)
    ///
    /// Deliberately long-only, not just AllowShorts=false by config: backtesting
    /// (2013-2026, multiple SMA periods) found the short side weak/inconsistent
    /// at every period tested, and at some periods significantly NEGATIVE
    /// expectancy. Rather than expose an AllowShorts toggle that could be
    /// flipped back on without re-validation (exactly what happened to
    /// XAU_SQZ_V1 — see STRATEGY_LEDGER.md's long-only vs live-config note),
    /// the short path simply doesn't exist in this signal.
    /// </summary>
    public static class EntrySignal
    {
        public static EntryDecision Evaluate(double price, double trendSma, double efficiency, double gateThreshold)
        {
            if (double.IsNaN(price) || double.IsInfinity(price))
                throw new ArgumentException("price must be finite", nameof(price));
            if (double.IsNaN(trendSma) || double.IsInfinity(trendSma))
                throw new ArgumentException("trendSma must be finite", nameof(trendSma));
            if (double.IsNaN(efficiency) || double.IsInfinity(efficiency))
                throw new ArgumentException("efficiency must be finite", nameof(efficiency));

            Side trendSide = TrendGate.Evaluate(price, trendSma);
            if (trendSide != Side.Long)
                return EntryDecision.NoOp($"not_above_trend_sma trend={trendSide}");

            if (efficiency < gateThreshold)
                return EntryDecision.NoOp($"efficiency_below_gate eff={efficiency:F4} gate={gateThreshold:F4}");

            return EntryDecision.Enter(Side.Long, $"trend_long eff={efficiency:F4}");
        }
    }
}
