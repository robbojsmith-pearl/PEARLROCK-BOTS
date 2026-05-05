using System;
using PearlrockBots.SqueezeBreakout.Core;
using Xunit;

namespace PearlrockBots.SqueezeBreakout.Tests
{
    public class TrendGateTests
    {
        [Fact]
        public void Price_Above_SMA_Returns_Long() =>
            Assert.Equal(Side.Long, TrendGate.Evaluate(price: 2050, sma: 2000));

        [Fact]
        public void Price_Below_SMA_Returns_Short() =>
            Assert.Equal(Side.Short, TrendGate.Evaluate(price: 1950, sma: 2000));

        [Fact]
        public void Price_Equals_SMA_Returns_None() =>
            Assert.Equal(Side.None, TrendGate.Evaluate(price: 2000, sma: 2000));

        [Fact]
        public void Tiny_Difference_Above_Returns_Long() =>
            Assert.Equal(Side.Long, TrendGate.Evaluate(price: 2000.001, sma: 2000));

        [Fact]
        public void Tiny_Difference_Below_Returns_Short() =>
            Assert.Equal(Side.Short, TrendGate.Evaluate(price: 1999.999, sma: 2000));

        [Fact]
        public void Rejects_NaN_Price() =>
            Assert.Throws<ArgumentException>(() => TrendGate.Evaluate(double.NaN, 2000));

        [Fact]
        public void Rejects_NaN_Sma() =>
            Assert.Throws<ArgumentException>(() => TrendGate.Evaluate(2000, double.NaN));
    }
}
