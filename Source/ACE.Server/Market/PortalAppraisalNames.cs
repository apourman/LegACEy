using System;
using System.Collections.Generic;

using ACE.Database.Market;
using ACE.DatLoader;
using ACE.DatLoader.FileTypes;
using ACE.Entity.Enum;

namespace ACE.Server.Market
{
    /// <summary>
    /// The spell and material names an appraisal line needs, read from the portal DAT the game server already loads. The Market API reads the same
    /// names from its own copy of the DAT (GameData), with the same rules (DatNames, AppraisalSpell), so a listing's lines are the same from either front door.
    /// </summary>
    public sealed class PortalAppraisalNames : IAppraisalNames
    {
        public static readonly PortalAppraisalNames Instance = new PortalAppraisalNames();

        private static readonly Lazy<IReadOnlyDictionary<uint, string>> materials = new Lazy<IReadOnlyDictionary<uint, string>>(() =>
            DatNames.Materials(DatManager.PortalDat.ReadFromDat<DualDidMapper>(DatNames.MaterialTable).ClientEnumToName));

        private PortalAppraisalNames()
        {
        }

        public AppraisalSpell FindSpell(int spellId) =>
            DatManager.PortalDat.SpellTable.Spells.TryGetValue(unchecked((uint)spellId), out var spell) ? AppraisalSpell.From(spell.Name, (SpellFlags)spell.Bitfield) : null;

        public string MaterialName(int materialType) => materials.Value.TryGetValue(unchecked((uint)materialType), out var name) ? name : null;
    }
}
