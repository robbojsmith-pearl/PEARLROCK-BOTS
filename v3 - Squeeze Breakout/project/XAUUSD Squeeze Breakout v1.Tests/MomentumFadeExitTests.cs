using System;
using PearlrockBots.SqueezeBreakout.Core;
using Xunit;

namespace PearlrockBots.SqueezeBreakout.Tests
{
    public class MomentumFadeExitTests
    {
        // ── Long side ────────────────────────────────────────────────

        [Fact]
        public void Long_Stays_When_Momentum_Strengthening()
        {
            // both positive, current >= prev → stay.
            var d = MomentumFadeExit.Evaluate(Side.Long, currentMomentum: 12, previousMomentum: 10);
            Assert.False(d.ShouldExit);
        }

        [Fact]
        public void Long_Stays_When_Momentum_Decays_But_Above_Threshold()
        {
            // prev 10, curr 6. 6 > 5 (= 0.5*10). Stay.
            var d = MomentumFadeExit.Evaluate(Side.Long, currentMomentum: 6, previousMomentum: 10);
            Assert.False(d.ShouldExit);
        }

        [Fact]
        public void Long_Exits_When_Momentum_Below_Half_Of_Previous()
        {
            // prev 10, curr 4. 4 < 5. Both positive. Exit.
            var d = MomentumFadeExit.Evaluate(Side.Long, currentMomentum: 4, previousMomentum: 10);
            Assert.True(d.ShouldExit);
            Assert.Contains("momentum_faded", d.Reason);
        }

        [Fact]
        public void Long_Exits_When_Momentum_Flips_Negative()
        {
            var d = MomentumFadeExit.Evaluate(Side.Long, currentMomentum: -1, previousMomentum: 10);
            Assert.True(d.ShouldExit);
            Assert.Contains("momentum_flipped", d.Reason);
        }

        [Fact]
        public void Long_Exits_When_Momentum_Hits_Zero()
        {
            var d = MomentumFadeExit.Evaluate(Side.Long, currentMomentum: 0, previousMomentum: 10);
            Assert.True(d.ShouldExit);
        }

        // ── Short side (mirror, plus the QC sign-flip case) ─────────

        [Fact]
        public void Short_Stays_When_Momentum_Strengthening_Down()
        {
            // both negative. -12 stronger than -10 in shorts terms.
            // alignedCurr = +12, alignedPrev = +10. Curr > prev, no fade.
            var d = MomentumFadeExit.Evaluate(Side.Short, currentMomentum: -12, previousMomentum: -10);
            Assert.False(d.ShouldExit);
        }

        [Fact]
        public void Short_Stays_When_Momentum_Decays_But_Above_Threshold()
        {
            // prev -10, curr -6. alignedPrev=10, alignedCurr=6. 6 > 5. Stay.
            var d = MomentumFadeExit.Evaluate(Side.Short, currentMomentum: -6, previousMomentum: -10);
            Assert.False(d.ShouldExit);
        }

        [Fact]
        public void Short_Exits_When_Momentum_Strength_Below_Half()
        {
            // prev -10, curr -4. alignedPrev=10, alignedCurr=4. 4 < 5. Exit.
            var d = MomentumFadeExit.Evaluate(Side.Short, currentMomentum: -4, previousMomentum: -10);
            Assert.True(d.ShouldExit);
            Assert.Contains("momentum_faded", d.Reason);
        }

        [Fact]
        public void Short_Exits_When_Momentum_Flips_Positive()
        {
            var d = MomentumFadeExit.Evaluate(Side.Short, currentMomentum: 2, previousMomentum: -10);
            Assert.True(d.ShouldExit);
            Assert.Contains("momentum_flipped", d.Reason);
        }

        [Fact]
        public void Short_Exits_When_Momentum_Hits_Zero()
        {
            var d = MomentumFadeExit.Evaluate(Side.Short, currentMomentum: 0, previousMomentum: -10);
            Assert.True(d.ShouldExit);
        }

        // ── Custom fade ratio ────────────────────────────────────────

        [Fact]
        public void Custom_Fade_Ratio_Tightens_Exit_Threshold()
        {
            // With fadeRatio = 0.8, prev 10 → threshold 8.
            // curr 7 < 8 → exits even though > half.
            var d = MomentumFadeExit.Evaluate(Side.Long, 7, 10, fadeRatio: 0.8);
            Assert.True(d.ShouldExit);
        }

        // ── Validation ───────────────────────────────────────────────

        [Fact]
        public void Rejects_Side_None() =>
            Assert.Throws<ArgumentException>(() =>
                MomentumFadeExit.Evaluate(Side.None, 1, 2));

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-0.1)]
        [InlineData(1.5)]
        public void Rejects_Invalid_Fade_Ratio(double ratio) =>
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                MomentumFadeExit.Evaluate(Side.Long, 5, 10, ratio));

        [Fact]
        public void Rejects_NaN_Inputs() =>
            Assert.Throws<ArgumentException>(() =>
                MomentumFadeExit.Evaluate(Side.Long, double.NaN, 10));
    }
}
