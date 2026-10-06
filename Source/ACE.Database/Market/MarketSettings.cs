using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;

namespace ACE.Database.Market
{
    public sealed class MarketSetting
    {
        /// <summary>
        /// The key in config_properties_long
        /// </summary>
        public string Key { get; }

        public long Default { get; }

        public string Description { get; }

        public MarketSetting(string key, long @default, string description)
        {
            Key = key;
            Default = @default;
            Description = description;
        }
    }

    /// <summary>
    /// The market's server settings and their defaults, shared by the game server and the Market API.
    /// Values live in the shard's config_properties_long table; a missing row means the default.
    /// Read the table directly: the Market API has no PropertyManager, and the startup purge runs before it loads.
    /// </summary>
    public static class MarketSettings
    {
        public static readonly MarketSetting VaultSize = new("market_vault_size", 1000, "the most items an account's Vault holds (a stack counts as one)");

        public static readonly MarketSetting ActiveListings = new("market_active_listings", 200, "the most active listings an account can have");

        public static readonly MarketSetting ListingLifetimeDays = new("market_listing_lifetime_days", 14, "the number of days before a listing expires back to the seller's Vault");

        public static readonly MarketSetting PurchasesPerMinute = new("market_purchases_per_minute", 10, "the most purchases an account can make per minute");

        public static readonly MarketSetting SignInAccountFailures = new("market_signin_account_failures", 5, "failed web sign-ins within the account window that lock an account's web sign-in");

        public static readonly MarketSetting SignInAccountWindowMinutes = new("market_signin_account_window_minutes", 15, "the window, in minutes, for counting an account's failed web sign-ins");

        public static readonly MarketSetting SignInAccountLockMinutes = new("market_signin_account_lock_minutes", 15, "the number of minutes an account's web sign-in stays locked");

        public static readonly MarketSetting SignInIpFailures = new("market_signin_ip_failures", 20, "failed web sign-ins within the IP window that block an IP address");

        public static readonly MarketSetting SignInIpWindowMinutes = new("market_signin_ip_window_minutes", 15, "the window, in minutes, for counting an IP address's failed web sign-ins");

        public static readonly MarketSetting SignInIpLockMinutes = new("market_signin_ip_lock_minutes", 15, "the number of minutes a blocked IP address stays blocked");

        public static readonly MarketSetting LinkCodeMinutes = new("market_link_code_minutes", 5, "the number of minutes a /vault link code stays valid");

        public static readonly MarketSetting PluginTokenDays = new("market_plugin_token_days", 90, "the number of days a plugin token lasts, renewed each time it's used");

        public static readonly MarketSetting WebSessionIdleDays = new("market_web_session_idle_days", 14, "the number of days a web session lasts unused; each use renews it (written at most once an hour), never past its absolute lifetime");

        public static readonly MarketSetting WebSessionAbsoluteDays = new("market_web_session_absolute_days", 30, "the number of days after sign-in that a web session ends, however much it's used");

        public static readonly MarketSetting ChannelSeconds = new("vault_channel_seconds", 60, "the number of seconds a /vault deposit or withdrawal channels before it completes");

        public static readonly IReadOnlyList<MarketSetting> All = new[]
        {
            VaultSize,
            ActiveListings,
            ListingLifetimeDays,
            PurchasesPerMinute,
            SignInAccountFailures,
            SignInAccountWindowMinutes,
            SignInAccountLockMinutes,
            SignInIpFailures,
            SignInIpWindowMinutes,
            SignInIpLockMinutes,
            LinkCodeMinutes,
            PluginTokenDays,
            WebSessionIdleDays,
            WebSessionAbsoluteDays,
            ChannelSeconds,
        };

        /// <summary>
        /// The setting's value from the configured shard database, or its default when no row exists
        /// </summary>
        public static long Get(MarketSetting setting)
        {
            using (var context = new ShardDbContext())
                return Get(context, setting);
        }

        /// <summary>
        /// The setting's value through the given shard context, or its default when no row exists
        /// </summary>
        public static long Get(ShardDbContext context, MarketSetting setting)
        {
            var row = context.ConfigPropertiesLong.AsNoTracking().FirstOrDefault(r => r.Key == setting.Key);

            return row?.Value ?? setting.Default;
        }
    }
}
