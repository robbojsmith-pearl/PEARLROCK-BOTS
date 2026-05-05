using System;

namespace PearlrockBots.SqueezeBreakout.Core
{
    /// <summary>
    /// Wall-clock source. Tests inject a fake clock; production uses cAlgo's Server.Time.
    /// </summary>
    public interface IClock
    {
        /// <summary>Current time in UTC. Kind = Utc.</summary>
        DateTime UtcNow { get; }
    }

    /// <summary>
    /// Read-only view of a bar series. Convention: closed bars only.
    ///   Count = number of closed bars accessible.
    ///   barsAgo: 1 = newest closed; Count = oldest accessible.
    /// </summary>
    public interface IBarSeries
    {
        int Count { get; }
        double Open(int barsAgo);
        double High(int barsAgo);
        double Low(int barsAgo);
        double Close(int barsAgo);
        DateTime OpenTime(int barsAgo);
    }

    /// <summary>Symbol metadata + sizing helpers.</summary>
    public interface ISymbol
    {
        string Name { get; }
        double PipSize { get; }
        double VolumeInUnitsMin { get; }
        double VolumeInUnitsMax { get; }
        double Bid { get; }
        double Ask { get; }
        double Spread { get; }
        double NormalizeVolume(double units);
    }
}
