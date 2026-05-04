using System;
using PearlrockBots.WeeklyMeanReversion.Core;

namespace PearlrockBots.WeeklyMeanReversion.Tests.Helpers
{
    public sealed class FakeSymbol : ISymbol
    {
        public string Name { get; set; } = "FAKE";
        public double PipSize { get; set; } = 0.0001;
        public double VolumeInUnitsMin { get; set; } = 1000;
        public double VolumeInUnitsMax { get; set; } = 1_000_000_000;
        public double Bid { get; set; }
        public double Ask { get; set; }
        public double Spread => Ask - Bid;

        /// <summary>Defaults: equity * (riskPct/100) / (slPips * 0.1)  (USD per pip ≈ 0.1).</summary>
        public Func<double, double, double, double> VolumeForRiskFn { get; set; } =
            (equity, riskPct, slPips) =>
            {
                double riskUsd = equity * (riskPct / 100.0);
                double usdPerPipPerUnit = 0.0001;  // approx for a 5-decimal FX pair
                if (slPips <= 0) return 0;
                return riskUsd / (slPips * usdPerPipPerUnit);
            };

        /// <summary>Defaults: round down to nearest VolumeInUnitsMin step.</summary>
        public Func<double, double> NormalizeVolumeFn { get; set; }

        public FakeSymbol()
        {
            NormalizeVolumeFn = u => Math.Floor(u / VolumeInUnitsMin) * VolumeInUnitsMin;
        }

        public double VolumeForRisk(double equity, double riskPercent, double slPips)
            => VolumeForRiskFn(equity, riskPercent, slPips);

        public double NormalizeVolume(double units) => NormalizeVolumeFn(units);
    }
}
