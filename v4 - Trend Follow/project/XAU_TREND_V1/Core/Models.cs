using System;

namespace PearlrockBots.TrendFollow.Core
{
    /// <summary>Trade direction. Note: integer values match LONG = +1, SHORT = -1
    /// for ergonomic math (e.g. signed distances). XAU_TREND_V1 is long-only by
    /// design (see EntrySignal) but Side keeps the Short case for completeness
    /// and to stay consistent with the rest of the Pearlrock codebase.</summary>
    public enum Side
    {
        None  =  0,
        Long  =  1,
        Short = -1
    }

    /// <summary>Decision returned by EntrySignal.Evaluate.</summary>
    public readonly struct EntryDecision
    {
        public Side Direction { get; }
        public bool ShouldEnter { get; }
        public string Reason { get; }

        private EntryDecision(Side dir, bool enter, string reason)
        {
            Direction = dir; ShouldEnter = enter; Reason = reason;
        }

        public static EntryDecision NoOp(string reason = "no-op") =>
            new EntryDecision(Side.None, false, reason);

        public static EntryDecision Enter(Side dir, string reason) =>
            new EntryDecision(dir, true, reason);
    }

    /// <summary>Decision returned by exit checks (trail, etc.).</summary>
    public readonly struct ExitDecision
    {
        public bool ShouldExit { get; }
        public string Reason { get; }

        private ExitDecision(bool exit, string reason)
        {
            ShouldExit = exit; Reason = reason;
        }

        public static ExitDecision Stay() => new ExitDecision(false, "stay");
        public static ExitDecision Exit(string reason) => new ExitDecision(true, reason);
    }

    public readonly struct DayFilter
    {
        public bool Enabled { get; }
        public bool Monday { get; } public bool Tuesday { get; }
        public bool Wednesday { get; } public bool Thursday { get; } public bool Friday { get; }

        public DayFilter(bool enabled, bool mo, bool tu, bool we, bool th, bool fr)
        {
            Enabled = enabled;
            Monday = mo; Tuesday = tu; Wednesday = we; Thursday = th; Friday = fr;
        }
    }
}
