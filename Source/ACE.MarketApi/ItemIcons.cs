using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

using ACE.Database.Models.Shard.Market;

namespace ACE.MarketApi
{
    /// <summary>
    /// An item's icon as the client draws it: the underlay, then the icon with its overlay and its UI-effect outline (one composed PNG), then the
    /// secondary overlay. No item-type plate. Each layer is a PNG served by IconEndpoints.
    /// </summary>
    public static class ItemIcons
    {
        public sealed class IconView
        {
            /// <summary>
            /// Bottom to top
            /// </summary>
            public IReadOnlyList<IconLayer> Layers { get; set; }
        }

        public sealed class IconLayer
        {
            /// <summary>
            /// underlay, base or overlaySecondary
            /// </summary>
            public string Kind { get; set; }

            /// <summary>
            /// The texture id in the portal DAT (0x06…); on the base layer, the icon before its overlay
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

            void Add(string kind, uint texture, int? paletteTemplate, string file) =>
                layers.Add(new IconLayer { Kind = kind, Id = texture, PaletteTemplate = paletteTemplate, Url = MarketApi.PathBase + "/icons/" + file });

            if (item.IconUnderlay is uint underlay && underlay != 0)
                Add("underlay", underlay, null, FileName(underlay, null));

            // as the game does when it sets an item's palette, and as the client does when it draws one: the icon comes from the clothing table
            var clothingIcon = item.PaletteTemplate is int template && item.ClothingBase is uint clothingBase ? gameData.ClothingIcon(clothingBase, template) : 0;
            var baseIcon = clothingIcon != 0 ? clothingIcon : item.Icon ?? 0;
            var baseTemplate = clothingIcon != 0 ? item.PaletteTemplate : null;
            var overlay = item.IconOverlay ?? 0;
            var uiEffects = unchecked((uint)(item.UiEffects ?? 0));
            if (baseIcon != 0)
                Add("base", baseIcon, baseTemplate, CompositeFileName(baseIcon, baseTemplate, overlay, uiEffects));

            if (item.IconOverlaySecondary is uint secondary && secondary != 0)
                Add("overlaySecondary", secondary, null, FileName(secondary, null));

            return new IconView { Layers = layers };
        }

        /// <summary>
        /// "0x06003237.png", or "0x06003237_p19.png" for palette template 19 (the reference site's names)
        /// </summary>
        public static string FileName(uint id, int? paletteTemplate) => $"0x{id:X8}{Palette(paletteTemplate)}.png";

        /// <summary>
        /// The base icon with its overlay and UI effects composed in: "0x06003237_p19_o060026D5_e00000001.png". Made on request, not kept on disk.
        /// </summary>
        public static string CompositeFileName(uint id, int? paletteTemplate, uint overlay, uint uiEffects) =>
            $"0x{id:X8}{Palette(paletteTemplate)}_o{overlay:X8}_e{uiEffects:X8}.png";

        private static string Palette(int? paletteTemplate) =>
            paletteTemplate is int template ? "_p" + template.ToString("D2", CultureInfo.InvariantCulture) : "";
    }
}
