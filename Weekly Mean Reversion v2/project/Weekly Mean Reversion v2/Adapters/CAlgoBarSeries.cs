using System;
using cAlgo.API;
using PearlrockBots.WeeklyMeanReversion.Core;

namespace PearlrockBots.WeeklyMeanReversion.Adapters
{
    /// <summary>
    /// Wraps a cAlgo <see cref="Bars"/> as an <see cref="IBarSeries"/>.
    ///
    /// Convention reminder:
    ///   - cAlgo's <c>Bars.Count</c> includes the currently-forming bar at the end.
    ///   - cAlgo's <c>OpenPrices.Last(0)</c> = forming bar; <c>Last(1)</c> = newest closed.
    ///   - We expose <c>Count</c> as <c>Bars.Count - 1</c> so Core code only ever
    ///     iterates over CLOSED bars (barsAgo 1..Count).
    /// </summary>
    public sealed class CAlgoBarSeries : IBarSeries
    {
        private readonly Bars _bars;

        public CAlgoBarSeries(Bars bars)
        {
            _bars = bars ?? throw new ArgumentNullException(nameof(bars));
        }

        public int Count => Math.Max(0, _bars.Count - 1);

        public double Open(int barsAgo)  => _bars.OpenPrices.Last(barsAgo);
        public double High(int barsAgo)  => _bars.HighPrices.Last(barsAgo);
        public double Low(int barsAgo)   => _bars.LowPrices.Last(barsAgo);
        public double Close(int barsAgo) => _bars.ClosePr