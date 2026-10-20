using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;

namespace ACE.Database.Market
{
    /// <summary>
    /// The names in the game's DAT that appraisal lines show. The Market API and the game server both read them from client_portal.dat.
    /// </summary>
    public static class DatNames
    {
        /// <summary>The DAT's material enum-to-name table, as RecipeManager.GetMaterialName reads it</summary>
        public const uint MaterialTable = 0x27000000;

        /// <summary>The materials by their enum value, named as the client shows them: "Smoky_Quartz" → "Smoky Quartz"</summary>
        public static Dictionary<uint, string> Materials(IEnumerable<KeyValuePair<uint, string>> clientEnumToName) =>
            clientEnumToName.ToDictionary(material => material.Key, material => material.Value.Replace("_", " "));
    }


    /// <summary>
    /// The game's names an appraisal needs: spells and materials. The Market API reads them from client_portal.dat (GameData); the game server reads its own DAT.
    /// </summary>
    public interface IAppraisalNames
    {
        /// <summary>The spell, or null when the spell table doesn't have it</summary>
        AppraisalSpell FindSpell(int spellId);

        /// <summary>"Smoky Quartz", or null when the DAT doesn't name the material</summary>
        string MaterialName(int materialType);
    }

    /// <param name="Name">the spell's name</param>
    /// <param name="Cantrip">a cantrip, which the client leaves out of item descriptions</param>
    public sealed record AppraisalSpell(string Name, bool Cantrip)
    {
        private static readonly string[] cantripTiers = { "Minor ", "Moderate ", "Major ", "Epic ", "Legendary " };

        /// <summary>
        /// A spell from the spell table. A cantrip is an item spell the client leaves out of item descriptions (every Minor to Legendary one but a few, and the
        /// Feeble ones), or one named by a cantrip tier (the few: the wards and Hermetic Links).
        /// </summary>
        public static AppraisalSpell From(string name, SpellFlags flags) =>
            new(name, flags.HasFlag(SpellFlags.ExcludedFromItemDescriptions) || cantripTiers.Any(tier => name.StartsWith(tier, StringComparison.Ordinal)));
    }
}
