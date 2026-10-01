using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using SkiaSharp;

using ACE.Common;
using ACE.Database.Models.Shard.Market;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;
using ACE.MarketApi.Tests.Support;

namespace ACE.MarketApi.Tests
{
    /// <summary>
    /// Icons: PNGs made from the server's own portal DAT on first request and cached on disk, and the layers, plates and glow in listing responses.
    /// The reference is market.acdreamweave.com (/icons/0x06003237_p19.png is its Nariyid Breastplate, Listing?id=1099).
    /// </summary>
    [TestClass]
    public class MarketApiIconTests
    {
        // the plates the client draws under an icon, by item type
        private const uint WeaponPlate = 0x060011D2;
        private const uint ArmorPlate = 0x060011CF;
        private const uint ClothingPlate = 0x060011F3;
        private const uint JewelryPlate = 0x060011D5;
        private const uint GemPlate = 0x060011D3;
        private const uint OtherPlate = 0x060011D4;

        // the Nariyid Breastplate's clothing table (weenie 27227), and its icons for palette templates 19 and 20 (the weenie's default)
        private const uint NariyidClothingBase = 0x1000054B;
        private const uint NariyidIconPalette19 = 0x06003237;
        private const uint NariyidIconPalette20 = 0x0600323C;

        private const uint Underlay = 0x0600335A;
        private const uint Overlay = 0x060026D5;
        private const uint OverlaySecondary = 0x060026D0;

        /// <summary>
        /// A palette-indexed (P8) 64×64 texture and its default palette, to prove the raw-pixel method applies palettes
        /// </summary>
        private const uint IndexedTexture = 0x06005CE1;

        private static PortalDatDatabase portal;

        private static PortalDatDatabase Portal
        {
            get
            {
                if (portal == null)
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    portal = new PortalDatDatabase(Path.Combine(ConfigManager.Config.Server.DatFilesDirectory, "client_portal.dat"));
                }
                return portal;
            }
        }

        private static string NewCacheDirectory() => Path.Combine(Path.GetTempPath(), "ace-market-api-tests", "icons-" + Guid.NewGuid().ToString("N"));

        private static Task<MarketApiHost> StartAsync(string cacheDirectory) =>
            MarketApiHost.StartOnAsync(MarketApiTestData.ShardDatabase, extraArgs: new[] { $"--Market:IconCachePath={cacheDirectory}" });

        private sealed class Seller
        {
            public uint AccountId;
            public uint CharacterId;
            public string Cookie;
        }

        private static async Task<Seller> NewSellerAsync(MarketApiHost host)
        {
            var name = MarketApiTestData.UniqueName("iconseller");
            var accountId = MarketApiTestData.CreateAccount(name, "pass");

            return new Seller
            {
                AccountId = accountId,
                CharacterId = MarketApiTestData.AddCharacter(accountId, MarketApiTestData.UniqueName("Iconseller")),
                Cookie = await host.SignInForCookieAsync(name, "pass"),
            };
        }

        /// <summary>
        /// Deposits an item with the given Vault search columns, lists it and returns the listing page's icon object
        /// </summary>
        private static async Task<JsonElement> ListedIconAsync(MarketApiHost host, Seller seller, ItemType itemType, string vaultColumns)
        {
            var guid = MarketApiTestData.AddVaultItem(seller.AccountId, seller.CharacterId, "Iconic " + itemType, VaultItemState.Held);
            MarketApiTestData.SetVaultColumns(guid, $"item_Type = {(int)itemType}" + (vaultColumns != null ? ", " + vaultColumns : ""));

            var listed = await host.PostJsonAsync("/api/listings", new { itemGuid = guid, price = 5 }, seller.Cookie);
            Assert.AreEqual(HttpStatusCode.Created, listed.StatusCode, await listed.Content.ReadAsStringAsync());
            var id = (await MarketApiHost.JsonAsync(listed)).GetProperty("id").GetInt64();

            var detail = await host.GetAsync($"/api/listings/{id}");
            Assert.AreEqual(HttpStatusCode.OK, detail.StatusCode);
            var icon = (await MarketApiHost.JsonAsync(detail)).GetProperty("icon");

            // browse rows carry the same icon
            var browse = await MarketApiHost.JsonAsync(await host.GetAsync("/api/listings?limit=100"));
            var row = browse.GetProperty("listings").EnumerateArray().Single(l => l.GetProperty("id").GetInt64() == id);
            Assert.AreEqual(icon.GetRawText(), row.GetProperty("icon").GetRawText());

            return icon;
        }

        private static List<(string Kind, uint Id, string Url)> Layers(JsonElement icon) =>
            icon.GetProperty("layers").EnumerateArray()
                .Select(l => (l.GetProperty("kind").GetString(), l.GetProperty("id").GetUInt32(), l.GetProperty("url").GetString()))
                .ToList();

        private static string Glow(JsonElement icon) => icon.GetProperty("glow").ValueKind == JsonValueKind.Null ? null : icon.GetProperty("glow").GetString();

        /// <summary>
        /// Decodes a PNG to straight (not premultiplied) RGBA, so pixel values compare exactly
        /// </summary>
        private static SKBitmap DecodePng(byte[] png)
        {
            using var codec = SKCodec.Create(new MemoryStream(png));
            Assert.IsNotNull(codec, "not an image");
            Assert.AreEqual(SKEncodedImageFormat.Png, codec.EncodedFormat);

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var bitmap = new SKBitmap(info);
            Assert.AreEqual(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels()));

            return bitmap;
        }

        /// <summary>
        /// An A8R8G8B8 texture's pixels read straight from the DAT, with the client's transparent white made transparent
        /// </summary>
        private static SKColor[] DatPixels(uint textureId)
        {
            var texture = Portal.ReadFromDat<Texture>(textureId);
            Assert.AreEqual(SurfacePixelFormat.PFID_A8R8G8B8, texture.Format);

            return Enumerable.Range(0, texture.Width * texture.Height)
                .Select(i => BitConverter.ToUInt32(texture.SourceData, i * 4))
                .Select(argb => argb == 0xFFFFFFFF ? SKColors.Transparent : new SKColor(argb))
                .ToArray();
        }

        /// <summary>
        /// Fully transparent pixels compare equal whatever their color
        /// </summary>
        private static void AssertSamePixels(IReadOnlyList<SKColor> expected, SKBitmap actual, string what)
        {
            Assert.AreEqual(expected.Count, actual.Width * actual.Height, what + ": size");

            for (var i = 0; i < expected.Count; i++)
            {
                var got = actual.GetPixel(i % actual.Width, i / actual.Width);
                var want = expected[i];

                if (want.Alpha == 0 && got.Alpha == 0)
                    continue;

                Assert.AreEqual(want, got, $"{what}: pixel ({i % actual.Width}, {i / actual.Width})");
            }
        }

        private static async Task<byte[]> GetPngAsync(MarketApiHost host, string path)
        {
            var response = await host.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
            Assert.AreEqual("image/png", response.Content.Headers.ContentType?.MediaType, path);

            return await response.Content.ReadAsByteArrayAsync();
        }

        // ---- PNGs from the DAT, with no Windows-only code

        [TestMethod]
        public async Task Icon_IsAPngMadeFromTheServersOwnDat()
        {
            await using var host = await StartAsync(NewCacheDirectory());

            foreach (var id in new[] { ArmorPlate, WeaponPlate, NariyidIconPalette20 })
            {
                using var bitmap = DecodePng(await GetPngAsync(host, $"/api/icons/0x{id:X8}.png"));

                Assert.AreEqual(32, bitmap.Width);
                Assert.AreEqual(32, bitmap.Height);
                AssertSamePixels(DatPixels(id), bitmap, $"0x{id:X8}");
            }
        }

        [TestMethod]
        public void IconPath_ReferencesNoSystemDrawing()
        {
            // System.Drawing is Windows-only: the API encodes with SkiaSharp and reads pixels through Texture.GetPixels
            var references = typeof(MarketApi).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

            CollectionAssert.DoesNotContain(references, "System.Drawing");
            CollectionAssert.DoesNotContain(references, "System.Drawing.Common");
            CollectionAssert.DoesNotContain(references, "System.Drawing.Primitives");
            CollectionAssert.Contains(references, "SkiaSharp");
        }

        [TestMethod]
        public void Texture_GetPixels_AppliesThePaletteAndCustomColors()
        {
            var texture = Portal.ReadFromDat<Texture>(IndexedTexture);
            Assert.AreEqual(SurfacePixelFormat.PFID_P8, texture.Format);
            var palette = Portal.ReadFromDat<Palette>(texture.DefaultPaletteId.Value);

            var pixels = texture.GetPixels(palette);

            Assert.AreEqual(texture.Width * texture.Height * 4, pixels.Length);
            for (var i = 0; i < texture.Width * texture.Height; i++)
            {
                var argb = palette.Colors[texture.SourceData[i]];
                CollectionAssert.AreEqual(new[] { (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24) }, pixels.Skip(i * 4).Take(4).ToArray(), $"pixel {i}");
            }

            // a custom color replaces its palette index in the output, and leaves the DAT's cached palette alone
            var index = texture.SourceData[0];
            var original = palette.Colors[index];
            var recoloured = Portal.ReadFromDat<Texture>(IndexedTexture);
            recoloured.CustomPaletteColors[index] = 0xFF102030;
            try
            {
                var custom = recoloured.GetPixels(palette);

                CollectionAssert.AreEqual(new byte[] { 0x10, 0x20, 0x30, 0xFF }, custom.Take(4).ToArray());
                Assert.AreEqual(original, palette.Colors[index]);
            }
            finally
            {
                recoloured.CustomPaletteColors.Clear();
            }
        }

        // ---- the disk cache

        [TestMethod]
        public async Task Icon_SecondRequest_IsServedFromTheDiskCache()
        {
            var cache = NewCacheDirectory();
            await using var host = await StartAsync(cache);

            var first = await GetPngAsync(host, $"/api/icons/0x{ArmorPlate:X8}.png");
            var cached = Path.Combine(cache, $"0x{ArmorPlate:X8}.png");
            Assert.IsTrue(File.Exists(cached), "the first request writes the PNG to the disk cache");
            CollectionAssert.AreEqual(first, File.ReadAllBytes(cached));

            // swap the cached file for another icon's: a second request that serves it came from the disk, not the DAT
            var other = await GetPngAsync(host, $"/api/icons/0x{WeaponPlate:X8}.png");
            File.WriteAllBytes(cached, other);

            CollectionAssert.AreEqual(other, await GetPngAsync(host, $"/api/icons/0x{ArmorPlate:X8}.png"));

            // palette variants are cached under their own key
            await GetPngAsync(host, $"/api/icons/0x{NariyidIconPalette19:X8}_p19.png");
            Assert.IsTrue(File.Exists(Path.Combine(cache, $"0x{NariyidIconPalette19:X8}_p19.png")));
        }

        [TestMethod]
        public async Task Icon_NotAnIcon_IsNotFoundAndNothingIsCached()
        {
            var cache = NewCacheDirectory();
            await using var host = await StartAsync(cache);

            foreach (var path in new[]
            {
                "/api/icons/0x12345678.png",                          // not a texture id
                "/api/icons/0x06FFFFF0.png",                          // not in the DAT
                $"/api/icons/0x{IndexedTexture:X8}.png",              // a texture, but not a 32×32 icon
                $"/api/icons/0x{NariyidIconPalette19:X8}_p99.png",    // no clothing table gives this icon for palette 99
                "/api/icons/0x060011CF.gif",
                "/api/icons/060011CF.png",
                "/api/icons/0x060011CF_p.png",
                "/api/icons/0x060011CF_p123456789012.png",
            })
            {
                var response = await host.GetAsync(path);
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);
            }

            Assert.IsFalse(Directory.Exists(cache) && Directory.EnumerateFiles(cache).Any(), "nothing cached");
        }

        // ---- layers and plates in listing responses

        [TestMethod]
        public async Task Listing_IconLayers_PlatePerItemTypeAndTheItemsOwnLayersInClientOrder()
        {
            await using var host = await StartAsync(NewCacheDirectory());
            var seller = await NewSellerAsync(host);

            var plates = new (ItemType Type, uint Plate)[]
            {
                (ItemType.MeleeWeapon, WeaponPlate),
                (ItemType.MissileWeapon, WeaponPlate),
                (ItemType.Caster, WeaponPlate),
                (ItemType.Armor, ArmorPlate),
                (ItemType.Clothing, ClothingPlate),
                (ItemType.Jewelry, JewelryPlate),
                (ItemType.Gem, GemPlate),
                (ItemType.Key, OtherPlate),
                (ItemType.TinkeringMaterial, OtherPlate),
                (ItemType.Writable, OtherPlate),
                (ItemType.Misc, OtherPlate),
            };

            foreach (var (type, plate) in plates)
            {
                var icon = await ListedIconAsync(host, seller, type,
                    $"icon_Underlay = {Underlay}, icon = {NariyidIconPalette20}, icon_Overlay = {Overlay}, icon_Overlay_Secondary = {OverlaySecondary}");

                var expected = new List<(string, uint, string)>
                {
                    ("plate", plate, $"/api/icons/0x{plate:X8}.png"),
                    ("underlay", Underlay, $"/api/icons/0x{Underlay:X8}.png"),
                    ("base", NariyidIconPalette20, $"/api/icons/0x{NariyidIconPalette20:X8}.png"),
                    ("overlay", Overlay, $"/api/icons/0x{Overlay:X8}.png"),
                    ("overlaySecondary", OverlaySecondary, $"/api/icons/0x{OverlaySecondary:X8}.png"),
                };
                CollectionAssert.AreEqual(expected, Layers(icon), type.ToString());
            }

            // layers the item doesn't have are left out; the plate is always there
            var plain = await ListedIconAsync(host, seller, ItemType.Gem, $"icon = {NariyidIconPalette20}");
            CollectionAssert.AreEqual(new List<(string, uint, string)>
            {
                ("plate", GemPlate, $"/api/icons/0x{GemPlate:X8}.png"),
                ("base", NariyidIconPalette20, $"/api/icons/0x{NariyidIconPalette20:X8}.png"),
            }, Layers(plain));

            // every layer URL serves a PNG
            await using var fresh = await StartAsync(NewCacheDirectory());
            foreach (var (_, _, url) in Layers(await ListedIconAsync(host, seller, ItemType.Armor,
                $"icon_Underlay = {Underlay}, icon = {NariyidIconPalette20}, icon_Overlay = {Overlay}, icon_Overlay_Secondary = {OverlaySecondary}")))
                await GetPngAsync(fresh, url);
        }

        // ---- a dyed armor piece shows its palette's colors; _pNN is the palette template

        [TestMethod]
        public async Task Listing_DyedBreastplate_BaseIconIsTheClothingTablesIconForItsPalette()
        {
            await using var host = await StartAsync(NewCacheDirectory());
            var seller = await NewSellerAsync(host);

            // dyed to palette template 19; the stored icon is still the weenie's default (template 20)
            var icon = await ListedIconAsync(host, seller, ItemType.Armor,
                $"icon = {NariyidIconPalette20}, palette_Template = 19, clothing_Base = {NariyidClothingBase}");

            var baseLayer = icon.GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("kind").GetString() == "base");
            Assert.AreEqual(NariyidIconPalette19, baseLayer.GetProperty("id").GetUInt32());
            Assert.AreEqual(19, baseLayer.GetProperty("paletteTemplate").GetInt32());
            Assert.AreEqual($"/api/icons/0x{NariyidIconPalette19:X8}_p19.png", baseLayer.GetProperty("url").GetString());

            // its colors are template 19's, not the default's
            using var dyed = DecodePng(await GetPngAsync(host, baseLayer.GetProperty("url").GetString()));
            AssertSamePixels(DatPixels(NariyidIconPalette19), dyed, "palette 19");
            var undyed = DatPixels(NariyidIconPalette20);
            Assert.IsTrue(Enumerable.Range(0, 32 * 32).Count(i => dyed.GetPixel(i % 32, i / 32) != undyed[i]) > 100, "the dyed icon differs from the default palette's");

            // a palette template without a clothing base, or one its clothing table doesn't have, keeps the stored icon
            foreach (var columns in new[] { $"icon = {NariyidIconPalette20}, palette_Template = 19", $"icon = {NariyidIconPalette20}, palette_Template = 99, clothing_Base = {NariyidClothingBase}" })
            {
                var kept = (await ListedIconAsync(host, seller, ItemType.Armor, columns)).GetProperty("layers").EnumerateArray().Single(l => l.GetProperty("kind").GetString() == "base");
                Assert.AreEqual(NariyidIconPalette20, kept.GetProperty("id").GetUInt32(), columns);
                Assert.AreEqual($"/api/icons/0x{NariyidIconPalette20:X8}.png", kept.GetProperty("url").GetString(), columns);
                Assert.IsFalse(kept.TryGetProperty("paletteTemplate", out _), columns);
            }

            // a single-digit template is written the reference's way, two digits
            var twoDigits = await ListedIconAsync(host, seller, ItemType.Armor, $"icon = {NariyidIconPalette20}, palette_Template = 2, clothing_Base = {NariyidClothingBase}");
            StringAssert.EndsWith(Layers(twoDigits).Single(l => l.Kind == "base").Url, "_p02.png");
            await GetPngAsync(host, Layers(twoDigits).Single(l => l.Kind == "base").Url);
        }

        [TestMethod]
        public async Task Icon_DyedBreastplate_MatchesTheReferenceSitesIcon()
        {
            await using var host = await StartAsync(NewCacheDirectory());

            // the reference site's own PNG for its Nariyid Breastplate (palette template 19), downloaded 2026-09-30
            using var reference = DecodePng(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference_0x06003237_p19.png")));
            using var ours = DecodePng(await GetPngAsync(host, $"/api/icons/0x{NariyidIconPalette19:X8}_p19.png"));

            AssertSamePixels(Enumerable.Range(0, reference.Width * reference.Height).Select(i => reference.GetPixel(i % reference.Width, i / reference.Width)).ToArray(), ours, "reference");
        }

        // ---- glow

        [TestMethod]
        public async Task Listing_MagicalItems_CarryTheGlowClassForTheirUiEffects()
        {
            await using var host = await StartAsync(NewCacheDirectory());
            var seller = await NewSellerAsync(host);

            var cases = new (string Columns, string Glow)[]
            {
                ("ui_Effects = 1", "icon-glow--magical"),
                ("ui_Effects = 2", "icon-glow--poisoned"),
                ("ui_Effects = 4", "icon-glow--boost-health"),
                ("ui_Effects = 32", "icon-glow--fire"),
                ("ui_Effects = 64", "icon-glow--lightning"),
                ("ui_Effects = 128", "icon-glow--frost"),
                ("ui_Effects = 256", "icon-glow--acid"),
                ("ui_Effects = 4096", "icon-glow--nether"),
                ("ui_Effects = 65", "icon-glow--magical icon-glow--lightning"),
                ("ui_Effects = 0", null),
                ("ui_Effects = NULL", null),
            };

            foreach (var (columns, glow) in cases)
                Assert.AreEqual(glow, Glow(await ListedIconAsync(host, seller, ItemType.MeleeWeapon, $"icon = {NariyidIconPalette20}, {columns}")), columns);
        }

        [TestMethod]
        public async Task GlowStylesheet_DefinesAGlowForEveryUiEffect()
        {
            await using var host = await StartAsync(NewCacheDirectory());

            var response = await host.GetAsync("/api/icons/glow.css");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/css", response.Content.Headers.ContentType?.MediaType);
            var css = await response.Content.ReadAsStringAsync();

            foreach (var effect in Enum.GetValues<UiEffects>().Where(e => e != UiEffects.Undef))
            {
                var kebab = string.Concat(effect.ToString().Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
                StringAssert.Contains(css, $".icon-glow--{kebab} {{", effect.ToString());
            }

            // the reference's color for magical items
            StringAssert.Contains(css, ".icon-glow--magical { --icon-glow: rgba(127, 182, 255, 0.75); }");
        }
    }
}
