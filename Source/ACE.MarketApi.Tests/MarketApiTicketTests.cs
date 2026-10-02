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
    /// The game bridge from the web side: POST /api/vault/withdraw and POST /api/mmd/withdraw create tickets, GET /api/tickets/{id} shows what the game server did.
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
            host.PostJsonAsync("/api/mmd/withdraw", new { characterId, amount, idempotencyKey }, cookie);

        private static Task<HttpResponseMessage> VaultWithdrawAsync(MarketApiHost host, string cookie, uint characterId, uint itemGuid, string idempotencyKey) =>
            host.PostJsonAsync("/api/vault/withdraw", new { characterId, itemGuid, idempotencyKey }, cookie);

        private static Task<HttpResponseMessage> InventorySnapshotAsync(MarketApiHost host, string cookie, uint characterId, string idempotencyKey) =>
            host.PostJsonAsync("/api/inventory/snapshot", new { characterId, idempotencyKey }, cookie);

        private static Task<HttpResponseMessage> VaultDepositAsync(MarketApiHost host, string cookie, uint characterId, uint itemGuid, string idempotencyKey) =>
            host.PostJsonAsync("/api/vault/deposit", new { characterId, itemGuid, idempotencyKey }, cookie);

        private static async Task<JsonElement> AcceptedAsync(HttpResponseMessage response)
        {
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());

            var ticket = await MarketApiHost.JsonAsync(response);
            Assert.AreEqual($"/api/tickets/{ticket.GetProperty("id").GetInt64()}", response.Headers.Location?.OriginalString);

            return ticket;
        }

        private static async Task<JsonElement> TicketAsync(MarketApiHost host, string cookie, long id)
        {
            var response = await host.GetAsync($"/api/tickets/{id}", cookie);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            return await MarketApiHost.JsonAsync(response);
        }

        /// <summary>
        /// Runs the body with the market paused, and resumes it afterwards whatever happens
        /// </summary>
        private static async Task WhilePausedAsync(Func<Task> body)
        {
            using (var shard = MarketApiTestData.Shard())
                MarketPause.Pause(shard, "ticket replay test", DateTime.UtcNow);

            try
            {
                await body();
            }
            finally
            {
                using var shard = MarketApiTestData.Shard();
                MarketPause.Resume(shard, "test cleanup", DateTime.UtcNow);
            }
        }

        /// <summary>
        /// Adds count tickets for the player straight into the table: CLAIMED (claimed just now, so no poller takes them) or finished just now
        /// </summary>
        private static void AddTickets(Player player, int count, string status)
        {
            var finished = status == TicketStatus.Claimed ? "NULL" : "UTC_TIMESTAMP(6)";

            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase,
                "INSERT INTO market_ticket (kind, account_Id, character_Id, payload, status, idempotency_Key, created_Time, claimed_Time, finished_Time) " +
                $"WITH RECURSIVE n (i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {count}) " +
                $"SELECT '{TicketKind.MmdWithdraw}', {player.AccountId}, {player.CharacterId}, '{{\"amount\":1}}', '{status}', REPLACE(UUID(), '-', ''), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), {finished} FROM n;");
        }

        private static async Task<JsonElement[]> TicketListAsync(MarketApiHost host, string cookie)
        {
            var response = await host.GetAsync("/api/tickets", cookie);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            return (await MarketApiHost.JsonAsync(response)).EnumerateArray().ToArray();
        }

        private static void AssertNewestFirst(JsonElement[] tickets)
        {
            var ids = tickets.Select(ticket => ticket.GetProperty("id").GetInt64()).ToArray();
            CollectionAssert.AreEqual(ids.OrderByDescending(id => id).ToArray(), ids);
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
            Assert.AreEqual(JsonValueKind.Null, ticket.GetProperty("progress").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, ticket.GetProperty("progressTime").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, ticket.GetProperty("progressUntil").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, ticket.GetProperty("result").ValueKind);

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
        public async Task InventorySnapshot_AndVaultDeposit_CreateTickets_AndReplayBeforeCharacterValidation()
        {
            var player = NewPlayer("deposit");
            var stranger = NewPlayer("depositstranger");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");
            var snapshotKey = NewKey();

            var snapshot = await AcceptedAsync(await InventorySnapshotAsync(host, cookie, player.CharacterId, snapshotKey));
            var snapshotId = snapshot.GetProperty("id").GetInt64();

            Assert.AreEqual(TicketKind.InventorySnapshot, snapshot.GetProperty("kind").GetString());
            Assert.AreEqual(TicketStatus.Waiting, snapshot.GetProperty("status").GetString());
            Assert.AreEqual(player.CharacterId, snapshot.GetProperty("characterId").GetUInt32());

            var replay = await AcceptedAsync(await InventorySnapshotAsync(host, cookie, stranger.CharacterId, snapshotKey));
            Assert.AreEqual(snapshotId, replay.GetProperty("id").GetInt64(), "an idempotent retry is returned before validating its changed body");
            Assert.AreEqual("key_reused", await MarketApiHost.ErrorAsync(await VaultDepositAsync(host, cookie, player.CharacterId, 0xC0000001, snapshotKey)));

            var depositKey = NewKey();
            var deposit = await AcceptedAsync(await VaultDepositAsync(host, cookie, player.CharacterId, 0xC0000001, depositKey));
            Assert.AreEqual(TicketKind.VaultDeposit, deposit.GetProperty("kind").GetString());
            Assert.AreEqual(0xC0000001u, deposit.GetProperty("itemGuid").GetUInt32());
            var depositReplay = await AcceptedAsync(await VaultDepositAsync(host, cookie, stranger.CharacterId, 0xC0000002, depositKey));
            Assert.AreEqual(deposit.GetProperty("id").GetInt64(), depositReplay.GetProperty("id").GetInt64());
            Assert.AreEqual(0xC0000001u, depositReplay.GetProperty("itemGuid").GetUInt32());

            Assert.AreEqual("invalid_character", await MarketApiHost.ErrorAsync(await InventorySnapshotAsync(host, cookie, stranger.CharacterId, NewKey())));
            Assert.AreEqual("invalid_character", await MarketApiHost.ErrorAsync(await VaultDepositAsync(host, cookie, stranger.CharacterId, 0xC0000002, NewKey())));
            Assert.AreEqual(2L, Tickets(player.AccountId), "the two valid requests create tickets; validation failures do not");
        }

        [TestMethod]
        public async Task InventorySnapshot_AndVaultDeposit_AreRefusedForAFrozenAccount()
        {
            var player = NewPlayer("depositfrozen");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");
            MarketApiTestData.Ban(player.AccountId, host.Clock.GetUtcNow().UtcDateTime.AddDays(1));

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await InventorySnapshotAsync(host, cookie, player.CharacterId, NewKey())).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await VaultDepositAsync(host, cookie, player.CharacterId, 0xC0000003, NewKey())).StatusCode);
            Assert.AreEqual(0L, Tickets(player.AccountId), "a frozen account cannot create tickets");
        }

        [TestMethod]
        public async Task InventorySnapshot_ResultTurnsRawIconLayersIntoApiUrls()
        {
            var player = NewPlayer("snapshoticons");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");
            var created = await AcceptedAsync(await InventorySnapshotAsync(host, cookie, player.CharacterId, NewKey()));
            var id = created.GetProperty("id").GetInt64();
            var result = "{\"snapshotTime\":\"2026-10-02T00:00:00Z\",\"items\":[{\"itemGuid\":3221225473,\"name\":\"Snapshot Sword\",\"stackSize\":1,\"itemType\":2,\"icon\":100677439,\"iconUnderlay\":null,\"iconOverlay\":null,\"iconOverlaySecondary\":null,\"uiEffects\":null,\"paletteTemplate\":null,\"clothingBase\":null,\"refusalCode\":null}]}";

            using (var shard = MarketApiTestData.Shard())
            {
                TicketStore.ClaimOne(shard, id, host.Clock.GetUtcNow().UtcDateTime);
                TicketStore.Complete(shard, new TicketCompletion(id, "Inventory snapshot is ready.", result), host.Clock.GetUtcNow().UtcDateTime);
                shard.SaveChanges();
            }

            var ticket = await TicketAsync(host, cookie, id);
            var item = ticket.GetProperty("result").GetProperty("items")[0];
            var layers = item.GetProperty("icon").GetProperty("layers").EnumerateArray().ToArray();

            Assert.IsTrue(layers.Length >= 2, "the plate and base icon are represented");
            Assert.IsTrue(layers.Any(layer => layer.GetProperty("kind").GetString() == "base" && layer.GetProperty("url").GetString() == "/api/icons/0x0600373F.png"),
                "the snapshot's base icon URL points at the item's actual portal DAT icon");
            Assert.AreEqual(JsonValueKind.Null, item.GetProperty("refusalCode").ValueKind);
        }

        [TestMethod]
        public async Task InventorySnapshot_MalformedResultShapes_DoNotBreakTicketList()
        {
            var player = NewPlayer("snapshotmalformed");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");
            var malformedResults = new[]
            {
                "{\"snapshotTime\":\"2026-10-02T00:00:00Z\",\"items\":{}}",
                "{\"snapshotTime\":\"2026-10-02T00:00:00Z\",\"items\":[{\"itemGuid\":3221225473,\"stackSize\":1,\"itemType\":2,\"icon\":100677439}]}",
                "{\"snapshotTime\":\"2026-10-02T00:00:00Z\",\"items\":[null]}"
            };
            var ticketIds = new long[malformedResults.Length];

            for (var i = 0; i < malformedResults.Length; i++)
            {
                var created = await AcceptedAsync(await InventorySnapshotAsync(host, cookie, player.CharacterId, NewKey()));
                var id = created.GetProperty("id").GetInt64();
                ticketIds[i] = id;

                using var shard = MarketApiTestData.Shard();
                TicketStore.ClaimOne(shard, id, host.Clock.GetUtcNow().UtcDateTime);
                TicketStore.Complete(shard, new TicketCompletion(id, "Inventory snapshot is ready.", malformedResults[i]), host.Clock.GetUtcNow().UtcDateTime);
                shard.SaveChanges();
            }

            var itemResponses = new System.Collections.Generic.List<HttpResponseMessage>();
            var requestFailures = new System.Collections.Generic.List<string>();
            foreach (var id in ticketIds)
            {
                try
                {
                    itemResponses.Add(await host.GetAsync($"/api/tickets/{id}", cookie));
                }
                catch (Exception exception)
                {
                    requestFailures.Add($"GET /api/tickets/{id}: {exception.GetType().Name}");
                }
            }

            HttpResponseMessage listResponse = null;
            try
            {
                listResponse = await host.GetAsync("/api/tickets", cookie);
            }
            catch (Exception exception)
            {
                requestFailures.Add($"GET /api/tickets: {exception.GetType().Name}");
            }

            Assert.AreEqual(0, requestFailures.Count, string.Join("; ", requestFailures));
            Assert.AreEqual(ticketIds.Length, itemResponses.Count);
            Assert.AreEqual(HttpStatusCode.OK, listResponse.StatusCode);

            foreach (var response in itemResponses)
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            var tickets = (await MarketApiHost.JsonAsync(listResponse)).EnumerateArray().ToArray();
            Assert.AreEqual(3, tickets.Length);
            Assert.IsTrue(tickets.All(ticket => ticket.GetProperty("kind").GetString() == TicketKind.InventorySnapshot));
            Assert.IsTrue(tickets.All(ticket => ticket.GetProperty("result").ValueKind == JsonValueKind.Null),
                "malformed snapshot results are omitted while the ticket list remains available");
            foreach (var response in itemResponses)
            {
                var ticket = await MarketApiHost.JsonAsync(response);
                Assert.AreEqual(JsonValueKind.Null, ticket.GetProperty("result").ValueKind, "a malformed result is omitted by the single-ticket endpoint");
            }
        }

        [TestMethod]
        public async Task Withdraw_WithAPluginToken_CreatesATicket()
        {
            var player = NewPlayer();

            await using var host = await MarketApiHost.StartAsync();

            string code;
            using (var shard = MarketApiTestData.Shard())
                code = PluginAuth.NewLinkCode(shard, player.AccountId, player.CharacterId, host.Clock.GetUtcNow().UtcDateTime);

            var exchanged = await host.PostJsonAsync("/api/auth/plugin-token", new { code, label = "plugin" });
            Assert.AreEqual(HttpStatusCode.OK, exchanged.StatusCode, await exchanged.Content.ReadAsStringAsync());
            var token = (await MarketApiHost.JsonAsync(exchanged)).GetProperty("token").GetString();

            var ticket = await AcceptedAsync(await host.PostJsonAsync("/api/mmd/withdraw", new { characterId = player.CharacterId, amount = 3, idempotencyKey = NewKey() }, token: token));

            var read = await host.GetWithTokenAsync($"/api/tickets/{ticket.GetProperty("id").GetInt64()}", token);
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
        public async Task MmdWithdrawalReplay_ReturnsOriginalBeforePauseAndRequestValidation()
        {
            var player = NewPlayer();
            var key = NewKey();

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");
            var first = await AcceptedAsync(await MmdWithdrawAsync(host, cookie, player.CharacterId, 20, key));

            await WhilePausedAsync(async () =>
            {
                // A retry belongs to the original request even if the new body is invalid and withdrawals are paused.
                var replay = await MmdWithdrawAsync(host, cookie, 0, 1.5m, key);
                var replayed = await AcceptedAsync(replay);
                Assert.AreEqual(first.GetProperty("id").GetInt64(), replayed.GetProperty("id").GetInt64());
                Assert.AreEqual(20, replayed.GetProperty("amount").GetInt64());

                var newRequest = await MmdWithdrawAsync(host, cookie, player.CharacterId, 5, NewKey());
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, newRequest.StatusCode);
                Assert.AreEqual("paused", await MarketApiHost.ErrorAsync(newRequest));
                Assert.AreEqual(1L, Tickets(player.AccountId));
            });
        }

        [TestMethod]
        public async Task VaultWithdrawalReplay_ReturnsOriginalWhilePausedAndBeforeCharacterAndItemValidation()
        {
            var player = NewPlayer();
            var guid = MarketApiTestData.AddVaultItem(player.AccountId, player.CharacterId, "Replay Sword", VaultItemState.Held);
            var key = NewKey();

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");
            var first = await AcceptedAsync(await VaultWithdrawAsync(host, cookie, player.CharacterId, guid, key));

            await WhilePausedAsync(async () =>
            {
                var replayResponse = await host.PostJsonAsync("/api/vault/withdraw", new { characterId = 0, itemGuid = (uint?)null, idempotencyKey = key }, cookie);
                var replay = await AcceptedAsync(replayResponse);

                Assert.AreEqual(first.GetProperty("id").GetInt64(), replay.GetProperty("id").GetInt64());
                Assert.AreEqual(guid, replay.GetProperty("itemGuid").GetUInt32());
                Assert.AreEqual(player.CharacterId, replay.GetProperty("characterId").GetUInt32());
                Assert.AreEqual(1L, Tickets(player.AccountId));
            });
        }

        [TestMethod]
        public async Task VaultDepositReplay_ReturnsOriginalWhilePaused_AndNewDepositAndSnapshotRequestsAreAccepted()
        {
            var player = NewPlayer("depositpaused");
            var key = NewKey();

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(player.Name, "pass");
            var first = await AcceptedAsync(await VaultDepositAsync(host, cookie, player.CharacterId, 0xC0000010, key));

            await WhilePausedAsync(async () =>
            {
                var replayResponse = await host.PostJsonAsync("/api/vault/deposit", new { characterId = 0, itemGuid = (uint?)null, idempotencyKey = key }, cookie);
                var replay = await AcceptedAsync(replayResponse);
                Assert.AreEqual(first.GetProperty("id").GetInt64(), replay.GetProperty("id").GetInt64());
                Assert.AreEqual(0xC0000010u, replay.GetProperty("itemGuid").GetUInt32());

                var deposit = await AcceptedAsync(await VaultDepositAsync(host, cookie, player.CharacterId, 0xC0000011, NewKey()));
                Assert.AreEqual(TicketKind.VaultDeposit, deposit.GetProperty("kind").GetString());

                var snapshot = await AcceptedAsync(await InventorySnapshotAsync(host, cookie, player.CharacterId, NewKey()));
                Assert.AreEqual(TicketKind.InventorySnapshot, snapshot.GetProperty("kind").GetString());

                Assert.AreEqual(3L, Tickets(player.AccountId), "pause does not stop new deposits or inventory snapshots");
            });
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
            await AssertRefused(host.PostJsonAsync("/api/mmd/withdraw", new { amount = 5, idempotencyKey = NewKey() }, cookie), HttpStatusCode.BadRequest, "invalid_character");
            await AssertRefused(MmdWithdrawAsync(host, cookie, deleted, 5, NewKey()), HttpStatusCode.BadRequest, "invalid_character");
            await AssertRefused(MmdWithdrawAsync(host, cookie, stranger.CharacterId, 5, NewKey()), HttpStatusCode.BadRequest, "invalid_character");
            await AssertRefused(MmdWithdrawAsync(host, cookie, player.CharacterId, 0, NewKey()), HttpStatusCode.BadRequest, "invalid_amount");
            await AssertRefused(MmdWithdrawAsync(host, cookie, player.CharacterId, 1.5m, NewKey()), HttpStatusCode.BadRequest, "invalid_amount");
            await AssertRefused(MmdWithdrawAsync(host, cookie, player.CharacterId, null, NewKey()), HttpStatusCode.BadRequest, "invalid_amount");
            await AssertRefused(host.PostJsonAsync("/api/vault/withdraw", new { characterId = player.CharacterId, idempotencyKey = NewKey() }, cookie), HttpStatusCode.BadRequest, "bad_request");
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

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync($"/api/tickets/{id}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/api/tickets")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await MmdWithdrawAsync(host, null, owner.CharacterId, 5, NewKey())).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await VaultWithdrawAsync(host, null, owner.CharacterId, 1, NewKey())).StatusCode);

            var otherCookie = await host.SignInForCookieAsync(other.Name, "pass");
            var response = await host.GetAsync($"/api/tickets/{id}", otherCookie);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.AreEqual("not_found", await MarketApiHost.ErrorAsync(response));

            Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync("/api/tickets/999999999", cookie)).StatusCode);
        }

        [TestMethod]
        public async Task TicketList_IncludesOldUnfinishedAndRecentlyFinishedOnlyForThisAccount()
        {
            var owner = NewPlayer();
            var other = NewPlayer();

            await using var host = await MarketApiHost.StartAsync();
            var ownerCookie = await host.SignInForCookieAsync(owner.Name, "pass");
            var otherCookie = await host.SignInForCookieAsync(other.Name, "pass");

            async Task<long> Create(string cookie, uint characterId, int amount)
            {
                var ticket = await AcceptedAsync(await MmdWithdrawAsync(host, cookie, characterId, amount, NewKey()));
                return ticket.GetProperty("id").GetInt64();
            }

            var waiting = await Create(ownerCookie, owner.CharacterId, 10);
            var recentDone = await Create(ownerCookie, owner.CharacterId, 11);
            var oldDone = await Create(ownerCookie, owner.CharacterId, 12);
            var otherTicket = await Create(otherCookie, other.CharacterId, 13);

            MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase,
                $"UPDATE market_ticket SET created_Time = UTC_TIMESTAMP(6) - INTERVAL 25 HOUR WHERE id = {waiting}; " +
                $"UPDATE market_ticket SET status = 'DONE', created_Time = UTC_TIMESTAMP(6) - INTERVAL 25 HOUR, finished_Time = UTC_TIMESTAMP(6) WHERE id = {recentDone}; " +
                $"UPDATE market_ticket SET status = 'FAILED', created_Time = UTC_TIMESTAMP(6) - INTERVAL 25 HOUR, finished_Time = UTC_TIMESTAMP(6) - INTERVAL 25 HOUR WHERE id = {oldDone}; " +
                $"UPDATE market_ticket SET status = 'WAITING', created_Time = UTC_TIMESTAMP(6) - INTERVAL 25 HOUR WHERE id = {otherTicket};");

            var response = await host.GetAsync("/api/tickets", ownerCookie);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var tickets = await MarketApiHost.JsonAsync(response);
            var ids = tickets.EnumerateArray().Select(ticket => ticket.GetProperty("id").GetInt64()).ToArray();

            CollectionAssert.AreEqual(new[] { recentDone, waiting }, ids);
            Assert.AreEqual("DONE", tickets[0].GetProperty("status").GetString());
            Assert.AreEqual(JsonValueKind.Null, tickets[0].GetProperty("progress").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, tickets[0].GetProperty("result").ValueKind);
            Assert.IsTrue(tickets[0].GetProperty("finishedTime").GetString() != null);
            Assert.IsFalse(ids.Contains(oldDone));
            Assert.IsFalse(ids.Contains(otherTicket));
        }

        [TestMethod]
        public async Task TicketList_KeepsEveryUnfinishedTicketEvenPastTheCap()
        {
            var player = NewPlayer();
            AddTickets(player, 3, TicketStatus.Done);
            AddTickets(player, TicketStore.MaxVisible + 5, TicketStatus.Claimed);

            await using var host = await MarketApiHost.StartAsync();
            var tickets = await TicketListAsync(host, await host.SignInForCookieAsync(player.Name, "pass"));

            Assert.AreEqual(TicketStore.MaxVisible + 5, tickets.Length);
            Assert.IsTrue(tickets.All(ticket => ticket.GetProperty("status").GetString() == TicketStatus.Claimed));
            AssertNewestFirst(tickets);
        }

        [TestMethod]
        public async Task TicketList_FillsTheCapWithTheNewestFinishedTicketsAfterEveryUnfinishedOne()
        {
            var player = NewPlayer();
            AddTickets(player, 30, TicketStatus.Done);
            AddTickets(player, 40, TicketStatus.Claimed);
            AddTickets(player, 40, TicketStatus.Failed);

            await using var host = await MarketApiHost.StartAsync();
            var tickets = await TicketListAsync(host, await host.SignInForCookieAsync(player.Name, "pass"));

            Assert.AreEqual(TicketStore.MaxVisible, tickets.Length);
            Assert.AreEqual(40, tickets.Count(ticket => ticket.GetProperty("status").GetString() == TicketStatus.Claimed));
            Assert.AreEqual(40, tickets.Count(ticket => ticket.GetProperty("status").GetString() == TicketStatus.Failed));
            Assert.AreEqual(20, tickets.Count(ticket => ticket.GetProperty("status").GetString() == TicketStatus.Done));
            AssertNewestFirst(tickets);

            // the DONE tickets left out are the oldest ones
            var oldestKept = tickets.Where(ticket => ticket.GetProperty("status").GetString() == TicketStatus.Done).Min(ticket => ticket.GetProperty("id").GetInt64());
            Assert.AreEqual(10L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_ticket WHERE account_Id = {player.AccountId} AND status = '{TicketStatus.Done}' AND id < {oldestKept};"));
        }
    }
}
