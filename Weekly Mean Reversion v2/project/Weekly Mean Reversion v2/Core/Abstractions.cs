using System;

namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>
    /// Abstraction over a wall-clock source. Tests inject a fake clock; production
    /// uses cAlgo's Server.Time.
    /// </summary>
    public interface IClock
    {
        /// <summary>Current time, expressed in UTC. Kind = Utc.</summary>
        DateTime UtcNow { get; }
    }

    /// <summary>
    /// Read-only view of a bar series. The Core convention covers ONLY closed bars:
    ///   <c>Count</c> = number of closed bars accessible (not including any forming bar).
    ///   barsAgo: <c>1</c> = newest closed; <c>Count</c> = oldest accessible.
    /// The cAlgo adapter subtracts 1 from <c>Bars.Count</c> to hide the forming bar.
    /// </summary>
    public interface IBarSeries
    {
        int Count { get; }
        double Open(int barsAgo);
        double High(int barsAgo);
        double Low(int barsAgo);
        double Close(int barsAgo);
        /// <summary>Open time of the bar, in UTC.</summary>
        DateTime OpenTime(int barsAgo);
    }

    /// <summary>
    /// Symbol metadata + sizing helpers. Mirrors only the bits we need from
    /// cAlgo.API.Symbol so we can fake it in tests.
    /// </summary>
    public interface ISymbol
    {
        string Name { get; }
        double PipSize { get; }
        double VolumeInUnitsMin { get; }
        double VolumeInUnitsMax { get; }
        double Bid { get; }
        double Ask { get; }
        double Spread { get; }

        /// <summary>
        /// Compute volume in units that risks riskPercent% of equity if stopped out
        /// at slPips. Rounds DOWN to the nearest tradable step.
        /// </summary>
        double VolumeForRisk(double equity, double riskPercent, double slPips);

        /// <summary>Round volume DOWN to a tradable step.</summary>
        double NormalizeVolume(double units);
    }
}
