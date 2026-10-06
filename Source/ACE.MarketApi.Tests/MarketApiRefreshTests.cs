using System.Linq;
using System.Net;
using System.Threading.Tasks;

using ACE.Database.Market;
using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// Browse after the game's /market refresh: the API shows whatever the Vault row's search columns say, so a refreshed row is what visitors see.
    /// The game builds the columns from the item (seam 2); this writes them through the same VaultStore call and reads them back over HTTP.
    /// </summary>
    [TestClass]
    public class MarketApiRefreshTests
    {
        [TestMethod]
        public async Task Browse_AfterTheSearchColumnsAreRefreshed_ShowsTheRefreshedItem_AndTheListingStillSells()
        {
            await using var host = await MarketApiHost.StartAsync();

            var token = MarketApiTestData.UniqueName("rf");
            var seller = MarketApiTestData.UniqueName("seller");
            var sellerId = MarketApiTestData.CreateAccount(seller, "pass");
            var sellerChar = MarketApiTestData.AddCharacter(sellerId, seller + "Main");
            var guid = MarketApiTestData.AddVaultItem(sellerId, sellerChar, $"{token} Old Name", VaultItemState.Held);
            MarketApiTestData.SetVaultColumns(guid, $"item_Type = {(int)ItemType.MeleeWeapon}, workmanship = 3");

            var session = await host.SignInForSessionAsync(seller, "pass");
            var listed = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 40 }, session);
            Assert.AreEqual(HttpStatusCode.Created, listed.StatusCode, await listed.Content.ReadAsStringAsync());
            var rowVersion = MarketApiTestData.Scalar($"SELECT row_Version FROM market_vault_item WHERE item_Guid = {guid};");

            using (var shard = MarketApiTestData.Shard())
            {
                var updated = VaultStore.UpdateSearchColumns(shard, new VaultItem
                {
                    ItemGuid = guid,
                    Wcid = 35,
                    Name = $"{token} New Name",
                    ItemType = (int)ItemType.MeleeWeapon,
                    StackSize = 1,
                    Value = 900,
                    Workmanship = 8,
                    WieldRequirements = (int)WieldRequirement.Level,
                    WieldDifficulty = 120,
                    ArcaneLore = 75,
                });

                Assert.IsTrue(updated, "the row was there to refresh");
            }

            var page = await MarketApiHost.JsonAsync(await host.GetAsync("/api/listings?q=" + System.Uri.EscapeDataString(token)));
            var row = page.GetProperty("listings").EnumerateArray().Single();

            Assert.AreEqual($"{token} New Name", row.GetProperty("name").GetString());
            Assert.AreEqual(8, row.GetProperty("workmanship").GetInt32());
            Assert.AreEqual(120, row.GetProperty("level").GetInt32());
            Assert.AreEqual(75, row.GetProperty("arcaneLore").GetInt32());

            var byOldName = await MarketApiHost.JsonAsync(await host.GetAsync("/api/listings?q=" + System.Uri.EscapeDataString(token + " Old")));
            Assert.AreEqual(0, byOldName.GetProperty("listings").GetArrayLength(), "the old name no longer matches");

            Assert.AreEqual(rowVersion, MarketApiTestData.Scalar($"SELECT row_Version FROM market_vault_item WHERE item_Guid = {guid};"),
                "a refresh doesn't bump the row version, so it never makes a purchase or delisting in flight fail");
            Assert.AreEqual(VaultItemState.Listed, MarketApiTestData.Rows($"SELECT state FROM market_vault_item WHERE item_Guid = {guid};").Single());
        }

        [TestMethod]
        public void UpdateSearchColumns_ARowThatIsGone_ReturnsFalse()
        {
            using var shard = MarketApiTestData.Shard();

            Assert.IsFalse(VaultStore.UpdateSearchColumns(shard, new VaultItem { ItemGuid = 0xBFFFFFFE, Name = "nothing" }));
        }
    }
}
