using System;

namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>
    /// Custom optimizer fitness — ported from v1.
    ///
    /// Why this exists: cTrader's default fitness is "net profit", which happily
    /// ranks under-trading curve-fit combinations near the top of the results grid
    /// (e.g. 9 trades over 8 years with PF 5.0 — looks great, won't survive OOS).
    ///
    /// Custom fitness hard-rejects those candidates at the optimizer level so the
    /// results grid is pre-filtered. Rejected combinations get a large negative
    /// score and sink to the bottom.
    ///
    /// Math:
    ///   reject if any of:
    ///     - total trades &lt; MinTotalTrades
    ///     - trades / year &lt; MinTradesPerYear
    ///     - net profit ≤ 0
    ///     - max equity DD% &gt; MaxDrawdownPct
    ///   else:
    ///     score = (log10(1 + net) × min(PF, PfCap) × √trades × winRate²) / DD%^1.5
    ///
    /// PF is capped at PfCap (default 3.0) so absurd PFs from overfit setups
    /// don't dominate the score — we want robustness, not flair.
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

            // Hard rejections — return a very negative score so the optimizer
            // ranks these last.
            if (x.TotalTrades < cfg.MinTotalTrades) return cfg.RejectionScore;
            if (tradesPerYear < cfg.MinTradesPerYear) return cfg.RejectionScore;
            if (x.NetProfit <= 0) return cfg.RejectionScore;
            if (ddPct > cfg.MaxDrawdownPct) return cfg.RejectionScore;

            double profitScore = Math.Log10(1.0 + x.NetProfit);
            double tradeScore = Math.Sqrt(x.TotalTrades);
            double ddPenalty = Math.Pow(ddPct, 1.5);
            double winBonus = Math.Pow(winRate, 2);

            return (profitScore * pf * tradeScore * winBonus) / ddPenalty;
        }
    }

    /// <summary>Inputs to the fitness calculation.</summary>
    public readonly struct FitnessInput
    {
        public double TotalTrades { get; }
        public double WinningTrades { get; }
        public double NetProfit { get; }
        /// <summary>Max equity drawdown expressed as a percentage (e.g. 12.5 for 12.5%).</summary>
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
        /// <summary>Hard floor on total trades. Below this, score = RejectionScore.</summary>
        public int MinTotalTrades { get; set; } = 40;
        /// <summary>Hard floor on trades per year (after dividing by BacktestYears).
        /// Default is the user's settled minimum (6/yr); crank to 10 for stricter runs.</summary>
        public double MinTradesPerYear { get; set; } = 6;
        /// <summary>Hard ceiling on max equity DD%. Above this, score = RejectionScore.</summary>
        public double MaxDrawdownPct { get; set; } = 20.0;
        /// <summary>Profit factor is capped at this value before scoring.</summary>
        public double PfCap { get; set; } = 3.0;
        /// <summary>Score returned when any hard rejection fires.</summary>
        public double RejectionScore { get; set; } = -1_000_000;
    }
}
