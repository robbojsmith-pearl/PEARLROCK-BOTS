using System;

namespace PearlrockBots.WeeklyMeanReversion.Core
{
    public enum TradeDirection
    {
        None = 0,
        Long = 1,
        Short = -1
    }

    /// <summary>State of the deviation-and-confirm signal engine.</summary>
    public enum SignalPhase
    {
        /// <summary>Waiting for price to deviate from Monday open by threshold.</summary>
        Idle,
        /// <summary>Threshold met; waiting for confirmation bars.</summary>
        DeviationDetected,
        /// <summary>Counting consecutive bars closing toward Monday open.</summary>
        Confirming
    }

    /// <summary>Pure decision returned by SignalEngine.Evaluate.</summary>
    public readonly struct SignalDecision
    {
        public TradeDirection Direction { get; }
        public bool ShouldEnter { get; }
        /// <summary>Human-readable reason — used for logging only, never branched on.</summary>
        public string Reason { get; }

        private SignalDecision(TradeDirection dir, bool enter, string reason)
        {
            Direction = dir;
            ShouldEnter = enter;
            Reason = reason;
        }

        public static SignalDecision NoOp() =>
            new SignalDecision(TradeDirection.None, false, "no-op");

        public static SignalDecision Wait(string reason) =>
            new SignalDecision(TradeDirection.None, false, reason);

        public static SignalDecision Enter(TradeDirection dir, string reason) =>
            new SignalDecision(dir, true, reason);
    }

    /// <summary>
    /// Inputs to SignalEngine.Evaluate at a given OnBar tick. Pure data — no behaviour.
    /// </summary>
    public readonly struct SignalEngineInput
    {
        public double MondayOpen { get; }
        public double LastClose { get; }
        public double PrevClose { get; }
        public double D1Atr { get; }
        public double PipSize { get; }

        public SignalEngineInput(
            double mondayOpen, double lastClose, double prevClose, double d1Atr, double pipSize)
        {
            MondayOpen = mondayOpen;
            LastClose = lastClose;
            PrevClose = prevClose;
            D1Atr = d1Atr;
            PipSize = pipSize;
        }
    }

    /// <summary>
    /// Result of RiskManager.BuildEntry — what to actually order, or null if blocked.
    /// </summary>
    public readonly struct EntrySpec
    {
        public TradeDirection Direction { get; }
        public double Volume { get; }
        public double SlPips { get; }
        public double TpPips { get; }
        public string Note { get; }

        public EntrySpec(TradeDirection dir, double volume, double slPips, double tpPips, string note)
        {
            Direction = dir;
            Volume = volume;
            SlPips = slPips;
            TpPips = tpPips;
            Note = note;
        }
    }

    /// <summary>Inputs to RiskManager.BuildEntry. Pure data.</summary>
    public readonly struct EntryContext
    {
        public TradeDirection Direction { get; }
        public double MondayOpen { get; }
        public double M5Atr { get; }
        public double Equity { get; }
        public double Bid { get; }
        public double Ask { get; }
        public double PipSize { get; }
        public double VolumeInUnitsMin { get; }
        public double VolumeInUnitsMax { get; }
        public Func<double, double, double, double> VolumeForRisk { get; }
        public Func<double, double> NormalizeVolume { get; }

        public EntryContext(
            TradeDirection direction,
            double mondayOpen,
            double m5Atr,
            double equity,
            double bid,
            double ask,
            double pipSize,
            double volumeInUnitsMin,
            double volumeInUnitsMax,
            Func<double, double, double, double> volumeForRisk,
            Func<double, double> normalizeVolume)
        {
            Direction = direction;
            MondayOpen = mondayOpen;
            M5Atr = m5Atr;
            Equity = equity;
            Bid = bid;
            Ask = ask;
            PipSize = pipSize;
            VolumeInUnitsMin = volumeInUnitsMin;
            VolumeInUnitsMax = volumeInUnitsMax;
            VolumeForRisk = volumeForRisk;
            NormalizeVolume = normalizeVolume;
        }
    }
}
