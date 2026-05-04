using PearlrockBots.WeeklyMeanReversion.Core;
using Xunit;

namespace PearlrockBots.WeeklyMeanReversion.Tests
{
    public class SignalEngineTests
    {
        private const double Pip = 0.0001;
        private const double MO = 1.2000;          // Monday open
        private const double D1Atr = 0.0050;       // 50 pips D1 ATR

        private SignalEngine New(SignalEngineConfig cfg = null)
        {
            cfg ??= new SignalEngineConfig
            {
                DeviationAtrMult = 1.0,
                ConfirmationBars = 2,
                AllowLongs = true,
                AllowShorts = true,
                ResetBufferPips = 0
            };
            return new SignalEngine(cfg);
        }

        private static SignalEngineInput Tick(double last, double prev) =>
            new SignalEngineInput(MO, last, prev, D1Atr, Pip);

        // ── Idle phase ───────────────────────────────────────────────

        [Fact]
        public void Idle_Below_Threshold_Stays_Idle()
        {
            var e = New();
            var d = e.Evaluate(Tick(1.2030, 1.2029));  // 30 pips below 50 pip threshold
            Assert.False(d.ShouldEnter);
            Assert.Equal(SignalPhase.Idle, e.Phase);
        }

        [Fact]
        public void Idle_Above_Threshold_Long_Means_Price_Below_MO()
        {
            // Price below Monday open by >= threshold → expect LONG (mean reversion up).
            var e = New();
            e.Evaluate(Tick(1.1949, 1.1948));  // 51 pips below
            Assert.Equal(SignalPhase.DeviationDetected, e.Phase);
            Assert.Equal(TradeDirection.Long, e.PendingDirection);
        }

        [Fact]
        public void Idle_Above_Threshold_Short_Means_Price_Above_MO()
        {
            var e = New();
            e.Evaluate(Tick(1.2051, 1.2052));  // 51 pips above
            Assert.Equal(SignalPhase.DeviationDetected, e.Phase);
            Assert.Equal(TradeDirection.Short, e.PendingDirection);
        }

        // ── Direction filter ─────────────────────────────────────────

        [Fact]
        public void Long_Signal_Skipped_When_Longs_Disabled()
        {
            var e = New(new SignalEngineConfig
            {
                DeviationAtrMult = 1.0,
                ConfirmationBars = 2,
                AllowLongs = false,
                AllowShorts = true
            });
            e.Evaluate(Tick(1.1949, 1.1948));
            Assert.Equal(SignalPhase.Idle, e.Phase);
            Assert.Equal(TradeDirection.None, e.PendingDirection);
        }

        // ── Confirmation ─────────────────────────────────────────────

        [Fact]
        public void Two_Bars_Toward_Mean_Fires_Long_Entry()
        {
            var e = New();
            // 1: deviation reached, short the mean? No, deviation < 0 → LONG
            e.Evaluate(Tick(1.1949, 1.1948));
            Assert.Equal(SignalPhase.DeviationDetected, e.Phase);

            // 2: bar closes higher than prev (toward MO) → confirm 1/2
            var d2 = e.Evaluate(Tick(1.1955, 1.1949));
            Assert.False(d2.ShouldEnter);
            Assert.Equal(SignalPhase.Confirming, e.Phase);
            Assert.Equal(1, e.ConfirmationCount);

            // 3: another higher close → confirm 2/2 → fire entry
            var d3 = e.Evaluate(Tick(1.1962, 1.1955));
            Assert.True(d3.ShouldEnter);
            Assert.Equal(TradeDirection.Long, d3.Direction);
            // Engine resets after firing
            Assert.Equal(SignalPhase.Idle, e.Phase);
        }

        [Fact]
        public void Confirmation_Broken_By_Wrong_Direction_Resets_Count()
        {
            var e = New();
            e.Evaluate(Tick(1.1949, 1.1948));        // deviation
            e.Evaluate(Tick(1.1955, 1.1949));        // confirm 1
            Assert.Equal(1, e.ConfirmationCount);

            // Bar closes back DOWN — confirmation breaks, count resets to 0.
            e.Evaluate(Tick(1.1950, 1.1955));
            Assert.Equal(0, e.ConfirmationCount);
            Assert.Equal(SignalPhase.DeviationDetected, e.Phase);
        }

        // ── Reset on close back through Monday open ─────────────────

        [Fact]
        public void Long_Setup_Resets_When_Close_Back_Through_MO_With_No_Buffer()
        {
            var e = New();
            e.Evaluate(Tick(1.1949, 1.1948));        // long setup armed
            e.Evaluate(Tick(1.2010, 1.1949));        // closes well above MO (10 pips)
            // ResetBufferPips=0 → any close above MO resets a long setup.
            Assert.Equal(SignalPhase.Idle, e.Phase);
            Assert.Equal(TradeDirection.None, e.PendingDirection);
        }

        [Fact]
        public void Reset_Buffer_Suppresses_Wick_Noise()
        {
            // With a 5-pip buffer, a close 3 pips above MO should NOT reset a long setup.
            var e = New(new SignalEngineConfig
            {
                DeviationAtrMult = 1.0,
                ConfirmationBars = 2,
                AllowLongs = true,
                AllowShorts = true,
                ResetBufferPips = 5
            });
            e.Evaluate(Tick(1.1949, 1.1948));        // long setup armed
            e.Evaluate(Tick(1.2003, 1.1949));        // 3 pips above MO — within buffer
            // Setup must still be alive — reset buffer not breached.
            Assert.NotEqual(SignalPhase.Idle, e.Phase);
            Assert.Equal(TradeDirection.Long, e.PendingDirection);
        }

        // ── Manual reset ─────────────────────────────────────────────

        [Fact]
        public void Reset_Returns_To_Idle()
        {
            var e = New();
            e.Evaluate(Tick(1.1949, 1.1948));
            e.Reset();
            Assert.Equal(SignalPhase.Idle, e.Phase);
            Assert.Equal(0, e.ConfirmationCount);
            Assert.Equal(TradeDirection.None, e.PendingDirection);
        }
    }
}
