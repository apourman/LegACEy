using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using LegACEy.GameArt;
using SkiaSharp;

namespace ACE.MarketApi
{
    /// <summary>
    /// Icon PNGs by texture id and optional palette template ("/api/icons/0x06003237.png", "/api/icons/0x06003237_p19.png"), and the base
    /// icons composed with their overlay and UI-effect outline. No sign-in.
    /// </summary>
    public static class IconEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/icons/{file}", Icon).File(200, "image/png", 404);
        }

        private static IResult Icon(string file, IconStore icons, HttpResponse response)
        {
            var png = icons.Get(file);

            if (png == null)
                return MarketHttp.Error(StatusCodes.Status404NotFound, "not_found");

            // the DAT changes only with a server update
            response.Headers.CacheControl = "public, max-age=86400";

            return Results.Bytes(png, "image/png");
        }
    }

    /// <summary>
    /// Makes icon PNGs from the portal DAT on their first request and keeps them on disk under their file names: plain icons, and composites
    /// with no overlay (their names are bounded, since the effect is one a listing gives). A composite with an overlay is made on each request,
    /// since the overlay in its name isn't bounded.
    /// </summary>
    public sealed class IconStore
    {
        private static readonly Regex fileName = new Regex(@"^0x(?<id>[0-9A-Fa-f]{8})(?:_p(?<template>[0-9]{1,4}))?(?:_o(?<overlay>[0-9A-Fa-f]{8})_e(?<effects>[0-9A-Fa-f]{8}))?\.png\z", RegexOptions.CultureInvariant);

        private readonly GameData gameData;

        private readonly string directory;

        public IconStore(GameData gameData, string directory)
        {
            this.gameData = gameData;
            this.directory = Path.GetFullPath(directory);
        }

        /// <summary>
        /// The PNG, or null when the name isn't a 32×32 texture in the DAT, names a palette template that no clothing table gives that icon,
        /// or names an effect other than the canonical one (0, or one bit up to 0x800)
        /// </summary>
        public byte[] Get(string file)
        {
            var match = fileName.Match(file);
            if (!match.Success)
                return null;

            var id = ParseHex(match.Groups["id"].Value);
            int? template = match.Groups["template"].Success ? int.Parse(match.Groups["template"].Value, CultureInfo.InvariantCulture) : null;

            // only names a listing can give out, so requests can't fill the cache with variants
            if (template is int paletteTemplate && !gameData.IsClothingIcon(id, paletteTemplate))
                return null;

            if (!match.Groups["effects"].Success)
                return Cached(ItemIcons.FileName(id, template), () => Plain(id));

            var overlay = ParseHex(match.Groups["overlay"].Value);
            var uiEffects = ParseHex(match.Groups["effects"].Value);
            if (ItemIconOutline.LowestEffect(uiEffects) != uiEffects)
                return null;

            if (overlay != 0)
                return Composite(id, overlay, uiEffects);

            return Cached(ItemIcons.CompositeFileName(id, template, 0, uiEffects), () => Composite(id, 0, uiEffects));
        }

        private static uint ParseHex(string value) => uint.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        /// <summary>The PNG kept on disk under <paramref name="name"/>, made once by <paramref name="make"/>; null when it can't be made</summary>
        private byte[] Cached(string name, Func<byte[]> make)
        {
            var path = Path.Combine(directory, name);

            try
            {
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                // another request is moving the same icon into place (Windows locks it): make it again
            }

            var png = make();
            if (png == null)
                return null;

            // written aside and moved in, so a concurrent request never reads half a file
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, png);
                File.Move(temporary, path, overwrite: true);
            }
            catch (IOException)
            {
                // another request cached the same icon first; this one is identical
            }
            finally
            {
                File.Delete(temporary);
            }

            return png;
        }

        private byte[] Plain(uint id)
        {
            // The clothing table's icon for the palette is already drawn in that palette's colors: every icon a clothing table names is A8R8G8B8,
            // so there is no palette left to apply. A palette-indexed icon gets its own default palette.
            var pixels = gameData.IconPixels(id);
            return pixels == null ? null : EncodePng(pixels, GameData.IconSize, GameData.IconSize);
        }

        /// <summary>
        /// A base icon with its overlay and UI-effect outline composed in, as the client draws it (ItemIconOutline), premultiplied so a partly
        /// transparent overlay blends the same way. ponytail: an overlay's composite is read from the DAT on each request; keep it on disk too if
        /// reads show up in profiles.
        /// </summary>
        private byte[] Composite(uint id, uint overlay, uint uiEffects)
        {
            var icon = gameData.IconPixels(id);
            if (icon == null)
                return null;

            var overlayPixels = overlay == 0 ? null : gameData.IconPixels(overlay);
            // a null outline texture is black
            var outline = gameData.IconPixels(ItemIconOutline.TextureFor(uiEffects));

            var pixels = ItemIconOutline.Compose(GameData.IconSize, GameData.IconSize, ItemIconOutline.Premultiply(icon),
                overlayPixels == null ? null : ItemIconOutline.Premultiply(overlayPixels),
                outline == null ? null : ItemIconOutline.Premultiply(outline));

            return Encode(pixels, GameData.IconSize, GameData.IconSize, SKAlphaType.Premul);
        }

        /// <summary>
        /// Straight RGBA to PNG with SkiaSharp. Pure white is transparent in the raw icons (the reference site draws them the same way).
        /// </summary>
        private static byte[] EncodePng(byte[] rgba, int width, int height)
        {
            for (var i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i] == 0xFF && rgba[i + 1] == 0xFF && rgba[i + 2] == 0xFF && rgba[i + 3] == 0xFF)
                    rgba[i + 3] = 0;
            }

            return Encode(rgba, width, height, SKAlphaType.Unpremul);
        }

        private static byte[] Encode(byte[] rgba, int width, int height, SKAlphaType alpha)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha));
            Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);

            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);

            return data.ToArray();
        }
    }
}
