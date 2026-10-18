using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    /// <summary>
    /// One value a character has in one realm, under a key the game chooses. Generic: no game rules live here.
    /// Mapped by attributes, so re-scaffolding ShardDbContext leaves it alone. The schema is
    /// Database/Updates/Shard/2026-10-17-00-Character-Realm-Progress.sql.
    /// </summary>
    [Table("character_realm_progress")]
    [PrimaryKey(nameof(CharacterId), nameof(RealmId), nameof(Key))]
    public class CharacterRealmProgress
    {
        [Column("character_Id")]
        public ulong CharacterId { get; set; }

        [Column("realm_Id")]
        public ushort RealmId { get; set; }

        [Column("key")]
        [MaxLength(64)]
        public string Key { get; set; }

        [Column("value")]
        public long Value { get; set; }

        [Column("last_Modified")]
        public DateTime LastModified { get; set; }
    }
}
