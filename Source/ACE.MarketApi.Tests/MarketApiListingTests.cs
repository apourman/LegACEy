using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// POST /api/listings, delisting, expiry, bans, and GET /api/listings/{id}
    /// </summary>
    [TestClass]
    public class MarketApiListingTests
    {
        private sealed record Seller(string Name, uint AccountId, uint CharacterId, string CharacterName);

        private static Seller NewSeller(string prefix = "seller")
        {
            var name = MarketApiTestData.UniqueName(prefix);
            var accountId = MarketApiTestData.CreateAccount(name, "pass");
            var characterName = name + "Main";
            var characterId = MarketApiTestData.AddCharacter(accountId, characterName);

            return new Seller(name, accountId, characterId, characterName);
        }

        private static async Task<HttpResponseMessage> ListAsync(MarketApiHost host, string session, uint itemGuid, long price) =>
            await host.PostJsonAsync("/api/listings", new { itemGuid, price }, session);

        private static async Task<long> ListOkAsync(MarketApiHost host, string session, uint itemGuid, long price)
        {
            var response = await ListAsync(host, session, itemGuid, price);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());

            return (await MarketApiHost.JsonAsync(response)).GetProperty("id").GetInt64();
        }

        private static async Task<JsonElement[]> BrowseAsync(MarketApiHost host, string query)
        {
            var response = await host.GetAsync("/api/listings?" + query);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            return (await MarketApiHost.JsonAsync(response)).GetProperty("listings").EnumerateArray().ToArray();
        }

        private static string VaultState(uint itemGuid) => MarketApiTestData.Rows($"SELECT state FROM market_vault_item WHERE item_Guid = {itemGuid};").Single();

        private static string ListingStatus(long listingId) => MarketApiTestData.Rows($"SELECT status FROM market_listing WHERE id = {listingId};").Single();

        private static string[] Events(uint itemGuid) => MarketApiTestData.Rows($"SELECT CONCAT(kind, ':', IFNULL(listing_Id, '-')) FROM market_item_event WHERE item_Guid = {itemGuid} ORDER BY id;").ToArray();

        // ---- listing

        [TestMethod]
        public async Task List_HeldItem_AppearsInBrowseAndDetail()
        {
            var seller = NewSeller();
            var name = MarketApiTestData.UniqueName("Bone Slicer ");
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, name, VaultItemState.Held);
            MarketApiTestData.SetVaultColumns(guid, $"item_Type = {(int)ItemType.MeleeWeapon}, material_Type = {(int)MaterialType.WhiteSapphire}, workmanship = 8, arcane_Lore = 120, " +
                $"wield_Requirements = {(int)WieldRequirement.Skill}, wield_Skill_Type = {(int)Skill.MissileWeapons}, wield_Difficulty = 390, stack_Size = 1, icon = 100667000");

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");
            var listedAt = host.Clock.GetUtcNow().UtcDateTime;

            var response = await ListAsync(host, session, guid, 150);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            var id = (await MarketApiHost.JsonAsync(response)).GetProperty("id").GetInt64();
            Assert.AreEqual($"/api/listings/{id}", response.Headers.Location?.OriginalString);

            Assert.AreEqual(VaultItemState.Listed, VaultState(guid));
            Assert.AreEqual(Database.Models.Shard.Market.ListingStatus.Active, ListingStatus(id));
            Assert.AreEqual(1L, MarketApiTestData.Scalar($"SELECT row_Version FROM market_vault_item WHERE item_Guid = {guid};"));
            CollectionAssert.AreEqual(new[] { $"list:{id}" }, Events(guid));

            // browse is public: no session
            var row = (await BrowseAsync(host, "q=" + Uri.EscapeDataString(name))).Single();
            Assert.AreEqual(id, row.GetProperty("id").GetInt64());
            Assert.AreEqual(guid, row.GetProperty("itemGuid").GetUInt32());
            Assert.AreEqual(name, row.GetProperty("name").GetString());
            Assert.AreEqual(150, row.GetProperty("price").GetInt64());
            Assert.AreEqual(seller.CharacterName, row.GetProperty("seller").GetString());
            Assert.AreEqual("MeleeWeapon", row.GetProperty("itemType").GetString());
            Assert.AreEqual("White Sapphire", row.GetProperty("material").GetString());
            Assert.AreEqual(8, row.GetProperty("workmanship").GetInt32());
            Assert.AreEqual(120, row.GetProperty("arcaneLore").GetInt32());
            Assert.AreEqual(JsonValueKind.Null, row.GetProperty("level").ValueKind);
            Assert.AreEqual(1, row.GetProperty("quantity").GetInt32());
            var baseLayer = row.GetProperty("icon").GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("kind").GetString() == "base");
            Assert.AreEqual(100667000u, baseLayer.GetProperty("id").GetUInt32());

            var detailResponse = await host.GetAsync($"/api/listings/{id}");
            Assert.AreEqual(HttpStatusCode.OK, detailResponse.StatusCode);
            var detail = await MarketApiHost.JsonAsync(detailResponse);
            Assert.AreEqual(id, detail.GetProperty("id").GetInt64());
            Assert.AreEqual(name, detail.GetProperty("name").GetString());
            Assert.AreEqual("MeleeWeapon", detail.GetProperty("itemType").GetString());
            Assert.AreEqual("White Sapphire", detail.GetProperty("material").GetString());
            Assert.AreEqual(150, detail.GetProperty("price").GetInt64());
            Assert.AreEqual(seller.CharacterName, detail.GetProperty("seller").GetString());
            Assert.AreEqual("Missile Weapons 390", detail.GetProperty("wield").GetString());
            // stored as datetime(6): whole microseconds
            Assert.AreEqual(Database.Market.ListingStore.Truncate(listedAt), detail.GetProperty("listedTime").GetDateTime().ToUniversalTime(), "listed time");
        }

        [TestMethod]
        public async Task List_WieldLevelRequirement_IsShownAsLevel()
        {
            var seller = NewSeller();
            var name = MarketApiTestData.UniqueName("Leveled Helm ");
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, name, VaultItemState.Held);
            MarketApiTestData.SetVaultColumns(guid, $"wield_Requirements = {(int)WieldRequirement.Level}, wield_Difficulty = 150");

            await using var host = await MarketApiHost.StartAsync();
            var id = await ListOkAsync(host, await host.SignInForSessionAsync(seller.Name, "pass"), guid, 5);

            var detail = await MarketApiHost.JsonAsync(await host.GetAsync($"/api/listings/{id}"));
            Assert.AreEqual("Level 150", detail.GetProperty("wield").GetString());
            Assert.AreEqual(150, (await BrowseAsync(host, "q=" + Uri.EscapeDataString(name))).Single().GetProperty("level").GetInt32());
        }

        [TestMethod]
        public async Task List_PriceBelowOneOrNotWhole_IsRefused()
        {
            var seller = NewSeller();
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Cheap Sword", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");

            foreach (var price in new[] { 0L, -5L })
            {
                var response = await ListAsync(host, session, guid, price);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, price.ToString());
                Assert.AreEqual("invalid_price", await MarketApiHost.ErrorAsync(response));
            }

            var fractional = await host.PostJsonAsync("/api/listings", $"{{\"itemGuid\": {guid}, \"price\": 1.5}}", session);
            Assert.AreEqual(HttpStatusCode.BadRequest, fractional.StatusCode);
            Assert.AreEqual("invalid_price", await MarketApiHost.ErrorAsync(fractional));

            var missing = await host.PostJsonAsync("/api/listings", $"{{\"itemGuid\": {guid}}}", session);
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.AreEqual("invalid_price", await MarketApiHost.ErrorAsync(missing));

            Assert.AreEqual(VaultItemState.Held, VaultState(guid));
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE item_Guid = {guid};"));
            Assert.AreEqual(0, Events(guid).Length);

            // 1 is the minimum
            await ListOkAsync(host, session, guid, 1);
        }

        [TestMethod]
        public async Task List_ItemNotInTheSellersVault_IsRefused()
        {
            var seller = NewSeller();
            var other = NewSeller("other");
            var othersItem = MarketApiTestData.AddVaultItem(other.AccountId, other.CharacterId, "Not Yours", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");

            foreach (var guid in new[] { othersItem, 0xDEADBEEFu })
            {
                var response = await ListAsync(host, session, guid, 10);
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
                Assert.AreEqual("not_in_vault", await MarketApiHost.ErrorAsync(response));
            }

            Assert.AreEqual(VaultItemState.Held, VaultState(othersItem));
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE item_Guid = {othersItem};"));
        }

        [TestMethod]
        public async Task List_ItemNotHeld_IsRefused()
        {
            var seller = NewSeller();
            var withdrawing = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Leaving", VaultItemState.Withdrawing);
            var held = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Twice", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");

            var response = await ListAsync(host, session, withdrawing, 10);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            Assert.AreEqual("not_held", await MarketApiHost.ErrorAsync(response));
            Assert.AreEqual(VaultItemState.Withdrawing, VaultState(withdrawing));

            // already listed
            await ListOkAsync(host, session, held, 10);
            var again = await ListAsync(host, session, held, 20);
            Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
            Assert.AreEqual("not_held", await MarketApiHost.ErrorAsync(again));
            Assert.AreEqual(1L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE item_Guid = {held};"));
        }

        [TestMethod]
        public async Task List_201stActiveListing_IsRefused()
        {
            var seller = NewSeller();

            await using var host = await MarketApiHost.StartAsync();
            var now = host.Clock.GetUtcNow().UtcDateTime;

            // 200 active listings, plus closed ones that don't count
            for (var i = 0; i < 200; i++)
            {
                var listed = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Stock " + i, VaultItemState.Listed);
                MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, listed, 10, Database.Models.Shard.Market.ListingStatus.Active, now);
            }
            var closed = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Old Stock", VaultItemState.Held);
            MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, closed, 10, Database.Models.Shard.Market.ListingStatus.Delisted, now);
            MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, closed, 10, Database.Models.Shard.Market.ListingStatus.Expired, now);

            var extra = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "One Too Many", VaultItemState.Held);
            var session = await host.SignInForSessionAsync(seller.Name, "pass");

            var response = await ListAsync(host, session, extra, 10);
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            Assert.AreEqual("listing_limit", await MarketApiHost.ErrorAsync(response));
            Assert.AreEqual(VaultItemState.Held, VaultState(extra));
            Assert.AreEqual(200L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE seller_Account_Id = {seller.AccountId} AND status = 'active';"));

            // the cap is a server setting
            MarketApiTestData.SetSetting("market_active_listings", 201);
            try
            {
                await ListOkAsync(host, session, extra, 10);
            }
            finally
            {
                MarketApiTestData.ClearSetting("market_active_listings");
            }
        }

        [TestMethod]
        public async Task List_ConcurrentRequestsForOneItem_ExactlyOneSucceeds()
        {
            var seller = NewSeller();
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Contested", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => ListAsync(host, session, guid, 10 + i))));

            Assert.AreEqual(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created), string.Join(",", responses.Select(r => (int)r.StatusCode)));
            Assert.IsTrue(responses.Where(r => r.StatusCode != HttpStatusCode.Created).All(r => r.StatusCode == HttpStatusCode.Conflict));
            Assert.AreEqual(1L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE item_Guid = {guid};"));
            Assert.AreEqual(1L, MarketApiTestData.Scalar($"SELECT row_Version FROM market_vault_item WHERE item_Guid = {guid};"));
            Assert.AreEqual(1, Events(guid).Length);
        }

        [TestMethod]
        public async Task List_WithoutASession_IsRefused()
        {
            var seller = NewSeller();
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Anon", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await ListAsync(host, null, guid, 10)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.PostJsonAsync("/api/listings/1/delist", new { })).StatusCode);
            Assert.AreEqual(VaultItemState.Held, VaultState(guid));
        }

        [TestMethod]
        public async Task List_AsAnotherOfTheAccountsCharacters_ShowsThatSeller()
        {
            var seller = NewSeller();
            var altName = seller.Name + "Alt";
            var alt = MarketApiTestData.AddCharacter(seller.AccountId, altName);
            var stranger = NewSeller("stranger");
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, MarketApiTestData.UniqueName("Alt Item "), VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");

            var foreign = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 3, characterId = stranger.CharacterId }, session);
            Assert.AreEqual(HttpStatusCode.BadRequest, foreign.StatusCode);
            Assert.AreEqual("invalid_character", await MarketApiHost.ErrorAsync(foreign));

            var response = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 3, characterId = alt }, session);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            var id = (await MarketApiHost.JsonAsync(response)).GetProperty("id").GetInt64();

            Assert.AreEqual(altName, (await MarketApiHost.JsonAsync(await host.GetAsync($"/api/listings/{id}"))).GetProperty("seller").GetString());
            Assert.AreEqual((long)alt, MarketApiTestData.Scalar($"SELECT seller_Character_Id FROM market_listing WHERE id = {id};"));
        }

        // ---- delisting

        [TestMethod]
        public async Task Delist_ReturnsTheItemToHeld()
        {
            var seller = NewSeller();
            var other = NewSeller("other");
            var name = MarketApiTestData.UniqueName("Delist Me ");
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, name, VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");
            var id = await ListOkAsync(host, session, guid, 40);

            // someone else's listing, or one that doesn't exist, isn't found
            var notMine = await host.PostJsonAsync($"/api/listings/{id}/delist", new { }, await host.SignInForSessionAsync(other.Name, "pass"));
            Assert.AreEqual(HttpStatusCode.NotFound, notMine.StatusCode);
            Assert.AreEqual("not_found", await MarketApiHost.ErrorAsync(notMine));
            Assert.AreEqual(HttpStatusCode.NotFound, (await host.PostJsonAsync("/api/listings/987654321/delist", new { }, session)).StatusCode);
            Assert.AreEqual(VaultItemState.Listed, VaultState(guid));

            var response = await host.PostJsonAsync($"/api/listings/{id}/delist", new { }, session);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            Assert.AreEqual(VaultItemState.Held, VaultState(guid));
            Assert.AreEqual(Database.Models.Shard.Market.ListingStatus.Delisted, ListingStatus(id));
            Assert.AreEqual(2L, MarketApiTestData.Scalar($"SELECT row_Version FROM market_vault_item WHERE item_Guid = {guid};"));
            Assert.AreEqual(1L, MarketApiTestData.Scalar($"SELECT row_Version FROM market_listing WHERE id = {id};"));
            Assert.AreEqual(1L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE id = {id} AND closed_Time IS NOT NULL;"));
            CollectionAssert.AreEqual(new[] { $"list:{id}", $"delist:{id}" }, Events(guid));
            Assert.AreEqual(0, (await BrowseAsync(host, "q=" + Uri.EscapeDataString(name))).Length);

            var twice = await host.PostJsonAsync($"/api/listings/{id}/delist", new { }, session);
            Assert.AreEqual(HttpStatusCode.Conflict, twice.StatusCode);
            Assert.AreEqual("not_active", await MarketApiHost.ErrorAsync(twice));

            // the Vault shows it held, and it can be listed again
            var vault = await MarketApiHost.JsonAsync(await host.GetAsync("/api/vault", session));
            Assert.AreEqual("held", vault.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("itemGuid").GetUInt32() == guid).GetProperty("state").GetString());
            await ListOkAsync(host, session, guid, 45);
        }

        // ---- expiry

        [TestMethod]
        public async Task Listing_PastItsLifetime_ExpiresToHeldWithAnExpireEvent()
        {
            var seller = NewSeller();
            var name = MarketApiTestData.UniqueName("Stale ");
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, name, VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");
            var id = await ListOkAsync(host, session, guid, 12);

            host.Clock.Advance(TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1));
            Assert.AreEqual(1, (await BrowseAsync(host, "q=" + Uri.EscapeDataString(name))).Length, "still listed just before its lifetime ends");
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/api/listings/{id}")).StatusCode);

            host.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(0, (await BrowseAsync(host, "q=" + Uri.EscapeDataString(name))).Length);

            Assert.AreEqual(Database.Models.Shard.Market.ListingStatus.Expired, ListingStatus(id));
            Assert.AreEqual(VaultItemState.Held, VaultState(guid));
            CollectionAssert.AreEqual(new[] { $"list:{id}", $"expire:{id}" }, Events(guid));
            Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/api/listings/{id}")).StatusCode);

            // relisting works (the session is 14 days old, so sign in again)
            await ListOkAsync(host, await host.SignInForSessionAsync(seller.Name, "pass"), guid, 12);
        }

        [TestMethod]
        public async Task Listing_Lifetime_ComesFromTheServerSetting()
        {
            var seller = NewSeller();
            var name = MarketApiTestData.UniqueName("Short Lived ");
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, name, VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var id = await ListOkAsync(host, await host.SignInForSessionAsync(seller.Name, "pass"), guid, 12);

            MarketApiTestData.SetSetting("market_listing_lifetime_days", 2);
            try
            {
                host.Clock.Advance(TimeSpan.FromDays(1));
                Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/api/listings/{id}")).StatusCode);

                host.Clock.Advance(TimeSpan.FromDays(1));
                Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/api/listings/{id}")).StatusCode);
                Assert.AreEqual(Database.Models.Shard.Market.ListingStatus.Expired, ListingStatus(id));
                Assert.AreEqual(VaultItemState.Held, VaultState(guid));
            }
            finally
            {
                MarketApiTestData.ClearSetting("market_listing_lifetime_days");
            }
        }

        // ---- bans

        [TestMethod]
        public async Task Browse_BannedSeller_IsHiddenAndBanReturnedToTheVault()
        {
            var seller = NewSeller("banned");
            var token = MarketApiTestData.UniqueName("Contraband ");
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, token + " A", VaultItemState.Held);
            var second = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, token + " B", VaultItemState.Held);
            var honest = NewSeller("honest");
            var honestItem = MarketApiTestData.AddVaultItem(honest.AccountId, honest.CharacterId, token + " C", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");
            var id = await ListOkAsync(host, session, guid, 99);
            var secondId = await ListOkAsync(host, session, second, 98);
            var honestId = await ListOkAsync(host, await host.SignInForSessionAsync(honest.Name, "pass"), honestItem, 97);
            Assert.AreEqual(3, (await BrowseAsync(host, "q=" + Uri.EscapeDataString(token))).Length);

            MarketApiTestData.Ban(seller.AccountId, host.Clock.GetUtcNow().UtcDateTime.AddDays(3));

            var rows = await BrowseAsync(host, "q=" + Uri.EscapeDataString(token));
            CollectionAssert.AreEqual(new[] { honestId }, rows.Select(r => r.GetProperty("id").GetInt64()).ToArray());

            foreach (var (listing, item) in new[] { (id, guid), (secondId, second) })
            {
                Assert.AreEqual(Database.Models.Shard.Market.ListingStatus.BanReturned, ListingStatus(listing));
                Assert.AreEqual(VaultItemState.Held, VaultState(item));
                CollectionAssert.AreEqual(new[] { $"list:{listing}", $"ban_return:{listing}" }, Events(item));
                Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/api/listings/{listing}")).StatusCode);
            }

            Assert.AreEqual(Database.Models.Shard.Market.ListingStatus.Active, ListingStatus(honestId));
        }

        [TestMethod]
        public async Task Detail_BannedSeller_IsNotFoundAndBanReturned()
        {
            var seller = NewSeller("banned");
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Direct Link", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var id = await ListOkAsync(host, await host.SignInForSessionAsync(seller.Name, "pass"), guid, 99);

            MarketApiTestData.Ban(seller.AccountId, host.Clock.GetUtcNow().UtcDateTime.AddDays(3));

            Assert.AreEqual(HttpStatusCode.NotFound, (await host.GetAsync($"/api/listings/{id}")).StatusCode);
            Assert.AreEqual(Database.Models.Shard.Market.ListingStatus.BanReturned, ListingStatus(id));
            Assert.AreEqual(VaultItemState.Held, VaultState(guid));
        }

        [TestMethod]
        [DataRow(false, DisplayName = "ban expires")]
        [DataRow(true, DisplayName = "ban lifted")]
        public async Task BanEnds_AccountListsAgain_VaultUnchanged_BalanceUnchanged_NothingRelisted(bool lifted)
        {
            var seller = NewSeller("tempban");
            var token = MarketApiTestData.UniqueName("Frozen ");
            var listed = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, token + " Listed", VaultItemState.Held);
            var held = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, token + " Held", VaultItemState.Held);
            MarketApiTestData.SetVaultColumns(listed, "workmanship = 6, material_Type = 61, stack_Size = 3, value = 5000");
            MarketApiTestData.SetBalance(seller.AccountId, 77);

            const string columns = "item_Guid, account_Id, character_Id, deposited_Time, wcid, name, item_Type, stack_Size, value, workmanship, material_Type";
            string Snapshot() => string.Join("|", MarketApiTestData.Rows($"SELECT {columns} FROM market_vault_item WHERE account_Id = {seller.AccountId} ORDER BY item_Guid;")) +
                "#" + string.Join("|", MarketApiTestData.Rows($"SELECT id, weenie_Class_Id FROM biota WHERE id IN ({listed}, {held}) ORDER BY id;"));
            var before = Snapshot();

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");
            var id = await ListOkAsync(host, session, listed, 50);

            var banEnds = host.Clock.GetUtcNow().UtcDateTime.AddDays(2);
            MarketApiTestData.Ban(seller.AccountId, banEnds);

            // noticed by browse
            Assert.AreEqual(0, (await BrowseAsync(host, "q=" + Uri.EscapeDataString(token))).Length);
            Assert.AreEqual(Database.Models.Shard.Market.ListingStatus.BanReturned, ListingStatus(id));

            // frozen: no sign-in, the session no longer works
            Assert.AreEqual(HttpStatusCode.Forbidden, (await host.SignInAsync(seller.Name, "pass")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await ListAsync(host, session, held, 5)).StatusCode);

            if (lifted)
                MarketApiTestData.LiftBan(seller.AccountId);
            else
                host.Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(1));

            var newSession = await host.SignInForSessionAsync(seller.Name, "pass");

            var vault = await MarketApiHost.JsonAsync(await host.GetAsync("/api/vault", newSession));
            var items = vault.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("itemGuid").GetUInt32(), i => i.GetProperty("state").GetString());
            CollectionAssert.AreEquivalent(new[] { listed, held }, items.Keys.ToArray());
            Assert.IsTrue(items.Values.All(s => s == "held"), string.Join(",", items.Values));
            Assert.AreEqual(before, Snapshot(), "same GUIDs and properties");

            Assert.AreEqual(77, (await MarketApiHost.JsonAsync(await host.GetAsync("/api/me", newSession))).GetProperty("balance").GetInt64());
            Assert.AreEqual(0L, MarketApiTestData.Scalar($"SELECT COUNT(*) FROM market_listing WHERE seller_Account_Id = {seller.AccountId} AND status = 'active';"), "nothing relisted");
            Assert.AreEqual(0, (await BrowseAsync(host, "q=" + Uri.EscapeDataString(token))).Length);

            // and it can list again
            await ListOkAsync(host, newSession, listed, 60);
            await ListOkAsync(host, newSession, held, 61);
            Assert.AreEqual(2, (await BrowseAsync(host, "q=" + Uri.EscapeDataString(token))).Length);
        }

        [TestMethod]
        public async Task Session_BanNoticed_ReturnsTheAccountsListings()
        {
            var seller = NewSeller("selfban");
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Own Request", VaultItemState.Held);

            await using var host = await MarketApiHost.StartAsync();
            var session = await host.SignInForSessionAsync(seller.Name, "pass");
            var id = await ListOkAsync(host, session, guid, 99);

            MarketApiTestData.Ban(seller.AccountId, host.Clock.GetUtcNow().UtcDateTime.AddDays(3));

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.GetAsync("/api/vault", session)).StatusCode);
            Assert.AreEqual(Database.Models.Shard.Market.ListingStatus.BanReturned, ListingStatus(id));
            Assert.AreEqual(VaultItemState.Held, VaultState(guid));
        }

        // ---- listing page

        [TestMethod]
        public async Task Detail_SoldDelistedExpiredBanReturnedAndUnknown_AreNotFound()
        {
            var seller = NewSeller();
            var buyer = NewSeller("buyer");

            await using var host = await MarketApiHost.StartAsync();
            var now = host.Clock.GetUtcNow().UtcDateTime;
            var session = await host.SignInForSessionAsync(seller.Name, "pass");

            // sold: purchases are ticket 08, so the row is written as a purchase leaves it
            var soldItem = MarketApiTestData.AddVaultItem(buyer.AccountId, buyer.CharacterId, "Sold Thing", VaultItemState.Held);
            var sold = MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, soldItem, 10, Database.Models.Shard.Market.ListingStatus.Sold, now);

            var delistedItem = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Delisted Thing", VaultItemState.Held);
            var delisted = await ListOkAsync(host, session, delistedItem, 10);
            Assert.AreEqual(HttpStatusCode.OK, (await host.PostJsonAsync($"/api/listings/{delisted}/delist", new { }, session)).StatusCode);

            var expiredItem = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Expired Thing", VaultItemState.Listed);
            var expired = MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, expiredItem, 10, Database.Models.Shard.Market.ListingStatus.Active, now.AddDays(-15));

            var returnedItem = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Returned Thing", VaultItemState.Held);
            var returned = MarketApiTestData.AddListing(seller.AccountId, seller.CharacterId, returnedItem, 10, Database.Models.Shard.Market.ListingStatus.BanReturned, now);

            var activeItem = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Active Thing", VaultItemState.Held);
            var active = await ListOkAsync(host, session, activeItem, 10);

            foreach (var id in new[] { sold, delisted, expired, returned, 987654321L })
            {
                var response = await host.GetAsync($"/api/listings/{id}");
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, id.ToString());
                Assert.AreEqual("not_found", await MarketApiHost.ErrorAsync(response));
            }

            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync($"/api/listings/{active}")).StatusCode);
            Assert.AreEqual(VaultItemState.Held, VaultState(expiredItem), "the overdue listing was expired when noticed");
        }
    }
}
