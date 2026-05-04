using PearlrockBots.WeeklyMeanReversion.Core;
using Xunit;

namespace PearlrockBots.WeeklyMeanReversion.Tests
{
    public class FitnessTests
    {
        private FitnessConfig DefaultCfg() => new FitnessConfig
        {
            MinTotalTrades   = 40,
            MinTradesPerYear = 6,
            MaxDrawdownPct   = 20.0,
            PfCap            = 3.0,
            RejectionScore   = -1_000_000
        };

        private FitnessInput Healthy(double trades = 60, double wins = 18,
                                     double net = 5000, double dd = 12.0,
                                     double pf = 1.6, double years = 8) =>
            new FitnessInput(trades, wins, net, dd, pf, years);

        // ── Hard rejections ──────────────────────────────────────────

        [Fact]
        public void Rejects_When_Total_Trades_Below_Floor()
        {
            // 9 trades / 8 years (the user's example) — under-trading curve fit.
            var x = Healthy(trades: 9, wins: 6, net: 5000, dd: 10, pf: 4.5);
            Assert.Equal(-1_000_000, Fitness.Compute(x, DefaultCfg()));
        }

        [Fact]
        public void Rejects_When_Trades_Per_Year_Below_Floor()
        {
            // 40 trades over 8 years = 5/year — fails MinTradesPerYear=6.
            // Passes MinTotalTrades=40 floor exactly.
            var x = Healthy(trades: 40, wins: 12, net: 3000, dd: 10, pf: 2.0, years: 8);
            Assert.Equal(-1_000_000, Fitness.Compute(x, DefaultCfg()));
        }

        [Fact]
        public void Rejects_When_Net_Profit_Zero_Or_Negative()
        {
            var x = Healthy(net: 0);
            Assert.Equal(-1_000_000, Fitness.Compute(x, DefaultCfg()));
            var y = Healthy(net: -100);
            Assert.Equal(-1_000_000, Fitness.Compute(y, DefaultCfg()));
        }

        [Fact]
        public void Rejects_When_Drawdown_Above_Cap()
        {
            var x = Healthy(dd: 25.0);  // > MaxDrawdownPct = 20
            Assert.Equal(-1_000_000, Fitness.Compute(x, DefaultCfg()));
        }

        // ── Scoring ──────────────────────────────────────────────────

        [Fact]
        public void Healthy_Run_Produces_Positive_Score()
        {
            var x = Healthy();
            double score = Fitness.Compute(x, DefaultCfg());
            Assert.True(score > 0, $"score was {score}");
        }

        [Fact]
        public void Higher_Net_Profit_Increases_Score()
        {
            var lo = Fitness.Compute(Healthy(net: 2000), DefaultCfg());
            var hi = Fitness.Compute(Healthy(net: 8000), DefaultCfg());
            Assert.True(hi > lo);
        }

        [Fact]
        public void Higher_Drawdown_Decreases_Score()
        {
            var low_dd  = Fitness.Compute(Healthy(dd: 8),  DefaultCfg());
            var high_dd = Fitness.Compute(Healthy(dd: 18), DefaultCfg());
            Assert.True(low_dd > high_dd);
        }

        [Fact]
        public void PF_Cap_Limits_Score_Inflation_From_Absurd_PFs()
        {
            // Two runs identical except PF: capped at 3.0, so 5.6 PF should
            // produce the same score as 3.0 PF.
            var cfg = DefaultCfg();
            var capped = Fitness.Compute(Healthy(pf: 3.0), cfg);
            var absurd = Fitness.Compute(Healthy(pf: 5.6), cfg);
            Assert.Equal(capped, absurd, 6);
        }

        [Fact]
        public void Higher_Win_Rate_Strongly_Increases_Score()
        {
            // winRate is squared in the score, so 30% vs 15% should
            // roughly quadruple this term.
            var lo_wr = Fitness.Compute(Healthy(trades: 60, wins: 9),  DefaultCfg());  // 15%
            var hi_wr = Fitness.Compute(Healthy(trades: 60, wins: 18), DefaultCfg());  // 30%
            Assert.True(hi_wr > lo_wr * 3);
        }

        [Fact]
        public void Trade_Count_Scales_Score_Sublinearly()
        {
            // sqrt(trades) — doubling trade count should NOT double the score.
            var cfg = DefaultCfg();
            var fewer = Fitness.Compute(Healthy(trades: 60, wins: 18), cfg);
            var more  = Fitness.Compute(Healthy(trades: 240, wins: 72), cfg);  // 4x trades
            Assert.True(more < fewer * 4);  // scales by sqrt(4) = 2x, not 4x
        }

        // ── Edge cases ───────────────────────────────────────────────

        [Fact]
        public void Zero_DD_Treated_As_0_01_Pct()
        {
            // Avoids divide-by-zero. Should not throw and should produce a finite score.
            var x = Healthy(dd: 0);
            double score = Fitness.Compute(x, DefaultCfg());
            Assert.True(double.IsFinite(score));
            Assert.True(score > 0);
        }

        [Fact]
        public void Stricter_Trades_Per_Year_Floor_Rejects_More()
        {
            // 60 trades / 8 years = 7.5/year. Passes default 6/yr, fails 10/yr.
            var x = Healthy(trades: 60);
            var lax    = DefaultCfg();
            var strict = DefaultCfg();
            strict.MinTradesPerYear = 10;
            Assert.True(Fitness.Compute(x, lax) > 0);
            Assert.Equal(-1_000_000, Fitness.Compute(x, strict));
        }
    }
}
