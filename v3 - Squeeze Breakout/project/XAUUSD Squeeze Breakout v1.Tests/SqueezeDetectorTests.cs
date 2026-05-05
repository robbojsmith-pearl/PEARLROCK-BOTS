using System;
using PearlrockBots.SqueezeBreakout.Core;
using Xunit;

namespace PearlrockBots.SqueezeBreakout.Tests
{
    public class SqueezeDetectorTests
    {
        // Helper: standard valid bands with squeeze on
        private void TickSqueezeOn(SqueezeDetector d) =>
            d.Update(bbUpper: 1.05, bbLower: 0.95, kcUpper: 1.10, kcLower: 0.90);

        // Helper: bands with squeeze off (BB outside KC on at least one side)
        private void TickSqueezeOff(SqueezeDetector d) =>
            d.Update(bbUpper: 1.15, bbLower: 0.85, kcUpper: 1.10, kcLower: 0.90);

        [Fact]
        public void New_Detector_Has_No_History()
        {
            var d = new SqueezeDetector();
            Assert.False(d.HasHistory);
            Assert.False(d.IsOn);
            Assert.False(d.JustReleased);
        }

        [Fact]
        public void BB_Inside_KC_Sets_IsOn()
        {
            var d = new SqueezeDetector();
            TickSqueezeOn(d);
            Assert.True(d.IsOn);
            Assert.False(d.JustReleased);  // no prior history
        }

        [Fact]
        public void BB_Outside_KC_Sets_IsOff()
        {
            var d = new SqueezeDetector();
            TickSqueezeOff(d);
            Assert.False(d.IsOn);
            Assert.False(d.JustReleased);
        }

        [Fact]
        public void On_Then_Off_Triggers_JustReleased()
        {
            var d = new SqueezeDetector();
            TickSqueezeOn(d);
            TickSqueezeOff(d);
            Assert.True(d.JustReleased);
            Assert.False(d.IsOn);
        }

        [Fact]
        public void Off_Then_Off_Does_Not_Trigger_JustReleased()
        {
            var d = new SqueezeDetector();
            TickSqueezeOff(d);
            TickSqueezeOff(d);
            Assert.False(d.JustReleased);
        }

        [Fact]
        public void On_Then_On_Does_Not_Trigger_JustReleased()
        {
            var d = new SqueezeDetector();
            TickSqueezeOn(d);
            TickSqueezeOn(d);
            Assert.False(d.JustReleased);
            Assert.True(d.IsOn);
        }

        [Fact]
        public void JustReleased_Only_True_For_One_Bar()
        {
            var d = new SqueezeDetector();
            TickSqueezeOn(d);
            TickSqueezeOff(d);   // released here
            Assert.True(d.JustReleased);
            TickSqueezeOff(d);   // next bar: no longer "just" released
            Assert.False(d.JustReleased);
        }

        [Fact]
        public void Reset_Clears_State()
        {
            var d = new SqueezeDetector();
            TickSqueezeOn(d);
            d.Reset();
            Assert.False(d.HasHistory);
            Assert.False(d.IsOn);
            Assert.False(d.JustReleased);
        }

        // Edge cases — exact equality on either band means BB is NOT strictly
        // inside KC, so squeeze should be off.
        [Fact]
        public void Exactly_Touching_Lower_Band_Is_Not_On()
        {
            var d = new SqueezeDetector();
            d.Update(bbUpper: 1.05, bbLower: 0.90, kcUpper: 1.10, kcLower: 0.90); // touching
            Assert.False(d.IsOn);
        }

        [Fact]
        public void Exactly_Touching_Upper_Band_Is_Not_On()
        {
            var d = new SqueezeDetector();
            d.Update(bbUpper: 1.10, bbLower: 0.95, kcUpper: 1.10, kcLower: 0.90); // touching
            Assert.False(d.IsOn);
        }

        // Validation
        [Fact]
        public void Rejects_Inverted_BB() =>
            Assert.Throws<ArgumentException>(() =>
                new SqueezeDetector().Update(bbUpper: 0.95, bbLower: 1.05, kcUpper: 1.10, kcLower: 0.90));

        [Fact]
        public void Rejects_Inverted_KC() =>
            Assert.Throws<ArgumentException>(() =>
                new SqueezeDetector().Update(bbUpper: 1.05, bbLower: 0.95, kcUpper: 0.90, kcLower: 1.10));

        [Fact]
        public void Rejects_NaN_Input() =>
            Assert.Throws<ArgumentException>(() =>
                new SqueezeDetector().Update(bbUpper: double.NaN, bbLower: 0.95, kcUpper: 1.10, kcLower: 0.90));
    }
}
