using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Market;
using ACE.Database.Models.Shard;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClientChannel;
using ACE.Server.Managers;
using ACE.Server.Market;
using ACE.Server.Tests.ClientChannel;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.ClientChannel.ChannelTestClient;

namespace ACE.Server.Tests.Market
{
    /// <summary>
    /// The market window's browse, suggest, facets and detail actions, through the channel test client in the Vault's test world. The catalog is shared
    /// by every test, so each test names its listings with its own token and searches by it.
    /// </summary>
    public partial class VaultTests
    {
        /// <summary>A spell in the DAT's spell table, put in the item's spell book</summary>
        private const int BrowseSpell = 2;

        [TestMethod]
        public void MarketBrowse_NeedsTheMarketStation()
        {
            var player = ConnectedPlayer();

            var refused = Request(player, MarketBrowseActions.Browse, BrowseBody("newest", true, "", 10));

            Assert.AreEqual(ChannelStatus.NoStation, refused.Status);
            Assert.AreEqual("Use the market station to do that.", Text(refused.Body));
        }

        [TestMethod]
        public void MarketBrowse_WhileMarketEnabledIsOff_IsRefusedWithThePlainMessage()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            PropertyManager.ModifyBool(Vault.MarketEnabledKey, false);
            ChannelEvent refused;
            try
            {
                refused = Request(player, MarketBrowseActions.Browse, BrowseBody("newest", true, "", 10));
            }
            finally
            {
                PropertyManager.ModifyBool(Vault.MarketEnabledKey, true);
            }

            Assert.AreEqual(ChannelStatus.Unavailable, refused.Status);
            Assert.AreEqual(VaultMessages.For(VaultOutcome.MarketClosed, null), Text(refused.Body));
        }

        [TestMethod]
        public void MarketBrowse_FiltersEachKind_AndSortsByPrice()
        {
            var token = Token();
            var seller = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var other = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var blade = Listed(seller, $"{token} blade", 100, item => Stats(item, workmanship: 5, level: 120));
            var helm = Listed(seller, $"{token} helm", 300, item => Stats(item, workmanship: 9, level: 180, arcane: 250));
            var ring = Listed(other, $"{token} ring", 200, item => Stats(item, workmanship: 2));
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            CollectionAssert.AreEqual(new[] { blade, ring, helm }, Browse(player, ("name", token, "")).Select(i => i.Guid).ToArray(), "name, cheapest first");
            CollectionAssert.AreEqual(new[] { ring }, Browse(player, ("name", token, ""), ("price", "150", "250")).Select(i => i.Guid).ToArray(), "price range");
            CollectionAssert.AreEqual(new[] { blade, ring }, Browse(player, ("name", token, ""), ("price", "", "250")).Select(i => i.Guid).ToArray(), "price, maximum only");
            CollectionAssert.AreEqual(new[] { ring, helm }, Browse(player, ("name", token, ""), ("price", "150", "")).Select(i => i.Guid).ToArray(), "price, minimum only");

            // the seller's name as the catalog reads it (the test players' character names aren't the ones they were created with)
            var sellerName = Browse(player, ("name", token, "")).First(i => i.Guid == blade).Seller;
            var bySeller = Browse(player, ("name", token, ""), ("seller", sellerName, ""));
            Assert.IsTrue(bySeller.All(i => i.Seller == sellerName), "seller: only that seller's listings");
            Assert.IsTrue(bySeller.Any(i => i.Guid == blade) && bySeller.Any(i => i.Guid == helm), "seller: that seller's listings are there");

            CollectionAssert.AreEqual(new[] { helm }, Browse(player, ("name", token, ""), ("level", "150", "")).Select(i => i.Guid).ToArray(), "minimum wield level");
            CollectionAssert.AreEqual(new[] { blade, helm }, Browse(player, ("name", token, ""), ("workmanship", "5", "")).Select(i => i.Guid).ToArray(), "minimum workmanship");
            CollectionAssert.AreEqual(new[] { helm }, Browse(player, ("name", token, ""), ("arcane", "200", "")).Select(i => i.Guid).ToArray(), "minimum arcane lore");
            Assert.AreEqual(3, Browse(player, ("name", token, ""), ("category", "MeleeWeapon", "")).Count, "category");
        }

        [TestMethod]
        public void MarketBrowse_PagesWithTheCursor_AndRefusesACursorFromAnotherSort()
        {
            var token = Token();
            var seller = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var first = Listed(seller, $"{token} one", 10);
            var second = Listed(seller, $"{token} two", 20);
            var third = Listed(seller, $"{token} three", 30);
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            var page1 = ReadBrowse(Request(player, MarketBrowseActions.Browse, BrowseBody("price", false, "", 2, ("name", token, ""))).Body);
            CollectionAssert.AreEqual(new[] { first, second }, page1.Items.Select(i => i.Guid).ToArray(), "first page");
            Assert.IsFalse(string.IsNullOrEmpty(page1.Next), "a next page");

            var page2 = ReadBrowse(Request(player, MarketBrowseActions.Browse, BrowseBody("price", false, page1.Next, 2, ("name", token, ""))).Body);
            CollectionAssert.AreEqual(new[] { third }, page2.Items.Select(i => i.Guid).ToArray(), "the rest, with no repeats");
            Assert.AreEqual("", page2.Next, "the last page");

            var otherSort = Request(player, MarketBrowseActions.Browse, BrowseBody("name", false, page1.Next, 2, ("name", token, "")));
            Assert.AreEqual(ChannelStatus.BadRequest, otherSort.Status, "a cursor from another sort is refused");
        }

        [TestMethod]
        public void MarketBrowse_EverySortInEitherDirection_PagesToTheOrderOfOnePage()
        {
            var token = Token();
            var seller = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            var other = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            Listed(seller, $"{token} a", 300, item => Stats(item, workmanship: 5, level: 120));
            Listed(seller, $"{token} b", 100, item => Stats(item, workmanship: 9, level: 180, arcane: 250));
            Listed(other, $"{token} c", 200, item => Stats(item, workmanship: 9));
            Listed(other, $"{token} d", 200);
            Listed(seller, $"{token} e", 50, item => Stats(item, arcane: 100));
            foreach (var sort in ListingCatalog.Sorts)
            {
                foreach (var descending in new[] { false, true })
                {
                    // a player for each order: the channel rate-limits each player's requests
                    var player = ConnectedPlayer();
                    Use(player, NewStation(player, MarketStation));
                    WaitForPushes(player, 1);

                    var whole = ReadBrowse(Request(player, MarketBrowseActions.Browse, BrowseBody(sort.Value, descending, "", 100, ("name", token, ""))).Body).Items;
                    var paged = new List<ShownListing>();
                    var cursor = "";
                    do
                    {
                        var reply = Request(player, MarketBrowseActions.Browse, BrowseBody(sort.Value, descending, cursor, 2, ("name", token, "")));
                        Assert.AreEqual(ChannelStatus.Ok, reply.Status, $"{sort.Value} {descending}");
                        var page = ReadBrowse(reply.Body);
                        paged.AddRange(page.Items);
                        cursor = page.Next;
                    } while (cursor.Length > 0);

                    Assert.AreEqual(5, whole.Count, $"{sort.Value} {descending}: every listing");
                    CollectionAssert.AreEqual(whole.Select(i => i.Guid).ToArray(), paged.Select(i => i.Guid).ToArray(), $"{sort.Value} {descending}: paged order");
                }
            }
        }

        [TestMethod]
        public void MarketBrowse_ShowsTheOwnListingsAndTheAppraisalAndSpells()
        {
            var token = Token();
            var seller = ConnectedPlayer();
            var listed = Listed(seller, $"{token} mine", 500, item =>
            {
                Stats(item, workmanship: 7);
                item.MaterialType = MaterialType.Steel;
                item.Biota.PropertiesSpellBook ??= new Dictionary<int, float>();
                item.Biota.PropertiesSpellBook[BrowseSpell] = 2.0f;
            });
            Use(seller, NewStation(seller, MarketStation));
            WaitForPushes(seller, 1);

            var mine = Browse(seller, ("name", token, ""));
            Assert.IsTrue(mine.Single().Own, "the seller's own listing is marked");

            var detail = ReadDetail(Request(seller, MarketBrowseActions.Detail, BitConverter.GetBytes(mine.Single().Id)).Body);
            Assert.AreEqual(listed, detail.Item.Guid);
            Assert.IsTrue(detail.Lines.Contains("Workmanship: 7"), "the appraisal line is the portal's");
            Assert.AreEqual(DatMaterialName(MaterialType.Steel), detail.Item.Material, "the material is the DAT's name");
            CollectionAssert.Contains(detail.Spells, DatSpellName(BrowseSpell), "the spell is the DAT's name");
        }

        [TestMethod]
        public void MarketSuggest_NamesTheVisibleListingsThatStartWithTheText()
        {
            var token = Token();
            var seller = VaultTestWorld.NewPlayer(VaultTestWorld.NewAccountId());
            Listed(seller, $"{token} suggested", 50);
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            var reply = Request(player, MarketBrowseActions.Suggest, ChannelWire.Body(w => ChannelWire.WriteString(w, token)));

            Assert.AreEqual(ChannelStatus.Ok, reply.Status);
            var reader = new BinaryReader(new MemoryStream(reply.Body));
            var count = reader.ReadInt32();
            var names = Enumerable.Range(0, count).Select(_ => ChannelWire.ReadString(reader)).ToList();
            Assert.AreEqual($"{token} suggested", string.Join("|", names));
        }

        [TestMethod]
        public void MarketFacets_ListTheSortsAndTheFilterKindsWithTheCategoriesAsTheirOptions()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            var reader = new BinaryReader(new MemoryStream(Request(player, MarketBrowseActions.Facets, Array.Empty<byte>()).Body));
            var sorts = Enumerable.Range(0, reader.ReadInt32()).Select(_ => (Value: ChannelWire.ReadString(reader), Label: ChannelWire.ReadString(reader), Descending: reader.ReadByte() != 0)).ToList();
            var kinds = Enumerable.Range(0, reader.ReadInt32()).Select(_ => ReadKind(reader)).ToList();

            Assert.AreEqual(ListingCatalog.Sorts.Count, sorts.Count, "sorts");
            Assert.IsTrue(sorts.Any(s => s.Value == "price" && !s.Descending), "price sorts up by default");
            Assert.AreEqual(MarketBrowseActions.FilterKinds.Count, kinds.Count, "filter kinds");
            Assert.AreEqual(MarketBrowseActions.FilterShape.Range, kinds.Single(k => k.Key == "price").Shape);
            Assert.AreEqual(0, kinds.Single(k => k.Key == "price").Options.Count, "a range has no options");
            var categories = kinds.Single(k => k.Key == "category").Options;
            Assert.IsTrue(categories.Any(c => c.Value == "MeleeWeapon" && c.Label == "Melee Weapon" && c.Count > 0), "categories are the choice's options, with counts");
        }

        [TestMethod]
        public void MarketDetail_OfAListingThatIsGone_IsRefused()
        {
            var player = ConnectedPlayer();
            Use(player, NewStation(player, MarketStation));
            WaitForPushes(player, 1);

            var reply = Request(player, MarketBrowseActions.Detail, BitConverter.GetBytes(long.MaxValue));

            Assert.AreEqual(ChannelStatus.BadRequest, reply.Status);
            Assert.AreEqual("That listing is no longer for sale.", Text(reply.Body));
        }

        private static string Token() => "tok" + Guid.NewGuid().ToString("N").Substring(0, 10);

        /// <summary>Deposits a sword with the name and lists it; returns its guid</summary>
        private static uint Listed(Player seller, string name, long price, Action<WorldObject> configure = null)
        {
            var guid = DepositedNamed(seller, 1, _ => name, configure)[0];
            using var shard = new ShardDbContext();
            Assert.AreEqual(ListingOutcome.Ok, ListingStore.List(shard, seller.Character.AccountId, guid, price, null, DateTime.UtcNow, () => false).Outcome);
            return guid;
        }

        private static void Stats(WorldObject item, int? workmanship = null, int? level = null, int? arcane = null)
        {
            if (workmanship is int work)
                item.ItemWorkmanship = work;
            if (level is int required)
            {
                item.WieldRequirements = WieldRequirement.Level;
                item.WieldDifficulty = required;
            }
            if (arcane is int lore)
                item.SetProperty(PropertyInt.ItemDifficulty, lore);
        }

        /// <summary>The material's name as the DAT gives it, with its underscores as spaces</summary>
        private static string DatMaterialName(MaterialType material) =>
            DatNames.Materials(DatManager.PortalDat.ReadFromDat<DualDidMapper>(DatNames.MaterialTable).ClientEnumToName)[(uint)material];

        private static string DatSpellName(int spell) => DatManager.PortalDat.SpellTable.Spells[(uint)spell].Name;

        private static byte[] BrowseBody(string sort, bool descending, string cursor, int limit, params (string Key, string Low, string High)[] filters) => ChannelWire.Body(w =>
        {
            ChannelWire.WriteString(w, sort);
            w.Write((byte)(descending ? 1 : 0));
            ChannelWire.WriteString(w, cursor);
            w.Write(limit);
            w.Write(filters.Length);
            foreach (var (key, low, high) in filters)
            {
                ChannelWire.WriteString(w, key);
                ChannelWire.WriteString(w, low);
                ChannelWire.WriteString(w, high);
            }
        });

        /// <summary>Browses by price, cheapest first, through every page; the listings in order</summary>
        private static List<ShownListing> Browse(Player player, params (string Key, string Low, string High)[] filters)
        {
            var all = new List<ShownListing>();
            var cursor = "";
            do
            {
                var page = ReadBrowse(Request(player, MarketBrowseActions.Browse, BrowseBody("price", false, cursor, 100, filters)).Body);
                all.AddRange(page.Items);
                cursor = page.Next;
            } while (cursor.Length > 0);
            return all;
        }

        private sealed class ShownListing
        {
            public long Id;
            public uint Guid;
            public string Name;
            public long Price;
            public string Seller;
            public string Material;
            public bool Own;
        }

        private static (List<ShownListing> Items, string Next) ReadBrowse(byte[] body)
        {
            var reader = new BinaryReader(new MemoryStream(body));
            var count = reader.ReadInt32();
            var items = Enumerable.Range(0, count).Select(_ => ReadListing(reader)).ToList();
            return (items, ChannelWire.ReadString(reader));
        }

        private static (ShownListing Item, List<string> Lines, List<string> Spells) ReadDetail(byte[] body)
        {
            var reader = new BinaryReader(new MemoryStream(body));
            var item = ReadListing(reader);
            var lines = Enumerable.Range(0, reader.ReadInt32()).Select(_ => ChannelWire.ReadString(reader)).ToList();
            var spells = Enumerable.Range(0, reader.ReadInt32()).Select(_ =>
            {
                var name = ChannelWire.ReadString(reader);
                reader.ReadByte();
                return name;
            }).ToList();
            return (item, lines, spells);
        }

        private static (string Key, string Label, MarketBrowseActions.FilterShape Shape, string Query, List<(string Value, string Label, int Count)> Options) ReadKind(BinaryReader reader)
        {
            var key = ChannelWire.ReadString(reader);
            var label = ChannelWire.ReadString(reader);
            var shape = (MarketBrowseActions.FilterShape)reader.ReadByte();
            var query = ChannelWire.ReadString(reader);
            var options = Enumerable.Range(0, reader.ReadInt32()).Select(_ => (Value: ChannelWire.ReadString(reader), Label: ChannelWire.ReadString(reader), Count: reader.ReadInt32())).ToList();
            return (key, label, shape, query, options);
        }

        private static ShownListing ReadListing(BinaryReader reader)
        {
            var listing = new ShownListing { Id = reader.ReadInt64(), Guid = reader.ReadUInt32() };
            listing.Name = ChannelWire.ReadString(reader);
            ChannelWire.ReadString(reader); // type
            ChannelWire.ReadString(reader); // type label
            listing.Material = ChannelWire.ReadString(reader);
            reader.ReadInt32(); // workmanship
            reader.ReadInt32(); // level
            reader.ReadInt32(); // arcane lore
            ChannelWire.ReadString(reader); // summary
            reader.ReadInt32(); // stack size
            listing.Price = reader.ReadInt64();
            listing.Seller = ChannelWire.ReadString(reader);
            reader.ReadInt64(); // listed time
            listing.Own = reader.ReadByte() != 0;
            for (var field = 0; field < 6; field++)
                reader.ReadUInt32(); // plate, underlay, icon, overlay, overlay secondary, ui effects
            return listing;
        }
    }
}
