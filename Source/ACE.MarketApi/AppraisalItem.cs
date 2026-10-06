using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum.Properties;

namespace ACE.MarketApi
{
    /// <summary>
    /// An escrowed item's stored properties (its biota rows), read for formatting. Read only: the API never writes item tables.
    /// </summary>
    public sealed class AppraisalItem
    {
        private readonly Dictionary<ushort, int> ints = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, double> floats = new Dictionary<ushort, double>();
        private readonly Dictionary<ushort, string> strings = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, bool> bools = new Dictionary<ushort, bool>();
        private readonly List<int> spells = new List<int>();

        public GameData GameData { get; }

        private AppraisalItem(GameData gameData)
        {
            GameData = gameData;
        }

        public int? Int(PropertyInt key) => ints.TryGetValue((ushort)key, out var value) ? value : null;

        public double? Float(PropertyFloat key) => floats.TryGetValue((ushort)key, out var value) ? value : null;

        public string String(PropertyString key) => strings.TryGetValue((ushort)key, out var value) ? value : null;

        public bool? Bool(PropertyBool key) => bools.TryGetValue((ushort)key, out var value) ? value : null;

        /// <summary>
        /// The spell book's spell ids, lowest first (the table keeps no order of its own)
        /// </summary>
        public IReadOnlyList<int> Spells => spells;

        /// <summary>
        /// The stored properties of each item, one query per property table. Items with no rows get an empty entry.
        /// </summary>
        public static Dictionary<uint, AppraisalItem> Load(ShardDbContext shard, GameData gameData, IReadOnlyCollection<uint> itemGuids)
        {
            var items = itemGuids.Distinct().ToDictionary(g => g, _ => new AppraisalItem(gameData));

            if (items.Count == 0)
                return items;

            var ids = items.Keys.ToList();

            foreach (var row in shard.BiotaPropertiesInt.AsNoTracking().Where(r => ids.Contains(r.ObjectId)))
                items[row.ObjectId].ints[row.Type] = row.Value;

            foreach (var row in shard.BiotaPropertiesFloat.AsNoTracking().Where(r => ids.Contains(r.ObjectId)))
                items[row.ObjectId].floats[row.Type] = row.Value;

            foreach (var row in shard.BiotaPropertiesString.AsNoTracking().Where(r => ids.Contains(r.ObjectId)))
                items[row.ObjectId].strings[row.Type] = row.Value;

            foreach (var row in shard.BiotaPropertiesBool.AsNoTracking().Where(r => ids.Contains(r.ObjectId)))
                items[row.ObjectId].bools[row.Type] = row.Value;

            foreach (var row in shard.BiotaPropertiesSpellBook.AsNoTracking().Where(r => ids.Contains(r.ObjectId)).OrderBy(r => r.Spell))
                items[row.ObjectId].spells.Add(row.Spell);

            return items;
        }
    }
}
