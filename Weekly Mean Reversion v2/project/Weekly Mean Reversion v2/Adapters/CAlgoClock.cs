using System;
using cAlgo.API;
using PearlrockBots.WeeklyMeanReversion.Core;

namespace PearlrockBots.WeeklyMeanReversion.Adapters
{
    /// <summary>
    /// Wraps cAlgo's <see cref="Robot.Server"/>.Time as an <see cref="IClock"/>.
    /// Server.Time is UTC in cAlgo.
    /// </summary>
    public sealed class CAlgoClock : IClock
    {
        private readonly Func<DateTime> _serverTimeProvider;

        public CAlgoClock(Func<DateTime> serverTimeProvider)
        {
            _serverTimeProvider = serverTimeProvider
                ?? throw new ArgumentNullException(nameof(serverTimeProvider));
        }

        public DateTime UtcNow
        {
            get
            {
                var t = _serverTimeProvider();
                return t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);
            }
        }
    }
}
