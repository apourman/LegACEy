using Microsoft.EntityFrameworkCore;

namespace ACE.Database.Models.Shard
{
    /// <summary>
    /// The realm progress table. Kept out of the generated ShardDbContext.cs so that re-scaffolding leaves it alone;
    /// its mapping is the attributes on CharacterRealmProgress.
    /// </summary>
    public partial class ShardDbContext
    {
        public virtual DbSet<CharacterRealmProgress> CharacterRealmProgress { get; set; }
    }
}
