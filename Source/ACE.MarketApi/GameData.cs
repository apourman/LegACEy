using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;

namespace ACE.MarketApi
{
    /// <summary>
    /// What the appraisal needs from the game's own data files: spell names (and which spells are cantrips) and material names.
    /// Read once from client_portal.dat, the same file the game server uses.
    /// </summary>
    public sealed class GameData
    {
        public const string PortalDatFile = "client_portal.dat";

        /// <summary>
        /// The DAT's material enum-to-name table, as RecipeManager.GetMaterialName reads it
        /// </summary>
        private const uint MaterialNamesId = 0x27000000;

        public sealed record Spell(string Name, bool Cantrip);

        private static readonly ConcurrentDictionary<string, GameData> loaded = new ConcurrentDictionary<string, GameData>();

        private readonly IReadOnlyDictionary<uint, Spell> spells;

        private readonly IReadOnlyDictionary<uint, string> materials;

        private GameData(IReadOnlyDictionary<uint, Spell> spells, IReadOnlyDictionary<uint, string> materials)
        {
            this.spells = spells;
            this.materials = materials;
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

            // cantrips are the item spells the client leaves out of item descriptions
            var spells = portal.SpellTable.Spells.ToDictionary(s => s.Key, s => new Spell(s.Value.Name, ((SpellFlags)s.Value.Bitfield).HasFlag(SpellFlags.ExcludedFromItemDescriptions)));

            var materials = portal.ReadFromDat<DualDidMapper>(MaterialNamesId).ClientEnumToName.ToDictionary(m => m.Key, m => m.Value.Replace("_", " "));

            return new GameData(spells, materials);
        }

        /// <summary>
        /// The spell, or null when the spell table doesn't have it
        /// </summary>
        public Spell FindSpell(int spellId) => spells.TryGetValue(unchecked((uint)spellId), out var spell) ? spell : null;

        /// <summary>
        /// "Smoky Quartz", or null when the DAT doesn't name the material
        /// </summary>
        public string MaterialName(int materialType) => materials.TryGetValue(unchecked((uint)materialType), out var name) ? name : null;
    }
}
