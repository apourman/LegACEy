using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using ACE.Database.Market;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;

namespace ACE.MarketApi
{
    /// <summary>
    /// What the market needs from the game's own data files: spell names (and which spells are cantrips) and material names for the appraisal,
    /// and icon textures and clothing tables for icons. Read from client_portal.dat, the same file the game server uses.
    /// </summary>
    public sealed class GameData : IAppraisalNames
    {
        public const string PortalDatFile = "client_portal.dat";


        /// <summary>
        /// Icons, and each layer of one, are 32×32
        /// </summary>
        public const int IconSize = 32;

        private static readonly ConcurrentDictionary<string, GameData> loaded = new ConcurrentDictionary<string, GameData>();

        private readonly IReadOnlyDictionary<uint, AppraisalSpell> spells;

        private readonly IReadOnlyDictionary<uint, string> materials;

        /// <summary>
        /// Kept open for icons, which are read on demand. Its reads are thread safe.
        /// </summary>
        private readonly PortalDatDatabase portal;

        /// <summary>
        /// Every (icon, palette template) pair a clothing table names, read on first use
        /// </summary>
        private readonly Lazy<HashSet<(uint Icon, int PaletteTemplate)>> clothingIcons;

        private GameData(IReadOnlyDictionary<uint, AppraisalSpell> spells, IReadOnlyDictionary<uint, string> materials, PortalDatDatabase portal)
        {
            this.spells = spells;
            this.materials = materials;
            this.portal = portal;

            // a failed scan is retried on the next request rather than remembered
            clothingIcons = new Lazy<HashSet<(uint, int)>>(ReadClothingIcons, System.Threading.LazyThreadSafetyMode.PublicationOnly);
        }

        /// <summary>
        /// Reads the portal DAT in the directory, once per process. Throws FileNotFoundException when it isn't there.
        /// </summary>
        public static GameData Load(string datDirectory) => loaded.GetOrAdd(Path.GetFullPath(datDirectory), Read);

        private static GameData Read(string datDirectory)
        {
            var path = Path.Combine(datDirectory, PortalDatFile);

            if (!File.Exists(path))
                throw new FileNotFoundException($"The game data file {PortalDatFile} was not found in {datDirectory}. Set DatFilesDirectory in Config.js.", path);

            // the DAT's strings are Windows-1252
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var portal = new PortalDatDatabase(path);

            var spells = portal.SpellTable.Spells.ToDictionary(s => s.Key, s => AppraisalSpell.From(s.Value.Name, (SpellFlags)s.Value.Bitfield));

            var materials = DatNames.Materials(portal.ReadFromDat<DualDidMapper>(DatNames.MaterialTable).ClientEnumToName);

            return new GameData(spells, materials, portal);
        }

        /// <summary>
        /// The spell, or null when the spell table doesn't have it
        /// </summary>
        public AppraisalSpell FindSpell(int spellId) => spells.TryGetValue(unchecked((uint)spellId), out var spell) ? spell : null;

        /// <summary>
        /// "Smoky Quartz", or null when the DAT doesn't name the material
        /// </summary>
        public string MaterialName(int materialType) => materials.TryGetValue(unchecked((uint)materialType), out var name) ? name : null;

        /// <summary>
        /// Reads every clothing table once, past the DAT's file cache, so the ~1,900 tables aren't kept in memory
        /// </summary>
        private HashSet<(uint, int)> ReadClothingIcons()
        {
            var icons = new HashSet<(uint, int)>();

            foreach (var id in portal.AllFiles.Keys.Where(id => id >> 24 == 0x10))
            {
                var table = new ClothingTable();

                using (var reader = new BinaryReader(new MemoryStream(portal.GetReaderForFile(id).Buffer)))
                    table.Unpack(reader);

                foreach (var effect in table.ClothingSubPalEffects)
                    icons.Add((effect.Value.Icon, (int)effect.Key));
            }

            return icons;
        }

        /// <summary>
        /// A 32×32 texture's pixels as straight RGBA (its default palette applied when it has one), or null when the id isn't a 32×32 texture in the DAT
        /// </summary>
        public byte[] IconPixels(uint textureId)
        {
            // 0x06 files are textures; checking the index first keeps unknown ids out of the DAT's file cache
            if (textureId >> 24 != 0x06 || !portal.AllFiles.ContainsKey(textureId))
                return null;

            var texture = portal.ReadFromDat<Texture>(textureId);

            if (texture.Width != IconSize || texture.Height != IconSize || texture.Format == SurfacePixelFormat.PFID_CUSTOM_RAW_JPEG)
                return null;

            var palette = texture.DefaultPaletteId is uint paletteId ? portal.ReadFromDat<Palette>(paletteId) : null;

            return texture.GetPixels(palette);
        }

        /// <summary>
        /// The icon a clothing table gives a palette template (as ClothingTable.GetIcon, which the game uses when it sets an item's palette), or 0 when it has none
        /// </summary>
        public uint ClothingIcon(uint clothingBase, int paletteTemplate)
        {
            if (clothingBase >> 24 != 0x10 || paletteTemplate < 0 || !portal.AllFiles.ContainsKey(clothingBase))
                return 0;

            return portal.ReadFromDat<ClothingTable>(clothingBase).GetIcon((uint)paletteTemplate);
        }

        /// <summary>
        /// Whether some clothing table gives this icon for this palette template
        /// </summary>
        public bool IsClothingIcon(uint icon, int paletteTemplate) => clothingIcons.Value.Contains((icon, paletteTemplate));
    }
}
