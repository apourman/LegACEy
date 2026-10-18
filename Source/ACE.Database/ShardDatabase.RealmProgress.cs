using System;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Realm progress: one value per character, realm and key. Generic: the game decides what the keys mean.
    /// </summary>
    public partial class ShardDatabase
    {
        /// <summary>The value saved for the character's key in the realm, or null if there is none.</summary>
        public long? GetRealmProgress(ulong characterId, ushort realmId, string key)
        {
            using (var context = new ShardDbContext())
                return context.CharacterRealmProgress
                    .AsNoTracking()
                    .Where(p => p.CharacterId == characterId && p.RealmId == realmId && p.Key == key)
                    .Select(p => (long?)p.Value)
                    .FirstOrDefault();
        }

        /// <summary>Saves the value for the character's key in the realm, adding or replacing its row.</summary>
        public void SetRealmProgress(ulong characterId, ushort realmId, string key, long value)
        {
            using (var context = new ShardDbContext())
            {
                var row = context.CharacterRealmProgress.Find(characterId, realmId, key);

                if (row == null)
                {
                    context.CharacterRealmProgress.Add(new CharacterRealmProgress
                    {
                        CharacterId = characterId,
                        RealmId = realmId,
                        Key = key,
                        Value = value,
                        LastModified = DateTime.UtcNow,
                    });
                }
                else
                {
                    row.Value = value;
                    row.LastModified = DateTime.UtcNow;
                }

                context.SaveChanges();
            }
        }
    }
}
