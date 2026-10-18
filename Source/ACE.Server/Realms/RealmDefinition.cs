using System.Collections.Generic;

using ACE.Entity;

namespace ACE.Server.Realms
{
    public enum RealmKind
    {
        World,
        EphemeralTemplate
    }

    /// <summary>
    /// A realm read from Content/realms/*.json. Realm 0 is the base world and has no file.
    /// </summary>
    public sealed class RealmDefinition
    {
        /// <summary>The base world: realm 0, with its rules at their defaults</summary>
        public static readonly RealmDefinition BaseWorld = new RealmDefinition
        {
            Id = 0,
            Name = "base world",
            Kind = RealmKind.World,
            Rules = new Dictionary<string, bool>(),
        };

        public ushort Id { get; init; }
        public string Name { get; init; }
        public RealmKind Kind { get; init; }

        /// <summary>Only the rules the file sets. Anything else is the registry default.</summary>
        public IReadOnlyDictionary<string, bool> Rules { get; init; }

        // Ephemeral templates only. The landblocks a copy is made of, where the admin enters (Entry, in one of them),
        // how long a copy lives, and where its players go when it is destroyed (Exit, a base-world position).
        public IReadOnlyList<ushort> Landblocks { get; init; }
        public int? TtlMinutes { get; init; }
        public Position Entry { get; init; }
        public Position Exit { get; init; }
    }
}
