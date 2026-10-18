using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Realms
{
    /// <summary>
    /// One rule a realm file can set. Every rule is a bool: a realm file must write true or false for it.
    /// Adding a rule means adding an entry in RealmRules and the check that reads it.
    /// </summary>
    public sealed record RealmRule(string Key, bool Default);

    public static class RealmRules
    {
        /// <summary>PK decision points treat every player as PK, and PK status is set when they enter</summary>
        public static readonly RealmRule Pvp = new("pvp", false);

        /// <summary>A character may make this realm their home; @sethomerealm warns when it is false</summary>
        public static readonly RealmRule CanBeHomeworld = new("canBeHomeworld", false);

        public static readonly IReadOnlyList<RealmRule> All = new[] { Pvp, CanBeHomeworld };

        public static RealmRule Find(string key) => All.FirstOrDefault(rule => rule.Key == key);
    }
}
