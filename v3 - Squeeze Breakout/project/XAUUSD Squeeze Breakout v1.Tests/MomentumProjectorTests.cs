using System;
using PearlrockBots.SqueezeBreakout.Core;
using Xunit;

namespace PearlrockBots.SqueezeBreakout.Tests
{
    public class MomentumProjectorTests
    {
        // ── Midline ──────────────────────────────────────────────────

        [Fact]
        public void Midline_Is_Average_Of_HL_Midpoint_And_SMA()
        {
            // (high+low)/2 = (2100+1900)/2 = 2000.  ((2000 + 2010)/2 = 2005)
            double midline = MomentumProjector.ComputeMidline(periodHigh: 2100, periodLow: 1900, periodSma: 2010);
            Assert.Equal(2005.0, midline, 5);
        }

        [Fact]
        public void Midline_When_HL_Equals_SMA_Returns_That_Value()
        {
            double midline = MomentumProjector.ComputeMidline(periodHigh: 2010, periodLow: 1990, periodSma: 2000);
            // HL midpoint = 2000, SMA = 2000 → midline = 2000.
            Assert.Equal(2000.0, midline, 5);
        }

        // ── LR projection: pure ──────────────────────────────────────

        [Fact]
        public void LR_Projection_On_Constant_Series_Equals_That_Constant()
        {
            var values = new double[] { 5, 5, 5, 5, 5 };
            double proj = MomentumProjector.ComputeLrProjection(values);
            Assert.Equal(5.0, proj, 6);
        }

        [Fact]
        public void LR_Projection_On_Linear_Series_Returns_Last_Value()
        {
            // y = 2x + 1, x = 0..4 → values 1, 3, 5, 7, 9.
            // Projection at x = N-1 = 4 → 2*4 + 1 = 9.
            var values = new double[] { 1, 3, 5, 7, 9 };
            double proj = MomentumProjector.ComputeLrProjection(values);
            Assert.Equal(9.0, proj, 6);
        }

        [Fact]
        public void LR_Projection_On_Noisy_Linear_Series_Approximates_Trend()
        {
            // Underlying y = x. Inject small noise. Projection at last point should ≈ N-1.
            var values = new double[] { 0.1, 0.95, 2.05, 2.95, 4.1 };
            double proj = MomentumProjector.ComputeLrProjection(values);
            // True trend gives 4.0 at x=4. Noise should leave us within 0.2 of that.
            Assert.InRange(proj, 3.8, 4.2);
        }

        [Fact]
        public void LR_Projection_Slope_Direction_Matches_Series_Direction()
        {
            var rising = new double[] { 1, 2, 3, 4, 5 };
            var falling = new double[] { 5, 4, 3, 2, 1 };
            // Last fitted point of rising = 5; falling = 1.
            Assert.Equal(5.0, MomentumProjector.ComputeLrProjection(rising), 6);
            Assert.Equal(1.0, MomentumProjector.ComputeLrProjection(falling), 6);
        }

        [Fact]
        public void LR_Projection_Rejects_Single_Value() =>
            Assert.Throws<ArgumentException>(() =>
                MomentumProjector.ComputeLrProjection(new double[] { 1 }));

        [Fact]
        public void LR_Projection_Rejects_NaN() =>
            Assert.Throws<ArgumentException>(() =>
                MomentumProjector.ComputeLrProjection(new double[] { 1, 2, double.NaN, 4 }));

        // ── Window management ───────────────────────────────────────

        [Fact]
        public void Projector_Becomes_Ready_When_Window_Full()
        {
            var p = new MomentumProjector(windowSize: 5);
            for (int i = 0; i < 4; i++) { p.Add(i); Assert.False(p.IsReady); }
            p.Add(4);
            Assert.True(p.IsReady);
        }

        [Fact]
        public void Projector_Drops_Oldest_When_Over_Capacity()
        {
            var p = new MomentumProjector(windowSize: 3);
            p.Add(1); p.Add(2); p.Add(3);
            Assert.Equal(3, p.Count);

            p.Add(4); // should evict 1
            Assert.Equal(3, p.Count);
            // Latest 3 are 2, 3, 4 → linear, projection = 4.
            Assert.Equal(4.0, p.Project(), 6);
        }

        [Fact]
        public void Project_Throws_When_Not_Ready()
        {
            var p = new MomentumProjector(windowSize: 5);
            p.Add(1);
            Assert.Throws<InvalidOperationException>(() => p.Project());
        }

        [Fact]
        public void Reset_Clears_Window()
        {
            var p = new MomentumProjector(windowSize: 3);
            p.Add(1); p.Add(2); p.Add(3);
            p.Reset();
            Assert.False(p.IsReady);
            Assert.Equal(0, p.Count);
        }

        // ── Validation ───────────────────────────────────────────────

        [Fact]
        public void Rejects_Window_Size_Below_Two() =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new MomentumProjector(1));

        [Fact]
        public void Add_Rejects_NaN()
        {
            var p = new MomentumProjector(5);
            Assert.Throws<ArgumentException>(() => p.Add(double.NaN));
        }
    }
}
