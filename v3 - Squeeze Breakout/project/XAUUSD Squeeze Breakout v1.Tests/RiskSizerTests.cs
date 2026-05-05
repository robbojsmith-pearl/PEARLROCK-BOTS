using System;
using PearlrockBots.SqueezeBreakout.Core;
using PearlrockBots.SqueezeBreakout.Tests.Helpers;
using Xunit;

namespace PearlrockBots.SqueezeBreakout.Tests
{
    public class RiskSizerTests
    {
        private RiskSizer Default(double risk = 0.5, double maxLev = 10.0, double buffer = 0.2)
            => new RiskSizer(new RiskSizerConfig
            {
                RiskPercent = risk,
                MaxLeverage = maxLev,
                LeverageBuffer = buffer
            });

        private SizingInput Build(double equity, double stopDist, double price)
        {
            var sym = new FakeSymbol();
            return new SizingInput(equity, stopDist, price,
                                   sym.VolumeInUnitsMin, sym.VolumeInUnitsMax,
                                   sym.NormalizeVolume);
        }

        // ── Standard math ────────────────────────────────────────────

        [Fact]
        public void Risk_Based_Sizing_Without_Leverage_Cap()
        {
            // $10k × 0.5% = $50 risk. Stop dist $30. Volume = 50/30 = 1.666... oz.
            // Normalized DOWN to 0.01 step → 1.66.
            var rs = Default();
            var (vol, reason) = rs.Calculate(Build(equity: 10_000, stopDist: 30, price: 2000));
            Assert.Null(reason);
            Assert.Equal(1.66, vol.Value, 2);
        }

        [Fact]
        public void Leverage_Cap_Engages_When_Risk_Volume_Too_Big()
        {
            // Tiny stop ($1) → risk-based vol huge: 50/1 = 50 oz on $10k = $100k notional = 10x.
            // Effective leverage = 10 - 0.2 = 9.8x. Max notional = $98k. Max vol = 98000/2000 = 49 oz.
            var rs = Default();
            var (vol, reason) = rs.Calculate(Build(equity: 10_000, stopDist: 1, price: 2000));
            Assert.Null(reason);
            Assert.Equal(49.0, vol.Value, 1);
        }

        [Fact]
        public void Volume_Below_Min_Is_Rejected()
        {
            // Tiny equity. $10 × 0.5% = $0.05 risk. Stop $30. Vol = 0.05/30 ≈ 0.00167.
            // Normalized DOWN to 0 (< min 0.01) → reject.
            var rs = Default();
            var (vol, reason) = rs.Calculate(Build(equity: 10, stopDist: 30, price: 2000));
            Assert.Null(vol);
            Assert.Contains("volume_below_min", reason);
        }

        // ── Validation ───────────────────────────────────────────────

        [Fact]
        public void Rejects_Zero_Stop_Distance()
        {
            var (vol, reason) = Default().Calculate(Build(10_000, 0, 2000));
            Assert.Null(vol);
            Assert.Equal("stop_distance_invalid", reason);
        }

        [Fact]
        public void Rejects_Zero_Equity()
        {
            var (vol, reason) = Default().Calculate(Build(0, 30, 2000));
            Assert.Null(vol);
            Assert.Equal("equity_invalid", reason);
        }

        [Fact]
        public void Rejects_Zero_Price()
        {
            var (vol, reason) = Default().Calculate(Build(10_000, 30, 0));
            Assert.Null(vol);
            Assert.Equal("price_invalid", reason);
        }

        [Fact]
        public void Constructor_Rejects_Invalid_Config()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RiskSizer(new RiskSizerConfig { RiskPercent = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RiskSizer(new RiskSizerConfig { RiskPercent = 0.5, MaxLeverage = 0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new RiskSizer(new RiskSizerConfig { RiskPercent = 0.5, MaxLeverage = 10, LeverageBuffer = -0.1 }));
        }

        [Fact]
        public void Higher_Risk_Percent_Produces_Larger_Volume()
        {
            var lo = Default(risk: 0.5).Calculate(Build(10_000, 30, 2000));
            var hi = Default(risk: 1.0).Calculate(Build(10_000, 30, 2000));
            Assert.True(hi.volume > lo.volume);
        }
    }
}
