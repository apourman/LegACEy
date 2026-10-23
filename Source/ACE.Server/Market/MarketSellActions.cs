using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using log4net;

using Microsoft.EntityFrameworkCore;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClientChannel;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Market
{
    /// <summary>
    /// The Sell screen's actions on the in-band server channel: the Vault's page, which items can be listed, listing one, delisting one and the
    /// account's listings.
    /// Listing and delisting call the shared listing store, so its rules and the lifetime and cap settings are the portal's. Each action first expires
    /// the listings past their lifetime, as every portal listing read does. A pack item is checked before it moves (the cap, the ban, the channel's
    /// start rules and the deposit's own rules), then deposited instantly, then listed; if the listing is then refused by a race, the item stays in
    /// the Vault and the reply says so. Every action needs the market station and is refused while market_enabled is off.
    /// Bodies are written with ChannelWire. The database reads run off the world thread; the pack and the deposit run on it.
    /// </summary>
    public static class MarketSellActions
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string Sellable = "market.sellable";
        public const string List = "market.list";
        public const string Delist = "market.delist";
        public const string Reprice = "market.reprice";
        public const string Listings = "market.listings";

        /// <summary>
        /// The Vault's page for the Sell screen's Vault tab: vault.list's body and reply, at the market station (vault.list needs the Vault chest's)
        /// </summary>
        public const string VaultPage = "market.vault";

        /// <summary>
        /// The most items one market.sellable names. Must match the Market client's MarketProtocol.SellablePageSize.
        /// </summary>
        public const int SellablePageSize = VaultChannelActions.PageSize;

        /// <summary>
        /// The most listings one market.listings reply holds: the default active cap, so the Active tab shows every one. Must match the Market client's
        /// MarketProtocol.ListingsPageSize.
        /// </summary>
        public const int ListingsPageSize = 200;

        /// <summary>
        /// The tabs market.listings answers: the first byte of its request. Must match MarketProtocol in the Market client.
        /// </summary>
        public const byte ActiveTab = 0;
        public const byte SoldTab = 1;
        public const byte ExpiredTab = 2;

        private const string NotInPackOrVault = "That item is not in your pack or Vault.";
        internal const string InvalidPrice = "Set a whole price of at least 1 MMD.";

        public static void Register()
        {
            ServerChannel.Register(Sellable, HandleSellable, MarketChannelActions.Station);
            ServerChannel.Register(List, HandleList, MarketChannelActions.Station);
            ServerChannel.Register(Delist, HandleDelist, MarketChannelActions.Station);
            ServerChannel.Register(Reprice, HandleReprice, MarketChannelActions.Station);
            ServerChannel.Register(Listings, HandleListings, MarketChannelActions.Station);
            ServerChannel.Register(VaultPage, context => { if (MarketChannelActions.Open(context)) VaultChannelActions.HandleList(context); }, MarketChannelActions.Station);
        }

        /// <summary>
        /// Whether each item (a count, then the guids, pack or Vault) can be listed. The reply is a count, then per item its guid, a byte for
        /// sellable and the reason it isn't (empty when it is). The pack's answers are the deposit's rules, read on the world thread; the Vault's and
        /// the expiry are database reads, made off it.
        /// </summary>
        private static void HandleSellable(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context) || !TryReadGuids(context, out var guids))
                return;

            var player = context.Player;
            var accountId = player.Character.AccountId;
            var pack = guids.Distinct().ToDictionary(guid => guid, guid => PackAnswer(player, guid));

            Task.Run(() =>
            {
                try
                {
                    ExpireOverdue();
                    var rows = VaultStore.Owned(accountId, guids);

                    context.Reply(ChannelWire.Body(w =>
                    {
                        w.Write(guids.Length);
                        foreach (var guid in guids)
                        {
                            string reason;
                            if (rows.TryGetValue(guid, out var row))
                                reason = VaultReason(guid, row);
                            else if (pack[guid].InPack)
                                reason = pack[guid].Reason;
                            else
                                reason = NotInPackOrVault;

                            w.Write(guid);
                            w.Write((byte)(reason == null ? 1 : 0));
                            ChannelWire.WriteString(w, reason ?? string.Empty);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] Channel sellable check for account {accountId} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "Your items could not be checked.");
                }
            });
        }

        /// <summary>
        /// Whether a pack item is in the player's pack, and why it can't be listed if it is: the channel's start rules, then the deposit's rules, then bonded.
        /// Rules only: nothing is read from the database, and the Vault's room is not checked (the deposit checks it when the item moves).
        /// </summary>
        private static (bool InPack, string Reason) PackAnswer(Player player, uint guid)
        {
            var rules = Vault.CheckDepositRules(player, guid, out var item);

            if (rules == VaultOutcome.NotInPack)
                return (false, null);

            var refusal = VaultChannel.CheckStart(player) ?? rules;

            if (refusal != null)
                return (true, VaultMessages.For(refusal.Value, item?.Name));

            return (true, item.Bonded == BondedStatus.Bonded ? VaultMessages.For(VaultOutcome.Bonded, item.Name) : null);
        }

        /// <summary>
        /// Why a Vault item can't be listed, from its row, or null when it can: held is listable, unless bonded (read from the item).
        /// </summary>
        private static string VaultReason(uint guid, VaultItem row) => row.State switch
        {
            VaultItemState.Held => IsBondedInVault(guid) ? VaultMessages.For(VaultOutcome.Bonded, row.Name) : null,
            VaultItemState.Listed => "Already listed.",
            _ => VaultMessages.For(VaultOutcome.Withdrawing, row.Name),
        };

        /// <summary>
        /// Whether a Vault item is bonded, from its item row. A database read: made off the world thread.
        /// </summary>
        private static bool IsBondedInVault(uint guid)
        {
            var biota = DatabaseManager.Shard.BaseDatabase.GetBiota(guid, doNotAddToCache: true);

            return biota?.BiotaPropertiesInt.Any(property => property.Type == (ushort)PropertyInt.Bonded && property.Value == (int)BondedStatus.Bonded) == true;
        }

        /// <summary>
        /// Lists an item (body: the item guid, then a whole-MMD price for the stack). A Vault item is listed as it is. A pack item is refused before
        /// it moves when the answer is known (the price, the listing limit, a ban), then deposited on the world thread and listed. The reply is a
        /// byte for listed, a byte for whether the item is in the Vault now, then the message.
        /// </summary>
        private static void HandleList(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context))
                return;

            uint itemGuid;
            long price;

            try
            {
                using (var body = context.Body())
                {
                    itemGuid = body.ReadUInt32();
                    price = body.ReadInt64();
                }
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, "An item and a price are required.");
                return;
            }

            var accountId = context.Player.Character.AccountId;

            Task.Run(() =>
            {
                try
                {
                    ExpireOverdue();
                    var row = VaultStore.Owned(accountId, new[] { itemGuid }).GetValueOrDefault(itemGuid);

                    // what is known before anything moves: the price, the listing limit and the ban
                    var refusal = BeforeAnythingMoves(accountId, price, row?.Name);
                    if (refusal != null)
                    {
                        ReplyOutcome(context, false, row != null, refusal);
                        return;
                    }

                    if (row != null)
                    {
                        ListVaultItem(context, accountId, itemGuid, price, row.Name);
                        return;
                    }

                    // a pack item: the deposit runs on the world thread, and its listing comes back off it
                    WorldManager.EnqueueAction(new ActionEventDelegate(() => DepositAndList(context, accountId, itemGuid, price)));
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] Channel list of 0x{itemGuid:X8} for account {accountId} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "Your listing could not be made. Check your Vault.");
                }
            });
        }

        /// <summary>
        /// The refusal that is known before an item moves, or null: a price under 1, the account at its listing limit, or the account banned. These are the
        /// listing store's own rules, read here so a refused item stays where it is.
        /// </summary>
        private static string BeforeAnythingMoves(uint accountId, long price, string name)
        {
            if (!ListingStore.IsValidPrice(price))
                return InvalidPrice;

            using var shard = new ShardDbContext();
            if (ListingStore.IsAtListingLimit(shard, accountId))
                return ListingLimitMessage();

            return MarketChannelActions.IsBanned(accountId) ? VaultMessages.For(VaultOutcome.Banned, name) : null;
        }

        /// <summary>
        /// Deposits a pack item and lists it, on the world thread. The channel's start rules and the deposit's rules come first, then bonded.
        /// </summary>
        private static void DepositAndList(ChannelContext context, uint accountId, uint itemGuid, long price)
        {
            var player = context.Player;

            var refusal = VaultChannel.CheckStart(player);
            WorldObject item = null;
            refusal ??= Vault.CheckDeposit(player, itemGuid, out item);

            if (refusal != null)
            {
                ReplyOutcome(context, false, false, refusal == VaultOutcome.NotInPack ? NotInPackOrVault : VaultMessages.For(refusal.Value, item?.Name));
                return;
            }

            if (item.Bonded == BondedStatus.Bonded)
            {
                ReplyOutcome(context, false, false, VaultMessages.For(VaultOutcome.Bonded, item.Name));
                return;
            }

            var name = item.Name;

            Vault.Deposit(player, itemGuid, deposited =>
            {
                // the deposit's own message is in chat; a refused deposit is reported as it is, with nothing listed
                if (!deposited.Success)
                {
                    ReplyOutcome(context, false, false, deposited.Message);
                    return;
                }

                // now in the Vault: the listing is the Vault's, made off the world thread
                Task.Run(() => ListDeposited(context, accountId, itemGuid, price, name));
            });
        }

        /// <summary>
        /// Lists a deposited item. A refusal here is a race (the limit or the ban changed in between), so the item is in the Vault and the reply says so.
        /// </summary>
        private static void ListDeposited(ChannelContext context, uint accountId, uint itemGuid, long price, string name)
        {
            try
            {
                var result = ListShared(accountId, itemGuid, price);
                if (result.Outcome == ListingOutcome.Ok)
                {
                    ReplyListed(context, itemGuid, price);
                    return;
                }

                ReplyOutcome(context, false, true, $"{ListingRefusal(result.Outcome, name)} {VaultMessages.InPlace(name, inPack: false)}");
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] Channel list of deposited 0x{itemGuid:X8} for account {accountId} failed: {ex}");
                context.Fail(ChannelStatus.Error, "Your listing could not be made. Check your Vault.");
            }
        }

        /// <summary>
        /// Lists a Vault item with the shared store, and replies with the outcome.
        /// </summary>
        private static void ListVaultItem(ChannelContext context, uint accountId, uint itemGuid, long price, string name)
        {
            try
            {
                if (IsBondedInVault(itemGuid))
                {
                    ReplyOutcome(context, false, true, VaultMessages.For(VaultOutcome.Bonded, name));
                    return;
                }

                var result = ListShared(accountId, itemGuid, price);
                if (result.Outcome == ListingOutcome.Ok)
                {
                    ReplyListed(context, itemGuid, price);
                    return;
                }

                ReplyOutcome(context, false, true, ListingRefusal(result.Outcome, name));
            }
            catch (Exception ex)
            {
                log.Error($"[MARKET] Channel list of 0x{itemGuid:X8} for account {accountId} failed: {ex}");
                context.Fail(ChannelStatus.Error, "Your listing could not be made. Check your Vault.");
            }
        }

        private static ListingResult ListShared(uint accountId, uint itemGuid, long price)
        {
            using var shard = new ShardDbContext();
            return ListingStore.List(shard, accountId, itemGuid, price, null, DateTime.UtcNow, () => MarketChannelActions.IsBanned(accountId));
        }

        private static void ReplyListed(ChannelContext context, uint itemGuid, long price)
        {
            var days = MarketSettings.Get(MarketSettings.ListingLifetimeDays);
            var name = ItemNameOf(itemGuid, VaultStore.Get(itemGuid));
            ReplyOutcome(context, true, true, $"{name} is listed for {price:N0} MMD for {days:N0} day{(days == 1 ? string.Empty : "s")}. Unsold, it returns to your Vault.");
        }

        private static string ListingLimitMessage()
        {
            var cap = MarketSettings.Get(MarketSettings.ActiveListings);
            return $"You already have {cap:N0} active listing{(cap == 1 ? string.Empty : "s")}, the most your account can have. Delist one first.";
        }

        /// <summary>
        /// What a player is told when the listing store refuses a Vault item to list
        /// </summary>
        private static string ListingRefusal(ListingOutcome outcome, string name) => outcome switch
        {
            ListingOutcome.InvalidPrice => InvalidPrice,
            ListingOutcome.NotInVault => VaultMessages.For(VaultOutcome.NotInVault, name),
            ListingOutcome.NotHeld => $"{VaultMessages.ItemName(name)} is already listed or being moved.",
            ListingOutcome.ListingLimit => ListingLimitMessage(),
            ListingOutcome.Banned => VaultMessages.For(VaultOutcome.Banned, name),
            _ => $"{VaultMessages.ItemName(name)} could not be listed.",
        };

        /// <summary>
        /// Takes an active listing of the account's down; the item goes back to the Vault (body: the listing id). The reply is the outcome shape of a list.
        /// </summary>
        private static void HandleDelist(ChannelContext context) => ChangeListing(context, "A listing id is required.", reader => reader.ReadInt64(),
            (shard, accountId, listingId) => ListingStore.Delist(shard, accountId, listingId, DateTime.UtcNow, () => MarketChannelActions.IsBanned(accountId)),
            (_, listing) => $"{ItemNameOf((uint)listing.ItemGuid, VaultStore.Get((uint)listing.ItemGuid))} is back in your Vault.");

        /// <summary>
        /// Changes an active listing's price (body: the listing id, then the whole-MMD price). The listing keeps its id, its item and its lifetime, and
        /// the item stays listed. The reply is the outcome shape of a list.
        /// </summary>
        private static void HandleReprice(ChannelContext context) => ChangeListing(context, "A listing id and a price are required.",
            reader => (ListingId: reader.ReadInt64(), Price: reader.ReadInt64()),
            (shard, accountId, request) => ListingStore.Reprice(shard, accountId, request.ListingId, request.Price, () => MarketChannelActions.IsBanned(accountId)),
            (request, listing) => $"{ItemNameOf((uint)listing.ItemGuid, VaultStore.Get((uint)listing.ItemGuid))} is now listed for {request.Price:N0} MMD.");

        /// <summary>
        /// The shared body of the actions that change one of the account's listings: the request is read (a bad one is refused), the overdue listings
        /// expire, the shared listing store makes the change, and the reply says whether it went through. The database work runs off the world thread.
        /// </summary>
        private static void ChangeListing<T>(ChannelContext context, string badRequest, Func<BinaryReader, T> read,
            Func<ShardDbContext, uint, T, ListingResult> change, Func<T, Listing, string> listed)
        {
            if (!MarketChannelActions.Open(context))
                return;

            T request;

            try
            {
                using (var body = context.Body())
                    request = read(body);
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, badRequest);
                return;
            }

            var accountId = context.Player.Character.AccountId;

            Task.Run(() =>
            {
                try
                {
                    ExpireOverdue();

                    ListingResult result;
                    using (var shard = new ShardDbContext())
                        result = change(shard, accountId, request);

                    if (result.Outcome == ListingOutcome.Ok)
                        ReplyOutcome(context, true, true, listed(request, result.Listing));
                    else
                        ReplyOutcome(context, false, true, ListingChangeRefusal(result.Outcome));
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] Channel change of a listing for account {accountId} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "The listing could not be changed.");
                }
            });
        }

        /// <summary>
        /// What a player is told when the listing store refuses a change to one of the account's listings
        /// </summary>
        private static string ListingChangeRefusal(ListingOutcome outcome) => outcome switch
        {
            ListingOutcome.NotFound => "That listing is not yours.",
            ListingOutcome.NotActive => "That listing is no longer active.",
            ListingOutcome.InvalidPrice => InvalidPrice,
            ListingOutcome.Banned => VaultMessages.For(VaultOutcome.Banned, null),
            ListingOutcome.Changed => "The listing changed meanwhile. Try again.",
            _ => "That listing could not be changed.",
        };

        /// <summary>
        /// One tab of the account's listings (a byte for the tab, then the most to show). The reply is the server's clock, the lifetime setting in
        /// days, the tab's total, the count shown, then each listing: id, item guid, name, price, status, created and closed times (Unix seconds,
        /// closed zero while open), and its icon (plate, underlay, icon, overlay, second overlay, UI effects; zeros once the Vault row is gone). A sold or expired listing's name is the item's own, which outlives the Vault row a buyer withdraws.
        /// </summary>
        private static void HandleListings(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context))
                return;

            byte tab;
            int count;

            try
            {
                using (var body = context.Body())
                {
                    tab = body.ReadByte();
                    count = Math.Clamp(body.ReadInt32(), 1, ListingsPageSize);
                }
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, "A tab and a count are required.");
                return;
            }

            var status = tab switch
            {
                ActiveTab => ListingStatus.Active,
                SoldTab => ListingStatus.Sold,
                ExpiredTab => ListingStatus.Expired,
                _ => null,
            };

            if (status == null)
            {
                context.Fail(ChannelStatus.BadRequest, "Choose the active, sold or expired listings.");
                return;
            }

            var accountId = context.Player.Character.AccountId;

            Task.Run(() =>
            {
                try
                {
                    ExpireOverdue();

                    using var shard = new ShardDbContext();
                    var listings = shard.MarketListings.AsNoTracking().Where(l => l.SellerAccountId == accountId && l.Status == status);
                    var total = listings.Count();
                    var ordered = status == ListingStatus.Active ? listings.OrderByDescending(l => l.CreatedTime) : listings.OrderByDescending(l => l.ClosedTime);
                    var rows = ordered.Take(count).ToList();

                    var guids = rows.Select(l => (uint)l.ItemGuid).ToList();
                    var rowsByGuid = shard.MarketVaultItems.AsNoTracking().Where(v => guids.Select(g => (ulong)g).Contains(v.ItemGuid)).ToDictionary(v => (uint)v.ItemGuid);
                    var lifetime = MarketSettings.Get(shard, MarketSettings.ListingLifetimeDays);

                    context.Reply(ChannelWire.Body(w =>
                    {
                        w.Write(UnixSeconds(DateTime.UtcNow));
                        w.Write((int)lifetime);
                        w.Write(total);
                        w.Write(rows.Count);
                        foreach (var listing in rows)
                        {
                            var itemGuid = (uint)listing.ItemGuid;
                            w.Write(listing.Id);
                            w.Write(itemGuid);
                            ChannelWire.WriteString(w, ItemNameOf(itemGuid, rowsByGuid.GetValueOrDefault(itemGuid)));
                            w.Write(listing.Price);
                            ChannelWire.WriteString(w, listing.Status);
                            w.Write(UnixSeconds(listing.CreatedTime));
                            w.Write(listing.ClosedTime is DateTime closed ? UnixSeconds(closed) : 0L);
                            var row = rowsByGuid.GetValueOrDefault(itemGuid);
                            w.Write(row == null ? 0u : ListingCatalog.Plate(row.ItemType));
                            w.Write(row?.IconUnderlay ?? 0);
                            w.Write(row?.Icon ?? 0);
                            w.Write(row?.IconOverlay ?? 0);
                            w.Write(row?.IconOverlaySecondary ?? 0);
                            w.Write(row?.UiEffects ?? 0);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] Channel listings for account {accountId} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "Your listings could not be read.");
                }
            });
        }

        /// <summary>
        /// An item's name with its material: from its Vault row while it has one, else from the item's own record, which a sale and a withdrawal leave in place
        /// </summary>
        private static string ItemNameOf(uint itemGuid, VaultItem row)
        {
            if (row?.Name is { Length: > 0 } name)
                return WithMaterial(name, row.MaterialType);

            var biota = DatabaseManager.Shard.BaseDatabase.GetBiota(itemGuid, doNotAddToCache: true);
            var named = biota?.BiotaPropertiesString.FirstOrDefault(property => property.Type == (ushort)PropertyString.Name)?.Value;
            var material = biota?.BiotaPropertiesInt.FirstOrDefault(property => property.Type == (ushort)PropertyInt.MaterialType)?.Value;
            return string.IsNullOrEmpty(named) ? VaultMessages.ItemName(named) : WithMaterial(named, material);
        }

        private static string WithMaterial(string name, int? materialType) =>
            AppraisalRules.NameWithMaterial(name, materialType is int material and not 0 ? PortalAppraisalNames.Instance.MaterialName(material) : null);

        /// <summary>
        /// Expires every listing past its lifetime, as the portal does before it reads or changes listings. Database write, off the world thread.
        /// </summary>
        private static void ExpireOverdue()
        {
            using var shard = new ShardDbContext();
            ListingStore.ExpireOverdue(shard, DateTime.UtcNow);
        }

        /// <summary>
        /// The one outcome reply of a list or delist: whether it went through, whether the item is in the Vault, and the message the player is told
        /// </summary>
        private static void ReplyOutcome(ChannelContext context, bool done, bool inVault, string message) => context.Reply(ChannelWire.Body(w =>
        {
            w.Write((byte)(done ? 1 : 0));
            w.Write((byte)(inVault ? 1 : 0));
            ChannelWire.WriteString(w, message);
        }));

        /// <summary>
        /// Reads a batch of item guids: a count from 1 to SellablePageSize, then that many guids. Anything else is a bad request.
        /// </summary>
        private static bool TryReadGuids(ChannelContext context, out uint[] guids)
        {
            try
            {
                using (var body = context.Body())
                {
                    var count = body.ReadInt32();
                    if (count >= 1 && count <= SellablePageSize)
                    {
                        guids = new uint[count];
                        for (var index = 0; index < count; index++)
                            guids[index] = body.ReadUInt32();
                        return true;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                // a body cut short is the same bad request as one with bad bounds
            }

            guids = null;
            context.Fail(ChannelStatus.BadRequest, $"Choose between 1 and {SellablePageSize} items.");
            return false;
        }

        /// <summary>A database time, which is UTC, as Unix seconds for the wire.</summary>
        internal static long UnixSeconds(DateTime time) => new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }
}
