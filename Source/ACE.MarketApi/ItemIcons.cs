using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

using ACE.Database.Models.Shard.Market;
using ACE.Entity.Enum;

namespace ACE.MarketApi
{
    /// <summary>
    /// An item's icon as the client draws it: up to five 32×32 layers stacked in order, plate (by item type), underlay, base icon, overlay and
    /// secondary overlay, plus a glow class for magical items. Each layer is a PNG served by IconEndpoints.
    /// </summary>
    public static class ItemIcons
    {
        private const uint WeaponPlate = 0x060011D2;
        private const uint ArmorPlate = 0x060011CF;
        private const uint ClothingPlate = 0x060011F3;
        private const uint JewelryPlate = 0x060011D5;
        private const uint GemPlate = 0x060011D3;
        private const uint OtherPlate = 0x060011D4;

        public sealed class IconView
        {
            /// <summary>
            /// Bottom to top
            /// </summary>
            public IReadOnlyList<IconLayer> Layers { get; set; }

            /// <summary>
            /// CSS classes (IconEndpoints' glow.css), one per UI effect; null for none
            /// </summary>
            public string Glow { get; set; }
        }

        public sealed class IconLayer
        {
            /// <summary>
            /// plate, underlay, base, overlay or overlaySecondary
            /// </summary>
            public string Kind { get; set; }

            /// <summary>
            /// The texture id in the portal DAT (0x06…)
            /// </summary>
            public uint Id { get; set; }

            /// <summary>
            /// On a base icon chosen from the item's clothing table by its palette template
            /// </summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? PaletteTemplate { get; set; }

            public string Url { get; set; }
        }

        public static IconView For(VaultItem item, GameData gameData)
        {
            var layers = new List<IconLayer>();

            void Add(string kind, uint? id, int? paletteTemplate = null)
            {
                if (id is uint texture && texture != 0)
                    layers.Add(new IconLayer { Kind = kind, Id = texture, PaletteTemplate = paletteTemplate, Url = "/icons/" + FileName(texture, paletteTemplate) });
            }

            Add("plate", Plate(item.ItemType));
            Add("underlay", item.IconUnderlay);

            // as the game does when it sets an item's palette, and as the client does when it draws one: the icon comes from the clothing table
            var clothingIcon = item.PaletteTemplate is int template && item.ClothingBase is uint clothingBase ? gameData.ClothingIcon(clothingBase, template) : 0;
            if (clothingIcon != 0)
                Add("base", clothingIcon, item.PaletteTemplate);
            else
                Add("base", item.Icon);

            Add("overlay", item.IconOverlay);
            Add("overlaySecondary", item.IconOverlaySecondary);

            return new IconView { Layers = layers, Glow = GlowClasses(item.UiEffects) };
        }

        /// <summary>
        /// "0x06003237.png", or "0x06003237_p19.png" for palette template 19 (the reference site's names)
        /// </summary>
        public static string FileName(uint id, int? paletteTemplate) =>
            $"0x{id:X8}" + (paletteTemplate is int template ? "_p" + template.ToString("D2", CultureInfo.InvariantCulture) : "") + ".png";

        /// <summary>
        /// The plate the client draws under an icon, by item type
        /// </summary>
        private static uint Plate(int itemType)
        {
            var type = (ItemType)unchecked((uint)itemType);

            if ((type & ItemType.WeaponOrCaster) != 0)
                return WeaponPlate;
            if ((type & ItemType.Armor) != 0)
                return ArmorPlate;
            if ((type & ItemType.Clothing) != 0)
                return ClothingPlate;
            if ((type & ItemType.Jewelry) != 0)
                return JewelryPlate;
            if ((type & ItemType.Gem) != 0)
                return GemPlate;

            return OtherPlate;
        }

        // ---- glow

        /// <summary>
        /// The glow color for each UI effect. There is no glow texture in the game data. Magical is the reference site's placeholder blue;
        /// the others are picked to read as their element or vital.
        /// </summary>
        private static readonly (UiEffects Effect, string Color)[] glows =
        {
            (UiEffects.Magical, "rgba(127, 182, 255, 0.75)"),
            (UiEffects.Poisoned, "rgba(96, 200, 72, 0.75)"),
            (UiEffects.BoostHealth, "rgba(230, 64, 64, 0.75)"),
            (UiEffects.BoostMana, "rgba(64, 112, 255, 0.75)"),
            (UiEffects.BoostStamina, "rgba(240, 200, 64, 0.75)"),
            (UiEffects.Fire, "rgba(255, 120, 32, 0.75)"),
            (UiEffects.Lightning, "rgba(200, 120, 255, 0.75)"),
            (UiEffects.Frost, "rgba(160, 230, 255, 0.75)"),
            (UiEffects.Acid, "rgba(150, 230, 40, 0.75)"),
            (UiEffects.Bludgeoning, "rgba(200, 180, 140, 0.75)"),
            (UiEffects.Slashing, "rgba(220, 220, 230, 0.75)"),
            (UiEffects.Piercing, "rgba(190, 200, 215, 0.75)"),
            (UiEffects.Nether, "rgba(150, 60, 200, 0.75)"),
        };

        /// <summary>
        /// "icon-glow--magical", one class per UI effect the item has (in the game every item has one at most); null for none
        /// </summary>
        public static string GlowClasses(int? uiEffects)
        {
            var effects = (UiEffects)unchecked((uint)(uiEffects ?? 0));

            var classes = glows.Where(g => effects.HasFlag(g.Effect)).Select(g => GlowClass(g.Effect)).ToList();

            return classes.Count > 0 ? string.Join(" ", classes) : null;
        }

        private static string GlowClass(UiEffects effect)
        {
            // BoostHealth → boost-health
            var name = new StringBuilder();
            foreach (var c in effect.ToString())
            {
                if (char.IsUpper(c) && name.Length > 0)
                    name.Append('-');
                name.Append(char.ToLowerInvariant(c));
            }

            return "icon-glow--" + name;
        }

        /// <summary>
        /// The stylesheet for the glow classes: a box-shadow in the effect's color, as the reference site draws it
        /// </summary>
        public static readonly string GlowStylesheet = BuildGlowStylesheet();

        private static string BuildGlowStylesheet()
        {
            var css = new StringBuilder();

            css.AppendLine("/* Item icon glows, one class per UI effect. Draw the icon layers inside an element with these classes. */");
            css.AppendLine(string.Join(",\n", glows.Select(g => "." + GlowClass(g.Effect))) + " {");
            css.AppendLine("    box-shadow: 0 0 6px 1px var(--icon-glow);");
            css.AppendLine("}");

            foreach (var (effect, color) in glows)
                css.AppendLine($".{GlowClass(effect)} {{ --icon-glow: {color}; }}");

            return css.ToString();
        }
    }
}
