using System;
using PearlrockBots.SqueezeBreakout.Core;

namespace PearlrockBots.SqueezeBreakout.Tests.Helpers
{
    public sealed class FakeSymbol : ISymbol
    {
        public string Name { get; set; } = "XAUUSD";
        public double PipSize { get; set; } = 0.01;          // gold quoted to cents
        public double VolumeInUnitsMin { get; set; } = 0.01;  // 0.01 oz min
        public double VolumeInUnitsMax { get; set; } = 1_000_000;
        public double Bid { get; set; }
        public double Ask { get; set; }
        public double Spread => Ask - Bid;

        public Func<double, double> NormalizeVolumeFn { get; set; }

        public FakeSymbol()
        {
            // Round DOWN to nearest VolumeInUnitsMin step.
            NormalizeVolumeFn = u => Math.Floor(u / VolumeInUnitsMin) * VolumeInUnitsMin;
        }

        public double NormalizeVolume(double units) => NormalizeVolumeFn(units);
    }
}
