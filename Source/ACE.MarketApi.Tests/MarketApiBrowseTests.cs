using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// GET /api/listings filters, sorts and cursor paging, GET /api/listings/suggest and GET /api/facets, on seeded data
    /// </summary>
    [TestClass]
    public class MarketApiBrowseTests
    {
        /// <summary>
        /// Five listings whose names all contain Token, listed a minute apart in order Axe, Bow, Cap, Dagger, Egg.
        /// Aaron sells Axe, Cap and Egg; Bella sells Bow and Dagger.
        /// </summary>
        private sealed class Market
        {
            public string Token;
            public string Aaron;
            public string Bella;
            public Dictionary<string, long> Ids = new Dictionary<string, long>();

            public string Query => "q=" + Uri.EscapeDataString(Token);
        }

        private static async Task<Market> SeedAsync(MarketApiHost host)
        {
            var market = new Market { Token = MarketApiTestData.UniqueName("qz") };

            var aaron = MarketApiTestData.UniqueName("aaron");
            var aaronId = MarketApiTestData.CreateAccount(aaron, "pass");
            market.Aaron = "Aaron" + market.Token;
            var aaronChar = MarketApiTestData.AddCharacter(aaronId, market.Aaron);

            var bella = MarketApiTestData.UniqueName("bella");
            var bellaId = MarketApiTestData.CreateAccount(bella, "pass");
            market.Bella = "Bella" + market.Token;
            var bellaChar = MarketApiTestData.AddCharacter(bellaId, market.Bella);

            var level = (int)WieldRequirement.Level;
            var skill = (int)WieldRequirement.Skill;

            var items = new (string Name, bool Aaron, int Type, string Columns, long Price)[]
            {
                ("Axe", true, (int)ItemType.MeleeWeapon, $"workmanship = 5, wield_Requirements = {level}, wield_Difficulty = 100, arcane_Lore = 50", 300),
                ("Bow", false, (int)ItemType.MissileWeapon, $"workmanship = 9, wield_Requirements = {skill}, wield_Skill_Type = {(int)Skill.MissileWeapons}, wield_Difficulty = 300", 100),
                ("Cap", true, (int)ItemType.Armor, $"wield_Requirements = {level}, wield_Difficulty = 50, arcane_Lore = 10", 200),
                ("Dagger", false, (int)ItemType.MeleeWeapon, $"workmanship = 7, wield_Requirements = {level}, wield_Difficulty = 150, arcane_Lore = 90", 50),
                ("Egg", true, (int)ItemType.Food, "workmanship = 1", 1000),
            };

            var aaronCookie = await host.SignInForCookieAsync(aaron, "pass");
            var bellaCookie = await host.SignInForCookieAsync(bella, "pass");

            foreach (var item in items)
            {
                var guid = MarketApiTestData.AddVaultItem(item.Aaron ? aaronId : bellaId, item.Aaron ? aaronChar : bellaChar, $"{market.Token} {item.Name}", VaultItemState.Held);
                MarketApiTestData.SetVaultColumns(guid, $"item_Type = {item.Type}, {item.Columns}");

                var response = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = item.Price }, item.Aaron ? aaronCookie : bellaCookie);
                Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
                market.Ids[item.Name] = (await MarketApiHost.JsonAsync(response)).GetProperty("id").GetInt64();

                host.Clock.Advance(TimeSpan.FromMinutes(1));
            }

            return market;
        }

        private static async Task<JsonElement> GetOkAsync(MarketApiHost host, string path)
        {
            var response = await host.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path + ": " + await response.Content.ReadAsStringAsync());

            return await MarketApiHost.JsonAsync(response);
        }

        /// <summary>
        /// The item names (without the token) of one page, in order
        /// </summary>
        private static async Task<string[]> NamesAsync(MarketApiHost host, Market market, string query)
        {
            var page = await GetOkAsync(host, "/api/listings?" + query);

            return page.GetProperty("listings").EnumerateArray().Select(r => r.GetProperty("name").GetString().Substring(market.Token.Length + 1)).ToArray();
        }

        // ---- filters

        [TestMethod]
        public async Task Filter_Text_MatchesPartOfTheNameIgnoringCase()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            CollectionAssert.AreEqual(new[] { "Dagger" }, await NamesAsync(host, market, "q=" + Uri.EscapeDataString(market.Token + " DAG")));
            CollectionAssert.AreEquivalent(new[] { "Axe", "Bow", "Cap", "Dagger", "Egg" }, await NamesAsync(host, market, market.Query));
            CollectionAssert.AreEqual(Array.Empty<string>(), await NamesAsync(host, market, "q=" + Uri.EscapeDataString(market.Token + " Zebra")));

            // LIKE wildcards are literal
            CollectionAssert.AreEqual(Array.Empty<string>(), await NamesAsync(host, market, "q=" + Uri.EscapeDataString(market.Token + "%")));
            CollectionAssert.AreEqual(Array.Empty<string>(), await NamesAsync(host, market, "q=" + Uri.EscapeDataString(market.Token + "_A")));
        }

        [TestMethod]
        public async Task Filter_ItemType_ByNameOrNumber()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            CollectionAssert.AreEquivalent(new[] { "Axe", "Dagger" }, await NamesAsync(host, market, market.Query + "&type=MeleeWeapon"));
            CollectionAssert.AreEquivalent(new[] { "Cap" }, await NamesAsync(host, market, market.Query + "&type=" + (int)ItemType.Armor));
            CollectionAssert.AreEquivalent(new[] { "Bow" }, await NamesAsync(host, market, market.Query + "&type=missileweapon"));

            var bad = await host.GetAsync("/api/listings?type=Spaceship");
            Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.AreEqual("bad_type", await MarketApiHost.ErrorAsync(bad));
        }

        [TestMethod]
        public async Task Filter_PriceRange_IsInclusive()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            CollectionAssert.AreEquivalent(new[] { "Axe", "Bow", "Cap" }, await NamesAsync(host, market, market.Query + "&minPrice=100&maxPrice=300"));
            CollectionAssert.AreEquivalent(new[] { "Axe", "Egg" }, await NamesAsync(host, market, market.Query + "&minPrice=300"));
            CollectionAssert.AreEquivalent(new[] { "Dagger" }, await NamesAsync(host, market, market.Query + "&maxPrice=99"));
        }

        [TestMethod]
        public async Task Filter_Seller_ShowsOnlyThatSellersListings()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            // no text filter needed: the seller's name is unique
            CollectionAssert.AreEquivalent(new[] { "Bow", "Dagger" }, await NamesAsync(host, market, "seller=" + Uri.EscapeDataString(market.Bella)));
            CollectionAssert.AreEquivalent(new[] { "Axe", "Cap", "Egg" }, await NamesAsync(host, market, "seller=" + Uri.EscapeDataString(market.Aaron.ToUpperInvariant())));
            CollectionAssert.AreEqual(Array.Empty<string>(), await NamesAsync(host, market, "seller=Nobody" + market.Token));

            // filters combine
            CollectionAssert.AreEquivalent(new[] { "Axe" }, await NamesAsync(host, market, "seller=" + Uri.EscapeDataString(market.Aaron) + "&type=MeleeWeapon&maxPrice=500"));
        }

        // ---- sorts and paging

        private static readonly (string Query, string[] Expected)[] Orders =
        {
            ("", new[] { "Egg", "Dagger", "Cap", "Bow", "Axe" }),                           // newest is the default
            ("sort=newest", new[] { "Egg", "Dagger", "Cap", "Bow", "Axe" }),
            ("sort=newest&dir=asc", new[] { "Axe", "Bow", "Cap", "Dagger", "Egg" }),
            ("sort=name", new[] { "Axe", "Bow", "Cap", "Dagger", "Egg" }),
            ("sort=name&dir=desc", new[] { "Egg", "Dagger", "Cap", "Bow", "Axe" }),
            ("sort=workmanship", new[] { "Bow", "Dagger", "Axe", "Egg", "Cap" }),            // highest first, missing last
            ("sort=workmanship&dir=asc", new[] { "Egg", "Axe", "Dagger", "Bow", "Cap" }),
            ("sort=level", new[] { "Dagger", "Axe", "Cap", "Egg", "Bow" }),                  // a skill requirement isn't a level
            ("sort=level&dir=asc", new[] { "Cap", "Axe", "Dagger", "Bow", "Egg" }),
            ("sort=arcane", new[] { "Dagger", "Axe", "Cap", "Egg", "Bow" }),
            ("sort=arcane&dir=asc", new[] { "Cap", "Axe", "Dagger", "Bow", "Egg" }),
            ("sort=price", new[] { "Dagger", "Bow", "Cap", "Axe", "Egg" }),                  // cheapest first
            ("sort=price&dir=desc", new[] { "Egg", "Axe", "Cap", "Bow", "Dagger" }),
            ("sort=seller", new[] { "Axe", "Cap", "Egg", "Bow", "Dagger" }),
            ("sort=seller&dir=desc", new[] { "Dagger", "Bow", "Egg", "Cap", "Axe" }),
        };

        [TestMethod]
        public async Task Sort_EachOrder_IsCorrect()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            foreach (var (query, expected) in Orders)
                CollectionAssert.AreEqual(expected, await NamesAsync(host, market, market.Query + "&" + query), query + ": " + string.Join(",", await NamesAsync(host, market, market.Query + "&" + query)));
        }

        [TestMethod]
        public async Task Paging_EachOrder_WalksEveryListingOnceInOrder()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            foreach (var (query, expected) in Orders)
            {
                var walked = new List<string>();
                var pages = 0;
                string cursor = null;

                do
                {
                    var path = "/api/listings?" + market.Query + "&" + query + "&limit=2" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
                    var page = await GetOkAsync(host, path);
                    var rows = page.GetProperty("listings").EnumerateArray().Select(r => r.GetProperty("name").GetString().Substring(market.Token.Length + 1)).ToArray();

                    Assert.IsTrue(rows.Length <= 2, path);
                    walked.AddRange(rows);
                    pages++;

                    var next = page.GetProperty("nextCursor");
                    cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
                }
                while (cursor != null && pages < 10);

                CollectionAssert.AreEqual(expected, walked, query + ": " + string.Join(",", walked));
                Assert.AreEqual(3, pages, query);
            }
        }

        [TestMethod]
        public async Task Paging_ANewListingDoesNotShiftTheNextPage()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            var first = await GetOkAsync(host, "/api/listings?" + market.Query + "&sort=price&limit=2");
            var cursor = first.GetProperty("nextCursor").GetString();

            // a cheaper listing arrives; the next page carries on after Bow
            var extra = MarketApiTestData.UniqueName("cheap");
            var extraId = MarketApiTestData.CreateAccount(extra, "pass");
            var guid = MarketApiTestData.AddVaultItem(extraId, MarketApiTestData.AddCharacter(extraId, extra + "C"), market.Token + " Apple", VaultItemState.Held);
            Assert.AreEqual(HttpStatusCode.Created, (await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 2 }, await host.SignInForCookieAsync(extra, "pass"))).StatusCode);

            CollectionAssert.AreEqual(new[] { "Cap", "Axe" }, await NamesAsync(host, market, market.Query + "&sort=price&limit=2&cursor=" + Uri.EscapeDataString(cursor)));
        }

        [TestMethod]
        public async Task Paging_BadInput_IsRefused()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            var first = await GetOkAsync(host, "/api/listings?" + market.Query + "&sort=price&limit=2");
            var cursor = Uri.EscapeDataString(first.GetProperty("nextCursor").GetString());

            foreach (var (query, error) in new[]
            {
                ("sort=cheapest", "bad_sort"),
                ("dir=sideways", "bad_sort"),
                ("cursor=garbage!", "bad_cursor"),
                ($"sort=name&cursor={cursor}", "bad_cursor"),          // a cursor from another order
                ("sort=price&dir=asc&cursor=" + new ListingCatalog.Cursor("price", false, "99999999999999999999", 1).Encode(), "bad_cursor"),   // a key that overflows
                ("sort=newest&dir=desc&cursor=" + new ListingCatalog.Cursor("newest", true, "9999999999999999999", 1).Encode(), "bad_cursor"),     // ticks past the largest date
                ("limit=0", "bad_limit"),
                ("limit=abc", "bad_limit"),
                ("minPrice=cheap", "bad_price"),
            })
            {
                var response = await host.GetAsync("/api/listings?" + query);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, query);
                Assert.AreEqual(error, await MarketApiHost.ErrorAsync(response), query);
            }

            // an oversized limit is capped rather than refused
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/listings?limit=100000")).StatusCode);
        }

        [TestMethod]
        public async Task Browse_OnlyActiveListingsAreShown()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            MarkSold(market.Ids["Bow"]);

            CollectionAssert.AreEquivalent(new[] { "Axe", "Cap", "Dagger", "Egg" }, await NamesAsync(host, market, market.Query));
        }

        private static void MarkSold(long listingId) =>
            ACE.Database.Tests.Market.MarketTestDatabase.Execute(MarketApiTestData.ShardDatabase, $"UPDATE market_listing SET status = 'sold', closed_Time = UTC_TIMESTAMP(6), row_Version = row_Version + 1 WHERE id = {listingId};");

        // ---- suggest and facets

        [TestMethod]
        public async Task Suggest_ReturnsMatchingNamesOfActiveListings()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            var all = await GetOkAsync(host, "/api/listings/suggest?" + market.Query);
            CollectionAssert.AreEqual(new[] { "Axe", "Bow", "Cap", "Dagger", "Egg" }.Select(n => market.Token + " " + n).ToArray(),
                all.GetProperty("suggestions").EnumerateArray().Select(s => s.GetString()).ToArray());

            var some = await GetOkAsync(host, "/api/listings/suggest?q=" + Uri.EscapeDataString(market.Token + " d"));
            CollectionAssert.AreEqual(new[] { market.Token + " Dagger" }, some.GetProperty("suggestions").EnumerateArray().Select(s => s.GetString()).ToArray());

            // too short to suggest
            Assert.AreEqual(0, (await GetOkAsync(host, "/api/listings/suggest?q=")).GetProperty("suggestions").GetArrayLength());

            // gone listings aren't suggested
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/listings/suggest?q=x")).StatusCode);
            MarkSold(market.Ids["Dagger"]);
            Assert.AreEqual(0, (await GetOkAsync(host, "/api/listings/suggest?q=" + Uri.EscapeDataString(market.Token + " d"))).GetProperty("suggestions").GetArrayLength());
        }

        [TestMethod]
        public async Task Facets_ListTheItemTypesOnSaleAndTheSorts()
        {
            await using var host = await MarketApiHost.StartAsync();
            var market = await SeedAsync(host);

            var facets = await GetOkAsync(host, "/api/facets");
            var types = facets.GetProperty("itemTypes").EnumerateArray().ToDictionary(t => t.GetProperty("value").GetString(), t => t.GetProperty("count").GetInt32());

            // other tests' listings share the database, so check at least ours are counted
            Assert.IsTrue(types["MeleeWeapon"] >= 2);
            Assert.IsTrue(types["MissileWeapon"] >= 1);
            Assert.IsTrue(types["Armor"] >= 1);
            Assert.IsTrue(types["Food"] >= 1);
            Assert.IsFalse(types.ContainsKey("Gameboard"));

            var sorts = facets.GetProperty("sorts").EnumerateArray().Select(s => s.GetProperty("value").GetString()).ToArray();
            CollectionAssert.AreEqual(new[] { "newest", "name", "workmanship", "level", "arcane", "price", "seller" }, sorts);
        }
    }
}
