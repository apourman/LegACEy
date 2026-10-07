using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LegACEy.Client.GameArt;

internal sealed class DatSetup
{
    public uint[] Parts = Array.Empty<uint>();
    public Vec3[] Scales = Array.Empty<Vec3>();
    public Frame[] Frames = Array.Empty<Frame>();
}

internal readonly struct DatUv
{
    public DatUv(float u, float v) { U = u; V = v; }
    public float U { get; }
    public float V { get; }
}

internal sealed class DatVertex
{
    public Vec3 Position;
    public Vec3 Normal;
    public DatUv[] Uvs = Array.Empty<DatUv>();
}

internal sealed class DatPolygon
{
    public short[] Vertices = Array.Empty<short>();
    public short PosSurface;
    public short NegSurface;
    public byte[]? PosUv;
    public byte[]? NegUv;
}

internal sealed class DatGfxObj
{
    public uint[] Surfaces = Array.Empty<uint>();
    public Dictionary<short, DatVertex> Vertices = new();
    public List<DatPolygon> Polygons = new();
}

internal sealed class DatSurface
{
    public uint Texture;
    public uint Color;
    public bool Clipped;
    public float Translucency;
}

/// <summary>
/// The few portal.dat model files a character needs, read as ACE.DatLoader reads them (Setup 0x02, GfxObj 0x01,
/// Surface 0x08, SurfaceTexture 0x05, RenderSurface 0x06). Only what drawing needs is kept.
/// </summary>
internal static class DatModels
{
    private const uint SetupHasParent = 0x1, SetupHasDefaultScale = 0x2;
    private const uint GfxHasPhysics = 0x1, GfxHasDrawing = 0x2;
    private const uint SurfaceBase1Image = 0x2, SurfaceBase1ClipMap = 0x4;
    private const int StipplingNoPos = 0x4, StipplingNoNeg = 0x8;
    private const int SidesNone = 1, SidesClockwise = 2;

    public static DatSetup? ReadSetup(PortalDat dat, uint id)
    {
        var reader = Open(dat, id);
        if (reader == null) return null;
        reader.ReadUInt32();
        var flags = reader.ReadUInt32();
        var count = reader.ReadInt32();
        var setup = new DatSetup { Parts = new uint[count] };
        for (var i = 0; i < count; i++) setup.Parts[i] = reader.ReadUInt32();
        if ((flags & SetupHasParent) != 0) reader.BaseStream.Seek(count * 4, SeekOrigin.Current);
        if ((flags & SetupHasDefaultScale) != 0)
        {
            setup.Scales = new Vec3[count];
            for (var i = 0; i < count; i++) setup.Scales[i] = ReadVector(reader);
        }
        // holding locations, then connection points: key, part id, frame
        for (var table = 0; table < 2; table++)
            reader.BaseStream.Seek(reader.ReadInt32() * (4 + 4 + 28), SeekOrigin.Current);
        // the first placement is the default pose; its hooks, and the placements after it, aren't needed
        if (reader.ReadInt32() > 0)
        {
            reader.ReadInt32();
            setup.Frames = new Frame[count];
            for (var i = 0; i < count; i++) setup.Frames[i] = ReadFrame(reader);
        }
        return setup;
    }

    public static DatGfxObj? ReadGfxObj(PortalDat dat, uint id)
    {
        var reader = Open(dat, id);
        if (reader == null) return null;
        reader.ReadUInt32();
        var flags = reader.ReadUInt32();
        var model = new DatGfxObj { Surfaces = new uint[ReadCompressed(reader)] };
        for (var i = 0; i < model.Surfaces.Length; i++) model.Surfaces[i] = reader.ReadUInt32();

        reader.ReadInt32(); // vertex type 1
        var vertexCount = reader.ReadUInt32();
        for (var i = 0; i < vertexCount; i++)
        {
            var key = reader.ReadInt16();
            var uvCount = reader.ReadUInt16();
            var vertex = new DatVertex { Position = ReadVector(reader), Normal = ReadVector(reader), Uvs = new DatUv[uvCount] };
            for (var uv = 0; uv < uvCount; uv++) vertex.Uvs[uv] = new DatUv(reader.ReadSingle(), reader.ReadSingle());
            model.Vertices[key] = vertex;
        }

        if ((flags & GfxHasPhysics) != 0)
        {
            ReadPolygons(reader);
            SkipPhysicsBsp(reader);
        }
        ReadVector(reader); // sort centre
        if ((flags & GfxHasDrawing) != 0)
            model.Polygons = ReadPolygons(reader);
        return model;
    }

    public static DatSurface? ReadSurface(PortalDat dat, uint id)
    {
        var reader = Open(dat, id);
        if (reader == null) return null;
        var type = reader.ReadUInt32();
        var surface = new DatSurface { Clipped = (type & SurfaceBase1ClipMap) != 0 };
        if ((type & (SurfaceBase1Image | SurfaceBase1ClipMap)) != 0)
        {
            surface.Texture = reader.ReadUInt32();
            reader.ReadUInt32(); // the surface's palette: unused, the texture's own default is the base
        }
        else
            surface.Color = reader.ReadUInt32();
        surface.Translucency = reader.ReadSingle();
        return surface;
    }

    /// <summary>
    /// Decode a SurfaceTexture's first image to straight-alpha BGRA. Palette-indexed images use the character's
    /// default palette recoloured for the character, as ACViewer does; in a clip map, indices 0-7 are transparent.
    /// </summary>
    public static GameImage? ReadTexture(PortalDat dat, uint surfaceTextureId, Func<uint[], uint[]> recolor, bool clipped)
    {
        var reader = Open(dat, surfaceTextureId);
        if (reader == null) return null;
        reader.ReadUInt32();
        reader.ReadInt32();
        reader.ReadByte();
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var image = DecodeImage(dat, reader.ReadUInt32(), recolor, clipped);
            if (image != null) return image;
        }
        return null;
    }

    private static GameImage? DecodeImage(PortalDat dat, uint id, Func<uint[], uint[]> recolor, bool clipped)
    {
        var data = dat.ReadFile(id);
        if (data == null || data.Length < 24) return null;
        var width = BitConverter.ToInt32(data, 8);
        var height = BitConverter.ToInt32(data, 12);
        var format = BitConverter.ToUInt32(data, 16);
        var length = BitConverter.ToInt32(data, 20);
        const int source = 24;
        if (width <= 0 || height <= 0 || source + length > data.Length) return null;
        var pixels = new byte[width * height * 4];
        switch (format)
        {
            case 21: // A8R8G8B8
                if (length < pixels.Length) return null;
                Buffer.BlockCopy(data, source, pixels, 0, pixels.Length);
                break;
            case 20: // R8G8B8
                for (var i = 0; i < width * height; i++)
                    Set(pixels, i, data[source + (i * 3)], data[source + (i * 3) + 1], data[source + (i * 3) + 2], 0xFF);
                break;
            case 101: // INDEX16
            case 41: // P8
                {
                    var own = dat.ReadPalette(BitConverter.ToUInt32(data, source + length));
                    if (own == null) return null;
                    var palette = recolor(own);
                    for (var i = 0; i < width * height; i++)
                    {
                        int index = format == 101 ? BitConverter.ToUInt16(data, source + (i * 2)) : data[source + i];
                        var color = (clipped && index < 8) || index >= palette.Length ? 0u : palette[index];
                        Set(pixels, i, (byte)color, (byte)(color >> 8), (byte)(color >> 16), (byte)(color >> 24));
                    }
                }
                break;
            case 0x31545844: // DXT1
            case 0x33545844: // DXT3
            case 0x35545844: // DXT5
                Dxt.Decode(format, data, source, width, height, pixels);
                break;
            default:
                return null;
        }
        return new GameImage(width, height, pixels);
    }

    private static List<DatPolygon> ReadPolygons(BinaryReader reader)
    {
        var count = ReadCompressed(reader);
        var polygons = new List<DatPolygon>((int)count);
        for (var i = 0; i < count; i++)
        {
            reader.ReadUInt16(); // key
            var points = reader.ReadByte();
            var stippling = reader.ReadByte();
            var sides = reader.ReadInt32();
            var polygon = new DatPolygon { PosSurface = reader.ReadInt16(), NegSurface = reader.ReadInt16(), Vertices = new short[points] };
            for (var p = 0; p < points; p++) polygon.Vertices[p] = reader.ReadInt16();
            if ((stippling & StipplingNoPos) == 0) polygon.PosUv = reader.ReadBytes(points);
            if (sides == SidesClockwise && (stippling & StipplingNoNeg) == 0) polygon.NegUv = reader.ReadBytes(points);
            if (sides == SidesNone)
            {
                polygon.NegSurface = polygon.PosSurface;
                polygon.NegUv = polygon.PosUv;
            }
            polygons.Add(polygon);
        }
        return polygons;
    }

    /// <summary>Read past a physics BSP tree; it sits between the physics polygons and the drawing ones.</summary>
    private static void SkipPhysicsBsp(BinaryReader reader)
    {
        var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (tag == "FAEL") // "LEAF" reversed: index, solid, sphere, polygon ids
        {
            reader.BaseStream.Seek(4 + 4 + 16, SeekOrigin.Current);
            reader.BaseStream.Seek(reader.ReadUInt32() * 2, SeekOrigin.Current);
            return;
        }
        reader.BaseStream.Seek(16, SeekOrigin.Current); // splitting plane
        switch (tag)
        {
            case "nnPB": case "nIPB": SkipPhysicsBsp(reader); break; // BPnn, BPIn: positive child
            case "NIpB": case "NnpB": SkipPhysicsBsp(reader); break; // BpIN, BpnN: negative child
            case "NIPB": case "NnPB": SkipPhysicsBsp(reader); SkipPhysicsBsp(reader); break; // BPIN, BPnN: both
        }
        reader.BaseStream.Seek(16, SeekOrigin.Current); // bounding sphere
    }

    private static BinaryReader? Open(PortalDat dat, uint id)
    {
        var data = dat.ReadFile(id);
        return data == null ? null : new BinaryReader(new MemoryStream(data, writable: false));
    }

    private static uint ReadCompressed(BinaryReader reader)
    {
        var b0 = reader.ReadByte();
        if ((b0 & 0x80) == 0) return b0;
        var b1 = reader.ReadByte();
        if ((b0 & 0x40) == 0) return (uint)(((b0 & 0x7F) << 8) | b1);
        var s = reader.ReadUInt16();
        return (uint)(((((b0 & 0x3F) << 8) | b1) << 16) | s);
    }

    private static Vec3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static Frame ReadFrame(BinaryReader reader)
    {
        var origin = ReadVector(reader);
        return new Frame(origin, reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void Set(byte[] pixels, int index, byte b, byte g, byte r, byte a)
    {
        pixels[index * 4] = b;
        pixels[(index * 4) + 1] = g;
        pixels[(index * 4) + 2] = r;
        pixels[(index * 4) + 3] = a;
    }
}

/// <summary>DXT1/3/5 block decoding to straight-alpha BGRA.</summary>
internal static class Dxt
{
    public static void Decode(uint format, byte[] data, int offset, int width, int height, byte[] pixels)
    {
        var blockSize = format == 0x31545844 ? 8 : 16;
        var colors = new uint[4];
        var alphas = new byte[8];
        for (var by = 0; by < (height + 3) / 4; by++)
            for (var bx = 0; bx < (width + 3) / 4; bx++)
            {
                if (offset + blockSize > data.Length) return;
                var color = offset + blockSize - 8;
                var c0 = BitConverter.ToUInt16(data, color);
                var c1 = BitConverter.ToUInt16(data, color + 2);
                colors[0] = Rgb565(c0);
                colors[1] = Rgb565(c1);
                if (c0 > c1 || blockSize == 16)
                {
                    colors[2] = Mix(colors[0], colors[1], 2, 1, 3);
                    colors[3] = Mix(colors[0], colors[1], 1, 2, 3);
                }
                else
                {
                    colors[2] = Mix(colors[0], colors[1], 1, 1, 2);
                    colors[3] = 0; // transparent black
                }
                if (format == 0x35545844)
                {
                    alphas[0] = data[offset];
                    alphas[1] = data[offset + 1];
                    for (var i = 2; i < 8; i++)
                        alphas[i] = alphas[0] > alphas[1]
                            ? (byte)((((8 - i) * alphas[0]) + ((i - 1) * alphas[1])) / 7)
                            : i < 6 ? (byte)((((6 - i) * alphas[0]) + ((i - 1) * alphas[1])) / 5) : i == 6 ? (byte)0 : (byte)255;
                }
                var indices = BitConverter.ToUInt32(data, color + 4);
                ulong alphaBits = 0;
                if (format == 0x35545844)
                    for (var i = 0; i < 6; i++) alphaBits |= (ulong)data[offset + 2 + i] << (8 * i);
                for (var p = 0; p < 16; p++)
                {
                    var x = (bx * 4) + (p % 4);
                    var y = (by * 4) + (p / 4);
                    if (x >= width || y >= height) continue;
                    var c = colors[(indices >> (2 * p)) & 3];
                    var a = blockSize == 8 ? (byte)(c >> 24)
                        : format == 0x33545844 ? (byte)(((data[offset + (p / 2)] >> (4 * (p % 2))) & 0xF) * 17)
                        : alphas[(alphaBits >> (3 * p)) & 7];
                    var i = ((y * width) + x) * 4;
                    pixels[i] = (byte)c;
                    pixels[i + 1] = (byte)(c >> 8);
                    pixels[i + 2] = (byte)(c >> 16);
                    pixels[i + 3] = a;
                }
                offset += blockSize;
            }
    }

    private static uint Rgb565(ushort c)
    {
        uint r = (uint)((c >> 11) & 0x1F) * 255 / 31, g = (uint)((c >> 5) & 0x3F) * 255 / 63, b = (uint)(c & 0x1F) * 255 / 31;
        return 0xFF000000 | (r << 16) | (g << 8) | b;
    }

    private static uint Mix(uint a, uint b, uint wa, uint wb, uint total)
    {
        uint Channel(int shift) => ((((a >> shift) & 0xFF) * wa) + (((b >> shift) & 0xFF) * wb)) / total;
        return 0xFF000000 | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }
}
