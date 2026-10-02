using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using SkiaSharp;

namespace ACE.MarketApi
{
    /// <summary>
    /// Icon PNGs by texture id and optional palette template ("/api/icons/0x06003237.png", "/api/icons/0x06003237_p19.png"), and the glow stylesheet. No sign-in.
    /// </summary>
    public static class IconEndpoints
    {
        public static void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/icons/glow.css", () => Results.Text(ItemIcons.GlowStylesheet, "text/css")).File(200, "text/css");
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
    /// Makes an icon PNG from the portal DAT on its first request and keeps it on disk, keyed by texture id plus palette template
    /// </summary>
    public sealed class IconStore
    {
        private static readonly Regex fileName = new Regex(@"^0x(?<id>[0-9A-Fa-f]{8})(?:_p(?<template>[0-9]{1,4}))?\.png\z", RegexOptions.CultureInvariant);

        private readonly GameData gameData;

        private readonly string directory;

        public IconStore(GameData gameData, string directory)
        {
            this.gameData = gameData;
            this.directory = Path.GetFullPath(directory);
        }

        /// <summary>
        /// The PNG, or null when the name isn't a 32×32 texture in the DAT, or names a palette template that no clothing table gives that icon
        /// </summary>
        public byte[] Get(string file)
        {
            var match = fileName.Match(file);
            if (!match.Success)
                return null;

            var id = uint.Parse(match.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int? template = match.Groups["template"].Success ? int.Parse(match.Groups["template"].Value, CultureInfo.InvariantCulture) : null;

            // only names a listing can give out, so requests can't fill the cache with variants
            if (template is int paletteTemplate && !gameData.IsClothingIcon(id, paletteTemplate))
                return null;

            var path = Path.Combine(directory, ItemIcons.FileName(id, template));

            try
            {
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                // another request is moving the same icon into place (Windows locks it): make it again
            }

            // The clothing table's icon for the palette is already drawn in that palette's colors: every icon a clothing table names is A8R8G8B8,
            // so there is no palette left to apply. A palette-indexed icon gets its own default palette.
            var pixels = gameData.IconPixels(id);
            if (pixels == null)
                return null;

            var png = EncodePng(pixels, GameData.IconSize, GameData.IconSize);

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

        /// <summary>
        /// Straight RGBA to PNG with SkiaSharp. Pure white is transparent in the client's icons (the reference site draws them the same way).
        /// </summary>
        private static byte[] EncodePng(byte[] rgba, int width, int height)
        {
            for (var i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i] == 0xFF && rgba[i + 1] == 0xFF && rgba[i + 2] == 0xFF && rgba[i + 3] == 0xFF)
                    rgba[i + 3] = 0;
            }

            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);

            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);

            return data.ToArray();
        }
    }
}
