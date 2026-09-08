using System;

namespace PearlrockBots.TrendFollow.Core
{
    /// <summary>
    /// Position sizer for risk-based entries, with an optional continuous
    /// sizing multiplier applied on top (this bot uses Efficiency.SizingFactor
    /// for that — see Core/Efficiency.cs).
    ///
    /// volume_raw    = ((equity x risk%/100) / stop_distance_in_price) x sizingFactor
    /// max_volume    = (equity x (max_leverage - leverage_buffer)) / current_price
    /// volume_capped = min(volume_raw, max_volume)
    /// volume_final  = symbol.NormalizeVolume(volume_capped)
    ///
    /// Returns null + reason when:
    ///   - inputs invalid (non-positive equity / stop / price)
    ///   - normalized volume below symbol minimum
    /// </summary>
    public class RiskSizer
    {
        private readonly RiskSizerConfig _cfg;

        public RiskSizer(RiskSizerConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (cfg.RiskPercent <= 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.RiskPercent), "must be > 0");
            if (cfg.MaxLeverage <= 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.MaxLeverage), "must be > 0");
            if (cfg.LeverageBuffer < 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.LeverageBuffer), "must be >= 0");
            _cfg = cfg;
        }

        public (double? volume, string rejectReason) Calculate(SizingInput x)
        {
            if (x.StopDistance <= 0) return (null, "stop_distance_invalid");
            if (x.Equity <= 0) return (null, "equity_invalid");
            if (x.CurrentPrice <= 0) return (null, "price_invalid");
            if (x.SizingFactor <= 0) return (null, "sizing_factor_invalid");
            if (x.NormalizeVolume == null) return (null, "normalizer_missing");

            double riskDollars = x.Equity * (_cfg.RiskPercent / 100.0);
            double rawVolume = (riskDollars / x.StopDistance) * x.SizingFactor;

            double effectiveLeverage = Math.Max(0, _cfg.MaxLeverage - _cfg.LeverageBuffer);
            double maxLeverageVolume = (x.Equity * effectiveLeverage) / x.CurrentPrice;

            double cappedVolume = Math.Min(rawVolume, maxLeverageVolume);
            double normalized = x.NormalizeVolume(cappedVolume);

            if (normalized < x.MinVolume)
                return (null, $"volume_below_min_{normalized}<{x.MinVolume}");
            if (normalized > x.MaxVolume) normalized = x.MaxVolume;

            return (normalized, null);
        }
    }

    public readonly struct SizingInput
    {
        /// <summary>Account equity at the moment of entry (currency).</summary>
        public double Equity { get; }
        /// <summary>Stop loss distance from entry, in PRICE units (not pips).</summary>
        public double StopDistance { get; }
        /// <summary>Current price (entry approximation).</summary>
        public double CurrentPrice { get; }
        public double MinVolume { get; }
        public double MaxVolume { get; }
        /// <summary>Continuous multiplier on the base risk volume (e.g. from
        /// Efficiency.SizingFactor). Pass 1.0 for plain fixed-fractional sizing.</summary>
        public double SizingFactor { get; }
        public Func<double, double> NormalizeVolume { get; }

        public SizingInput(double equity, double stopDistance, double currentPrice,
                           double minVolume, double maxVolume, double sizingFactor,
                           Func<double, double> normalizeVolume)
        {
            Equity = equity;
            StopDistance = stopDistance;
            CurrentPrice = currentPrice;
            MinVolume = minVolume;
            MaxVolume = maxVolume;
            SizingFactor = sizingFactor;
            NormalizeVolume = normalizeVolume;
        }
    }

    public class RiskSizerConfig
    {
        public double RiskPercent { get; set; } = 1.0;
        public double MaxLeverage { get; set; } = 10.0;
        public double LeverageBuffer { get; set; } = 0.2;
    }
}
