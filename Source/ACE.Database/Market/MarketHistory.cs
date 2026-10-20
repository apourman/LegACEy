using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.Market
{
    /// <summary>
    /// The signed-in account's money and item history, worded for display. Only reads. The Market API's /history and the game's market.history
    /// both read it, so the two show the same lines.
    /// </summary>
    public static class MarketHistory
    {
        public const int DefaultLimit = 50;

        public const int MaxLimit = 100;

        private const string Minus = "−";

        private const string UnknownItem = "an unknown item";

        private const string UnknownCharacter = "someone";

        private static readonly System.Linq.Expressions.Expression<Func<LedgerEntry, LedgerRow>> LedgerRowProjection = e =>
            new LedgerRow(e.Sequence.Value, e.TransferId, e.Amount, e.BalanceAfter.Value, e.Memo,
                e.Transfer.Kind, e.Transfer.ListingId, e.Transfer.ReversesTransferId, e.Transfer.Memo, e.Transfer.CreatedTime);

        /// <summary>A ledger line, worded: what happened, the signed amount, the balance after it</summary>
        public sealed record Line(long Sequence, long TransferId, string Kind, long Amount, long BalanceAfter, DateTime Time, string Text, string Memo);

        /// <summary>An item movement, worded ("Listed Bone Slicer for 100 MMD")</summary>
        public sealed record Movement(long Id, uint ItemGuid, string Kind, string Name, long? ListingId, DateTime Time, string Text);

        /// <summary>
        /// The balance, the account's head sequence, one page of ledger lines and one page of item movements (newest first unless polling by <c>since</c>)
        /// </summary>
        public sealed record Page(long Balance, long Head, IReadOnlyList<Line> Lines, long? NextTransfersBefore, long? NextSince, bool More,
            IReadOnlyList<Movement> Movements, long? NextItemsBefore);

        /// <summary>The MMD the account earned from sales and spent on purchases over a span of time, and how many items each</summary>
        public sealed record Flow(long Earned, long Spent, int ItemsSold, int ItemsBought);

        /// <summary>
        /// Reads one page of the account's history. Backward browsing is newest first, paged by the cursors; polling passes the last sequence it received (since)
        /// and is oldest first. Limits are capped at MaxLimit, and a missing one is DefaultLimit.
        /// The head is read first, and every line up to it is already committed.
        /// Item movements have no per-account sequence, so their page is for browsing, not a poll cursor: an event id can commit after a higher one.
        /// A sale still reaches a poller through its ledger line.
        /// </summary>
        public static async Task<Page> ReadAsync(ShardDbContext shard, uint accountId, long? since, long? transfersBefore, long? itemsBefore,
            long? transfersLimit, long? itemsLimit)
        {
            var lineLimit = (int)Math.Min(transfersLimit ?? DefaultLimit, MaxLimit);
            var movementLimit = (int)Math.Min(itemsLimit ?? DefaultLimit, MaxLimit);

            // the head first: a line numbered up to it was saved with it, so the read below can't miss one that commits later
            var balance = await shard.MarketBalances.AsNoTracking()
                .Where(b => b.AccountId == accountId)
                .Select(b => new { b.Balance, b.LastSequence })
                .FirstOrDefaultAsync();

            var head = balance?.LastSequence ?? 0;

            var ledgerQuery = shard.MarketLedgerEntries.AsNoTracking()
                .Where(e => e.AccountId == accountId && e.Sequence <= head);
            List<LedgerRow> rows;
            long? nextTransfersBefore = null;
            long? nextSince = null;
            var more = false;

            if (since is long watermark)
            {
                rows = await ledgerQuery
                    .Where(e => e.Sequence > watermark)
                    .OrderBy(e => e.Sequence)
                    .Take(lineLimit)
                    .Select(LedgerRowProjection)
                    .ToListAsync();

                nextSince = rows.Count == 0 ? watermark : rows[^1].Sequence;
                more = nextSince < head;
            }
            else
            {
                var pageQuery = ledgerQuery;

                if (transfersBefore is long before)
                    pageQuery = pageQuery.Where(e => e.Sequence < before);

                rows = await pageQuery
                    .OrderByDescending(e => e.Sequence)
                    .Take(lineLimit + 1)
                    .Select(LedgerRowProjection)
                    .ToListAsync();

                if (rows.Count > lineLimit)
                {
                    rows.RemoveAt(rows.Count - 1);
                    nextTransfersBefore = rows[^1].Sequence;
                }
            }

            // destroyed trade notes: only a note deposit gives a deposit event a transfer, and its ledger line already tells it ("Deposited 50 trade notes")
            var eventQuery = shard.MarketItemEvents.AsNoTracking()
                .Where(e => e.AccountId == accountId && !(e.Kind == ItemEventKind.Deposit && e.TransferId != null));

            if (itemsBefore != null)
                eventQuery = eventQuery.Where(e => e.Id < itemsBefore);

            var events = await eventQuery.OrderByDescending(e => e.Id).Take(movementLimit + 1).ToListAsync();

            long? nextItemsBefore = null;

            if (events.Count > movementLimit)
            {
                events.RemoveAt(events.Count - 1);
                nextItemsBefore = events[^1].Id;
            }

            var names = await References.ReadAsync(shard,
                rows.Where(l => l.ListingId != null).Select(l => l.ListingId.Value).Concat(events.Where(e => e.ListingId != null).Select(e => e.ListingId.Value)),
                events.Select(e => (uint)e.ItemGuid));

            var lines = rows.Select(l => new Line(
                l.Sequence,
                l.TransferId,
                l.Kind,
                l.Amount,
                l.BalanceAfter,
                DateTime.SpecifyKind(l.Time, DateTimeKind.Utc),
                Describe(l, accountId, names),
                l.EntryMemo ?? l.TransferMemo)).ToList();
            var movements = events.Select(e => new Movement(
                e.Id,
                (uint)e.ItemGuid,
                e.Kind,
                names.Item((uint)e.ItemGuid),
                e.ListingId,
                DateTime.SpecifyKind(e.EventTime, DateTimeKind.Utc),
                Describe(e, names))).ToList();

            return new Page(balance?.Balance ?? 0, head, lines, nextTransfersBefore, nextSince, more, movements, nextItemsBefore);
        }

        /// <summary>
        /// What the account earned and spent on the market since a time, from its purchase ledger lines: a sale is the seller's price line, a purchase
        /// the buyer's. The seller's fee line, admin adjustments, reversals and trade notes are left out.
        /// </summary>
        public static async Task<Flow> ReadFlowAsync(ShardDbContext shard, uint accountId, DateTime since)
        {
            var lines = await shard.MarketLedgerEntries.AsNoTracking()
                .Where(e => e.AccountId == accountId && e.Transfer.Kind == TransferKind.Purchase && e.Transfer.CreatedTime >= since)
                .Select(e => new { e.Amount, e.Transfer.ListingId })
                .ToListAsync();

            var listingIds = lines.Where(l => l.ListingId != null).Select(l => l.ListingId.Value).ToHashSet();
            var sellers = listingIds.Count == 0 ? new Dictionary<long, uint>() :
                await shard.MarketListings.AsNoTracking().Where(l => listingIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.SellerAccountId);

            long earned = 0, spent = 0;
            var sold = 0;
            var bought = 0;

            foreach (var line in lines)
            {
                if (line.ListingId == null || !sellers.TryGetValue(line.ListingId.Value, out var seller))
                    continue;

                if (seller == accountId && line.Amount > 0)
                {
                    earned += line.Amount;
                    sold++;
                }
                else if (seller != accountId && line.Amount < 0)
                {
                    spent -= line.Amount;
                    bought++;
                }
            }

            return new Flow(earned, spent, sold, bought);
        }

        /// <summary>
        /// A sale as its seller is told of it: the listing, the item's name, the buyer's character, the price, the seller's balance after the sale's
        /// fee, and the seller's character that listed it
        /// </summary>
        public sealed record SaleNotice(long ListingId, string Item, string Buyer, long Price, long BalanceAfter, uint SellerCharacterId);

        /// <summary>
        /// The account's sales whose ledger lines are numbered after <paramref name="since"/> and up to <paramref name="head"/> (read first, so every
        /// line up to it is committed), made at or after <paramref name="notBefore"/>, oldest first. A sale is the seller's price line and fee line.
        /// </summary>
        public static async Task<IReadOnlyList<SaleNotice>> ReadSalesAsync(ShardDbContext shard, uint accountId, long since, long head, DateTime notBefore)
        {
            var lines = await shard.MarketLedgerEntries.AsNoTracking()
                .Where(e => e.AccountId == accountId && e.Sequence > since && e.Sequence <= head && e.Transfer.Kind == TransferKind.Purchase
                    && e.Transfer.CreatedTime >= notBefore)
                .OrderBy(e => e.Sequence)
                .Select(e => new { e.TransferId, e.Transfer.ListingId, e.BalanceAfter })
                .ToListAsync();

            var names = await References.ReadAsync(shard, lines.Where(l => l.ListingId != null).Select(l => l.ListingId.Value), Array.Empty<uint>());

            // a purchase the account made is the buyer's line, not a sale
            return lines.GroupBy(l => l.TransferId)
                .Select(sale => (Listing: names.Listing(sale.First().ListingId), BalanceAfter: sale.Last().BalanceAfter.Value))
                .Where(sale => sale.Listing?.SellerAccountId == accountId)
                .Select(sale => new SaleNotice(sale.Listing.Id, names.ItemOrUnknown((uint)sale.Listing.ItemGuid), names.Character(sale.Listing.BuyerCharacterId),
                    sale.Listing.Price, sale.BalanceAfter, sale.Listing.SellerCharacterId))
                .ToList();
        }

        private sealed record LedgerRow(long Sequence, long TransferId, long Amount, long BalanceAfter, string EntryMemo,
            string Kind, long? ListingId, long? ReversesTransferId, string TransferMemo, DateTime Time);

        /// <summary>
        /// A ledger line: what happened, then the signed amount ("Sold Bone Slicer to Bob · +100 MMD")
        /// </summary>
        private static string Describe(LedgerRow line, uint accountId, References names)
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
                TransferKind.Purchase when listing.SellerAccountId == accountId => $"Sold {names.ItemOrUnknown((uint)listing.ItemGuid)} to {names.Character(listing.BuyerCharacterId)}",
                TransferKind.Purchase => $"Bought {names.ItemOrUnknown((uint)listing.ItemGuid)} from {names.Character(listing.SellerCharacterId)}",
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
            var item = names.ItemOrUnknown((uint)e.ItemGuid);
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
        /// The listings, item names and character names the lines refer to, read in one batch each
        /// </summary>
        private sealed class References
        {
            private readonly Dictionary<long, Listing> listings;
            private readonly Dictionary<ulong, string> items;
            private readonly Dictionary<uint, string> characters;

            private References(Dictionary<long, Listing> listings, Dictionary<ulong, string> items, Dictionary<uint, string> characters)
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

                var guids = itemGuids.Select(g => (ulong)g).Concat(listings.Values.Select(l => l.ItemGuid)).ToHashSet();

                // the name copied onto the Vault row at deposit; once the item has left the Vault, the item's own name
                var items = guids.Count == 0 ? new Dictionary<ulong, string>() :
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

                var characterIds = listings.Values.SelectMany(l => new[] { (ulong)l.SellerCharacterId, l.BuyerCharacterId ?? 0 }).Where(id => id != 0).ToHashSet();

                var characters = characterIds.Count == 0 ? new Dictionary<uint, string>() :
                    await shard.Character.AsNoTracking().Where(c => characterIds.Contains(c.Id)).ToDictionaryAsync(c => (uint)c.Id, c => c.Name);

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
