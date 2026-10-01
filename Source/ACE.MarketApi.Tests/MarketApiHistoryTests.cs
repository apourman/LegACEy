using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// GET /api/history: the account's balance, head sequence and ledger lines worded for display, plus its item movements
    /// </summary>
    [TestClass]
    public class MarketApiHistoryTests
    {
        private const string Minus = "−";

        private sealed record Player(string Name, uint AccountId, uint CharacterId, string CharacterName);

        private sealed class FixedFeePolicy : IFeePolicy
        {
            private readonly SellerFee fee;

            public FixedFeePolicy(SellerFee fee)
            {
                this.fee = fee;
            }

            public SellerFee Quote(Sale sale) => fee;
        }

        private static Player NewPlayer(string prefix, long? balance = null)
        {
            var name = MarketApiTestData.UniqueName(prefix);
            var accountId = MarketApiTestData.CreateAccount(name, "pass");
            var characterName = name + "Main";
            var characterId = MarketApiTestData.AddCharacter(accountId, characterName);

            if (balance.HasValue)
                MarketApiTestData.SetBalance(accountId, balance.Value);

            return new Player(name, accountId, characterId, characterName);
        }

        /// <summary>
        /// Writes a transfer the way the game's note jobs and admin commands do: through the ledger, retried when another writer got there first
        /// </summary>
        private static void AddTransfer(string kind, uint accountId, long amount, string systemAccount, string memo = null, uint? characterId = null)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var shard = MarketTestDatabase.CreateContext(MarketApiTestData.ShardDatabase);

                var transfer = new Transfer { Kind = kind, ActorAccountId = accountId, ActorCharacterId = characterId, Memo = memo, CreatedTime = DateTime.UtcNow };
                transfer.Entries.Add(Ledger.PlayerEntry(accountId, amount));
                transfer.Entries.Add(Ledger.SystemEntry(systemAccount, -amount));

                Assert.IsTrue(Ledger.TryAdd(shard, transfer), "the seeded transfer would overdraw the account");

                try
                {
                    shard.SaveChanges();
                    return;
                }
                catch (DbUpdateException ex) when (Ledger.IsLostRace(ex) && attempt < 100)
                {
                }
            }
        }

        private static void DepositNotes(Player player, long amount) => AddTransfer(TransferKind.NoteDeposit, player.AccountId, amount, SystemAccount.Notes, characterId: player.CharacterId);

        private static long ListItem(MarketApiHost host, Player seller, string name, long price)
        {
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, name, VaultItemState.Listed);

            return MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, guid, price, ListingStatus.Active, host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));
        }

        private static async Task BuyAsync(MarketApiHost host, string cookie, long listingId, long price)
        {
            var response = await host.PostJsonAsync($"/api/listings/{listingId}/purchase", new { count = 1, expectedPrice = price, idempotencyKey = Guid.NewGuid().ToString("N") }, cookie);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private static async Task<JsonElement> HistoryAsync(MarketApiHost host, string cookie, string query = "")
        {
            var response = await host.GetAsync("/api/history" + query, cookie);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            return await MarketApiHost.JsonAsync(response);
        }

        private static string[] Texts(JsonElement history, string list = "transfers") => history.GetProperty(list).EnumerateArray().Select(t => t.GetProperty("text").GetString()).ToArray();

        private static long[] Sequences(JsonElement history) => history.GetProperty("transfers").EnumerateArray().Select(t => t.GetProperty("sequence").GetInt64()).ToArray();

        private static void AddItemEvent(uint itemGuid, Player player, string kind, DateTime time, long? listingId = null, long? transferId = null, int? quantity = null)
        {
            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase,
                "INSERT INTO market_item_event (item_Guid, account_Id, character_Id, kind, listing_Id, transfer_Id, quantity, event_Time) " +
                $"VALUES ({itemGuid}, {player.AccountId}, {player.CharacterId}, '{kind}', {(listingId?.ToString() ?? "NULL")}, {(transferId?.ToString() ?? "NULL")}, {(quantity?.ToString() ?? "NULL")}, '{time:yyyy-MM-dd HH:mm:ss.ffffff}');");
        }

        // ---- criterion 1: the account's own lines, newest first, worded for display

        [TestMethod]
        public async Task History_ListsOwnTransfersNewestFirstWordedForDisplay()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer");

            await using var host = await MarketApiHost.StartAsync();

            DepositNotes(buyer, 1500);
            DepositNotes(seller, 50);
            var listingId = ListItem(host, seller, "Bone Slicer", 100);
            await BuyAsync(host, await host.SignInForCookieAsync(buyer.Name, "pass"), listingId, 100);
            AddTransfer(TransferKind.NoteWithdraw, seller.AccountId, -20, SystemAccount.Notes, characterId: seller.CharacterId);
            AddTransfer(TransferKind.AdminAdjust, seller.AccountId, 5, SystemAccount.Admin, memo: "refund for lag");

            var history = await HistoryAsync(host, await host.SignInForCookieAsync(seller.Name, "pass"));

            Assert.AreEqual(135L, history.GetProperty("balance").GetInt64());
            Assert.AreEqual(5L, history.GetProperty("head").GetInt64());
            CollectionAssert.AreEqual(new long[] { 5, 4, 3, 2, 1 }, Sequences(history));
            CollectionAssert.AreEqual(new[]
            {
                "Admin adjustment: refund for lag · +5 MMD",
                $"Withdrew 20 trade notes · {Minus}20 MMD",
                $"Market fee · {Minus}0 MMD",
                $"Sold Bone Slicer to {buyer.CharacterName} · +100 MMD",
                "Deposited 50 trade notes · +50 MMD",
            }, Texts(history));

            var lines = history.GetProperty("transfers").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new long[] { 5, -20, 0, 100, 50 }, lines.Select(l => l.GetProperty("amount").GetInt64()).ToArray());
            CollectionAssert.AreEqual(new long[] { 135, 130, 150, 150, 50 }, lines.Select(l => l.GetProperty("balanceAfter").GetInt64()).ToArray());
            CollectionAssert.AreEqual(new[] { "admin_adjust", "note_withdraw", "purchase", "purchase", "note_deposit" }, lines.Select(l => l.GetProperty("kind").GetString()).ToArray());
            Assert.AreEqual(lines[2].GetProperty("transferId").GetInt64(), lines[3].GetProperty("transferId").GetInt64(), "the sale and its fee are one transfer");

            var buyerHistory = await HistoryAsync(host, await host.SignInForCookieAsync(buyer.Name, "pass"));

            Assert.AreEqual(1400L, buyerHistory.GetProperty("balance").GetInt64());
            Assert.AreEqual(2L, buyerHistory.GetProperty("head").GetInt64());
            CollectionAssert.AreEqual(new[]
            {
                $"Bought Bone Slicer from {seller.CharacterName} · {Minus}100 MMD",
                "Deposited 1,500 trade notes · +1,500 MMD",
            }, Texts(buyerHistory));
        }

        [TestMethod]
        public async Task History_FeeLineCarriesTheFeeReason()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);

            await using var host = await MarketApiHost.StartAsync(services => services.AddSingleton<IFeePolicy>(new FixedFeePolicy(new SellerFee(7, "7% sale fee"))));

            var listingId = ListItem(host, seller, "Frost Bow", 100);
            await BuyAsync(host, await host.SignInForCookieAsync(buyer.Name, "pass"), listingId, 100);

            var history = await HistoryAsync(host, await host.SignInForCookieAsync(seller.Name, "pass"));

            CollectionAssert.AreEqual(new[] { $"Market fee (7% sale fee) · {Minus}7 MMD", $"Sold Frost Bow to {buyer.CharacterName} · +100 MMD" }, Texts(history));
            Assert.AreEqual("7% sale fee", history.GetProperty("transfers")[0].GetProperty("memo").GetString());
            Assert.AreEqual(93L, history.GetProperty("balance").GetInt64());
        }

        [TestMethod]
        public async Task History_NeverShowsOtherAccountsOrSystemEntries()
        {
            var player = NewPlayer("player");
            var other = NewPlayer("other");

            await using var host = await MarketApiHost.StartAsync();

            DepositNotes(other, 70);
            DepositNotes(player, 10);
            DepositNotes(other, 30);

            var history = await HistoryAsync(host, await host.SignInForCookieAsync(player.Name, "pass"));

            // one line: the NOTES leg and the other account's transfers are never shown
            CollectionAssert.AreEqual(new[] { "Deposited 10 trade notes · +10 MMD" }, Texts(history));
            Assert.AreEqual(10L, history.GetProperty("balance").GetInt64());
            Assert.AreEqual(1L, history.GetProperty("head").GetInt64());

            // an account with no ledger lines at all
            var fresh = NewPlayer("fresh");
            var empty = await HistoryAsync(host, await host.SignInForCookieAsync(fresh.Name, "pass"));

            Assert.AreEqual(0L, empty.GetProperty("balance").GetInt64());
            Assert.AreEqual(0L, empty.GetProperty("head").GetInt64());
            Assert.AreEqual(0, empty.GetProperty("transfers").GetArrayLength());
            Assert.AreEqual(0, empty.GetProperty("items").GetArrayLength());
        }

        [TestMethod]
        public async Task History_NeedsASession_AndRefusesABadCursor()
        {
            var player = NewPlayer("player");

            await using var host = await MarketApiHost.StartAsync();

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/api/history")).StatusCode);

            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            foreach (var (bad, error) in new[] { ("?since=-1", "bad_cursor"), ("?since=abc", "bad_cursor"), ("?since=1.5", "bad_cursor"), ("?itemsBefore=0", "bad_cursor"), ("?itemsBefore=x", "bad_cursor"), ("?itemsLimit=0", "bad_limit"), ("?itemsLimit=x", "bad_limit") })
            {
                var response = await host.GetAsync("/api/history" + bad, cookie);

                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, bad);
                Assert.AreEqual(error, await MarketApiHost.ErrorAsync(response), bad);
            }

            // like the catalog's limit, a limit above the most is capped rather than refused
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/history?itemsLimit=101", cookie)).StatusCode);
        }

        // ---- criterion 2: since returns exactly the entries after it

        [TestMethod]
        public async Task History_Since_ReturnsExactlyTheEntriesAfterIt()
        {
            var player = NewPlayer("player");

            await using var host = await MarketApiHost.StartAsync();

            for (var i = 1; i <= 6; i++)
                DepositNotes(player, i);

            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            for (var since = 0; since <= 8; since++)
            {
                var history = await HistoryAsync(host, cookie, $"?since={since}");

                var expected = Enumerable.Range(since + 1, Math.Max(0, 6 - since)).Reverse().Select(s => (long)s).ToArray();

                CollectionAssert.AreEqual(expected, Sequences(history), $"since={since}");
                Assert.AreEqual(6L, history.GetProperty("head").GetInt64());
                Assert.AreEqual(21L, history.GetProperty("balance").GetInt64());
            }

            // the cursor is the account's own numbering, not the global entry id
            Assert.IsTrue(MarketApiTestData.Scalar($"SELECT MIN(id) FROM market_ledger_entry WHERE account_Id = {player.AccountId};") > 6);
        }

        // ---- criterion 3: polling during concurrent writes never misses an entry

        [TestMethod]
        public async Task History_PollingDuringConcurrentWrites_NeverMissesAnEntry()
        {
            const int writers = 4;
            const int perWriter = 25;

            var player = NewPlayer("hot");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var seen = new List<long>();
            var polls = 0;
            long since = 0;

            var writing = Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perWriter; i++)
                    DepositNotes(player, 1);
            })).ToArray();

            async Task PollAsync()
            {
                var history = await HistoryAsync(host, cookie, $"?since={since}");
                var head = history.GetProperty("head").GetInt64();
                var sequences = Sequences(history);

                // every poll returns exactly since+1..head, newest first: nothing skipped, nothing repeated
                CollectionAssert.AreEqual(Enumerable.Range((int)since + 1, (int)(head - since)).Reverse().Select(s => (long)s).ToArray(), sequences, $"poll since={since} head={head}");

                // the balance is the balance after the head entry
                if (sequences.Length > 0)
                    Assert.AreEqual(history.GetProperty("transfers")[0].GetProperty("balanceAfter").GetInt64(), history.GetProperty("balance").GetInt64());

                seen.AddRange(sequences);
                since = head;
                polls++;
            }

            while (!writing.All(t => t.IsCompleted))
                await PollAsync();

            await Task.WhenAll(writing);
            await PollAsync();

            CollectionAssert.AreEqual(Enumerable.Range(1, writers * perWriter).Select(s => (long)s).ToArray(), seen.OrderBy(s => s).ToArray());
            Assert.AreEqual((long)(writers * perWriter), since);
            Assert.IsTrue(polls > 2, $"the poller only ran {polls} times");
        }

        // ---- criterion 4: item movements with item names

        [TestMethod]
        public async Task History_ItemMovements_AppearWithItemNames()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer");

            await using var host = await MarketApiHost.StartAsync();
            var start = host.Clock.GetUtcNow().UtcDateTime.AddHours(-1);

            // a sword deposited, listed, delisted, listed again, expired, listed again and sold
            var sword = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Bone Slicer", VaultItemState.Held);
            var first = MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, sword, 90, ListingStatus.Delisted, start);
            var second = MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, sword, 95, ListingStatus.Expired, start);
            var third = MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, sword, 100, ListingStatus.Sold, start);
            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase, $"UPDATE market_listing SET buyer_Account_Id = {buyer.AccountId}, buyer_Character_Id = {buyer.CharacterId} WHERE id = {third};");
            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase, $"UPDATE market_vault_item SET account_Id = {buyer.AccountId}, character_Id = {buyer.CharacterId} WHERE item_Guid = {sword};");

            AddItemEvent(sword, seller, ItemEventKind.Deposit, start.AddMinutes(1));
            AddItemEvent(sword, seller, ItemEventKind.List, start.AddMinutes(2), first);
            AddItemEvent(sword, seller, ItemEventKind.Delist, start.AddMinutes(3), first);
            AddItemEvent(sword, seller, ItemEventKind.List, start.AddMinutes(4), second);
            AddItemEvent(sword, seller, ItemEventKind.Expire, start.AddMinutes(5), second);
            AddItemEvent(sword, seller, ItemEventKind.List, start.AddMinutes(6), third);
            AddItemEvent(sword, seller, ItemEventKind.Sold, start.AddMinutes(7), third);
            AddItemEvent(sword, buyer, ItemEventKind.Bought, start.AddMinutes(7), third);

            // a wand the seller deposited and took back out: its Vault row is gone, the item's own name remains
            var wand = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Vault copy of the name", VaultItemState.Held);
            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase, $"DELETE FROM market_vault_item WHERE item_Guid = {wand};");
            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase, $"INSERT INTO biota_properties_string (object_Id, type, value) VALUES ({wand}, 1, 'Wand of Sparks');");
            AddItemEvent(wand, seller, ItemEventKind.Deposit, start.AddMinutes(8));
            AddItemEvent(wand, seller, ItemEventKind.Withdraw, start.AddMinutes(9));

            // destroyed trade notes: the money line says it, so they aren't item movements
            DepositNotes(seller, 3);
            var noteTransfer = MarketApiTestData.Scalar($"SELECT MAX(transfer_Id) FROM market_ledger_entry WHERE account_Id = {seller.AccountId};");
            AddItemEvent(0xC7000000u + (uint)(seller.AccountId % 1000), seller, ItemEventKind.Deposit, start.AddMinutes(10), transferId: noteTransfer, quantity: 3);

            // another account's movement of its own item
            var other = NewPlayer("other");
            var otherItem = MarketApiTestData.AddVaultItem(other.AccountId, other.CharacterId, "Not Mine", VaultItemState.Held);
            AddItemEvent(otherItem, other, ItemEventKind.Deposit, start.AddMinutes(11));

            var history = await HistoryAsync(host, await host.SignInForCookieAsync(seller.Name, "pass"));

            CollectionAssert.AreEqual(new[]
            {
                "Withdrew Wand of Sparks",
                "Deposited Wand of Sparks",
                $"Sold Bone Slicer to {buyer.CharacterName}",
                "Listed Bone Slicer for 100 MMD",
                "Listing expired: Bone Slicer",
                "Listed Bone Slicer for 95 MMD",
                "Delisted Bone Slicer",
                "Listed Bone Slicer for 90 MMD",
                "Deposited Bone Slicer",
            }, Texts(history, "items"));

            var items = history.GetProperty("items").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { "withdraw", "deposit", "sold", "list", "expire", "list", "delist", "list", "deposit" }, items.Select(i => i.GetProperty("kind").GetString()).ToArray());
            Assert.AreEqual("Wand of Sparks", items[0].GetProperty("name").GetString());
            Assert.AreEqual(wand, items[0].GetProperty("itemGuid").GetUInt32());
            Assert.AreEqual(third, items[2].GetProperty("listingId").GetInt64());
            Assert.AreEqual(JsonValueKind.Null, items[0].GetProperty("listingId").ValueKind);

            var buyerHistory = await HistoryAsync(host, await host.SignInForCookieAsync(buyer.Name, "pass"));
            CollectionAssert.AreEqual(new[] { $"Bought Bone Slicer from {seller.CharacterName}" }, Texts(buyerHistory, "items"));

            // paging back through the movements by event id
            var cookie = await host.SignInForCookieAsync(seller.Name, "pass");
            var page = await HistoryAsync(host, cookie, "?itemsLimit=4");
            CollectionAssert.AreEqual(Texts(history, "items").Take(4).ToArray(), Texts(page, "items"));

            var next = page.GetProperty("nextItemsBefore").GetInt64();
            var rest = await HistoryAsync(host, cookie, $"?itemsLimit=4&itemsBefore={next}");
            CollectionAssert.AreEqual(Texts(history, "items").Skip(4).Take(4).ToArray(), Texts(rest, "items"));

            var last = await HistoryAsync(host, cookie, $"?itemsLimit=4&itemsBefore={rest.GetProperty("nextItemsBefore").GetInt64()}");
            CollectionAssert.AreEqual(Texts(history, "items").Skip(8).ToArray(), Texts(last, "items"));
            Assert.AreEqual(JsonValueKind.Null, last.GetProperty("nextItemsBefore").ValueKind);
        }

        [TestMethod]
        public async Task History_ItemMovements_WordBanReturnsAdminMovesAndMissingItems()
        {
            var player = NewPlayer("player");

            await using var host = await MarketApiHost.StartAsync();
            var start = host.Clock.GetUtcNow().UtcDateTime.AddHours(-1);

            var ring = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Ring of Thorns", VaultItemState.Held);
            var listing = MarketApiTestData.AddListing(player.AccountId, player.CharacterId, ring, 40, ListingStatus.BanReturned, start);
            AddItemEvent(ring, player, ItemEventKind.BanReturn, start.AddMinutes(1), listing);
            AddItemEvent(ring, player, ItemEventKind.Admin, start.AddMinutes(2));

            // an item that no longer exists anywhere
            AddItemEvent(0xC6FFFFF0u, player, ItemEventKind.Withdraw, start.AddMinutes(3));

            var history = await HistoryAsync(host, await host.SignInForCookieAsync(player.Name, "pass"));

            CollectionAssert.AreEqual(new[]
            {
                "Withdrew an unknown item",
                "Ring of Thorns moved by an admin",
                "Listing returned (account banned): Ring of Thorns",
            }, Texts(history, "items"));
        }

        [TestMethod]
        public async Task History_ItemMovementsFromTheRealListingAndPurchaseFlows_AreWorded()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 300);

            await using var host = await MarketApiHost.StartAsync();

            // the game's deposit job writes the Vault row and a deposit event
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Staff of Tides", VaultItemState.Held);
            AddItemEvent(guid, seller, ItemEventKind.Deposit, host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-5));

            var sellerCookie = await host.SignInForCookieAsync(seller.Name, "pass");

            async Task<long> ListAsync(long price)
            {
                var response = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price }, sellerCookie);
                Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
                return (await MarketApiHost.JsonAsync(response)).GetProperty("id").GetInt64();
            }

            var first = await ListAsync(250);
            Assert.AreEqual(HttpStatusCode.OK, (await host.PostJsonAsync($"/api/listings/{first}/delist", new { }, sellerCookie)).StatusCode);
            var second = await ListAsync(200);
            await BuyAsync(host, await host.SignInForCookieAsync(buyer.Name, "pass"), second, 200);

            var history = await HistoryAsync(host, await host.SignInForCookieAsync(seller.Name, "pass"));

            CollectionAssert.AreEqual(new[]
            {
                $"Sold Staff of Tides to {buyer.CharacterName}",
                "Listed Staff of Tides for 200 MMD",
                "Delisted Staff of Tides",
                "Listed Staff of Tides for 250 MMD",
                "Deposited Staff of Tides",
            }, Texts(history, "items"));
            CollectionAssert.AreEqual(new[] { $"Market fee · {Minus}0 MMD", $"Sold Staff of Tides to {buyer.CharacterName} · +200 MMD" }, Texts(history));

            var buyerHistory = await HistoryAsync(host, await host.SignInForCookieAsync(buyer.Name, "pass"));

            CollectionAssert.AreEqual(new[] { $"Bought Staff of Tides from {seller.CharacterName}" }, Texts(buyerHistory, "items"));
            CollectionAssert.AreEqual(new[] { $"Bought Staff of Tides from {seller.CharacterName} · {Minus}200 MMD" }, Texts(buyerHistory));
        }
    }
}
