using System;
using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.GameArt;

/// <summary>
/// How a character looks, as the server describes it: its setup (body model) and its ObjDesc, which recolours
/// the body and swaps parts and textures for what it wears. Palette offsets and lengths count blocks of 8 colours, as on the wire.
/// </summary>
public sealed class CharacterAppearance
{
    public CharacterAppearance(uint setupId, uint paletteId, IReadOnlyList<SubPalette> subPalettes, IReadOnlyList<TextureChange> textureChanges, IReadOnlyList<PartChange> partChanges)
    {
        SetupId = setupId;
        PaletteId = paletteId;
        SubPalettes = subPalettes;
        TextureChanges = textureChanges;
        PartChanges = partChanges;
    }

    public uint SetupId { get; }
    public uint PaletteId { get; }
    public IReadOnlyList<SubPalette> SubPalettes { get; }
    public IReadOnlyList<TextureChange> TextureChanges { get; }
    public IReadOnlyList<PartChange> PartChanges { get; }
}

public readonly struct SubPalette
{
    public SubPalette(uint paletteId, int offset, int length) { PaletteId = paletteId; Offset = offset; Length = length; }
    public uint PaletteId { get; }
    public int Offset { get; }
    public int Length { get; }
}

public readonly struct TextureChange
{
    public TextureChange(int part, uint oldTexture, uint newTexture) { Part = part; OldTexture = oldTexture; NewTexture = newTexture; }
    public int Part { get; }
    public uint OldTexture { get; }
    public uint NewTexture { get; }
}

public readonly struct PartChange
{
    public PartChange(int part, uint model) { Part = part; Model = model; }
    public int Part { get; }
    public uint Model { get; }
}

public struct ModelVertex
{
    public float X, Y, Z, NX, NY, NZ, U, V;
}

/// <summary>Triangles that share one texture. The texture is straight-alpha BGRA, rows top to bottom.</summary>
public sealed class ModelMesh
{
    public ModelMesh(GameImage texture, bool clipped) { Texture = texture; Clipped = clipped; }

    public GameImage Texture { get; }

    /// <summary>The texture has fully transparent pixels to cut out (hair, fringes), not see-through ones.</summary>
    public bool Clipped { get; }

    public List<ModelVertex> Vertices { get; } = new();
}

/// <summary>A character's parts placed in its default pose and grouped by texture, in AC object space (metres, Z up).</summary>
public sealed class CharacterModel
{
    public CharacterModel(IReadOnlyList<ModelMesh> meshes)
    {
        Meshes = meshes;
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var mesh in meshes)
            foreach (var vertex in mesh.Vertices)
            {
                min = Math.Min(min, vertex.Z);
                max = Math.Max(max, vertex.Z);
            }
        Bottom = meshes.Count == 0 ? 0 : min;
        Top = meshes.Count == 0 ? 0 : max;
    }

    public IReadOnlyList<ModelMesh> Meshes { get; }
    public float Bottom { get; }
    public float Top { get; }
    public int TriangleCount { get { var count = 0; foreach (var mesh in Meshes) count += mesh.Vertices.Count / 3; return count; } }

    /// <summary>
    /// Assemble a character from portal.dat: the setup's parts in its first placement (the default pose), with the
    /// appearance's part swaps, texture swaps and palette applied. Unreadable parts and textures are skipped or drawn grey.
    /// </summary>
    public static CharacterModel Build(PortalDat dat, CharacterAppearance appearance)
    {
        var setup = DatModels.ReadSetup(dat, appearance.SetupId) ?? throw new InvalidDataException($"Setup {appearance.SetupId:X8} is not in portal.dat.");
        var parts = (uint[])setup.Parts.Clone();
        foreach (var change in appearance.PartChanges)
            if (change.Part < parts.Length)
                parts[change.Part] = change.Model;
        var recolor = Recolor(dat, appearance);

        var meshes = new Dictionary<(uint Surface, uint Texture), ModelMesh?>();
        var ordered = new List<ModelMesh>();
        for (var part = 0; part < parts.Length; part++)
        {
            var model = DatModels.ReadGfxObj(dat, parts[part]);
            if (model == null) continue;
            var frame = part < setup.Frames.Length ? setup.Frames[part] : Frame.Identity;
            var scale = part < setup.Scales.Length ? setup.Scales[part] : new Vec3(1, 1, 1);

            foreach (var polygon in model.Polygons)
            {
                Add(polygon.PosSurface, polygon.PosUv, reverse: false);
                if (polygon.NegUv != null && polygon.NegSurface >= 0 && polygon.NegUv != polygon.PosUv)
                    Add(polygon.NegSurface, polygon.NegUv, reverse: true);

                void Add(int surfaceIndex, byte[]? uvs, bool reverse)
                {
                    if (uvs == null || surfaceIndex < 0 || surfaceIndex >= model.Surfaces.Length) return;
                    var mesh = MeshFor(model.Surfaces[surfaceIndex], part);
                    if (mesh == null) return;
                    for (var i = 1; i + 1 < polygon.Vertices.Length; i++)
                    {
                        var a = reverse ? i + 1 : i;
                        var b = reverse ? i : i + 1;
                        foreach (var corner in new[] { 0, a, b })
                            if (model.Vertices.TryGetValue(polygon.Vertices[corner], out var vertex))
                                mesh.Vertices.Add(Place(vertex, uvs[corner], frame, scale, reverse));
                    }
                }
            }
        }
        return new CharacterModel(ordered);

        ModelMesh? MeshFor(uint surfaceId, int part)
        {
            var surface = DatModels.ReadSurface(dat, surfaceId);
            if (surface == null) return null;
            var texture = surface.Texture;
            foreach (var change in appearance.TextureChanges)
                if (change.Part == part && change.OldTexture == texture)
                    texture = change.NewTexture;
            var key = (surfaceId, texture);
            if (meshes.TryGetValue(key, out var existing)) return existing;

            ModelMesh? mesh = null;
            if (surface.Translucency < 0.99f)
            {
                var image = texture == 0 ? Solid(surface.Color) : DatModels.ReadTexture(dat, texture, recolor, surface.Clipped) ?? Solid(0xFF808080);
                mesh = new ModelMesh(image, surface.Clipped);
                ordered.Add(mesh);
            }
            meshes[key] = mesh;
            return mesh;
        }
    }

    /// <summary>
    /// The character's recolouring: each sub-palette's range copied over a texture's own palette (the surface's, else the
    /// texture's default), as the client does. The appearance's base palette is not the starting point.
    /// </summary>
    private static Func<uint[], uint[]> Recolor(PortalDat dat, CharacterAppearance appearance)
    {
        var ranges = new List<(uint[] Source, int Start, int End)>();
        foreach (var sub in appearance.SubPalettes)
            if (dat.ReadPalette(sub.PaletteId) is { } source)
                ranges.Add((source, sub.Offset * 8, (sub.Offset + (sub.Length == 0 ? 256 : sub.Length)) * 8));
        return palette =>
        {
            if (ranges.Count == 0) return palette;
            var colors = (uint[])palette.Clone();
            foreach (var (source, start, end) in ranges)
                for (var i = start; i < end && i < colors.Length && i < source.Length; i++)
                    colors[i] = source[i];
            return colors;
        };
    }

    private static ModelVertex Place(DatVertex vertex, int uvIndex, Frame frame, Vec3 scale, bool flipNormal)
    {
        var position = frame.Origin + frame.Rotate(new Vec3(vertex.Position.X * scale.X, vertex.Position.Y * scale.Y, vertex.Position.Z * scale.Z));
        var normal = frame.Rotate(vertex.Normal);
        if (flipNormal) normal = new Vec3(-normal.X, -normal.Y, -normal.Z);
        var uv = uvIndex < vertex.Uvs.Length ? vertex.Uvs[uvIndex] : default;
        return new ModelVertex { X = position.X, Y = position.Y, Z = position.Z, NX = normal.X, NY = normal.Y, NZ = normal.Z, U = uv.U, V = uv.V };
    }

    private static GameImage Solid(uint argb) => new(1, 1, new[] { (byte)argb, (byte)(argb >> 8), (byte)(argb >> 16), (byte)0xFF });
}

public readonly struct Vec3
{
    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
}

/// <summary>A part's position and orientation (a unit quaternion) relative to the object.</summary>
public readonly struct Frame
{
    public static readonly Frame Identity = new(default, 1, 0, 0, 0);

    public Frame(Vec3 origin, float w, float x, float y, float z) { Origin = origin; W = w; X = x; Y = y; Z = z; }
    public Vec3 Origin { get; }
    public float W { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }

    /// <summary>v + 2w(q × v) + 2q × (q × v)</summary>
    public Vec3 Rotate(Vec3 v)
    {
        var cx = (Y * v.Z) - (Z * v.Y);
        var cy = (Z * v.X) - (X * v.Z);
        var cz = (X * v.Y) - (Y * v.X);
        var ccx = (Y * cz) - (Z * cy);
        var ccy = (Z * cx) - (X * cz);
        var ccz = (X * cy) - (Y * cx);
        return new Vec3(v.X + (2 * ((W * cx) + ccx)), v.Y + (2 * ((W * cy) + ccy)), v.Z + (2 * ((W * cz) + ccz)));
    }
}
