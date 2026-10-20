using System;
using System.Linq;
using System.Threading.Tasks;

using log4net;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Auth;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Server.ClientChannel;
using ACE.Server.WorldObjects;

namespace ACE.Server.Market
{
    /// <summary>
    /// The market window's actions on the in-band server channel, for the LegACEy Market client. Every action needs the market station in the
    /// Marketplace hub, and is refused with a plain message while market_enabled is off. Bodies are written with ChannelWire.
    /// </summary>
    public static class MarketChannelActions
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string Account = "market.account";

        /// <summary>
        /// The station every market action requires: the market station object in the Marketplace hub
        /// </summary>
        public const string Station = "market";

        private static readonly Lazy<MarketDatabase> database = new Lazy<MarketDatabase>(() => new MarketDatabase(ACE.Common.ConfigManager.Config.MySql));

        /// <summary>
        /// The market's plain contexts, as the Market API holds them, for the market actions that call the shared catalog and purchase rules
        /// </summary>
        internal static MarketDatabase Database => database.Value;

        public static void Register()
        {
            ServerChannel.Register(Account, HandleAccount, Station);
        }

        /// <summary>
        /// The account's summary: balance, frozen (banned), paused, the Vault's count and capacity, the active listings and their cap, and the character
        /// the window acts for. Body: balance (int64), frozen (byte), paused (byte), Vault count, Vault capacity, listing count, listing cap (int32 each), character (string).
        /// </summary>
        private static void HandleAccount(ChannelContext context)
        {
            if (!Open(context))
                return;

            var accountId = context.Player.Character.AccountId;
            var characterName = context.Player.Name;

            // database reads: off the world thread, replying when done
            Task.Run(() =>
            {
                try
                {
                    var summary = ReadSummary(accountId);
                    context.Reply(ChannelWire.Body(w =>
                    {
                        w.Write(summary.Balance);
                        w.Write((byte)(summary.Frozen ? 1 : 0));
                        w.Write((byte)(summary.Paused ? 1 : 0));
                        w.Write(summary.VaultCount);
                        w.Write(summary.VaultCapacity);
                        w.Write(summary.ListingCount);
                        w.Write(summary.ListingCap);
                        ChannelWire.WriteString(w, characterName);
                    }));
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] Channel account summary for {characterName} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "Your market account could not be read.");
                }
            });
        }

        /// <summary>
        /// Whether the market is open for the window's actions: the Vault is available and market_enabled is on. Otherwise the action is refused
        /// with the plain message, and this returns false.
        /// </summary>
        public static bool Open(ChannelContext context)
        {
            if (!Vault.Available)
            {
                context.Fail(ChannelStatus.Unavailable, VaultMessages.For(VaultOutcome.NotAvailable, null));
                return false;
            }

            if (!Vault.MarketEnabled)
            {
                context.Fail(ChannelStatus.Unavailable, VaultMessages.For(VaultOutcome.MarketClosed, null));
                return false;
            }

            return true;
        }

        private readonly record struct AccountSummary(long Balance, bool Frozen, bool Paused, int VaultCount, int VaultCapacity, int ListingCount, int ListingCap);

        /// <summary>
        /// True if the account is banned now, read from the auth database: the game login's rule, used by every market action that moves value
        /// </summary>
        internal static bool IsBanned(uint accountId)
        {
            using var auth = new AuthDbContext();
            var account = auth.Account.FirstOrDefault(a => a.AccountId == accountId);
            return account != null && account.IsBanned(DateTime.UtcNow);
        }

        private static AccountSummary ReadSummary(uint accountId)
        {
            var frozen = IsBanned(accountId);

            using var shard = new ShardDbContext();

            // a listing past its lifetime is expired before it is counted, as every listing read does
            ListingStore.ExpireOverdue(shard, DateTime.UtcNow);

            return new AccountSummary(
                Ledger.GetBalance(shard, accountId),
                frozen,
                MarketPause.IsPaused(shard),
                VaultStore.Count(accountId),
                (int)MarketSettings.Get(shard, MarketSettings.VaultSize),
                shard.MarketListings.Count(l => l.SellerAccountId == accountId && l.Status == ListingStatus.Active),
                (int)MarketSettings.Get(shard, MarketSettings.ActiveListings));
        }
    }
}
