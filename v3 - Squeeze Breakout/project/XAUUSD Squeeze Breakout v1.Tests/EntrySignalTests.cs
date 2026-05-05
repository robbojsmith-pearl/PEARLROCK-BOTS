using PearlrockBots.SqueezeBreakout.Core;
using Xunit;

namespace PearlrockBots.SqueezeBreakout.Tests
{
    public class EntrySignalTests
    {
        private EntrySignalConfig DefaultCfg() => new EntrySignalConfig
        {
            AllowLongs = true,
            AllowShorts = true
        };

        private EntrySignalInput LongAligned(bool squeezeJustReleased = true) =>
            new EntrySignalInput(
                squeezeJustReleased: squeezeJustReleased,
                price: 2050,
                trendSma: 2000,
                momentumCurrent: 12,
                momentumPrevious: 8);

        private EntrySignalInput ShortAligned(bool squeezeJustReleased = true) =>
            new EntrySignalInput(
                squeezeJustReleased: squeezeJustReleased,
                price: 1950,
                trendSma: 2000,
                momentumCurrent: -12,
                momentumPrevious: -8);

        // ── Happy paths ──────────────────────────────────────────────

        [Fact]
        public void All_Conditions_Aligned_Long_Fires_Long_Entry()
        {
            var d = EntrySignal.Evaluate(LongAligned(), DefaultCfg());
            Assert.True(d.ShouldEnter);
            Assert.Equal(Side.Long, d.Direction);
        }

        [Fact]
        public void All_Conditions_Aligned_Short_Fires_Short_Entry()
        {
            var d = EntrySignal.Evaluate(ShortAligned(), DefaultCfg());
            Assert.True(d.ShouldEnter);
            Assert.Equal(Side.Short, d.Direction);
        }

        // ── Each gate failing ────────────────────────────────────────

        [Fact]
        public void No_Squeeze_Release_NoOp()
        {
            var d = EntrySignal.Evaluate(LongAligned(squeezeJustReleased: false), DefaultCfg());
            Assert.False(d.ShouldEnter);
            Assert.Contains("no_squeeze_release", d.Reason);
        }

        [Fact]
        public void Trend_Neutral_NoOp()
        {
            // price exactly = SMA → TrendGate returns None → NoOp.
            var input = new EntrySignalInput(
                squeezeJustReleased: true,
                price: 2000, trendSma: 2000,
                momentumCurrent: 12, momentumPrevious: 8);
            var d = EntrySignal.Evaluate(input, DefaultCfg());
            Assert.False(d.ShouldEnter);
            Assert.Contains("trend_neutral", d.Reason);
        }

        [Fact]
        public void Long_Trend_But_Negative_Momentum_NoOp()
        {
            // Price above SMA but momentum negative → momentum_against_trend.
            var input = new EntrySignalInput(
                squeezeJustReleased: true,
                price: 2050, trendSma: 2000,
                momentumCurrent: -2, momentumPrevious: -5);
            var d = EntrySignal.Evaluate(input, DefaultCfg());
            Assert.False(d.ShouldEnter);
            Assert.Contains("momentum_against_trend", d.Reason);
        }

        [Fact]
        public void Long_Trend_Positive_But_Not_Strengthening_NoOp()
        {
            // curr 8 = prev 8, not strictly strengthening.
            var input = new EntrySignalInput(
                squeezeJustReleased: true,
                price: 2050, trendSma: 2000,
                momentumCurrent: 8, momentumPrevious: 8);
            var d = EntrySignal.Evaluate(input, DefaultCfg());
            Assert.False(d.ShouldEnter);
            Assert.Contains("momentum_not_strengthening", d.Reason);
        }

        [Fact]
        public void Long_Trend_Positive_But_Weakening_NoOp()
        {
            // curr 5 < prev 8.
            var input = new EntrySignalInput(
                squeezeJustReleased: true,
                price: 2050, trendSma: 2000,
                momentumCurrent: 5, momentumPrevious: 8);
            var d = EntrySignal.Evaluate(input, DefaultCfg());
            Assert.False(d.ShouldEnter);
            Assert.Contains("momentum_not_strengthening", d.Reason);
        }

        [Fact]
        public void Short_Trend_But_Positive_Momentum_NoOp()
        {
            // Price below SMA but momentum positive.
            var input = new EntrySignalInput(
                squeezeJustReleased: true,
                price: 1950, trendSma: 2000,
                momentumCurrent: 5, momentumPrevious: 3);
            var d = EntrySignal.Evaluate(input, DefaultCfg());
            Assert.False(d.ShouldEnter);
            Assert.Contains("momentum_against_trend", d.Reason);
        }

        // ── Direction filter ────────────────────────────────────────

        [Fact]
        public void Long_Aligned_But_Longs_Disabled_NoOp()
        {
            var cfg = new EntrySignalConfig { AllowLongs = false, AllowShorts = true };
            var d = EntrySignal.Evaluate(LongAligned(), cfg);
            Assert.False(d.ShouldEnter);
            Assert.Equal("longs_disabled", d.Reason);
        }

        [Fact]
        public void Short_Aligned_But_Shorts_Disabled_NoOp()
        {
            var cfg = new EntrySignalConfig { AllowLongs = true, AllowShorts = false };
            var d = EntrySignal.Evaluate(ShortAligned(), cfg);
            Assert.False(d.ShouldEnter);
            Assert.Equal("shorts_disabled", d.Reason);
        }

        // ── Edge cases ──────────────────────────────────────────────

        [Fact]
        public void Long_Aligned_With_Negative_Prev_But_Positive_Curr_Fires()
        {
            // Prev was -2, curr is +5. Both signs of momentum align with longs?
            // alignedCurr = 5 > 0, alignedPrev = -2. alignedCurr > alignedPrev. Fires.
            var input = new EntrySignalInput(
                squeezeJustReleased: true,
                price: 2050, trendSma: 2000,
                momentumCurrent: 5, momentumPrevious: -2);
            var d = EntrySignal.Evaluate(input, DefaultCfg());
            Assert.True(d.ShouldEnter);
            Assert.Equal(Side.Long, d.Direction);
        }

        [Fact]
        public void Short_Aligned_With_Positive_Prev_But_Negative_Curr_Fires()
        {
            // Symmetric case for short.
            var input = new EntrySignalInput(
                squeezeJustReleased: true,
                price: 1950, trendSma: 2000,
                momentumCurrent: -5, momentumPrevious: 2);
            var d = EntrySignal.Evaluate(input, DefaultCfg());
            Assert.True(d.ShouldEnter);
            Assert.Equal(Side.Short, d.Direction);
        }
    }
}
