using System;

namespace PearlrockBots.SqueezeBreakout.Core
{
    /// <summary>
    /// Custom optimizer fitness for v3 — extends v2's logic with a per-trade
    /// average-profit floor.
    ///
    /// Hard rejections (return RejectionScore):
    ///   - total trades &lt; MinTotalTrades
    ///   - trades / year &lt; MinTradesPerYear
    ///   - net profit ≤ 0
    ///   - max equity DD% &gt; MaxDrawdownPct
    ///   - net profit / total trades &lt; MinAverageTradeProfit  ← NEW for v3
    ///
    /// Otherwise:
    ///   score = (log10(1 + net) × min(PF, PfCap) × √trades × winRate²) / DD%^1.5
    ///
    /// Why MinAverageTradeProfit matters: a strategy that shows positive net via
    /// many tiny trades that barely beat costs is fragile in live conditions
    /// (slippage, spread variance, commission rounding). Requiring a meaningful
    /// per-trade edge filters those out.
    /// </summary>
    public static class Fitness
    {
        public static double Compute(FitnessInput x, FitnessConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            double years = x.BacktestYears > 0 ? x.BacktestYears : 1;
            double tradesPerYear = x.TotalTrades / years;
            double winRate = x.TotalTrades > 0 ? x.WinningTrades / x.TotalTrades : 0;
            double ddPct = Math.Max(x.MaxEquityDrawdownPct, 0.01);
            double pf = Math.Min(x.ProfitFactor, cfg.PfCap);
            double avgTrade = x.TotalTrades > 0 ? x.NetProfit / x.TotalTrades : 0;

            // Hard rejections
            if (x.TotalTrades < cfg.MinTotalTrades) return cfg.RejectionScore;
            if (tradesPerYear < cfg.MinTradesPerYear) return cfg.RejectionScore;
            if (x.NetProfit <= 0) return cfg.RejectionScore;
            if (ddPct > cfg.MaxDrawdownPct) return cfg.RejectionScore;
            if (avgTrade < cfg.MinAverageTradeProfit) return cfg.RejectionScore;

            double profitScore = Math.Log10(1.0 + x.NetProfit);
            double tradeScore = Math.Sqrt(x.TotalTrades);
            double ddPenalty = Math.Pow(ddPct, 1.5);
            double winBonus = Math.Pow(winRate, 2);

            return (profitScore * pf * tradeScore * winBonus) / ddPenalty;
        }
    }

    public readonly struct FitnessInput
    {
        public double TotalTrades { get; }
        public double WinningTrades { get; }
        public double NetProfit { get; }
        public double MaxEquityDrawdownPct { get; }
        public double ProfitFactor { get; }
        public double BacktestYears { get; }

        public FitnessInput(
            double totalTrades, double winningTrades, double netProfit,
            double maxEquityDrawdownPct, double profitFactor, double backtestYears)
        {
            TotalTrades = totalTrades;
            WinningTrades = winningTrades;
            NetProfit = netProfit;
            MaxEquityDrawdownPct = maxEquityDrawdownPct;
            ProfitFactor = profitFactor;
            BacktestYears = backtestYears;
        }
    }

    public class FitnessConfig
    {
        /// <summary>Hard floor on total trades.</summary>
        public int MinTotalTrades { get; set; } = 40;
        /// <summary>Hard floor on trades per year.</summary>
        public double MinTradesPerYear { get; set; } = 6;
        /// <summary>Hard ceiling on max equity DD%. v3 default 30 (vs v2's 20)
        /// because breakout strategies inherently run higher DD than mean reversion.</summary>
        public double MaxDrawdownPct { get; set; } = 30.0;
        /// <summary>Profit factor capped at this before scoring.</summary>
        public double PfCap { get; set; } = 3.0;
        /// <summary>NEW for v3: hard floor on net profit / total trades (currency).
        /// Default $10 — meaningful on a $10k-$100k account, low enough that any
        /// real edge passes.</summary>
        public double MinAverageTradeProfit { get; set; } = 10.0;
        /// <summary>Score returned when any hard rejection fires.</summary>
        public double RejectionScore { get; set; } = -1_000_000;
    }
}
