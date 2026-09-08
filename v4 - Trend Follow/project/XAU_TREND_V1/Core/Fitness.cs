using System;

namespace PearlrockBots.TrendFollow.Core
{
    /// <summary>
    /// Custom optimizer fitness — unchanged from XAU_SQZ_V1's v3 fitness
    /// (Core/Fitness.cs in the Squeeze Breakout project). Reused as-is: it
    /// isn't strategy-specific, and the same overfitting guardrails apply
    /// here (see the exit-multiple sweep in xau_trend_v1_exit_sweep.py,
    /// which found the naive train-selected "winner" underperformed on
    /// held-out data — exactly the failure mode MinAverageTradeProfit and
    /// the trade-count floors exist to discourage).
    ///
    /// Hard rejections (return RejectionScore):
    ///   - total trades &lt; MinTotalTrades
    ///   - trades / year &lt; MinTradesPerYear
    ///   - net profit &lt;= 0
    ///   - max equity DD% &gt; MaxDrawdownPct
    ///   - net profit / total trades &lt; MinAverageTradeProfit
    ///
    /// Otherwise:
    ///   score = (log10(1 + net) x min(PF, PfCap) x sqrt(trades) x winRate^2) / DD%^1.5
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
        public int MinTotalTrades { get; set; } = 40;
        public double MinTradesPerYear { get; set; } = 6;
        public double MaxDrawdownPct { get; set; } = 20.0;
        public double PfCap { get; set; } = 3.0;
        public double MinAverageTradeProfit { get; set; } = 10.0;
        public double RejectionScore { get; set; } = -1_000_000;
    }
}
