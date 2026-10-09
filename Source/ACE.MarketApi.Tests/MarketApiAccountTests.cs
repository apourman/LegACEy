using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum.Properties;
using ACE.MarketApi;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// GET /api/me and GET /api/vault, and the session surviving a restart
    /// </summary>
    [TestClass]
    public class MarketApiAccountTests
    {
        [TestMethod]
        public async Task BannedSession_NextMeIs401_AndNewSignInExplainsBan()
        {
            var name = MarketApiTestData.UniqueName("banview");
            var id = MarketApiTestData.CreateAccount(name, "pass");
            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(name, "pass");
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/me", session)).StatusCode);
            MarketApiTestData.Ban(id, host.Clock.GetUtcNow().UtcDateTime.AddDays(3));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/api/me", session)).StatusCode);
            var login = await host.SignInAsync(name, "pass");
            Assert.AreEqual(HttpStatusCode.Forbidden, login.StatusCode);
            Assert.AreEqual("banned", (await MarketApiHost.JsonAsync(login)).GetProperty("error").GetString());
        }

        [TestMethod]
        public async Task Me_ReturnsPauseAndOwnCurrentCountsAndConfiguredCaps()
        {
            var name = MarketApiTestData.UniqueName("counts");
            var id = MarketApiTestData.CreateAccount(name, "pass");
            var character = MarketApiTestData.AddCharacter(id, name + "Main");
            var held = MarketApiTestData.AddVaultItem(id, character, "Held", VaultItemState.Held);
            var listed = MarketApiTestData.AddVaultItem(id, character, "Listed", VaultItemState.Listed);
            var expired = MarketApiTestData.AddVaultItem(id, character, "Expired", VaultItemState.Listed);
            await using var host = await MarketApiHost.StartAsync();
            var now = host.Clock.GetUtcNow().UtcDateTime;
            MarketApiTestData.AddListing(id, character, listed, 5, ListingStatus.Active, now);
            MarketApiTestData.AddListing(id, character, expired, 5, ListingStatus.Active, now.AddDays(-30));
            MarketApiTestData.SetSetting(MarketSettings.VaultSize.Key, 42);
            MarketApiTestData.SetSetting(MarketSettings.ActiveListings.Key, 7);
            try
            {
                var session = await host.SignInForSessionAsync(name, "pass");
                var me = await MarketApiHost.JsonAsync(await host.GetAsync("/api/me", session));
                Assert.IsFalse(me.GetProperty("paused").GetBoolean());
                Assert.AreEqual(3, me.GetProperty("vaultCount").GetInt32());
                Assert.AreEqual(1, me.GetProperty("listingCount").GetInt32());
                Assert.AreEqual(42L, me.GetProperty("vaultCap").GetInt64());
                Assert.AreEqual(7L, me.GetProperty("listingCap").GetInt64());
                using var shard = host.App.Services.GetRequiredService<MarketDatabase>().CreateShard();
                ACE.Database.Market.MarketPause.Pause(shard, "test", now);
                me = await MarketApiHost.JsonAsync(await host.GetAsync("/api/me", session));
                Assert.IsTrue(me.GetProperty("paused").GetBoolean());
            }
            finally
            {
                MarketApiTestData.ClearSetting(MarketSettings.VaultSize.Key);
                MarketApiTestData.ClearSetting(MarketSettings.ActiveListings.Key);
                using var shard = host.App.Services.GetRequiredService<MarketDatabase>().CreateShard();
                ACE.Database.Market.MarketPause.Resume(shard, "test", host.Clock.GetUtcNow().UtcDateTime);
            }
        }

        [TestMethod]
        public async Task Me_ReturnsTheSignedInAccountsDataOnly()
        {
            var alice = MarketApiTestData.UniqueName("alice");
            var aliceId = MarketApiTestData.CreateAccount(alice, "a-pass");
            var bob = MarketApiTestData.UniqueName("bob");
            var bobId = MarketApiTestData.CreateAccount(bob, "b-pass");

            var aliceMain = MarketApiTestData.AddCharacter(aliceId, alice + "Main");
            var aliceMule = MarketApiTestData.AddCharacter(aliceId, alice + "Mule");
            MarketApiTestData.AddCharacter(aliceId, alice + "Gone", deleted: true);
            MarketApiTestData.AddCharacter(bobId, bob + "Main");
            MarketApiTestData.SetBalance(aliceId, 250);
            MarketApiTestData.SetBalance(bobId, 999);

            await using var host = await MarketApiHost.StartAsync();

            var me = await MarketApiHost.JsonAsync(await host.GetAsync("/api/me", await host.SignInForSessionAsync(alice, "a-pass")));

            Assert.AreEqual(aliceId, me.GetProperty("accountId").GetUInt32());
            Assert.AreEqual(alice, me.GetProperty("accountName").GetString());
            Assert.AreEqual(250, me.GetProperty("balance").GetInt64());
            Assert.IsFalse(me.GetProperty("frozen").GetBoolean());

            var characters = me.GetProperty("characters").EnumerateArray().Select(c => (c.GetProperty("id").GetUInt32(), c.GetProperty("name").GetString())).OrderBy(c => c.Item1).ToList();
            CollectionAssert.AreEqual(new[] { (aliceMain, alice + "Main"), (aliceMule, alice + "Mule") }, characters);

            // an account with no balance row has 0
            var carol = MarketApiTestData.UniqueName("carol");
            MarketApiTestData.CreateAccount(carol, "c-pass");
            var carolMe = await MarketApiHost.JsonAsync(await host.GetAsync("/api/me", await host.SignInForSessionAsync(carol, "c-pass")));
            Assert.AreEqual(0, carolMe.GetProperty("balance").GetInt64());
            Assert.AreEqual(0, carolMe.GetProperty("characters").GetArrayLength());
        }

        [TestMethod]
        public async Task Vault_ReturnsTheSignedInAccountsItemsAndTheirStatesOnly()
        {
            var alice = MarketApiTestData.UniqueName("valice");
            var aliceId = MarketApiTestData.CreateAccount(alice, "a-pass");
            var bob = MarketApiTestData.UniqueName("vbob");
            var bobId = MarketApiTestData.CreateAccount(bob, "b-pass");
            var aliceChar = MarketApiTestData.AddCharacter(aliceId, alice + "Main");
            var bobChar = MarketApiTestData.AddCharacter(bobId, bob + "Main");

            var held = MarketApiTestData.AddVaultItem(aliceId, aliceChar, "Bone Slicer", VaultItemState.Held);
            var listed = MarketApiTestData.AddVaultItem(aliceId, aliceChar, "Chainmail Basinet", VaultItemState.Listed);
            var withdrawing = MarketApiTestData.AddVaultItem(aliceId, aliceChar, "Trade Notes", VaultItemState.Withdrawing, wcid: 20630, stackSize: 7);
            var bobs = MarketApiTestData.AddVaultItem(bobId, bobChar, "Bob Sword", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();

            var vault = await MarketApiHost.JsonAsync(await host.GetAsync("/api/vault", await host.SignInForSessionAsync(alice, "a-pass")));
            var items = vault.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("itemGuid").GetUInt32());

            CollectionAssert.AreEquivalent(new[] { held, listed, withdrawing }, items.Keys.ToList());
            Assert.IsFalse(items.ContainsKey(bobs));
            Assert.AreEqual("held", items[held].GetProperty("state").GetString());
            Assert.AreEqual("listed", items[listed].GetProperty("state").GetString());
            Assert.AreEqual("withdrawing", items[withdrawing].GetProperty("state").GetString());
            Assert.AreEqual("Bone Slicer", items[held].GetProperty("name").GetString());
            Assert.AreEqual(20630u, items[withdrawing].GetProperty("wcid").GetUInt32());
            Assert.AreEqual(7, items[withdrawing].GetProperty("stackSize").GetInt32());
            Assert.AreEqual(aliceChar, items[held].GetProperty("characterId").GetUInt32());

            var bobVault = await MarketApiHost.JsonAsync(await host.GetAsync("/api/vault", await host.SignInForSessionAsync(bob, "b-pass")));
            CollectionAssert.AreEqual(new[] { bobs }, bobVault.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("itemGuid").GetUInt32()).ToList());
        }

        [TestMethod]
        public async Task Vault_ReturnsIconsListingDetailsAndWithdrawingTicketId()
        {
            var name = MarketApiTestData.UniqueName("vaultfields");
            var accountId = MarketApiTestData.CreateAccount(name, "pass");
            var characterId = MarketApiTestData.AddCharacter(accountId, name + "Main");
            var held = MarketApiTestData.AddVaultItem(accountId, characterId, "Held", VaultItemState.Held);
            var listed = MarketApiTestData.AddVaultItem(accountId, characterId, "Listed", VaultItemState.Listed);
            var withdrawing = MarketApiTestData.AddVaultItem(accountId, characterId, "Withdrawing", VaultItemState.Withdrawing);
            MarketApiTestData.SetVaultColumns(held, "icon = 100667000");
            MarketApiTestData.SetVaultColumns(listed, "icon = 100667001");
            MarketApiTestData.SetVaultColumns(withdrawing, "icon = 100667002");

            await using var host = await MarketApiHost.StartAsync();
            var now = host.Clock.GetUtcNow().UtcDateTime;
            var listingId = MarketApiTestData.AddListing(accountId, characterId, listed, 321, ListingStatus.Active, now);
            long ticketId;
            using (var shard = host.App.Services.GetRequiredService<MarketDatabase>().CreateShard())
                ticketId = TicketStore.Create(shard, accountId, characterId, TicketKind.VaultWithdraw,
                    new TicketPayload(ItemGuid: withdrawing), "vault-ticket-fields", now).Ticket.Id;

            var session = await host.SignInForSessionAsync(name, "pass");
            var vault = await MarketApiHost.JsonAsync(await host.GetAsync("/api/vault", session));
            var items = vault.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("itemGuid").GetUInt32());

            Assert.IsTrue(items[held].GetProperty("icon").GetProperty("layers").EnumerateArray()
                .Any(layer => layer.GetProperty("url").GetString() == "/api/icons/0x06000E78_o00000000_e00000000.png"));
            Assert.AreEqual(listingId, items[listed].GetProperty("listingId").GetInt64());
            Assert.AreEqual(321, items[listed].GetProperty("price").GetInt64());
            using (var shard = MarketApiTestData.Shard())
                Assert.AreEqual(Database.Market.ListingStore.Truncate(now).AddDays(MarketSettings.Get(shard, MarketSettings.ListingLifetimeDays)),
                    items[listed].GetProperty("expiresTime").GetDateTime().ToUniversalTime());
            Assert.AreEqual(ticketId, items[withdrawing].GetProperty("ticketId").GetInt64());
        }

        [TestMethod]
        public async Task VaultDetail_ReturnsOwnAppraisalAndHidesAnotherAccountsItem()
        {
            var alice = MarketApiTestData.UniqueName("vaultdetail");
            var aliceId = MarketApiTestData.CreateAccount(alice, "a-pass");
            var bob = MarketApiTestData.UniqueName("vaultdetailother");
            var bobId = MarketApiTestData.CreateAccount(bob, "b-pass");
            var aliceChar = MarketApiTestData.AddCharacter(aliceId, alice + "Main");
            var bobChar = MarketApiTestData.AddCharacter(bobId, bob + "Main");
            var aliceItem = MarketApiTestData.AddVaultItem(aliceId, aliceChar, "Appraised", VaultItemState.Held);
            var bobItem = MarketApiTestData.AddVaultItem(bobId, bobChar, "Private", VaultItemState.Held);
            MarketApiTestData.AddItemProperties(aliceItem, ints: new[] { (PropertyInt.Damage, 10) });

            await using var host = await MarketApiHost.StartAsync();
            var aliceSession = await host.SignInForSessionAsync(alice, "a-pass");
            var appraisal = await MarketApiHost.JsonAsync(await host.GetAsync($"/api/vault/{aliceItem}", aliceSession));

            CollectionAssert.Contains(appraisal.GetProperty("lines").EnumerateArray().Select(line => line.GetString()).ToArray(), "Damage: 10 - 10");
            Assert.IsTrue(appraisal.GetProperty("spells").ValueKind == System.Text.Json.JsonValueKind.Array);

            var forbidden = await host.GetAsync($"/api/vault/{bobItem}", aliceSession);
            Assert.AreEqual(HttpStatusCode.NotFound, forbidden.StatusCode);
            Assert.AreEqual("not_found", await MarketApiHost.ErrorAsync(forbidden));
        }

        [TestMethod]
        public async Task MeAndVault_WithoutASession_AreRefused()
        {
            await using var host = await MarketApiHost.StartAsync();

            foreach (var path in new[] { "/api/me", "/api/vault" })
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync(path)).StatusCode, path);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync(path, "market_session=forged")).StatusCode, path);
            }
        }

        [TestMethod]
        public async Task Endpoints_OnlyGetAndPost()
        {
            var name = MarketApiTestData.UniqueName("verbs");
            MarketApiTestData.CreateAccount(name, "pass");

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(name, "pass");

            foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
                Assert.AreEqual(HttpStatusCode.MethodNotAllowed, (await host.SendAsync(method, "/api/me", session)).StatusCode, method.Method);

            var response = await host.GetAsync("/api/me", session);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public async Task Session_SurvivesAnApiRestart()
        {
            var name = MarketApiTestData.UniqueName("restart");
            MarketApiTestData.CreateAccount(name, "pass");

            string session;

            await using (var first = await MarketApiHost.StartAsync())
                session = await first.SignInForSessionAsync(name, "pass");

            // the session lives in the database, so a new API process (no keys, nothing kept in memory) still takes it
            await using (var second = await MarketApiHost.StartAsync())
            {
                var response = await second.GetAsync("/api/me", session);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(name, (await MarketApiHost.JsonAsync(response)).GetProperty("accountName").GetString());

                // control: a token the database doesn't hold is worthless
                var forged = session.Substring(0, session.Length - 4) + (session.EndsWith("AAAA") ? "BBBB" : "AAAA");
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/me", forged)).StatusCode);
            }
        }

        [TestMethod]
        public async Task SignOut_EndsTheSession()
        {
            var name = MarketApiTestData.UniqueName("out");
            MarketApiTestData.CreateAccount(name, "pass");

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(name, "pass");

            var signOut = await host.SendAsync(HttpMethod.Delete, "/api/auth/session", session);
            Assert.AreEqual(HttpStatusCode.OK, signOut.StatusCode);
            Assert.IsTrue((await MarketApiHost.JsonAsync(signOut)).GetProperty("ok").GetBoolean());
            Assert.IsFalse(signOut.Headers.Contains("Set-Cookie"), "the API keeps no cookie to clear");

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/api/me", session)).StatusCode);
        }
    }
}
