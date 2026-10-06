using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.MarketApi
{
    /// <summary>
    /// One appraisal line: its label (null for an unlabelled flag) and its value from the stored item (null for no line)
    /// </summary>
    public sealed record AppraisalRule(string Label, Func<AppraisalItem, string> Value);

    /// <summary>
    /// The rule table behind the listing page's appraisal lines, in the reference market's style (not retail wording):
    /// plain "Label: value" lines, no trailing full stops, no thousands separators, signed percentages, and neutral modifiers left out.
    /// Lines are formatted when a listing is viewed, from the stored item, so a changed rule applies to items already listed.
    /// </summary>
    public sealed class AppraisalRules
    {
        public IReadOnlyList<AppraisalRule> Rules { get; }

        public AppraisalRules(IEnumerable<AppraisalRule> rules)
        {
            Rules = rules.ToList();
        }

        /// <summary>
        /// The spec's order: general, tinkering, weapons, casters, armor, magic, then the unlabelled flags
        /// </summary>
        public static readonly AppraisalRules Default = new AppraisalRules(new[]
        {
            // general
            new AppraisalRule("Value", i => Number(i.Int(PropertyInt.Value))),
            new AppraisalRule("Burden", i => Number(i.Int(PropertyInt.EncumbranceVal))),
            new AppraisalRule("Material", i => MaterialName(i, i.Int(PropertyInt.MaterialType))),
            new AppraisalRule("Workmanship", i => Number(i.Int(PropertyInt.ItemWorkmanship))),
            new AppraisalRule("Set", i => EnumName<EquipmentSet>(i.Int(PropertyInt.EquipmentSetId))),

            // tinkering
            new AppraisalRule("Number of Times Tinkered", i => i.Int(PropertyInt.NumTimesTinkered) is int times && times != 0 ? Number(times) : null),
            new AppraisalRule("Imbued Effect", ImbuedEffects),
            new AppraisalRule("Imbuer", i => i.String(PropertyString.ImbuerName)),

            // weapons
            new AppraisalRule("Damage Type", i => DamageTypes(i.Int(PropertyInt.DamageType))),
            new AppraisalRule("Damage", i => DamageRange(i, " - ")),
            new AppraisalRule("Damage Modifier", i => NonNeutral(Multiplier(i.Float(PropertyFloat.DamageMod)))),
            new AppraisalRule("Elemental Damage Bonus", i => NonNeutral(Signed(i.Int(PropertyInt.ElementalDamageBonus)))),
            new AppraisalRule("Attack Bonus", i => Multiplier(i.Float(PropertyFloat.WeaponOffense))),
            new AppraisalRule("Speed", i => Number(i.Int(PropertyInt.WeaponTime))),
            new AppraisalRule("Ammunition Type", i => EnumName<AmmoType>(i.Int(PropertyInt.AmmoType))),
            new AppraisalRule("Missile Velocity", i => i.Float(PropertyFloat.MaximumVelocity)?.ToString("0.0", CultureInfo.InvariantCulture)),
            new AppraisalRule("Melee Defense Bonus", i => NonNeutral(Multiplier(i.Float(PropertyFloat.WeaponDefense)))),
            new AppraisalRule("Missile Defense Bonus", i => NonNeutral(Multiplier(i.Float(PropertyFloat.WeaponMissileDefense)))),
            new AppraisalRule("Skill", i => i.Int(PropertyInt.WeaponSkill) is int skill && skill != 0 ? ((Skill)skill).ToSentence() : null),
            new AppraisalRule("Skill Required", i => SkillRequired(i.Int(PropertyInt.WieldRequirements), i.Int(PropertyInt.WieldSkillType), i.Int(PropertyInt.WieldDifficulty))),
            new AppraisalRule("Skill Required", i => SkillRequired(i.Int(PropertyInt.WieldRequirements2), i.Int(PropertyInt.WieldSkillType2), i.Int(PropertyInt.WieldDifficulty2))),

            // casters
            new AppraisalRule("Elemental Damage Modifier", i => NonNeutral(Multiplier(i.Float(PropertyFloat.ElementalDamageMod)))),
            new AppraisalRule("Mana Conversion Bonus", i => NonNeutral(Percent(i.Float(PropertyFloat.ManaConversionMod)))),
            new AppraisalRule("Mana Cost", i => Number(i.Int(PropertyInt.ItemManaCost))),

            // armor: the protections are shown even when neutral, as the reference does
            new AppraisalRule("Armor Level", i => Number(i.Int(PropertyInt.ArmorLevel))),
            new AppraisalRule("Slashing Protection", i => Protection(i.Float(PropertyFloat.ArmorModVsSlash))),
            new AppraisalRule("Piercing Protection", i => Protection(i.Float(PropertyFloat.ArmorModVsPierce))),
            new AppraisalRule("Bludgeoning Protection", i => Protection(i.Float(PropertyFloat.ArmorModVsBludgeon))),
            new AppraisalRule("Fire Protection", i => Protection(i.Float(PropertyFloat.ArmorModVsFire))),
            new AppraisalRule("Cold Protection", i => Protection(i.Float(PropertyFloat.ArmorModVsCold))),
            new AppraisalRule("Acid Protection", i => Protection(i.Float(PropertyFloat.ArmorModVsAcid))),
            new AppraisalRule("Lightning Protection", i => Protection(i.Float(PropertyFloat.ArmorModVsElectric))),
            new AppraisalRule("Nether Protection", i => Protection(i.Float(PropertyFloat.ArmorModVsNether))),

            // magic
            new AppraisalRule("Difficulty", i => Number(i.Int(PropertyInt.ItemDifficulty))),
            new AppraisalRule("Spellcraft", i => Number(i.Int(PropertyInt.ItemSpellcraft))),
            new AppraisalRule("Mana", i => i.Int(PropertyInt.ItemMaxMana) is int max ? $"{Number(i.Int(PropertyInt.ItemCurMana) ?? 0)} / {Number(max)}" : null),
            new AppraisalRule("Mana Rate", i => i.Float(PropertyFloat.ManaRate) is double rate && rate < 0 ? $"1 point every {Number((int)Math.Round(-1 / rate))} seconds" : null),
            new AppraisalRule("Gems", i => i.Int(PropertyInt.GemCount) is int count && count > 0 && MaterialName(i, i.Int(PropertyInt.GemType)) is string gem ? $"{Number(count)} {gem}" : null),

            // unlabelled flags
            new AppraisalRule(null, i => i.Bool(PropertyBool.Dyable) == true ? "Dyable" : null),
            new AppraisalRule(null, i => i.Int(PropertyInt.Bonded) == (int)BondedStatus.Bonded ? "Bonded" : null),
            new AppraisalRule(null, i => i.Int(PropertyInt.Attuned) == (int)AttunedStatus.Attuned ? "Attuned" : null),
            new AppraisalRule(null, i => i.Bool(PropertyBool.IsSellable) == false ? "Cannot be sold to a vendor" : null),
        });

        /// <summary>
        /// The item's lines, in rule order
        /// </summary>
        public IReadOnlyList<string> Format(AppraisalItem item)
        {
            var lines = new List<string>();

            foreach (var rule in Rules)
            {
                if (rule.Value(item) is string value)
                    lines.Add(rule.Label == null ? value : $"{rule.Label}: {value}");
            }

            return lines;
        }

        /// <summary>
        /// The browse row's one-line summary: for weapons and casters the damage range, damage modifier, elemental bonus and
        /// elemental damage modifier that aren't neutral, then the damage types ("+165% +18 (Bludgeoning)", "34-52 (Slashing)", "+16% (Nether)");
        /// "AL 282" for armor; null for anything else
        /// </summary>
        public static string Summary(AppraisalItem item)
        {
            var parts = new[]
            {
                DamageRange(item, "-"),
                NonNeutral(Multiplier(item.Float(PropertyFloat.DamageMod))),
                NonNeutral(Signed(item.Int(PropertyInt.ElementalDamageBonus))),
                NonNeutral(Multiplier(item.Float(PropertyFloat.ElementalDamageMod))),
                DamageTypes(item.Int(PropertyInt.DamageType)) is string types ? $"({types})" : null,
            }.Where(p => p != null).ToList();

            if (parts.Count > 0)
                return string.Join(" ", parts);

            return item.Int(PropertyInt.ArmorLevel) is int armorLevel ? $"AL {Number(armorLevel)}" : null;
        }

        /// <summary>
        /// The item's spells by name from the spell table, cantrips first. A spell the table doesn't have is shown by its id.
        /// </summary>
        public static IReadOnlyList<GameData.Spell> Spells(AppraisalItem item)
        {
            return item.Spells
                .Select(id => item.GameData.FindSpell(id) ?? new GameData.Spell($"Unknown spell {Number(id)}", false))
                .OrderBy(spell => spell.Cantrip ? 0 : 1)
                .ToList();
        }

        /// <summary>
        /// The game's name for a material ("Smoky Quartz"), or null when there's no material
        /// </summary>
        public static string MaterialName(AppraisalItem item, int? materialType)
        {
            if (materialType is not int value || value == 0)
                return null;

            return item.GameData.MaterialName(value) ?? (Enum.IsDefined((MaterialType)value) ? EnumName((MaterialType)value) : Number(value));
        }

        // ---- value formats

        /// <summary>
        /// Marks a signed value that means "no change" ("+0", "+0%"), so it produces no line
        /// </summary>
        private static string NonNeutral(string signed) => signed == "+0" || signed == "+0%" ? null : signed;

        private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

        private static string Signed(int? value) => value is int v ? (v < 0 ? "" : "+") + Number(v) : null;

        /// <summary>
        /// 1.18 → "+18%"
        /// </summary>
        private static string Multiplier(double? mod) => mod is double m ? Percent(m - 1) : null;

        /// <summary>
        /// 0.09 → "+9%"
        /// </summary>
        private static string Percent(double? fraction) => fraction is double f ? Signed((int)Math.Round(f * 100, MidpointRounding.AwayFromZero)) + "%" : null;

        private static string Protection(double? mod) => mod?.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>
        /// "34 - 52": the maximum and the maximum less its variance
        /// </summary>
        private static string DamageRange(AppraisalItem item, string separator)
        {
            if (item.Int(PropertyInt.Damage) is not int max)
                return null;

            var min = (int)Math.Round(max * (1 - (item.Float(PropertyFloat.DamageVariance) ?? 0)), MidpointRounding.AwayFromZero);

            return Number(min) + separator + Number(max);
        }

        private static readonly (DamageType Type, string Name)[] damageTypeNames =
        {
            (DamageType.Slash, "Slashing"),
            (DamageType.Pierce, "Piercing"),
            (DamageType.Bludgeon, "Bludgeoning"),
            (DamageType.Cold, "Cold"),
            (DamageType.Fire, "Fire"),
            (DamageType.Acid, "Acid"),
            (DamageType.Electric, "Lightning"),
            (DamageType.Nether, "Nether"),
        };

        /// <summary>
        /// "Slashing, Piercing"
        /// </summary>
        private static string DamageTypes(int? damageType)
        {
            if (damageType is not int value)
                return null;

            var names = damageTypeNames.Where(d => ((DamageType)value).HasFlag(d.Type)).Select(d => d.Name).ToList();

            return names.Count > 0 ? string.Join(", ", names) : null;
        }

        /// <summary>
        /// "Bludgeon Rending", from all five imbue properties
        /// </summary>
        private static string ImbuedEffects(AppraisalItem item)
        {
            var effects = new[] { PropertyInt.ImbuedEffect, PropertyInt.ImbuedEffect2, PropertyInt.ImbuedEffect3, PropertyInt.ImbuedEffect4, PropertyInt.ImbuedEffect5 }
                .Aggregate(0u, (all, key) => all | unchecked((uint)(item.Int(key) ?? 0)));

            var names = Enum.GetValues<ImbuedEffectType>()
                .Where(e => e != 0 && ((uint)e & ((uint)e - 1)) == 0 && (effects & (uint)e) != 0)
                .Select(e => EnumName(e))
                .ToList();

            return names.Count > 0 ? string.Join(", ", names) : null;
        }

        /// <summary>
        /// "Missile Weapons 390" for a skill or raw skill requirement
        /// </summary>
        private static string SkillRequired(int? requirement, int? skill, int? difficulty)
        {
            if (requirement is not int r || ((WieldRequirement)r != WieldRequirement.Skill && (WieldRequirement)r != WieldRequirement.RawSkill))
                return null;

            if (skill is not int s || difficulty is not int d)
                return null;

            return SkillText(s, d);
        }

        /// <summary>
        /// "Missile Weapons 390"
        /// </summary>
        public static string SkillText(int skill, int difficulty) => $"{((Skill)skill).ToSentence()} {Number(difficulty)}";

        /// <summary>
        /// The stored value's enum name split into words, or null when it's missing or 0
        /// </summary>
        private static string EnumName<T>(int? value) where T : struct, Enum => value is int v && v != 0 ? EnumName((T)Enum.ToObject(typeof(T), v)) : null;

        /// <summary>
        /// An enum's name split into words ("BludgeonRending" → "Bludgeon Rending"), or its number when it has no name
        /// </summary>
        private static string EnumName<T>(T value) where T : struct, Enum
        {
            return Enum.IsDefined(value) ? SplitWords(value.ToString()) : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        private static string SplitWords(string name) => Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
    }
}
