using System;

namespace PearlrockBots.WeeklyMeanReversion.Core
{
    /// <summary>
    /// Pure SL/TP/volume calculator. Returns null when the setup should be skipped.
    ///
    /// TP placement (v2.1):
    ///   target_distance = (entry_to_MO_distance) * TpFraction - TpBufferPips * pipSize
    /// With TpFraction = 1.0 this reduces to v2.0 behaviour (TP at Monday open
    /// minus buffer). TpFraction = 0.5 puts TP halfway between entry and Monday
    /// open — for testing whether partial mean reversion exists when full does not.
    /// </summary>
    public class RiskManager
    {
        private readonly RiskManagerConfig _cfg;

        public RiskManager(RiskManagerConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (cfg.RiskPercent <= 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.RiskPercent), "must be > 0");
            if (cfg.SlAtrMultiple <= 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.SlAtrMultiple), "must be > 0");
            if (cfg.MinTpToSlRatio < 0)
                throw new ArgumentOutOfRangeException(nameof(cfg.MinTpToSlRatio), "must be >= 0");
            if (cfg.TpFraction <= 0 || cfg.TpFraction > 1)
                throw new ArgumentOutOfRangeException(nameof(cfg.TpFraction), "must be in (0, 1]");
            _cfg = cfg;
        }

        public (EntrySpec? entry, string rejectReason) BuildEntry(EntryContext ctx)
        {
            if (ctx.Direction == TradeDirection.None) return (null, "direction_none");
            if (ctx.M5Atr <= 0) return (null, "m5_atr_not_ready");
            if (ctx.PipSize <= 0) return (null, "pip_size_invalid");
            if (ctx.Equity <= 0) return (null, "equity_invalid");

            double slDistance = ctx.M5Atr * _cfg.SlAtrMultiple;
            double slPips = slDistance / ctx.PipSize;

            if (_cfg.MaxSlPips > 0 && slPips > _cfg.MaxSlPips)
                return (null, $"sl_too_wide_{slPips:F1}p>{_cfg.MaxSlPips}p");

            double currentPrice = ctx.Direction == TradeDirection.Long ? ctx.Ask : ctx.Bid;

            // Distance from current price to Monday open, signed for direction.
            double distanceToMO = ctx.Direction == TradeDirection.Long
                ? ctx.MondayOpen - currentPrice
                : currentPrice - ctx.MondayOpen;

            // If price has already crossed Monday open by entry time (rare with the
            // signal engine's reset rule, but possible on fast prints), bail.
            if (distanceToMO <= 0)
                return (null, $"price_already_at_or_past_mo_dist={distanceToMO:F5}");

            double tpBuffer = _cfg.TpBufferPips * ctx.PipSize;
            double tpDistance = (distanceToMO * _cfg.TpFraction) - tpBuffer;
            if (tpDistance <= 0)
                return (null, $"tp_distance_non_positive_after_buffer_{tpDistance:F5}");

            double tpPips = tpDistance / ctx.PipSize;

            if (tpPips < slPips * _cfg.MinTpToSlRatio)
                return (null, $"tp_too_small_{tpPips:F1}p<{slPips * _cfg.MinTpToSlRatio:F1}p");

            double rawVolume = ctx.VolumeForRisk(ctx.Equity, _cfg.RiskPercent, slPips);
            double volume = ctx.NormalizeVolume(rawVolume);

            if (volume < ctx.VolumeInUnitsMin)
                return (null, $"volume_below_min_{volume}<{ctx.VolumeInUnitsMin}");
            if (volume > ctx.VolumeInUnitsMax)
                volume = ctx.VolumeInUnitsMax;

            return (new EntrySpec(
                ctx.Direction,
                volume,
                slPips,
                tpPips,
                $"sl={slPips:F1}p tp={tpPips:F1}p frac={_cfg.TpFraction:F2} vol={volume:F0}"
            ), null);
        }
    }

    public class RiskManagerConfig
    {
        /// <summary>Risk per trade as a percentage of equity (e.g. 0.5 for 0.5%).</summary>
        public double RiskPercent { get; set; } = 0.5;
        /// <summary>SL distance = M5 ATR x this multiple.</summary>
        public double SlAtrMultiple { get; set; } = 2.0;
        /// <summary>Hard cap on SL in pips (0 = no cap).</summary>
        public double MaxSlPips { get; set; } = 0;
        /// <summary>TP buffer in pips, subtracted from the partial-reversion target.</summary>
        public double TpBufferPips { get; set; } = 5;
        /// <summary>Reject entries where TP/SL ratio is below this. v1 used 0.5.</summary>
        public double MinTpToSlRatio { get; set; } = 0.5;
        /// <summary>
        /// Fraction of the entry-to-Monday-open distance to use as the TP target.
        /// 1.0 = full reversion to Monday open (v2.0 behaviour).
        /// 0.5 = halfway back. Lower values = more frequent winners, smaller wins.
        /// </summary>
        public double TpFraction { get; set; } = 1.0;
    }
}
