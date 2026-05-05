using System;
using System.Collections.Generic;
using PearlrockBots.SqueezeBreakout.Core;

namespace PearlrockBots.SqueezeBreakout.Tests.Helpers
{
    /// <summary>In-memory IBarSeries. Bars appended chronologically; barsAgo
    /// indexing applied at query time.</summary>
    public sealed class FakeBarSeries : IBarSeries
    {
        public sealed class Row
        {
            public DateTime UtcOpenTime { get; set; }
            public double Open { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Close { get; set; }
        }

        private readonly List<Row> _bars = new List<Row>();

        public int Count => _bars.Count;

        public void Add(DateTime utcOpenTime, double open, double high, double low, double close)
        {
            var t = utcOpenTime.Kind == DateTimeKind.Utc
                ? utcOpenTime
                : DateTime.SpecifyKind(utcOpenTime, DateTimeKind.Utc);
            _bars.Add(new Row { UtcOpenTime = t, Open = open, High = high, Low = low, Close = close });
        }

        public void AddFlat(DateTime utcOpenTime, double price)
            => Add(utcOpenTime, price, price, price, price);

        private Row At(int barsAgo)
        {
            int idx = _bars.Count - barsAgo;
            if (idx < 0 || idx >= _bars.Count)
                throw new ArgumentOutOfRangeException(nameof(barsAgo),
                    $"barsAgo={barsAgo} out of range for {_bars.Count} bars");
            return _bars[idx];
        }

        public double Open(int barsAgo)  => At(barsAgo).Open;
        public double High(int barsAgo)  => At(barsAgo).High;
        public double Low(int barsAgo)   => At(barsAgo).Low;
        public double Close(int barsAgo) => At(barsAgo).Close;
        public DateTime OpenTime(int barsAgo) => At(barsAgo).UtcOpenTime;
    }
}
