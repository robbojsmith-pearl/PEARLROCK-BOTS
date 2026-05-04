using System;
using PearlrockBots.WeeklyMeanReversion.Core;

namespace PearlrockBots.WeeklyMeanReversion.Tests.Helpers
{
    public sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; }

        public FakeClock(DateTime utcNow)
        {
            UtcNow = utcNow.Kind == DateTimeKind.Utc
                ? utcNow
                : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        }

        public void Advance(TimeSpan delta) => UtcNow = UtcNow + delta;
    }
}
