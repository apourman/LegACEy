using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using ACE.Database.Models.Shard.Market;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// GET /me and GET /vault, and the session surviving a restart
    /// </summary>
    [TestClass]
    public class MarketApiAccountTests
    {
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

            var me = await MarketApiHost.JsonAsync(await host.GetAsync("/me", await host.SignInForCookieAsync(alice, "a-pass")));

            Assert.AreEqual(aliceId, me.GetProperty("accountId").GetUInt32());
            Assert.AreEqual(alice, me.GetProperty("accountName").GetString());
            Assert.AreEqual(250, me.GetProperty("balance").GetInt64());
            Assert.IsFalse(me.GetProperty("frozen").GetBoolean());

            var characters = me.GetProperty("characters").EnumerateArray().Select(c => (c.GetProperty("id").GetUInt32(), c.GetProperty("name").GetString())).OrderBy(c => c.Item1).ToList();
            CollectionAssert.AreEqual(new[] { (aliceMain, alice + "Main"), (aliceMule, alice + "Mule") }, characters);

            // an account with no balance row has 0
            var carol = MarketApiTestData.UniqueName("carol");
            MarketApiTestData.CreateAccount(carol, "c-pass");
            var carolMe = await MarketApiHost.JsonAsync(await host.GetAsync("/me", await host.SignInForCookieAsync(carol, "c-pass")));
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

            var vault = await MarketApiHost.JsonAsync(await host.GetAsync("/vault", await host.SignInForCookieAsync(alice, "a-pass")));
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

            var bobVault = await MarketApiHost.JsonAsync(await host.GetAsync("/vault", await host.SignInForCookieAsync(bob, "b-pass")));
            CollectionAssert.AreEqual(new[] { bobs }, bobVault.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("itemGuid").GetUInt32()).ToList());
        }

        [TestMethod]
        public async Task MeAndVault_WithoutASession_AreRefused()
        {
            await using var host = await MarketApiHost.StartAsync();

            foreach (var path in new[] { "/me", "/vault" })
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
            var cookie = await host.SignInForCookieAsync(name, "pass");

            foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
                Assert.AreEqual(HttpStatusCode.MethodNotAllowed, (await host.SendAsync(method, "/me", cookie)).StatusCode, method.Method);

            var response = await host.GetAsync("/me", cookie);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public async Task Session_SurvivesAnApiRestart()
        {
            var name = MarketApiTestData.UniqueName("restart");
            MarketApiTestData.CreateAccount(name, "pass");

            var keysPath = MarketApiHost.NewKeysPath();
            string cookie;

            await using (var first = await MarketApiHost.StartAsync(keysPath))
                cookie = await first.SignInForCookieAsync(name, "pass");

            await using (var second = await MarketApiHost.StartAsync(keysPath))
            {
                var response = await second.GetAsync("/me", cookie);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(name, (await MarketApiHost.JsonAsync(response)).GetProperty("accountName").GetString());
            }

            // control: without the persisted keys the cookie is worthless
            await using (var other = await MarketApiHost.StartAsync(MarketApiHost.NewKeysPath()))
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await other.GetAsync("/me", cookie)).StatusCode);
        }

        [TestMethod]
        public async Task SignOut_EndsTheSession()
        {
            var name = MarketApiTestData.UniqueName("out");
            MarketApiTestData.CreateAccount(name, "pass");

            await using var host = await MarketApiHost.StartAsync();
            var cookie = await host.SignInForCookieAsync(name, "pass");

            var logout = await host.SendAsync(HttpMethod.Post, "/auth/logout", cookie);
            Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode);
            Assert.IsTrue((await MarketApiHost.JsonAsync(logout)).GetProperty("ok").GetBoolean());
            var cleared = MarketApiHost.SessionCookie(logout);

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/me", cleared)).StatusCode);
        }
    }
}
