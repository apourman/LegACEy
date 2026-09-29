using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// POST /listings/{id}/purchase: one save moves the item and the MMD, every refusal changes nothing, and a key is never charged twice
    /// </summary>
    [TestClass]
    public class MarketApiPurchaseTests
    {
        private sealed record Player(string Name, uint AccountId, uint CharacterId);

        private sealed record Listed(uint ItemGuid, long ListingId, long Price);

        private sealed class TestFeePolicy : IFeePolicy
        {
            private readonly Func<Sale, SellerFee> quote;

            public List<Sale> Sales { get; } = new List<Sale>();

            public TestFeePolicy(Func<Sale, SellerFee> quote)
            {
                this.quote = quote;
            }

            public SellerFee Quote(Sale sale)
            {
                lock (Sales)
                    Sales.Add(sale);

                return quote(sale);
            }
        }

        private sealed class TestPause : IMarketPause
        {
            public bool Paused { get; set; }

            public bool IsPaused => Paused;
        }

        private static Player NewPlayer(string prefix, long? balance = null)
        {
            var name = MarketApiTestData.UniqueName(prefix);
            var accountId = MarketApiTestData.CreateAccount(name, "pass");
            var characterId = MarketApiTestData.AddCharacter(accountId, name + "Main");

            if (balance.HasValue)
                MarketApiTestData.SetBalance(accountId, balance.Value);

            return new Player(name, accountId, characterId);
        }

        /// <summary>
        /// A listed Vault item of the seller's with an active listing created a minute before the host's clock
        /// </summary>
        private static Listed NewListing(MarketApiHost host, Player seller, long price, int stackSize = 1, string name = "Bone Slicer")
        {
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, name, VaultItemState.Listed, wcid: 35, stackSize: stackSize);
            var id = MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, guid, price, ListingStatus.Active, host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));

            return new Listed(guid, id, price);
        }

        private static Task<HttpResponseMessage> PurchaseAsync(MarketApiHost host, string cookie, Listed listed, string key = null, long? expectedPrice = null, int count = 1) =>
            host.PostJsonAsync($"/listings/{listed.ListingId}/purchase", new { count, expectedPrice = expectedPrice ?? listed.Price, idempotencyKey = key ?? Guid.NewGuid().ToString("N") }, cookie);

        private static long Balance(uint accountId) => MarketApiTestData.Scalar($"SELECT IFNULL((SELECT balance FROM market_balance WHERE account_Id = {accountId}), 0);");

        private static long Transfers(long listingId) => MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_transfer WHERE listing_Id = {listingId};");

        private static string ListingRow(long listingId) => MarketApiTestData.Rows($"SELECT CONCAT(status, '|', IFNULL(buyer_Account_Id, '-'), '|', IFNULL(buyer_Character_Id, '-'), '|', row_Version) FROM market_listing WHERE id = {listingId};").Single();

        private static string VaultRow(uint itemGuid) => MarketApiTestData.Rows($"SELECT CONCAT(account_Id, '|', character_Id, '|', state, '|', row_Version) FROM market_vault_item WHERE item_Guid = {itemGuid};").Single();

        /// <summary>
        /// Everything a refused purchase must leave alone
        /// </summary>
        private static string Snapshot(Listed listed, params Player[] players)
        {
            var balances = string.Join(",", players.Select(p => MarketApiTestData.Rows($"SELECT CONCAT(balance, ':', last_Sequence, ':', row_Version) FROM market_balance WHERE account_Id = {p.AccountId};").SingleOrDefault() ?? "none"));
            var events = MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_item_event WHERE item_Guid = {listed.ItemGuid};");

            return $"{ListingRow(listed.ListingId)} / {VaultRow(listed.ItemGuid)} / {balances} / transfers {Transfers(listed.ListingId)} / events {events}";
        }

        private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string error)
        {
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(status, response.StatusCode, body);
            Assert.AreEqual(error, JsonDocument.Parse(body).RootElement.GetProperty("error").GetString(), body);
        }

        // ---- a sale

        [TestMethod]
        public async Task Purchase_Success_MovesTheItemAndTheMoneyInOneZeroSumTransferWithAZeroFeePair()
        {
            var seller = NewPlayer("seller", balance: 5);
            var buyer = NewPlayer("buyer", balance: 500);

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 120);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var key = "buy-" + Guid.NewGuid().ToString("N");

            var response = await PurchaseAsync(host, cookie, listed, key);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = await MarketApiHost.JsonAsync(response);
            Assert.AreEqual("ok", body.GetProperty("status").GetString());
            Assert.AreEqual(listed.ListingId, body.GetProperty("listingId").GetInt64());
            Assert.AreEqual(listed.ItemGuid, body.GetProperty("itemGuid").GetUInt32());
            Assert.AreEqual(120L, body.GetProperty("price").GetInt64());
            Assert.AreEqual(0L, body.GetProperty("fee").GetInt64());
            Assert.AreEqual(380L, body.GetProperty("balance").GetInt64());

            // the listing is sold to the buyer and the Vault row is the buyer's, held; each row version moved once
            Assert.AreEqual($"sold|{buyer.AccountId}|{buyer.CharacterId}|1", ListingRow(listed.ListingId));
            Assert.AreEqual(60_000_000L, MarketApiTestData.Scalar($"SELECT TIMESTAMPDIFF(MICROSECOND, created_Time, closed_Time) FROM market_listing WHERE id = {listed.ListingId};"), "closed at the clock's time");
            Assert.AreEqual($"{buyer.AccountId}|{buyer.CharacterId}|held|1", VaultRow(listed.ItemGuid));

            Assert.AreEqual(380L, Balance(buyer.AccountId));
            Assert.AreEqual(125L, Balance(seller.AccountId));

            // one purchase transfer: buyer -price, seller +price, seller -fee, FEES +fee, adding up to zero
            var transfer = MarketApiTestData.Rows($"SELECT CONCAT(id, '|', kind, '|', actor_Account_Id, '|', actor_Character_Id, '|', request_Key) FROM market_transfer WHERE listing_Id = {listed.ListingId};").Single().Split('|');
            CollectionAssert.AreEqual(new[] { "purchase", buyer.AccountId.ToString(), buyer.CharacterId.ToString(), key }, transfer.Skip(1).ToArray());
            var entries = MarketApiTestData.Rows($"SELECT CONCAT(IFNULL(account_Id, system_Account), ':', amount, ':', IFNULL(balance_After, '-')) FROM market_ledger_entry WHERE transfer_Id = {transfer[0]} ORDER BY id;");
            CollectionAssert.AreEqual(new[] { $"{buyer.AccountId}:-120:380", $"{seller.AccountId}:120:125", $"{seller.AccountId}:0:125", "FEES:0:-" }, entries);
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT SUM(amount) FROM market_ledger_entry WHERE transfer_Id = {transfer[0]};"));

            // sold and bought item events, tied to the listing and the transfer
            var events = MarketApiTestData.Rows($"SELECT CONCAT(kind, ':', account_Id, ':', character_Id, ':', listing_Id, ':', transfer_Id) FROM market_item_event WHERE item_Guid = {listed.ItemGuid} ORDER BY id;");
            CollectionAssert.AreEqual(new[] { $"sold:{seller.AccountId}:{seller.CharacterId}:{listed.ListingId}:{transfer[0]}", $"bought:{buyer.AccountId}:{buyer.CharacterId}:{listed.ListingId}:{transfer[0]}" }, events);

            // the request row stores the answer
            Assert.AreEqual("purchase", MarketApiTestData.Rows($"SELECT kind FROM market_request WHERE account_Id = {buyer.AccountId} AND idempotency_Key = '{key}';").Single());

            // the buyer's Vault has it; the seller's doesn't
            var vault = await MarketApiHost.JsonAsync(await host.GetAsync("/vault", cookie));
            Assert.IsTrue(vault.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("itemGuid").GetUInt32() == listed.ItemGuid && i.GetProperty("state").GetString() == VaultItemState.Held));
            var sellerVault = await MarketApiHost.JsonAsync(await host.GetAsync("/vault", await host.SignInForCookieAsync(seller.Name, "pass")));
            Assert.IsFalse(sellerVault.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("itemGuid").GetUInt32() == listed.ItemGuid));

            // and it's gone from the catalog
            Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/listings/{listed.ListingId}")).StatusCode);
        }

        [TestMethod]
        public async Task Purchase_WholeStack_IsBought_PartOfAStackIsRefused()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 100);

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 30, stackSize: 5, name: "Stacked");
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var before = Snapshot(listed, buyer, seller);

            await AssertRefusedAsync(await PurchaseAsync(host, cookie, listed, count: 1), HttpStatusCode.BadRequest, "invalid_count");
            await AssertRefusedAsync(await PurchaseAsync(host, cookie, listed, count: 6), HttpStatusCode.BadRequest, "invalid_count");
            Assert.AreEqual(before, Snapshot(listed, buyer, seller));

            var response = await PurchaseAsync(host, cookie, listed, count: 5);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(70L, Balance(buyer.AccountId));
        }

        [TestMethod]
        public async Task Purchase_BadRequest_IsRefused()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 100);

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 30);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var path = $"/listings/{listed.ListingId}/purchase";

            await AssertRefusedAsync(await host.PostJsonAsync(path, new { count = 1, expectedPrice = 30 }, cookie), HttpStatusCode.BadRequest, "bad_request");
            await AssertRefusedAsync(await host.PostJsonAsync(path, new { count = 1, expectedPrice = 30, idempotencyKey = new string('k', 65) }, cookie), HttpStatusCode.BadRequest, "bad_request");
            await AssertRefusedAsync(await host.PostJsonAsync(path, new { count = 1, idempotencyKey = "k" }, cookie), HttpStatusCode.BadRequest, "invalid_price");
            await AssertRefusedAsync(await host.PostJsonAsync(path, new { count = 1, expectedPrice = 29.5, idempotencyKey = "k" }, cookie), HttpStatusCode.BadRequest, "invalid_price");
            await AssertRefusedAsync(await host.PostJsonAsync(path, new { expectedPrice = 30, idempotencyKey = "k" }, cookie), HttpStatusCode.BadRequest, "invalid_count");
            await AssertRefusedAsync(await host.PostJsonAsync(path, new { count = 1, expectedPrice = 30, idempotencyKey = "k", characterId = seller.CharacterId }, cookie), HttpStatusCode.BadRequest, "invalid_character");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await PurchaseAsync(host, null, listed)).StatusCode);

            Assert.AreEqual(ListingStatus.Active, ListingRow(listed.ListingId).Split('|')[0]);
            Assert.AreEqual(100L, Balance(buyer.AccountId));
        }

        [TestMethod]
        public async Task Purchase_AsAnotherOfTheBuyersCharacters_PutsThatCharacterOnTheVaultRow()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 100);
            var alt = MarketApiTestData.AddCharacter(buyer.AccountId, buyer.Name + "Alt");

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 30);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");

            var response = await host.PostJsonAsync($"/listings/{listed.ListingId}/purchase", new { count = 1, expectedPrice = 30, idempotencyKey = "alt", characterId = alt }, cookie);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual($"{buyer.AccountId}|{alt}|held|1", VaultRow(listed.ItemGuid));
            Assert.AreEqual($"sold|{buyer.AccountId}|{alt}|1", ListingRow(listed.ListingId));
        }

        // ---- the answers

        [TestMethod]
        public async Task Purchase_PriceChanged_AnswersPriceChangedWithTheCurrentPriceAndChangesNothing()
        {
            var seller = NewPlayer("seller", balance: 1);
            var buyer = NewPlayer("buyer", balance: 500);

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 120);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var before = Snapshot(listed, buyer, seller);

            var response = await PurchaseAsync(host, cookie, listed, expectedPrice: 100);

            await AssertRefusedAsync(response, HttpStatusCode.Conflict, "price_changed");
            Assert.AreEqual(120L, (await MarketApiHost.JsonAsync(response)).GetProperty("price").GetInt64());
            Assert.AreEqual(before, Snapshot(listed, buyer, seller));
        }

        [TestMethod]
        public async Task Purchase_ListingGone_AnswersGone_ForSoldDelistedExpiredReturnedAndUnknown()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var now = host.Clock.GetUtcNow().UtcDateTime;

            foreach (var status in new[] { ListingStatus.Sold, ListingStatus.Delisted, ListingStatus.Expired, ListingStatus.BanReturned })
            {
                var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Closed " + status, VaultItemState.Held);
                var listed = new Listed(guid, MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, guid, 10, status, now.AddMinutes(-1)), 10);
                var before = Snapshot(listed, buyer, seller);

                await AssertRefusedAsync(await PurchaseAsync(host, cookie, listed), HttpStatusCode.Gone, "gone");
                Assert.AreEqual(before, Snapshot(listed, buyer, seller), status);
            }

            // still marked active but past its lifetime
            var overdueGuid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Overdue", VaultItemState.Listed);
            var overdue = new Listed(overdueGuid, MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, overdueGuid, 10, ListingStatus.Active, now.AddDays(-14)), 10);
            await AssertRefusedAsync(await PurchaseAsync(host, cookie, overdue), HttpStatusCode.Gone, "gone");
            Assert.AreEqual(0L, Transfers(overdue.ListingId));

            await AssertRefusedAsync(await PurchaseAsync(host, cookie, new Listed(1, long.MaxValue, 10)), HttpStatusCode.Gone, "gone");
            Assert.AreEqual(500L, Balance(buyer.AccountId));
        }

        [TestMethod]
        public async Task Purchase_InsufficientFunds_AnswersInsufficientFundsAndChangesNothing()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 119);
            var penniless = NewPlayer("penniless");

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 120);
            var before = Snapshot(listed, buyer, seller, penniless);

            await AssertRefusedAsync(await PurchaseAsync(host, await host.SignInForCookieAsync(buyer.Name, "pass"), listed), HttpStatusCode.Conflict, "insufficient_funds");
            await AssertRefusedAsync(await PurchaseAsync(host, await host.SignInForCookieAsync(penniless.Name, "pass"), listed), HttpStatusCode.Conflict, "insufficient_funds");

            Assert.AreEqual(before, Snapshot(listed, buyer, seller, penniless));
        }

        [TestMethod]
        public async Task Purchase_FromAnotherCharacterOnTheSameAccount_AnswersOwnListing()
        {
            var seller = NewPlayer("seller", balance: 500);
            var alt = MarketApiTestData.AddCharacter(seller.AccountId, seller.Name + "Alt");

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 50);
            var cookie = await host.SignInForCookieAsync(seller.Name, "pass");
            var before = Snapshot(listed, seller);

            var response = await host.PostJsonAsync($"/listings/{listed.ListingId}/purchase", new { count = 1, expectedPrice = 50, idempotencyKey = "own", characterId = alt }, cookie);

            await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "own_listing");
            Assert.AreEqual(before, Snapshot(listed, seller));
        }

        [TestMethod]
        public async Task Purchase_TooFast_AnswersRateLimited_UntilAMinuteHasPassed()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 1000);

            MarketApiTestData.SetSetting(MarketSettings.PurchasesPerMinute.Key, 3);

            try
            {
                await using var host = await MarketApiHost.StartAsync();
                var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
                var listings = Enumerable.Range(0, 5).Select(i => NewListing(host, seller, 10, name: "Fast " + i)).ToArray();

                for (var i = 0; i < 3; i++)
                {
                    var ok = await PurchaseAsync(host, cookie, listings[i]);
                    Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, await ok.Content.ReadAsStringAsync());
                }

                var before = Snapshot(listings[3], buyer, seller);
                await AssertRefusedAsync(await PurchaseAsync(host, cookie, listings[3]), HttpStatusCode.TooManyRequests, "rate_limited");
                Assert.AreEqual(before, Snapshot(listings[3], buyer, seller));

                // still limited just before the minute is up
                host.Clock.Advance(TimeSpan.FromSeconds(59));
                await AssertRefusedAsync(await PurchaseAsync(host, cookie, listings[3]), HttpStatusCode.TooManyRequests, "rate_limited");

                // a minute after the first three
                host.Clock.Advance(TimeSpan.FromSeconds(2));
                var later = await PurchaseAsync(host, cookie, listings[3]);
                Assert.AreEqual(HttpStatusCode.OK, later.StatusCode, await later.Content.ReadAsStringAsync());

                // another buyer isn't slowed by this one
                var other = NewPlayer("other", balance: 100);
                var otherResponse = await PurchaseAsync(host, await host.SignInForCookieAsync(other.Name, "pass"), listings[4]);
                Assert.AreEqual(HttpStatusCode.OK, otherResponse.StatusCode, await otherResponse.Content.ReadAsStringAsync());
            }
            finally
            {
                MarketApiTestData.ClearSetting(MarketSettings.PurchasesPerMinute.Key);
            }
        }

        [TestMethod]
        public async Task Purchase_DefaultRateLimit_IsTenPerMinute()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 1000);

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var listed = NewListing(host, seller, 10);

            // ten refused attempts (a wrong price), then the eleventh is rate limited
            for (var i = 0; i < 10; i++)
                await AssertRefusedAsync(await PurchaseAsync(host, cookie, listed, expectedPrice: 9), HttpStatusCode.Conflict, "price_changed");

            await AssertRefusedAsync(await PurchaseAsync(host, cookie, listed), HttpStatusCode.TooManyRequests, "rate_limited");
        }

        [TestMethod]
        public async Task Purchase_MarketPaused_AnswersPausedAndChangesNothing()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);
            var pause = new TestPause { Paused = true };

            await using var host = await MarketApiHost.StartAsync(services => services.AddSingleton<IMarketPause>(pause));
            var listed = NewListing(host, seller, 50);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var before = Snapshot(listed, buyer, seller);

            await AssertRefusedAsync(await PurchaseAsync(host, cookie, listed), HttpStatusCode.ServiceUnavailable, "paused");
            Assert.AreEqual(before, Snapshot(listed, buyer, seller));

            // browsing still works while paused, and buying works again once it's lifted
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/listings/{listed.ListingId}")).StatusCode);

            pause.Paused = false;
            var response = await PurchaseAsync(host, cookie, listed);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // ---- idempotency

        [TestMethod]
        public async Task Purchase_SameKeyAgain_ReturnsTheFirstResultAndChargesNothingMore()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 120);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var key = "double-click";

            var first = await PurchaseAsync(host, cookie, listed, key);
            var firstBody = await first.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, firstBody);

            var after = Snapshot(listed, buyer, seller);

            // the same request again, and again after the balance changed: the stored answer each time
            var second = await PurchaseAsync(host, cookie, listed, key);
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
            Assert.AreEqual(firstBody, await second.Content.ReadAsStringAsync());

            Assert.AreEqual(after, Snapshot(listed, buyer, seller));

            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase, $"UPDATE market_balance SET balance = 0, row_Version = row_Version + 1 WHERE account_Id = {buyer.AccountId};");
            var third = await PurchaseAsync(host, cookie, listed, key);
            Assert.AreEqual(HttpStatusCode.OK, third.StatusCode);
            Assert.AreEqual(firstBody, await third.Content.ReadAsStringAsync());

            Assert.AreEqual(0L, Balance(buyer.AccountId));
            Assert.AreEqual(1L, Transfers(listed.ListingId));
            Assert.AreEqual(1L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_request WHERE account_Id = {buyer.AccountId};"));

            // keys are the buyer's own: another buyer's same key is a new request
            var other = NewPlayer("other", balance: 100);
            var second2 = NewListing(host, seller, 10);
            var otherResponse = await PurchaseAsync(host, await host.SignInForCookieAsync(other.Name, "pass"), second2, key);
            Assert.AreEqual(HttpStatusCode.OK, otherResponse.StatusCode, await otherResponse.Content.ReadAsStringAsync());
            Assert.AreEqual(second2.ListingId, (await MarketApiHost.JsonAsync(otherResponse)).GetProperty("listingId").GetInt64());
        }

        [TestMethod]
        public async Task Purchase_SameKeyConcurrently_ChargesOnceAndEveryRequestGetsTheSameAnswer()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 120);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");

            var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => PurchaseAsync(host, cookie, listed, "retry-storm"))));
            var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

            Assert.IsTrue(responses.All(r => r.StatusCode == HttpStatusCode.OK), string.Join(" ", bodies));
            Assert.AreEqual(1, bodies.Distinct().Count(), string.Join(" ", bodies));
            Assert.AreEqual(1L, Transfers(listed.ListingId));
            Assert.AreEqual(380L, Balance(buyer.AccountId));
        }

        // ---- concurrency

        [TestMethod]
        public async Task Purchase_ManyBuyersAtOnce_ExactlyOneWinsAndNoBalanceGoesNegative()
        {
            var seller = NewPlayer("seller");
            var buyers = Enumerable.Range(0, 8).Select(i => NewPlayer("rush", balance: 150)).ToArray();

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 100);
            var cookies = new List<string>();
            foreach (var buyer in buyers)
                cookies.Add(await host.SignInForCookieAsync(buyer.Name, "pass"));

            var responses = await Task.WhenAll(cookies.Select(cookie => Task.Run(() => PurchaseAsync(host, cookie, listed))));
            var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

            Assert.AreEqual(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK), string.Join(" ", bodies));
            Assert.IsTrue(responses.Where(r => r.StatusCode != HttpStatusCode.OK).All(r => r.StatusCode == HttpStatusCode.Gone), string.Join(" ", bodies));

            var winner = buyers[Array.FindIndex(responses, r => r.StatusCode == HttpStatusCode.OK)];
            Assert.AreEqual($"sold|{winner.AccountId}|{winner.CharacterId}|1", ListingRow(listed.ListingId));
            Assert.AreEqual($"{winner.AccountId}|{winner.CharacterId}|held|1", VaultRow(listed.ItemGuid));
            Assert.AreEqual(1L, Transfers(listed.ListingId));
            Assert.AreEqual(100L, Balance(seller.AccountId));

            foreach (var buyer in buyers)
                Assert.AreEqual(buyer == winner ? 50L : 150L, Balance(buyer.AccountId));
        }

        [TestMethod]
        public async Task Purchase_OneBuyerManyListingsAtOnce_NeverSpendsMoreThanTheBalance()
        {
            var buyer = NewPlayer("spender", balance: 250);
            var sellers = Enumerable.Range(0, 6).Select(i => NewPlayer("stall")).ToArray();

            await using var host = await MarketApiHost.StartAsync();
            var listings = sellers.Select(s => NewListing(host, s, 100)).ToArray();
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");

            var responses = await Task.WhenAll(listings.Select(l => Task.Run(() => PurchaseAsync(host, cookie, l))));
            var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

            Assert.AreEqual(2, responses.Count(r => r.StatusCode == HttpStatusCode.OK), string.Join(" ", bodies));
            Assert.IsTrue(bodies.Where((b, i) => responses[i].StatusCode != HttpStatusCode.OK).All(b => b.Contains("insufficient_funds")), string.Join(" ", bodies));
            Assert.AreEqual(50L, Balance(buyer.AccountId));
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_ledger_entry WHERE account_Id = {buyer.AccountId} AND balance_After < 0;"));
            Assert.AreEqual(200L, sellers.Sum(s => Balance(s.AccountId)));

            // the buyer's sequence has no gaps or duplicates
            CollectionAssert.AreEqual(new[] { "1", "2" }, MarketApiTestData.Rows($"SELECT sequence FROM market_ledger_entry WHERE account_Id = {buyer.AccountId} ORDER BY sequence;"));
        }

        // ---- bans

        [TestMethod]
        public async Task Purchase_SellerBanned_AnswersGoneReturnsTheListingAndChargesNothing()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 50);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            MarketApiTestData.Ban(seller.AccountId, DateTime.UtcNow.AddDays(3));

            await AssertRefusedAsync(await PurchaseAsync(host, cookie, listed), HttpStatusCode.Gone, "gone");

            Assert.AreEqual(500L, Balance(buyer.AccountId));
            Assert.AreEqual(0L, Transfers(listed.ListingId));
            Assert.AreEqual($"{seller.AccountId}|{seller.CharacterId}|held|1", VaultRow(listed.ItemGuid));
            Assert.AreEqual(ListingStatus.BanReturned, ListingRow(listed.ListingId).Split('|')[0]);
        }

        [TestMethod]
        public async Task Purchase_BuyerBanned_IsRefused()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);

            await using var host = await MarketApiHost.StartAsync();
            var listed = NewListing(host, seller, 50);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var before = Snapshot(listed, buyer, seller);
            MarketApiTestData.Ban(buyer.AccountId, DateTime.UtcNow.AddDays(3));

            // the session stops working once the ban is noticed
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await PurchaseAsync(host, cookie, listed)).StatusCode);
            Assert.AreEqual(before, Snapshot(listed, buyer, seller));
        }

        [TestMethod]
        public async Task Purchase_BanLandsBetweenTheReadAndTheSave_BuyerIsRefusedAndSellerIsGone()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);
            Player toBan = null;

            // the fee policy is asked after the listing was read and before the save: ban someone at that moment
            var policy = new TestFeePolicy(sale =>
            {
                MarketApiTestData.Ban(toBan.AccountId, DateTime.UtcNow.AddDays(3));
                return new SellerFee(0);
            });

            await using var host = await MarketApiHost.StartAsync(services => services.AddSingleton<IFeePolicy>(policy));
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");

            toBan = buyer;
            var first = NewListing(host, seller, 50);
            var before = Snapshot(first, buyer, seller);
            await AssertRefusedAsync(await PurchaseAsync(host, cookie, first), HttpStatusCode.Forbidden, "banned");
            Assert.AreEqual(before, Snapshot(first, buyer, seller));

            MarketApiTestData.LiftBan(buyer.AccountId);
            toBan = seller;
            var second = NewListing(host, seller, 50);
            await AssertRefusedAsync(await PurchaseAsync(host, cookie, second), HttpStatusCode.Gone, "gone");
            Assert.AreEqual(500L, Balance(buyer.AccountId));
            Assert.AreEqual(0L, Transfers(second.ListingId));
            Assert.AreEqual($"{seller.AccountId}|{seller.CharacterId}|held|1", VaultRow(second.ItemGuid));
            Assert.AreEqual(ListingStatus.BanReturned, ListingRow(second.ListingId).Split('|')[0]);
        }

        // ---- the fee policy

        [TestMethod]
        public async Task Purchase_FeePolicyOutsideTheContract_IsRejectedAndChargesNothing()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);
            var fee = 0m;

            await using var host = await MarketApiHost.StartAsync(services => services.AddSingleton<IFeePolicy>(new TestFeePolicy(_ => new SellerFee(fee, "test"))));
            var listed = NewListing(host, seller, 100);
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");
            var before = Snapshot(listed, buyer, seller);

            foreach (var bad in new[] { 101m, -1m, 2.5m })
            {
                fee = bad;
                await AssertRefusedAsync(await PurchaseAsync(host, cookie, listed), HttpStatusCode.InternalServerError, "invalid_fee");
                Assert.AreEqual(before, Snapshot(listed, buyer, seller), bad.ToString());
            }

            // the whole price is still inside the contract
            fee = 100m;
            var response = await PurchaseAsync(host, cookie, listed);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(0L, Balance(seller.AccountId));
        }

        [TestMethod]
        public async Task Purchase_ValidFee_IsChargedToTheSellerAndRecordedOnTheLedgerEntry()
        {
            var seller = NewPlayer("seller");
            var buyer = NewPlayer("buyer", balance: 500);
            var policy = new TestFeePolicy(sale => new SellerFee(7, "test fee"));

            await using var host = await MarketApiHost.StartAsync(services => services.AddSingleton<IFeePolicy>(policy));
            var listed = NewListing(host, seller, 100, stackSize: 3);
            MarketApiTestData.SetVaultColumns(listed.ItemGuid, "item_Type = 1");
            var cookie = await host.SignInForCookieAsync(buyer.Name, "pass");

            var response = await PurchaseAsync(host, cookie, listed, count: 3);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(7L, (await MarketApiHost.JsonAsync(response)).GetProperty("fee").GetInt64());
            Assert.AreEqual(400L, Balance(buyer.AccountId));
            Assert.AreEqual(93L, Balance(seller.AccountId));

            var entries = MarketApiTestData.Rows($"SELECT CONCAT(IFNULL(e.account_Id, e.system_Account), ':', e.amount, ':', IFNULL(e.memo, '-')) FROM market_ledger_entry e JOIN market_transfer t ON t.id = e.transfer_Id WHERE t.listing_Id = {listed.ListingId} ORDER BY e.id;");
            CollectionAssert.AreEqual(new[] { $"{buyer.AccountId}:-100:-", $"{seller.AccountId}:100:-", $"{seller.AccountId}:-7:test fee", "FEES:7:-" }, entries);

            // the policy was given the sale
            Assert.AreEqual(new Sale(seller.AccountId, buyer.AccountId, listed.ListingId, 35, 1, 3, 100), policy.Sales.Single());
        }

        [TestMethod]
        public void ZeroFeePolicy_ChargesNothing()
        {
            var fee = new ZeroFeePolicy().Quote(new Sale(1, 2, 3, 35, 1, 1, 1000));

            Assert.AreEqual(0m, fee.Amount);
        }
    }
}
