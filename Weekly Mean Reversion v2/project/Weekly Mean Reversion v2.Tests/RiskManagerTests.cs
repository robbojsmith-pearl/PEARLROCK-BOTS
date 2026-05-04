using PearlrockBots.WeeklyMeanReversion.Core;
using PearlrockBots.WeeklyMeanReversion.Tests.Helpers;
using Xunit;

namespace PearlrockBots.WeeklyMeanReversion.Tests
{
    public class RiskManagerTests
    {
        private const double Pip = 0.0001;
        private const double MO = 1.2000;

        private RiskManager New(RiskManagerConfig cfg = null)
        {
            cfg ??= new RiskManagerConfig
            {
                RiskPercent = 0.5,
                SlAtrMultiple = 2.0,
                MaxSlPips = 0,
                TpBufferPips = 5,
                MinTpToSlRatio = 0.5,
                TpFraction = 1.0
            };
            return new RiskManager(cfg);
        }

        private EntryContext Ctx(TradeDirection dir, double m5Atr, double bid, double ask)
        {
            var fake = new FakeSymbol { Bid = bid, Ask = ask };
            return new EntryContext(
                direction: dir,
                mondayOpen: MO,
                m5Atr: m5Atr,
                equity: 100_000,
                bid: bid,
                ask: ask,
                pipSize: Pip,
                volumeInUnitsMin: fake.VolumeInUnitsMin,
                volumeInUnitsMax: fake.VolumeInUnitsMax,
                volumeForRisk: fake.VolumeForRisk,
                normalizeVolume: fake.NormalizeVolume);
        }

        // ── Backwards compatibility: TpFraction = 1.0 = v2.0 behaviour ──

        [Fact]
        public void Long_Entry_Below_MO_With_Full_Reversion_Matches_v2_0()
        {
            // M5 ATR = 0.0010 (10 pips), SL multiple 2 -> 20 pips SL.
            // Buying at 1.1980 (ask). distanceToMO = 20 pips.
            // tpDistance = 20 * 1.0 - 5 (buffer) = 15 pips.
            var rm = New();
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.Long, 0.0010, 1.1979, 1.1980));
            Assert.NotNull(entry);
            Assert.Null(reason);
            Assert.Equal(20.0, entry.Value.SlPips, 1);
            Assert.Equal(15.0, entry.Value.TpPips, 1);
            Assert.True(entry.Value.Volume > 0);
        }

        [Fact]
        public void Short_Entry_Above_MO_With_Full_Reversion_Matches_v2_0()
        {
            var rm = New();
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.Short, 0.0010, 1.2020, 1.2021));
            Assert.NotNull(entry);
            Assert.Equal(20.0, entry.Value.SlPips, 1);
            Assert.Equal(15.0, entry.Value.TpPips, 1);
        }

        // ── Partial reversion: TpFraction = 0.5 ──

        [Fact]
        public void Long_Half_Reversion_TP_Is_Half_Distance_Minus_Buffer()
        {
            // distanceToMO = 50 pips, fraction = 0.5, buffer = 5 -> tp = 25 - 5 = 20 pips.
            var rm = New(new RiskManagerConfig
            {
                RiskPercent = 0.5, SlAtrMultiple = 2.0, MaxSlPips = 0,
                TpBufferPips = 5, MinTpToSlRatio = 0.5, TpFraction = 0.5
            });
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.Long, 0.0010, 1.1949, 1.1950));
            Assert.NotNull(entry);
            Assert.Null(reason);
            Assert.Equal(20.0, entry.Value.TpPips, 1);
        }

        [Fact]
        public void Short_Half_Reversion_TP_Is_Half_Distance_Minus_Buffer()
        {
            var rm = New(new RiskManagerConfig
            {
                RiskPercent = 0.5, SlAtrMultiple = 2.0, MaxSlPips = 0,
                TpBufferPips = 5, MinTpToSlRatio = 0.5, TpFraction = 0.5
            });
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.Short, 0.0010, 1.2050, 1.2051));
            Assert.NotNull(entry);
            Assert.Null(reason);
            Assert.Equal(20.0, entry.Value.TpPips, 1);
        }

        [Fact]
        public void Tp_Note_Includes_Fraction()
        {
            var rm = New(new RiskManagerConfig
            {
                RiskPercent = 0.5, SlAtrMultiple = 2.0, MaxSlPips = 0,
                TpBufferPips = 5, MinTpToSlRatio = 0.5, TpFraction = 0.5
            });
            var (entry, _) = rm.BuildEntry(Ctx(TradeDirection.Long, 0.0010, 1.1949, 1.1950));
            Assert.Contains("frac=0.50", entry.Value.Note);
        }

        // ── Rejection paths ──

        [Fact]
        public void Blocks_When_SL_Exceeds_Cap()
        {
            var rm = New(new RiskManagerConfig
            {
                RiskPercent = 0.5, SlAtrMultiple = 2.0, MaxSlPips = 50,
                TpBufferPips = 5, MinTpToSlRatio = 0.5, TpFraction = 1.0
            });
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.Long, 0.0100, 1.1900, 1.1901));
            Assert.Null(entry);
            Assert.Contains("sl_too_wide", reason);
        }

        [Fact]
        public void Blocks_When_TP_Too_Small_Relative_To_SL()
        {
            // 10 pip SL (M5 ATR 5p * 2). Need tpPips < 5 to trip MinTpToSlRatio = 0.5.
            // distanceToMO = 9 pips, fraction = 1.0, buffer = 5 -> tpDistance = 4 pips.
            // 4 < 10 * 0.5 = 5 -> blocked with tp_too_small.
            var rm = New();  // SL multiple 2, fraction 1.0
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.Long, 0.00025, 1.19909, 1.19910));
            Assert.Null(entry);
            Assert.Contains("tp_too_small", reason);
        }

        [Fact]
        public void Blocks_When_TP_Distance_Non_Positive_After_Buffer()
        {
            // distanceToMO = 3 pips, buffer = 5 -> tpDistance = -2 -> non-positive.
            var rm = New();
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.Long, 0.0005, 1.19967, 1.19970));
            Assert.Null(entry);
            Assert.Contains("tp_distance_non_positive", reason);
        }

        [Fact]
        public void Blocks_When_Price_Already_Past_Monday_Open()
        {
            // Long but ask is above MO -> distanceToMO is negative.
            var rm = New();
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.Long, 0.0010, 1.2009, 1.2010));
            Assert.Null(entry);
            Assert.Contains("price_already_at_or_past_mo", reason);
        }

        [Fact]
        public void Blocks_When_M5_Atr_Not_Ready()
        {
            var rm = New();
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.Long, 0.0, 1.1980, 1.1981));
            Assert.Null(entry);
            Assert.Equal("m5_atr_not_ready", reason);
        }

        [Fact]
        public void Blocks_When_Direction_None()
        {
            var rm = New();
            var (entry, reason) = rm.BuildEntry(Ctx(TradeDirection.None, 0.0010, 1.2000, 1.2001));
            Assert.Null(entry);
            Assert.Equal("direction_none", reason);
        }

        // ── Config validation ──

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.5)]
        [InlineData(1.5)]
        public void Constructor_Rejects_Invalid_TpFraction(double fraction)
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                new RiskManager(new RiskManagerConfig
                {
                    RiskPercent = 0.5, SlAtrMultiple = 2.0,
                    MinTpToSlRatio = 0.5, TpFraction = fraction
                }));
        }
    }
}
