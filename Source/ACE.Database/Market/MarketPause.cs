using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;

namespace ACE.Database.Market
{
    /// <param name="Reason">why and when it was paused, or when and by whom it was resumed; null if it never was</param>
    public sealed record MarketPauseState(bool Paused, string Reason);

    /// <summary>
    /// The server-wide market pause that a failed ledger audit sets and /market resume lifts. While paused, purchases and MMD withdrawals are refused;
    /// browsing, listing and deposits go on. It is a row in the shard's config_properties_boolean, so the game and the Market API read the same value.
    /// The key is not one of the game's PropertyManager defaults, so /modifybool can't change it: only the audit and /market resume do.
    /// </summary>
    public static class MarketPause
    {
        public const string Key = "market_paused";

        public static bool IsPaused(ShardDbContext context) => Get(context).Paused;

        public static MarketPauseState Get(ShardDbContext context)
        {
            var row = context.ConfigPropertiesBoolean.AsNoTracking().FirstOrDefault(r => r.Key == Key);

            return new MarketPauseState(row?.Value ?? false, row?.Description);
        }

        public static void Pause(ShardDbContext context, string reason, DateTime now) => Set(context, true, $"Paused {now:yyyy-MM-dd HH:mm:ss} UTC: {reason}");

        /// <param name="by">who lifted it, for the record</param>
        public static void Resume(ShardDbContext context, string by, DateTime now) => Set(context, false, $"Resumed {now:yyyy-MM-dd HH:mm:ss} UTC by {by}");

        /// <summary>
        /// One statement, so two writers can't both find the row missing and collide on its key
        /// </summary>
        private static void Set(ShardDbContext context, bool paused, string description)
        {
            context.Database.ExecuteSqlRaw(
                "INSERT INTO config_properties_boolean (`key`, `value`, `description`) VALUES ({0}, {1}, {2}) ON DUPLICATE KEY UPDATE `value` = VALUES(`value`), `description` = VALUES(`description`)",
                Key, paused, description);
        }
    }
}
