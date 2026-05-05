using System;
using PearlrockBots.SqueezeBreakout.Core;
using Xunit;

namespace PearlrockBots.SqueezeBreakout.Tests
{
    public class AtrTrailingStopTests
    {
        // ── Long ─────────────────────────────────────────────────────

        [Fact]
        public void Long_Trail_Ratchets_Up_When_Price_Rises()
        {
            // entry 2000, atr 10, mult 3 → initial trail = 2000 - 30 = 1970
            var trail = new AtrTrailingStop(Side.Long, 1970, 3.0);

            // Price rises to 2050, ATR still 10. New candidate = 2050 - 30 = 2020. Ratchet up.
            var d1 = trail.Update(currentPrice: 2050, currentAtr: 10);
            Assert.False(d1.ShouldExit);
            Assert.Equal(2020, trail.TrailLevel, 5);

            // Price rises again to 2080. Candidate = 2050. Ratchet.
            trail.Update(currentPrice: 2080, currentAtr: 10);
            Assert.Equal(2050, trail.TrailLevel, 5);
        }

        [Fact]
        public void Long_Trail_Does_Not_Ratchet_Down_When_Price_Falls()
        {
            var trail = new AtrTrailingStop(Side.Long, 1970, 3.0);
            trail.Update(currentPrice: 2050, currentAtr: 10);  // trail → 2020
            Assert.Equal(2020, trail.TrailLevel, 5);

            // Price falls to 2030. Candidate = 2030 - 30 = 2000. Don't ratchet down.
            trail.Update(currentPrice: 2030, currentAtr: 10);
            Assert.Equal(2020, trail.TrailLevel, 5);
        }

        [Fact]
        public void Long_Exits_When_Price_Below_Trail()
        {
            var trail = new AtrTrailingStop(Side.Long, 1970, 3.0);
            trail.Update(currentPrice: 2050, currentAtr: 10);  // trail → 2020

            // Price drops to 2010, below trail of 2020.
            var d = trail.Update(currentPrice: 2010, currentAtr: 10);
            Assert.True(d.ShouldExit);
            Assert.Contains("trail_hit_long", d.Reason);
        }

        // ── Short ────────────────────────────────────────────────────

        [Fact]
        public void Short_Trail_Ratchets_Down_When_Price_Falls()
        {
            // entry 2000, atr 10, mult 3 → initial trail = 2000 + 30 = 2030
            var trail = new AtrTrailingStop(Side.Short, 2030, 3.0);

            // Price falls to 1950, candidate = 1950 + 30 = 1980. Ratchet DOWN.
            trail.Update(currentPrice: 1950, currentAtr: 10);
            Assert.Equal(1980, trail.TrailLevel, 5);

            // Price falls further to 1920, candidate = 1950. Ratchet.
            trail.Update(currentPrice: 1920, currentAtr: 10);
            Assert.Equal(1950, trail.TrailLevel, 5);
        }

        [Fact]
        public void Short_Trail_Does_Not_Ratchet_Up_When_Price_Rises()
        {
            var trail = new AtrTrailingStop(Side.Short, 2030, 3.0);
            trail.Update(currentPrice: 1950, currentAtr: 10);  // trail → 1980

            // Price rises to 1970. Candidate = 1970 + 30 = 2000. Don't move trail up.
            trail.Update(currentPrice: 1970, currentAtr: 10);
            Assert.Equal(1980, trail.TrailLevel, 5);
        }

        [Fact]
        public void Short_Exits_When_Price_Above_Trail()
        {
            var trail = new AtrTrailingStop(Side.Short, 2030, 3.0);
            trail.Update(currentPrice: 1950, currentAtr: 10);  // trail → 1980

            // Price rises to 1990, above trail of 1980.
            var d = trail.Update(currentPrice: 1990, currentAtr: 10);
            Assert.True(d.ShouldExit);
            Assert.Contains("trail_hit_short", d.Reason);
        }

        // ── ATR-aware ratchet ───────────────────────────────────────

        [Fact]
        public void Trail_Distance_Scales_With_Current_ATR()
        {
            // If ATR widens, the trail distance widens too — but only if price has moved enough.
            var trail = new AtrTrailingStop(Side.Long, 1970, 3.0);
            trail.Update(currentPrice: 2050, currentAtr: 10);   // trail → 2020 (3*10 below)
            // ATR doubles to 20; candidate = 2050 - 60 = 1990. Don't ratchet (1990 < 2020).
            trail.Update(currentPrice: 2050, currentAtr: 20);
            Assert.Equal(2020, trail.TrailLevel, 5);
        }

        // ── Validation ───────────────────────────────────────────────

        [Fact]
        public void Rejects_Side_None() =>
            Assert.Throws<ArgumentException>(() =>
                new AtrTrailingStop(Side.None, 1970, 3.0));

        [Fact]
        public void Rejects_Non_Positive_Multiplier() =>
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new AtrTrailingStop(Side.Long, 1970, 0));

        [Fact]
        public void Rejects_Non_Positive_Price()
        {
            var trail = new AtrTrailingStop(Side.Long, 1970, 3.0);
            Assert.Throws<ArgumentException>(() => trail.Update(0, 10));
        }

        [Fact]
        public void Rejects_Non_Positive_ATR()
        {
            var trail = new AtrTrailingStop(Side.Long, 1970, 3.0);
            Assert.Throws<ArgumentException>(() => trail.Update(2000, 0));
        }
    }
}
