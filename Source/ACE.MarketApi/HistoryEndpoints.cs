using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum.Properties;

namespace ACE.MarketApi
{
    /// <summary>
    /// The signed-in account's money and item history, worded for display. Only reads.
    /// </summary>
    public static class HistoryEndpoints
    {
        public const int DefaultItemsLimit = 50;

        public const int MaxItemsLimit = 100;

        private const string Minus = "−";

        private const string UnknownItem = "an unknown item";

        private const string UnknownCharacter = "someone";

        public static void Map(WebApplication app)
        {
            app.MapGet("/history", History).RequireAuthorization();
        }

        /// <summary>
        /// GET /history?since={seq}&amp;itemsBefore={eventId}&amp;itemsLimit={n}
        /// → balance, head (the account's last ledger sequence), every one of the account's ledger lines with a sequence after since (newest first),
        /// and a page of its item movements (newest first, paged back by event id).
        /// A poller passes the head it was last given as since, and never misses a line: the head is read first, and every line up to it is already committed.
        /// Item movements have no per-account sequence, so their page is for browsing, not a poll cursor: an event id can commit after a higher one.
        /// A sale still reaches a poller through its ledger line.
        /// </summary>
        private static async Task<IResult> History(HttpContext context, MarketDatabase database)
        {
            var accountId = MarketHttp.AccountId(context);
            var query = context.Request.Query;

            if (!TryWhole(query["since"], 0, out var since) || !TryWhole(query["itemsBefore"], 1, out var itemsBefore))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_cursor");

            // like the catalog's limit: at least 1, and a larger one is capped
            if (!TryWhole(query["itemsLimit"], 1, out var limit))
                return MarketHttp.Error(StatusCodes.Status400BadRequest, "bad_limit");

            var itemsLimit = (int)Math.Min(limit ?? DefaultItemsLimit, MaxItemsLimit);

            using var shard = database.CreateShard();

            // the head first: a line numbered up to it was saved with it, so the read below can't miss one that commits later
            var balance = await shard.MarketBalances.AsNoTracking()
                .Where(b => b.AccountId == accountId)
                .Select(b => new { b.Balance, b.LastSequence })
                .FirstOrDefaultAsync();

            var head = balance?.LastSequence ?? 0;

            var lines = await shard.MarketLedgerEntries.AsNoTracking()
                .Where(e => e.AccountId == accountId && e.Sequence > (since ?? 0) && e.Sequence <= head)
                .OrderByDescending(e => e.Sequence)
                .Select(e => new LedgerLine(e.Sequence.Value, e.TransferId, e.Amount, e.BalanceAfter.Value, e.Memo,
                    e.Transfer.Kind, e.Transfer.ListingId, e.Transfer.ReversesTransferId, e.Transfer.Memo, e.Transfer.CreatedTime))
                .ToListAsync();

            // destroyed trade notes: only a note deposit gives a deposit event a transfer, and its ledger line already tells it ("Deposited 50 trade notes")
            var eventQuery = shard.MarketItemEvents.AsNoTracking()
                .Where(e => e.AccountId == accountId && !(e.Kind == ItemEventKind.Deposit && e.TransferId != null));

            if (itemsBefore != null)
                eventQuery = eventQuery.Where(e => e.Id < itemsBefore);

            var events = await eventQuery.OrderByDescending(e => e.Id).Take(itemsLimit + 1).ToListAsync();

            long? nextItemsBefore = null;

            if (events.Count > itemsLimit)
            {
                events.RemoveAt(events.Count - 1);
                nextItemsBefore = events[^1].Id;
            }

            var names = await References.ReadAsync(shard,
                lines.Where(l => l.ListingId != null).Select(l => l.ListingId.Value).Concat(events.Where(e => e.ListingId != null).Select(e => e.ListingId.Value)),
                events.Select(e => e.ItemGuid));

            return Results.Json(new
            {
                balance = balance?.Balance ?? 0,
                head,
                transfers = lines.Select(l => new
                {
                    sequence = l.Sequence,
                    transferId = l.TransferId,
                    kind = l.Kind,
                    amount = l.Amount,
                    balanceAfter = l.BalanceAfter,
                    // stored as UTC; EF reads datetime(6) as Unspecified
                    time = DateTime.SpecifyKind(l.Time, DateTimeKind.Utc),
                    text = Describe(l, accountId, names),
                    memo = l.EntryMemo ?? l.TransferMemo,
                }),
                items = events.Select(e => new
                {
                    id = e.Id,
                    itemGuid = e.ItemGuid,
                    kind = e.Kind,
                    name = names.Item(e.ItemGuid),
                    listingId = e.ListingId,
                    time = DateTime.SpecifyKind(e.EventTime, DateTimeKind.Utc),
                    text = Describe(e, names),
                }),
                nextItemsBefore,
            });
        }

        private sealed record LedgerLine(long Sequence, long TransferId, long Amount, long BalanceAfter, string EntryMemo,
            string Kind, long? ListingId, long? ReversesTransferId, string TransferMemo, DateTime Time);

        /// <summary>
        /// A ledger line: what happened, then the signed amount ("Sold Bone Slicer to Bob · +100 MMD")
        /// </summary>
        private static string Describe(LedgerLine line, uint accountId, References names)
        {
            var listing = names.Listing(line.ListingId);

            // a sale's seller has two lines: +price, then -fee (0 or less, even when the fee is 0).
            // The buyer's -price line is never the seller's: buying from your own account is refused (own_listing).
            var isFee = line.Kind == TransferKind.Purchase && listing != null && listing.SellerAccountId == accountId && line.Amount <= 0;

            var what = line.Kind switch
            {
                TransferKind.NoteDeposit => $"Deposited {Notes(line.Amount)}",
                TransferKind.NoteWithdraw => $"Withdrew {Notes(-line.Amount)}",
                TransferKind.Purchase when listing == null => "Purchase",
                TransferKind.Purchase when isFee => line.EntryMemo == null ? "Market fee" : $"Market fee ({line.EntryMemo})",
                TransferKind.Purchase when listing.SellerAccountId == accountId => $"Sold {names.ItemOrUnknown(listing.ItemGuid)} to {names.Character(listing.BuyerCharacterId)}",
                TransferKind.Purchase => $"Bought {names.ItemOrUnknown(listing.ItemGuid)} from {names.Character(listing.SellerCharacterId)}",
                TransferKind.AdminAdjust => $"Admin adjustment: {line.TransferMemo}",
                TransferKind.Reversal => $"Reversal of transfer {line.ReversesTransferId}: {line.TransferMemo}",
                _ => line.Kind,
            };

            var sign = isFee || line.Amount < 0 ? Minus : "+";

            return $"{what} · {sign}{Number(Math.Abs(line.Amount))} MMD";
        }

        /// <summary>
        /// An item movement ("Listed Bone Slicer for 100 MMD")
        /// </summary>
        private static string Describe(ItemEvent e, References names)
        {
            var item = names.ItemOrUnknown(e.ItemGuid);
            var listing = names.Listing(e.ListingId);

            return e.Kind switch
            {
                ItemEventKind.Deposit => $"Deposited {item}",
                ItemEventKind.Withdraw => $"Withdrew {item}",
                ItemEventKind.List when listing != null => $"Listed {item} for {Number(listing.Price)} MMD",
                ItemEventKind.List => $"Listed {item}",
                ItemEventKind.Delist => $"Delisted {item}",
                ItemEventKind.Expire => $"Listing expired: {item}",
                ItemEventKind.Sold => $"Sold {item} to {names.Character(listing?.BuyerCharacterId)}",
                ItemEventKind.Bought => $"Bought {item} from {names.Character(listing?.SellerCharacterId)}",
                ItemEventKind.BanReturn => $"Listing returned (account banned): {item}",
                ItemEventKind.Admin => $"{char.ToUpperInvariant(item[0])}{item[1..]} moved by an admin",
                _ => $"{e.Kind}: {item}",
            };
        }

        private static string Notes(long count) => count == 1 ? "1 trade note" : $"{Number(count)} trade notes";

        private static string Number(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>
        /// A whole number at least min, or null when the parameter is absent
        /// </summary>
        private static bool TryWhole(StringValues value, long min, out long? result)
        {
            result = null;

            if (MarketHttp.QueryValue(value) is not string text)
                return true;

            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < min)
                return false;

            result = parsed;
            return true;
        }

        /// <summary>
        /// The listings, item names and character names the lines refer to, read in one batch each
        /// </summary>
        private sealed class References
        {
            private readonly Dictionary<long, Listing> listings;
            private readonly Dictionary<uint, string> items;
            private readonly Dictionary<uint, string> characters;

            private References(Dictionary<long, Listing> listings, Dictionary<uint, string> items, Dictionary<uint, string> characters)
            {
                this.listings = listings;
                this.items = items;
                this.characters = characters;
            }

            public static async Task<References> ReadAsync(ShardDbContext shard, IEnumerable<long> listingIds, IEnumerable<uint> itemGuids)
            {
                var listingIdSet = listingIds.ToHashSet();

                var listings = listingIdSet.Count == 0 ? new Dictionary<long, Listing>() :
                    await shard.MarketListings.AsNoTracking().Where(l => listingIdSet.Contains(l.Id)).ToDictionaryAsync(l => l.Id);

                var guids = itemGuids.Concat(listings.Values.Select(l => l.ItemGuid)).ToHashSet();

                // the name copied onto the Vault row at deposit; once the item has left the Vault, the item's own name
                var items = guids.Count == 0 ? new Dictionary<uint, string>() :
                    await shard.MarketVaultItems.AsNoTracking().Where(v => guids.Contains(v.ItemGuid)).ToDictionaryAsync(v => v.ItemGuid, v => v.Name);

                var missing = guids.Where(g => !items.ContainsKey(g)).ToHashSet();

                if (missing.Count > 0)
                {
                    foreach (var row in await shard.BiotaPropertiesString.AsNoTracking()
                        .Where(p => missing.Contains(p.ObjectId) && p.Type == (ushort)PropertyString.Name)
                        .Select(p => new { p.ObjectId, p.Value })
                        .ToListAsync())
                        items[row.ObjectId] = row.Value;
                }

                var characterIds = listings.Values.SelectMany(l => new[] { l.SellerCharacterId, l.BuyerCharacterId ?? 0 }).Where(id => id != 0).ToHashSet();

                var characters = characterIds.Count == 0 ? new Dictionary<uint, string>() :
                    await shard.Character.AsNoTracking().Where(c => characterIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);

                return new References(listings, items, characters);
            }

            public Listing Listing(long? id) => id != null && listings.TryGetValue(id.Value, out var listing) ? listing : null;

            /// <summary>
            /// The item's name, or null when neither the Vault nor the item row still has it
            /// </summary>
            public string Item(uint itemGuid) => items.TryGetValue(itemGuid, out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;

            public string ItemOrUnknown(uint itemGuid) => Item(itemGuid) ?? UnknownItem;

            public string Character(uint? id) => id != null && characters.TryGetValue(id.Value, out var name) ? name : UnknownCharacter;
        }
    }
}
