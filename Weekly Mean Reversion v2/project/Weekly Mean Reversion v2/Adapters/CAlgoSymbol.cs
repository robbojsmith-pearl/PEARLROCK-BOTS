using System;
using cAlgo.API;
using cAlgo.API.Internals;
using PearlrockBots.WeeklyMeanReversion.Core;

namespace PearlrockBots.WeeklyMeanReversion.Adapters
{
    /// <summary>
    /// Wraps cAlgo's <see cref="Symbol"/> as the Core <see cref="ISymbol"/>.
    /// </summary>
    public sealed class CAlgoSymbol : ISymbol
    {
        private readonly Symbol _symbol;

        public CAlgoSymbol(Symbol symbol)
        {
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        }

        public string Name              => _symbol.Name;
        public double PipSize           => _symbol.PipSize;
        public double VolumeInUnitsMin  => _symbol.VolumeInUnitsMin;
        public double VolumeInUnitsMax  => _symbol.VolumeInUnitsMax;
        public double Bid               => _symbol.Bid;
        public double Ask               => _symbol.Ask;
        public double Spread            => _symbol.Ask - _symbol.Bid;

        public double VolumeForRisk(double equity, double riskPercent, double slPips)
        {
            // cAlgo's helper rounds to a tradable step DOWN by default.
            return _symbol.VolumeForProportionalRisk(
                ProportionalAmountType.Equity,
                riskPercent,
                slPips,
                RoundingMode.Down);
        }

        public double NormalizeVolume(double units)
        {
            return _symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
        }
    }
}
