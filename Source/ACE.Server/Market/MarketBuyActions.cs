using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using log4net;

using Microsoft.EntityFrameworkCore;

using ACE.Database;
using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Server.ClientChannel;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Market
{
    /// <summary>
    /// The Market window's buy actions on the in-band server channel: whether a listing's item would fit in the pack, and buying it. A purchase runs the
    /// shared purchase rules, as the portal's does, so the item becomes the buyer's in their Vault in one save. With pack delivery it is then withdrawn
    /// into the pack at once, as the Vault's batch withdrawal does; if that is refused, the purchase stands and the item stays in the Vault. How it was
    /// delivered is recorded with the purchase, so a repeated key gets the first answer and moves nothing.
    /// Every action needs the market station and is refused while market_enabled is off. Database work runs off the world thread; the pack checks on it.
    /// </summary>
    public static class MarketBuyActions
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string Buy = "market.buy";
        public const string Fits = "market.fits";

        /// <summary>A buy's delivery byte: the item stays in the Vault, or goes on into the pack</summary>
        public const byte ToVault = 0;
        public const byte ToPack = 1;

        /// <summary>The code of a purchase that went through, the first time or replayed for a repeated key</summary>
        public const string Ok = "ok";


        public static void Register()
        {
            ServerChannel.Register(Buy, HandleBuy, MarketChannelActions.Station);
            ServerChannel.Register(Fits, HandleFits, MarketChannelActions.Station);
        }

        /// <summary>
        /// Whether the listing's item would go into the pack now (body: the listing id), as the batch withdrawal would decide: a transfer under way,
        /// the channel's start rules, the pack's room, the burden and the uniques. Reply: a byte for fits, the pack's free slots and all its slots,
        /// then the reason it doesn't fit (empty when it does).
        /// </summary>
        private static void HandleFits(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context) || !TryReadId(context, out var listingId))
                return;

            var player = context.Player;

            Task.Run(() =>
            {
                try
                {
                    ACE.Database.Models.Shard.Biota biota = null;
                    using (var shard = MarketChannelActions.Database.CreateShard())
                    {
                        var listing = shard.MarketListings.AsNoTracking().FirstOrDefault(l => l.Id == listingId && l.Status == ListingStatus.Active);
                        if (listing != null)
                            biota = DatabaseManager.Shard.BaseDatabase.GetBiota((uint)listing.ItemGuid, doNotAddToCache: true);
                    }

                    // the pack is the world thread's
                    WorldManager.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        try
                        {
                            var item = biota == null ? null : WorldObjectFactory.CreateWorldObject(biota);
                            var refusal = item == null ? MarketBrowseActions.GoneMessage : Vault.CheckIntoPack(player, item) is VaultOutcome outcome ? VaultMessages.For(outcome, item.NameWithMaterial) : null;
                            var slots = (player.ItemCapacity ?? 0) + player.Inventory.Values.OfType<Container>().Sum(pack => pack.ItemCapacity ?? 0);

                            context.Reply(ChannelWire.Body(w =>
                            {
                                w.Write((byte)(refusal == null ? 1 : 0));
                                w.Write(player.GetFreeInventorySlots());
                                w.Write(slots);
                                ChannelWire.WriteString(w, refusal ?? string.Empty);
                            }));
                        }
                        catch (Exception ex)
                        {
                            FitFailed(context, listingId, ex);
                        }
                    }));
                }
                catch (Exception ex)
                {
                    FitFailed(context, listingId, ex);
                }
            });
        }

        private static void FitFailed(ChannelContext context, long listingId, Exception ex)
        {
            log.Error($"[MARKET] Channel fit check of listing {listingId} for {context.Player.Name} failed: {ex}");
            context.Fail(ChannelStatus.Error, "Your pack could not be checked.");
        }

        /// <summary>
        /// Buys a listing (body: listing id, the price the buyer saw, the whole stack's count, the idempotency key, the delivery byte). The reply is the
        /// outcome's code (the portal's: "ok", "price_changed", "gone" and so on), the player's message, the price (paid, or the new one for
        /// price_changed), the balance after (ok only), and a byte for the item being in the pack now.
        /// </summary>
        private static void HandleBuy(ChannelContext context)
        {
            if (!MarketChannelActions.Open(context))
                return;

            long listingId, expectedPrice;
            int count;
            string key;
            byte delivery;

            try
            {
                using var body = context.Body();
                listingId = body.ReadInt64();
                expectedPrice = body.ReadInt64();
                count = body.ReadInt32();
                key = ChannelWire.ReadString(body);
                delivery = body.ReadByte();
            }
            catch (EndOfStreamException)
            {
                context.Fail(ChannelStatus.BadRequest, "A listing, a price, a count, a request key and a delivery are required.");
                return;
            }

            if (delivery != ToVault && delivery != ToPack)
            {
                context.Fail(ChannelStatus.BadRequest, "Choose the pack or the Vault.");
                return;
            }

            // the front door's own checks, as the portal's endpoint makes them before the shared rules
            if (!ListingStore.IsValidPrice(expectedPrice))
            {
                ReplyBuy(context, "invalid_price", MarketSellActions.InvalidPrice);
                return;
            }

            if (count < 1)
            {
                var (code, message) = Refusal(BuyOutcome.InvalidCount, 0);
                ReplyBuy(context, code, message);
                return;
            }

            var player = context.Player;
            var accountId = player.Character.AccountId;
            var characterId = player.Guid.ClientGUID;

            Task.Run(() =>
            {
                try
                {
                    var database = MarketChannelActions.Database;
                    var result = PurchaseRules.Buy(database, new DatabaseMarketPause(database), MarketFees.Policy, listingId, accountId, characterId,
                        expectedPrice, count, key, DateTime.UtcNow);

                    if (result.Problem != null)
                        log.Error($"[MARKET] {result.Problem}");

                    if (result.Outcome != BuyOutcome.Ok)
                    {
                        var (code, message) = Refusal(result.Outcome, result.Price ?? 0);
                        ReplyBuy(context, code, message, result.Price ?? 0);
                        return;
                    }

                    if (result.Replayed)
                    {
                        Replay(context, accountId, key, result.Receipt);
                        return;
                    }

                    // the seller hears at once, if they're online; a replay sold nothing. The online characters are the world thread's.
                    WorldManager.EnqueueAction(new ActionEventDelegate(SaleNotices.Start));

                    Deliver(context, key, result.Receipt, delivery == ToPack);
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] Channel buy of listing {listingId} for {player.Name} failed: {ex}");
                    context.Fail(ChannelStatus.Error, "The purchase could not be confirmed. Try again to find out whether it went through.");
                }
            });
        }

        /// <summary>
        /// A repeated key: the first answer, as recorded with the purchase. Nothing moves. A purchase whose delivery hasn't settled yet (the first
        /// request is still moving it), or that the portal made, says only that it went through.
        /// </summary>
        private static void Replay(ChannelContext context, uint accountId, string key, PurchaseReceipt receipt)
        {
            PurchaseDelivery delivered;
            using (var shard = MarketChannelActions.Database.CreateShard())
                delivered = PurchaseStore.StoredDelivery(shard, accountId, key);

            ReplyBuy(context, Ok, delivered?.Message ?? "Your purchase went through. Check your pack and your Vault.", receipt.Price, receipt.Balance, delivered?.InPack ?? false);
        }

        /// <summary>
        /// The bought item is the buyer's, in their Vault. To the pack, it is withdrawn as a batch of one; a refusal leaves it in the Vault, and the
        /// reply says why. How it settled is recorded with the purchase before the reply. Off the world thread.
        /// </summary>
        private static void Deliver(ChannelContext context, string key, PurchaseReceipt receipt, bool toPack)
        {
            var player = context.Player;
            var row = VaultStore.Get(receipt.ItemGuid);
            var name = PortalAppraisalNames.Instance.NameWithMaterial(row?.Name, row?.MaterialType);

            void Settled(string message, bool inPack)
            {
                try
                {
                    using var shard = MarketChannelActions.Database.CreateShard();
                    PurchaseStore.RecordDelivery(shard, player.Character.AccountId, key, new PurchaseDelivery(message, inPack));
                }
                catch (Exception ex)
                {
                    // the purchase and the move stand; a repeat of the key then only says it went through
                    log.Error($"[MARKET] Recording the delivery of listing {receipt.ListingId} for {player.Name} failed: {ex}");
                }

                ReplyBuy(context, Ok, message, receipt.Price, receipt.Balance, inPack);
            }

            if (!toPack)
            {
                Settled(VaultMessages.InPlace(name, inPack: false), false);
                return;
            }

            // the purchase reports in the Market window, so the withdrawal's own chat line is left out
            WorldManager.EnqueueAction(new ActionEventDelegate(() => Vault.WithdrawMany(player, new[] { receipt.ItemGuid }, withdrawn =>
            {
                // the database decides where the rest are: the withdrawal's own message says it
                var inPack = withdrawn.Outcome == VaultOutcome.Withdrawn;
                var message = inPack ? VaultMessages.InPlace(name, inPack: true)
                    : withdrawn.Success ? VaultMessages.For(withdrawn.Outcome, name)
                    : $"{VaultMessages.For(withdrawn.Outcome, name)} {VaultMessages.InPlace(name, inPack: false)}";

                // the record is a database write: off the world thread
                Task.Run(() => Settled(message, inPack));
            }, chat: false)));
        }

        private static void ReplyBuy(ChannelContext context, string code, string message, long price = 0, long balance = 0, bool inPack = false) =>
            context.Reply(ChannelWire.Body(w =>
            {
                ChannelWire.WriteString(w, code);
                ChannelWire.WriteString(w, message);
                w.Write(price);
                w.Write(balance);
                w.Write((byte)(inPack ? 1 : 0));
            }));

        /// <summary>
        /// A refusal's code and what the player is told. The codes are the portal's, so both front doors name a refusal the same. The words are the
        /// portal's too (market-web's api.ts), deliberately, except where the game already says it: the ban and the gone listing are the game's own.
        /// </summary>
        private static (string Code, string Message) Refusal(BuyOutcome outcome, long newPrice) => outcome switch
        {
            BuyOutcome.InvalidKey => ("bad_request", "The request could not be read. Reload and try again."),
            BuyOutcome.KeyReused => ("key_reused", "This request key was already used. Close this dialog and start a new purchase."),
            BuyOutcome.Paused => ("paused", "The market is paused. Purchases and MMD withdrawals are temporarily unavailable."),
            BuyOutcome.RateLimited => ("rate_limited", "Too many purchase attempts. Wait a minute and try again."),
            BuyOutcome.InvalidCharacter => ("invalid_character", "This character can't buy for your account."),
            BuyOutcome.Gone => ("gone", MarketBrowseActions.GoneMessage),
            BuyOutcome.OwnListing => ("own_listing", "You cannot buy your own listing."),
            BuyOutcome.PriceChanged => ("price_changed", $"The price has changed to {newPrice:N0} MMD. Nothing was charged."),
            BuyOutcome.InvalidCount => ("invalid_count", "The whole stack must be purchased. Reload the listing."),
            BuyOutcome.InvalidFee => ("invalid_fee", "The server could not calculate the fee. Try again later."),
            BuyOutcome.InsufficientFunds => ("insufficient_funds", "You do not have enough MMD for this purchase."),
            BuyOutcome.Banned => ("banned", VaultMessages.For(VaultOutcome.Banned, null)),
            BuyOutcome.Busy => ("busy", "The server is busy. Try this purchase again."),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

        private static bool TryReadId(ChannelContext context, out long listingId)
        {
            try
            {
                using var body = context.Body();
                listingId = body.ReadInt64();
                return true;
            }
            catch (EndOfStreamException)
            {
                listingId = 0;
                context.Fail(ChannelStatus.BadRequest, "A listing id is required.");
                return false;
            }
        }
    }
}
