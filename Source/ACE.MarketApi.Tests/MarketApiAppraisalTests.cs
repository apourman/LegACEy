using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// Appraisal lines, spells and browse summaries in the reference market's style, formatted at view time from the stored item.
    /// The fixtures are the reference listings market.acdreamweave.com/Listing?id=1100 (Blunt Crossbow) and id=1099 (Nariyid Breastplate).
    /// </summary>
    [TestClass]
    public class MarketApiAppraisalTests
    {
        // spell ids in the portal spell table
        private const int MajorDefender = 2588;
        private const int AuraOfIncantationOfBloodDrinkerSelf = 4395;
        private const int AuraOfIncantationOfSwiftKillerSelf = 4417;
        private const int Hastening = 2081;
        private const int AuraOfCragstonesWill = 2101;
        private const int LegendaryLifeMagicAptitude = 6060;
        private const int IncantationOfRegenerationSelf = 4496;
        private const int TuskersBane = 2098;
        private const int BrogardsDefiance = 2108;

        // cantrips the client does describe (no ExcludedFromItemDescriptions flag), so only their tier names them
        private const int EpicBludgeonWard = 3955;

        /// <summary>
        /// 1 point every 15 seconds
        /// </summary>
        private const double ManaRate15 = -1.0 / 15;

        private sealed class Seller
        {
            public uint AccountId;
            public uint CharacterId;
            public string Cookie;
        }

        private static async Task<Seller> NewSellerAsync(MarketApiHost host)
        {
            var name = MarketApiTestData.UniqueName("appraiser");
            var accountId = MarketApiTestData.CreateAccount(name, "pass");

            return new Seller
            {
                AccountId = accountId,
                CharacterId = MarketApiTestData.AddCharacter(accountId, MarketApiTestData.UniqueName("Appraiser")),
                Cookie = await host.SignInForCookieAsync(name, "pass"),
            };
        }

        /// <summary>
        /// Deposits an item (a biota row plus its Vault row with search columns) and lists it. Returns the listing id.
        /// </summary>
        private static async Task<long> ListAsync(MarketApiHost host, Seller seller, string name, ItemType itemType, Action<uint> properties, string vaultColumns = null)
        {
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, name, VaultItemState.Held);
            MarketApiTestData.SetVaultColumns(guid, $"item_Type = {(int)itemType}" + (vaultColumns != null ? ", " + vaultColumns : ""));
            properties(guid);

            var response = await host.PostJsonAsync("/listings", new { itemGuid = guid, price = 50 }, seller.Cookie);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());

            return (await MarketApiHost.JsonAsync(response)).GetProperty("id").GetInt64();
        }

        private static Task<long> ListBluntCrossbowAsync(MarketApiHost host, Seller seller) =>
            ListAsync(host, seller, "Blunt Crossbow", ItemType.MissileWeapon, guid => MarketApiTestData.AddItemProperties(guid,
                ints: new[]
                {
                    (PropertyInt.ItemType, (int)ItemType.MissileWeapon),
                    (PropertyInt.Value, 16573),
                    (PropertyInt.EncumbranceVal, 1047),
                    (PropertyInt.MaterialType, (int)MaterialType.Opal),
                    (PropertyInt.ItemWorkmanship, 7),
                    (PropertyInt.NumTimesTinkered, 1),
                    (PropertyInt.ImbuedEffect, (int)ImbuedEffectType.BludgeonRending),
                    (PropertyInt.DamageType, (int)DamageType.Bludgeon),
                    (PropertyInt.ElementalDamageBonus, 18),
                    (PropertyInt.WeaponTime, 86),
                    (PropertyInt.AmmoType, (int)AmmoType.Bolt),
                    (PropertyInt.WeaponSkill, (int)Skill.MissileWeapons),
                    (PropertyInt.WieldRequirements, (int)WieldRequirement.RawSkill),
                    (PropertyInt.WieldSkillType, (int)Skill.MissileWeapons),
                    (PropertyInt.WieldDifficulty, 390),
                    (PropertyInt.ItemDifficulty, 193),
                    (PropertyInt.ItemSpellcraft, 370),
                    (PropertyInt.ItemCurMana, 801),
                    (PropertyInt.ItemMaxMana, 801),
                    (PropertyInt.GemCount, 4),
                    (PropertyInt.GemType, (int)MaterialType.Ruby),
                },
                floats: new[]
                {
                    (PropertyFloat.DamageMod, 2.65),
                    (PropertyFloat.WeaponOffense, 1.0),
                    (PropertyFloat.MaximumVelocity, 27.3),
                    (PropertyFloat.WeaponDefense, 1.18),
                    (PropertyFloat.WeaponMissileDefense, 1.03),
                    (PropertyFloat.ManaRate, ManaRate15),
                },
                strings: new[] { (PropertyString.ImbuerName, "Shocobow") },
                spells: new[] { AuraOfIncantationOfBloodDrinkerSelf, AuraOfIncantationOfSwiftKillerSelf, Hastening, MajorDefender, AuraOfCragstonesWill }),
                $"material_Type = {(int)MaterialType.Opal}, workmanship = 7");

        private static Task<long> ListNariyidBreastplateAsync(MarketApiHost host, Seller seller) =>
            ListAsync(host, seller, "Nariyid Breastplate", ItemType.Armor, guid => MarketApiTestData.AddItemProperties(guid,
                ints: new[]
                {
                    (PropertyInt.ItemType, (int)ItemType.Armor),
                    (PropertyInt.Value, 21832),
                    (PropertyInt.EncumbranceVal, 700),
                    (PropertyInt.ItemWorkmanship, 8),
                    (PropertyInt.MaterialType, (int)MaterialType.Silver),
                    (PropertyInt.EquipmentSetId, (int)EquipmentSet.Wise),
                    (PropertyInt.WieldRequirements, (int)WieldRequirement.Level),
                    (PropertyInt.WieldDifficulty, 180),
                    (PropertyInt.ItemDifficulty, 387),
                    (PropertyInt.ItemSpellcraft, 370),
                    (PropertyInt.ItemCurMana, 1280),
                    (PropertyInt.ItemMaxMana, 1280),
                    (PropertyInt.ArmorLevel, 282),
                    (PropertyInt.GemCount, 4),
                    (PropertyInt.GemType, (int)MaterialType.GreenGarnet),
                },
                floats: new[]
                {
                    (PropertyFloat.ManaRate, ManaRate15),
                    (PropertyFloat.ArmorModVsSlash, 1.3),
                    (PropertyFloat.ArmorModVsPierce, 1.0),
                    (PropertyFloat.ArmorModVsBludgeon, 1.0),
                    (PropertyFloat.ArmorModVsFire, 0.99),
                    (PropertyFloat.ArmorModVsCold, 0.87),
                    (PropertyFloat.ArmorModVsAcid, 0.6),
                    (PropertyFloat.ArmorModVsElectric, 0.4),
                    (PropertyFloat.ArmorModVsNether, 1.0),
                },
                bools: new[] { (PropertyBool.Dyable, true) },
                spells: new[] { IncantationOfRegenerationSelf, TuskersBane, BrogardsDefiance, LegendaryLifeMagicAptitude }),
                $"material_Type = {(int)MaterialType.Silver}, workmanship = 8, armor_Level = 282, wield_Requirements = {(int)WieldRequirement.Level}, wield_Difficulty = 180");

        private static async Task<JsonElement> DetailAsync(MarketApiHost host, long listingId)
        {
            var response = await host.GetAsync($"/listings/{listingId}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            return await MarketApiHost.JsonAsync(response);
        }

        private static string[] Lines(JsonElement detail) => detail.GetProperty("lines").EnumerateArray().Select(l => l.GetString()).ToArray();

        private static string[] SpellNames(JsonElement detail) => detail.GetProperty("spells").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray();

        private static void AssertLines(string[] expected, string[] actual)
        {
            Assert.AreEqual(string.Join("\n", expected), string.Join("\n", actual));
        }

        [TestMethod]
        public async Task Detail_BluntCrossbow_LinesMatchTheReference()
        {
            await using var host = await MarketApiHost.StartAsync();
            var seller = await NewSellerAsync(host);

            var detail = await DetailAsync(host, await ListBluntCrossbowAsync(host, seller));

            AssertLines(new[]
            {
                "Value: 16573",
                "Burden: 1047",
                "Material: Opal",
                "Workmanship: 7",
                "Number of Times Tinkered: 1",
                "Imbued Effect: Bludgeon Rending",
                "Imbuer: Shocobow",
                "Damage Type: Bludgeoning",
                "Damage Modifier: +165%",
                "Elemental Damage Bonus: +18",
                "Attack Bonus: +0%",
                "Speed: 86",
                "Ammunition Type: Bolt",
                "Missile Velocity: 27.3",
                "Melee Defense Bonus: +18%",
                "Missile Defense Bonus: +3%",
                "Skill: Missile Weapons",
                "Skill Required: Missile Weapons 390",
                "Difficulty: 193",
                "Spellcraft: 370",
                "Mana: 801 / 801",
                "Mana Rate: 1 point every 15 seconds",
                "Gems: 4 Ruby",
            }, Lines(detail));

            CollectionAssert.AreEqual(new[] { "Major Defender" }, detail.GetProperty("spells").EnumerateArray().Where(s => s.GetProperty("cantrip").GetBoolean()).Select(s => s.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("+165% +18 (Bludgeoning)", detail.GetProperty("summary").GetString());
        }

        /// <summary>
        /// The same lines as the reference page, in the spec's single order (the reference orders armor differently; ticket 11's decision)
        /// </summary>
        [TestMethod]
        public async Task Detail_NariyidBreastplate_LinesMatchTheReferenceInSpecOrder()
        {
            await using var host = await MarketApiHost.StartAsync();
            var seller = await NewSellerAsync(host);

            var detail = await DetailAsync(host, await ListNariyidBreastplateAsync(host, seller));

            AssertLines(new[]
            {
                "Value: 21832",
                "Burden: 700",
                "Material: Silver",
                "Workmanship: 8",
                "Set: Wise",
                "Armor Level: 282",
                "Slashing Protection: 1.30",
                "Piercing Protection: 1.00",
                "Bludgeoning Protection: 1.00",
                "Fire Protection: 0.99",
                "Cold Protection: 0.87",
                "Acid Protection: 0.60",
                "Lightning Protection: 0.40",
                "Nether Protection: 1.00",
                "Difficulty: 387",
                "Spellcraft: 370",
                "Mana: 1280 / 1280",
                "Mana Rate: 1 point every 15 seconds",
                "Gems: 4 Green Garnet",
                "Dyable",
            }, Lines(detail));

            Assert.AreEqual("AL 282", detail.GetProperty("summary").GetString());
            Assert.AreEqual("Level 180", detail.GetProperty("wield").GetString());
        }

        [TestMethod]
        public async Task Detail_NeutralModifiers_ProduceNoLine()
        {
            await using var host = await MarketApiHost.StartAsync();
            var seller = await NewSellerAsync(host);

            var listing = await ListAsync(host, seller, "Plain Wand", ItemType.Caster, guid => MarketApiTestData.AddItemProperties(guid,
                ints: new[]
                {
                    (PropertyInt.ItemType, (int)ItemType.Caster),
                    (PropertyInt.Value, 100),
                    (PropertyInt.NumTimesTinkered, 0),
                    (PropertyInt.ElementalDamageBonus, 0),
                    (PropertyInt.ItemManaCost, 175),
                },
                floats: new[]
                {
                    (PropertyFloat.DamageMod, 1.0),
                    (PropertyFloat.WeaponOffense, 1.0),
                    (PropertyFloat.WeaponDefense, 1.0),
                    (PropertyFloat.WeaponMissileDefense, 1.0),
                    (PropertyFloat.ElementalDamageMod, 1.0),
                    (PropertyFloat.ManaConversionMod, 0.0),
                }));

            var detail = await DetailAsync(host, listing);

            // Attack Bonus is shown even when neutral, as the reference does
            AssertLines(new[] { "Value: 100", "Attack Bonus: +0%", "Mana Cost: 175" }, Lines(detail));
            Assert.AreEqual(JsonValueKind.Null, detail.GetProperty("summary").ValueKind);
        }

        [TestMethod]
        public async Task Detail_NonNeutralCasterModifiers_AreSignedPercentages()
        {
            await using var host = await MarketApiHost.StartAsync();
            var seller = await NewSellerAsync(host);

            var listing = await ListAsync(host, seller, "Nether Sceptre", ItemType.Caster, guid => MarketApiTestData.AddItemProperties(guid,
                ints: new[]
                {
                    (PropertyInt.ItemType, (int)ItemType.Caster),
                    (PropertyInt.DamageType, (int)DamageType.Nether),
                    (PropertyInt.WieldRequirements2, (int)WieldRequirement.Skill),
                    (PropertyInt.WieldSkillType2, (int)Skill.VoidMagic),
                    (PropertyInt.WieldDifficulty2, 385),
                    (PropertyInt.Bonded, 1),
                    (PropertyInt.Attuned, 1),
                },
                floats: new[]
                {
                    (PropertyFloat.WeaponDefense, 1.15),
                    (PropertyFloat.ElementalDamageMod, 1.16),
                    (PropertyFloat.ManaConversionMod, 0.09),
                },
                bools: new[] { (PropertyBool.IsSellable, false) }));

            var detail = await DetailAsync(host, listing);

            AssertLines(new[]
            {
                "Damage Type: Nether",
                "Melee Defense Bonus: +15%",
                "Skill Required: Void Magic 385",
                "Elemental Damage Modifier: +16%",
                "Mana Conversion Bonus: +9%",
                "Bonded",
                "Attuned",
                "Cannot be sold to a vendor",
            }, Lines(detail));

            Assert.AreEqual("+16% (Nether)", detail.GetProperty("summary").GetString());
        }

        [TestMethod]
        public async Task Detail_Spells_AreNamedFromTheDatWithCantripsFirst()
        {
            await using var host = await MarketApiHost.StartAsync();
            var seller = await NewSellerAsync(host);

            var crossbow = await DetailAsync(host, await ListBluntCrossbowAsync(host, seller));
            var breastplate = await DetailAsync(host, await ListNariyidBreastplateAsync(host, seller));

            // cantrips first, then the others in spell table order (the spell book keeps no order of its own)
            CollectionAssert.AreEqual(new[]
            {
                "Major Defender",
                "Hastening",
                "Aura of Cragstone's Will",
                "Aura of Incantation of Blood Drinker Self",
                "Aura of Incantation of Swift Killer Self",
            }, SpellNames(crossbow));

            CollectionAssert.AreEqual(new[]
            {
                "Legendary Life Magic Aptitude",
                "Tusker's Bane",
                "Brogard's Defiance",
                "Incantation of Regeneration Self",
            }, SpellNames(breastplate));

            CollectionAssert.AreEqual(new[] { true, false, false, false }, breastplate.GetProperty("spells").EnumerateArray().Select(s => s.GetProperty("cantrip").GetBoolean()).ToArray());
        }

        [TestMethod]
        public async Task Detail_ACantripTheClientDescribes_IsStillACantrip()
        {
            await using var host = await MarketApiHost.StartAsync();
            var seller = await NewSellerAsync(host);

            var listing = await ListAsync(host, seller, "Warded Mace", ItemType.MeleeWeapon,
                guid => MarketApiTestData.AddItemProperties(guid, spells: new[] { Hastening, EpicBludgeonWard }));

            var spells = (await DetailAsync(host, listing)).GetProperty("spells").EnumerateArray().Select(s => (s.GetProperty("name").GetString(), s.GetProperty("cantrip").GetBoolean())).ToArray();

            CollectionAssert.AreEqual(new[] { ("Epic Bludgeon Ward", true), ("Hastening", false) }, spells);
        }

        [TestMethod]
        public async Task Detail_WeaponDamage_IsARangeWithItsTypes()
        {
            await using var host = await MarketApiHost.StartAsync();
            var seller = await NewSellerAsync(host);

            var listing = await ListAsync(host, seller, "Bastone", ItemType.MeleeWeapon, guid => MarketApiTestData.AddItemProperties(guid,
                ints: new[]
                {
                    (PropertyInt.ItemType, (int)ItemType.MeleeWeapon),
                    (PropertyInt.Damage, 52),
                    (PropertyInt.DamageType, (int)(DamageType.Slash | DamageType.Pierce)),
                },
                floats: new[] { (PropertyFloat.DamageVariance, 0.35) }));

            var detail = await DetailAsync(host, listing);

            AssertLines(new[] { "Damage Type: Slashing, Piercing", "Damage: 34 - 52" }, Lines(detail));
            Assert.AreEqual("34-52 (Slashing, Piercing)", detail.GetProperty("summary").GetString());
        }

        [TestMethod]
        public async Task Browse_Summaries_ReadLikeTheReference()
        {
            await using var host = await MarketApiHost.StartAsync();
            var seller = await NewSellerAsync(host);

            var crossbow = await ListBluntCrossbowAsync(host, seller);
            var breastplate = await ListNariyidBreastplateAsync(host, seller);

            var response = await host.GetAsync("/listings?seller=" + Uri.EscapeDataString(await SellerNameAsync(host, crossbow)));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var rows = (await MarketApiHost.JsonAsync(response)).GetProperty("listings").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetInt64(), r => r.GetProperty("summary").GetString());

            Assert.AreEqual("+165% +18 (Bludgeoning)", rows[crossbow]);
            Assert.AreEqual("AL 282", rows[breastplate]);
        }

        private static async Task<string> SellerNameAsync(MarketApiHost host, long listingId) => (await DetailAsync(host, listingId)).GetProperty("seller").GetString();

        [TestMethod]
        public async Task Detail_Material_UsesTheDatName()
        {
            await using var host = await MarketApiHost.StartAsync();
            var seller = await NewSellerAsync(host);

            // the MaterialType enum spells it "SmokeyQuartz"; the game's name is "Smoky Quartz"
            var listing = await ListAsync(host, seller, "Quartz Ring", ItemType.Jewelry,
                guid => MarketApiTestData.AddItemProperties(guid, ints: new[] { (PropertyInt.MaterialType, (int)MaterialType.SmokeyQuartz) }),
                $"material_Type = {(int)MaterialType.SmokeyQuartz}");

            var detail = await DetailAsync(host, listing);

            Assert.AreEqual("Smoky Quartz", detail.GetProperty("material").GetString());
            AssertLines(new[] { "Material: Smoky Quartz" }, Lines(detail));
        }

        /// <summary>
        /// Nothing formatted is stored: an item listed under one rule table shows the next rule table's text, and the database is untouched
        /// </summary>
        [TestMethod]
        public async Task ChangingARule_ChangesAnAlreadyListedItem_WithNoDatabaseChange()
        {
            long listing;
            string before;

            await using (var host = await MarketApiHost.StartAsync())
            {
                var seller = await NewSellerAsync(host);
                listing = await ListNariyidBreastplateAsync(host, seller);

                Assert.AreEqual("Value: 21832", Lines(await DetailAsync(host, listing))[0]);
                before = Snapshot(listing);
            }

            var changed = new AppraisalRules(AppraisalRules.Default.Rules.Select(rule => rule.Label == "Value" ? rule with { Label = "Worth" } : rule));

            await using (var host = await MarketApiHost.StartAsync(services => services.AddSingleton(changed)))
            {
                var lines = Lines(await DetailAsync(host, listing));

                Assert.AreEqual("Worth: 21832", lines[0]);
                Assert.AreEqual("Burden: 700", lines[1]);
            }

            Assert.AreEqual(before, Snapshot(listing));
        }

        /// <summary>
        /// The listing, its Vault row and the item's properties, as stored
        /// </summary>
        private static string Snapshot(long listingId)
        {
            var guid = MarketApiTestData.Scalar($"SELECT item_Guid FROM market_listing WHERE id = {listingId};");

            return string.Join("\n",
                MarketApiTestData.Rows($"SELECT * FROM market_listing WHERE id = {listingId};")
                    .Concat(MarketApiTestData.Rows($"SELECT * FROM market_vault_item WHERE item_Guid = {guid};"))
                    .Concat(MarketApiTestData.Rows($"SELECT type, value FROM biota_properties_int WHERE object_Id = {guid} ORDER BY type;"))
                    .Concat(MarketApiTestData.Rows($"SELECT type, value FROM biota_properties_float WHERE object_Id = {guid} ORDER BY type;")));
        }
    }
}
