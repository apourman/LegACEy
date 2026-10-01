using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Database.Tests.Market;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// The game bridge from the web side: POST /vault/withdraw and POST /mmd/withdraw create tickets, GET /tickets/{id} shows what the game server did.
    /// The game server's side is played here by the shared store it uses (TicketStore.Claim, Complete and Fail); seam 2 drives the real one.
    /// </summary>
    [TestClass]
    public class MarketApiTicketTests
    {
        private sealed record Player(string Name, uint AccountId, uint CharacterId);

        private static Player NewPlayer(string prefix = "bridge")
        {
            var name = MarketApiTestData.UniqueName(prefix);
            var accountId = MarketApiTestData.CreateAccount(name, "pass");

            return new Player(name, accountId, MarketApiTestData.AddCharacter(accountId, name + "Main"));
        }

        private static string NewKey() => Guid.NewGuid().ToString("N");

        private static Task<HttpResponseMessage> MmdWithdrawAsync(MarketApiHost host, string cookie, uint characterId, object amount, string idempotencyKey) =>
            host.PostJsonAsync("/mmd/withdraw", new { characterId, amount, idempotencyKey }, cookie);

        private static Task<HttpResponseMessage> VaultWithdrawAsync(MarketApiHost host, string cookie, uint characterId, uint itemGuid, string idempotencyKey) =>
            host.PostJsonAsync("/vault/withdraw", new { characterId, itemGuid, idempotencyKey }, cookie);

        private static async Task<JsonElement> AcceptedAsync(HttpResponseMessage response)
        {
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());

            var ticket = await MarketApiHost.JsonAsync(response);
            Assert.AreEqual($"/tickets/{ticket.GetProperty("id").GetInt64()}", response.Headers.Location?.OriginalString);

            return ticket;
        }

        private static async Task<JsonElement> TicketAsync(MarketApiHost host, string cookie, long id)
        {
            var response = await host.GetAsync($"/tickets/{id}", cookie);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            return await MarketApiHost.JsonAsync(response);
        }

        private static long Tickets(uint accountId) => MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_ticket WHERE account_Id = {accountId};");

        /// <summary>
        /// What the game server does first: claims the ticket (and only this test's ticket, since the shared shard holds every test's)
        /// </summary>
        private static void ClaimAsTheGame(long id) =>
            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase, $"UPDATE market_ticket SET status = '{TicketStatus.Claimed}', claimed_Time = UTC_TIMESTAMP(6) WHERE id = {id} AND status = '{TicketStatus.Waiting}';");

        // ---- creating tickets

        [TestMethod]
        public async Task MmdWithdraw_CreatesAWaitingTicket_AndItsStatusShowsWhatTheGameDid()
        {
            var player = NewPlayer();

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var ticket = await AcceptedAsync(await MmdWithdrawAsync(host, cookie, player.CharacterId, 20, NewKey()));
            var id = ticket.GetProperty("id").GetInt64();

            Assert.AreEqual(TicketKind.MmdWithdraw, ticket.GetProperty("kind").GetString());
            Assert.AreEqual(TicketStatus.Waiting, ticket.GetProperty("status").GetString());
            Assert.AreEqual(player.CharacterId, ticket.GetProperty("characterId").GetUInt32());
            Assert.AreEqual(20, ticket.GetProperty("amount").GetInt64());
            Assert.AreEqual(JsonValueKind.Null, ticket.GetProperty("itemGuid").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, ticket.GetProperty("resultCode").ValueKind);

            Assert.AreEqual($"{TicketKind.MmdWithdraw}|{player.AccountId}|{player.CharacterId}|{TicketStatus.Waiting}|20",
                MarketApiTestData.Rows($"SELECT kind, account_Id, character_Id, status, payload->>'$.amount' FROM market_ticket WHERE id = {id};").Single());
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_request WHERE account_Id = {player.AccountId};"), "a ticket is its own stored result");

            ClaimAsTheGame(id);
            Assert.AreEqual(TicketStatus.Claimed, (await TicketAsync(host, cookie, id)).GetProperty("status").GetString());

            using (var shard = MarketApiTestData.Shard())
            {
                TicketStore.Complete(shard, new TicketCompletion(id, "You withdraw 20 trade notes."), DateTime.UtcNow);
                shard.SaveChanges();
            }

            var done = await TicketAsync(host, cookie, id);
            Assert.AreEqual(TicketStatus.Done, done.GetProperty("status").GetString());
            Assert.AreEqual(TicketStore.Ok, done.GetProperty("resultCode").GetString());
            Assert.AreEqual("You withdraw 20 trade notes.", done.GetProperty("resultMessage").GetString());
            Assert.AreEqual(JsonValueKind.String, done.GetProperty("finishedTime").ValueKind);
        }

        [TestMethod]
        public async Task VaultWithdraw_CreatesAWaitingTicket_AndAFailureShowsItsReason()
        {
            var player = NewPlayer();
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Bridge Sword", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var ticket = await AcceptedAsync(await VaultWithdrawAsync(host, cookie, player.CharacterId, guid, NewKey()));
            var id = ticket.GetProperty("id").GetInt64();

            Assert.AreEqual(TicketKind.VaultWithdraw, ticket.GetProperty("kind").GetString());
            Assert.AreEqual(TicketStatus.Waiting, ticket.GetProperty("status").GetString());
            Assert.AreEqual(guid, ticket.GetProperty("itemGuid").GetUInt32());
            Assert.AreEqual(JsonValueKind.Null, ticket.GetProperty("amount").ValueKind);
            Assert.AreEqual(VaultItemState.Held, MarketApiTestData.Rows($"SELECT state FROM market_vault_item WHERE item_Guid = {guid};").Single(), "only the game server moves the item");

            ClaimAsTheGame(id);
            using (var shard = MarketApiTestData.Shard())
                Assert.IsTrue(TicketStore.Fail(shard, id, "offline", "Your character must be online.", DateTime.UtcNow));

            var failed = await TicketAsync(host, cookie, id);
            Assert.AreEqual(TicketStatus.Failed, failed.GetProperty("status").GetString());
            Assert.AreEqual("offline", failed.GetProperty("resultCode").GetString());
            Assert.AreEqual("Your character must be online.", failed.GetProperty("resultMessage").GetString());
        }

        [TestMethod]
        public async Task Withdraw_WithAPluginToken_CreatesATicket()
        {
            var player = NewPlayer();

            await using var host = await MarketApiHost.StartAsync();

            string code;
            using (var shard = MarketApiTestData.Shard())
                code = PluginAuth.NewLinkCode(shard, player.AccountId, player.CharacterId, host.Clock.GetUtcNow().UtcDateTime);

            var exchanged = await host.PostJsonAsync("/auth/plugin-token", new { code, label = "plugin" });
            Assert.AreEqual(HttpStatusCode.OK, exchanged.StatusCode, await exchanged.Content.ReadAsStringAsync());
            var token = (await MarketApiHost.JsonAsync(exchanged)).GetProperty("token").GetString();

            var ticket = await AcceptedAsync(await host.PostJsonAsync("/mmd/withdraw", new { characterId = player.CharacterId, amount = 3, idempotencyKey = NewKey() }, token: token));

            var read = await host.GetWithTokenAsync($"/tickets/{ticket.GetProperty("id").GetInt64()}", token);
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        }

        // ---- idempotency

        [TestMethod]
        public async Task SameKeyAgain_ReturnsTheSameTicket_AndCreatesNoOther()
        {
            var player = NewPlayer();
            var key = NewKey();

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var first = await AcceptedAsync(await MmdWithdrawAsync(host, cookie, player.CharacterId, 20, key));
            var again = await AcceptedAsync(await MmdWithdrawAsync(host, cookie, player.CharacterId, 20, key));

            // a retry with a changed body is still the first request
            var changed = await AcceptedAsync(await MmdWithdrawAsync(host, cookie, player.CharacterId, 99, key));

            Assert.AreEqual(first.GetProperty("id").GetInt64(), again.GetProperty("id").GetInt64());
            Assert.AreEqual(first.GetProperty("id").GetInt64(), changed.GetProperty("id").GetInt64());
            Assert.AreEqual(20, changed.GetProperty("amount").GetInt64());
            Assert.AreEqual(1L, Tickets(player.AccountId));

            // the replay shows the ticket as it is now
            ClaimAsTheGame(first.GetProperty("id").GetInt64());
            Assert.AreEqual(TicketStatus.Claimed, (await AcceptedAsync(await MmdWithdrawAsync(host, cookie, player.CharacterId, 20, key))).GetProperty("status").GetString());

            // another account's key space is its own
            var other = NewPlayer();
            var otherCookie = await host.SignInForCookieAsync(other.Name, "pass");
            var theirs = await AcceptedAsync(await MmdWithdrawAsync(host, otherCookie, other.CharacterId, 20, key));
            Assert.AreNotEqual(first.GetProperty("id").GetInt64(), theirs.GetProperty("id").GetInt64());
        }

        [TestMethod]
        public async Task SameKeyConcurrently_CreatesOneTicket_AndEveryRequestGetsIt()
        {
            var player = NewPlayer();
            var key = NewKey();

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => MmdWithdrawAsync(host, cookie, player.CharacterId, 5, key)));

            var ids = new long[responses.Length];
            for (var i = 0; i < responses.Length; i++)
                ids[i] = (await AcceptedAsync(responses[i])).GetProperty("id").GetInt64();

            Assert.AreEqual(1, ids.Distinct().Count(), "every request got the one ticket");
            Assert.AreEqual(1L, Tickets(player.AccountId));
        }

        [TestMethod]
        public async Task KeyUsedForTheOtherKindOfTicket_AnswersKeyReused_ButPurchaseKeysAreSeparate()
        {
            var player = NewPlayer();
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Bridge Sword", VaultItemState.Held);
            var key = NewKey();
            var purchaseKey = NewKey();
            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase, $"INSERT INTO market_request (account_Id, idempotency_Key, kind, result, created_Time) VALUES ({player.AccountId}, '{purchaseKey}', 'purchase', '{{}}', UTC_TIMESTAMP(6));");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            await AcceptedAsync(await MmdWithdrawAsync(host, cookie, player.CharacterId, 20, key));

            var reused = await VaultWithdrawAsync(host, cookie, player.CharacterId, guid, key);
            Assert.AreEqual(HttpStatusCode.Conflict, reused.StatusCode);
            Assert.AreEqual("key_reused", await MarketApiHost.ErrorAsync(reused));

            await AcceptedAsync(await VaultWithdrawAsync(host, cookie, player.CharacterId, guid, purchaseKey));
            Assert.AreEqual(2L, Tickets(player.AccountId));
        }

        // ---- refusals

        [TestMethod]
        public async Task BadRequests_AreRefused_AndCreateNoTicket()
        {
            var player = NewPlayer();
            var deleted = MarketApiTestData.AddCharacter(player.AccountId, player.Name + "Gone", deleted: true);
            var stranger = NewPlayer("stranger");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");

            async Task AssertRefused(Task<HttpResponseMessage> request, HttpStatusCode status, string error)
            {
                var response = await request;
                Assert.AreEqual(status, response.StatusCode, await response.Content.ReadAsStringAsync());
                Assert.AreEqual(error, await MarketApiHost.ErrorAsync(response));
            }

            await AssertRefused(MmdWithdrawAsync(host, cookie, player.CharacterId, 5, null), HttpStatusCode.BadRequest, "bad_request");
            await AssertRefused(MmdWithdrawAsync(host, cookie, player.CharacterId, 5, new string('k', 65)), HttpStatusCode.BadRequest, "bad_request");
            await AssertRefused(host.PostJsonAsync("/mmd/withdraw", new { amount = 5, idempotencyKey = NewKey() }, cookie), HttpStatusCode.BadRequest, "invalid_character");
            await AssertRefused(MmdWithdrawAsync(host, cookie, deleted, 5, NewKey()), HttpStatusCode.BadRequest, "invalid_character");
            await AssertRefused(MmdWithdrawAsync(host, cookie, stranger.CharacterId, 5, NewKey()), HttpStatusCode.BadRequest, "invalid_character");
            await AssertRefused(MmdWithdrawAsync(host, cookie, player.CharacterId, 0, NewKey()), HttpStatusCode.BadRequest, "invalid_amount");
            await AssertRefused(MmdWithdrawAsync(host, cookie, player.CharacterId, 1.5m, NewKey()), HttpStatusCode.BadRequest, "invalid_amount");
            await AssertRefused(MmdWithdrawAsync(host, cookie, player.CharacterId, null, NewKey()), HttpStatusCode.BadRequest, "invalid_amount");
            await AssertRefused(host.PostJsonAsync("/vault/withdraw", new { characterId = player.CharacterId, idempotencyKey = NewKey() }, cookie), HttpStatusCode.BadRequest, "bad_request");
            await AssertRefused(VaultWithdrawAsync(host, cookie, stranger.CharacterId, 1, NewKey()), HttpStatusCode.BadRequest, "invalid_character");

            Assert.AreEqual(0L, Tickets(player.AccountId));
            Assert.AreEqual(0L, Tickets(stranger.AccountId));
        }

        [TestMethod]
        public async Task Tickets_NeedASignIn_AndAnotherAccountsTicketIsNotFound()
        {
            var owner = NewPlayer();
            var other = NewPlayer();

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(owner.Name, "pass");
            var id = (await AcceptedAsync(await MmdWithdrawAsync(host, cookie, owner.CharacterId, 5, NewKey()))).GetProperty("id").GetInt64();

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync($"/tickets/{id}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await MmdWithdrawAsync(host, null, owner.CharacterId, 5, NewKey())).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await VaultWithdrawAsync(host, null, owner.CharacterId, 1, NewKey())).StatusCode);

            var otherCookie = await host.SignInForCookieAsync(other.Name, "pass");
            var response = await host.GetAsync($"/tickets/{id}", otherCookie);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.AreEqual("not_found", await MarketApiHost.ErrorAsync(response));

            Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync("/tickets/999999999", cookie)).StatusCode);
        }
    }
}
