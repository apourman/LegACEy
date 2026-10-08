using System;
using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.GameArt;

/// <summary>
/// Reads interface art (RenderSurface, 0x06xxxxxx) from client_portal.dat.
/// </summary>
/// <remarks>
/// The dat layout follows ACE.DatLoader: a header at 0x140, a B-tree directory of 62 branches
/// and up to 61 sorted entries per node, and files stored in chained blocks whose first four
/// bytes point at the next block. The file is opened for shared reading, since acclient keeps it open.
/// </remarks>
public sealed class PortalDat : IDisposable, IGameArtSource
{
    private const int HeaderOffset = 0x140;
    private const int BranchCount = 0x3E;
    private const int EntrySize = 24;
    private const int DirectorySize = (BranchCount * 4) + 4 + (EntrySize * (BranchCount - 1));

    private readonly FileStream _stream;
    private readonly uint _blockSize;
    private readonly uint _rootDirectory;
    // ponytail: unbounded; holds the window art and every item icon shown this session. Add eviction if a session's
    // icons ever reach a few MB.
    private readonly Dictionary<uint, GameImage?> _images = new();

    public PortalDat(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[40];
        _stream.Seek(HeaderOffset, SeekOrigin.Begin);
        ReadExactly(header, header.Length);
        _blockSize = BitConverter.ToUInt32(header, 4);
        _rootDirectory = BitConverter.ToUInt32(header, 32);
    }

    public string Path { get; }

    /// <summary>
    /// Decode an interface image to premultiplied BGRA, or return null if it is missing or in an unsupported format.
    /// Each id is decoded once; callers share the result and must not change its pixels.
    /// </summary>
    public GameImage? ReadImage(uint id)
    {
        if (!_images.TryGetValue(id, out var image))
            _images.Add(id, image = DecodeImage(id));
        return image;
    }

    private GameImage? DecodeImage(uint id)
    {
        var data = ReadFile(id);
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
            case 21: // PFID_A8R8G8B8: stored little-endian, so already B, G, R, A in memory
                if (length < pixels.Length) return null;
                Buffer.BlockCopy(data, source, pixels, 0, pixels.Length);
                break;
            case 20: // PFID_R8G8B8: B, G, R
                for (var i = 0; i < width * height; i++)
                    Set(pixels, i, data[source + (i * 3)], data[source + (i * 3) + 1], data[source + (i * 3) + 2], 0xFF);
                break;
            case 101: // PFID_INDEX16: 16-bit palette indices, palette id after the pixels
            case 41: // PFID_P8: 8-bit palette indices
                {
                    var paletteId = BitConverter.ToUInt32(data, source + length);
                    var palette = ReadPalette(paletteId);
                    if (palette == null) return null;
                    var wide = format == 101;
                    for (var i = 0; i < width * height; i++)
                    {
                        int index = wide ? BitConverter.ToUInt16(data, source + (i * 2)) : data[source + i];
                        var color = index < palette.Length ? palette[index] : 0u;
                        Set(pixels, i, (byte)color, (byte)(color >> 8), (byte)(color >> 16), (byte)(color >> 24));
                    }
                }
                break;
            default:
                return null;
        }

        Premultiply(pixels);
        return new GameImage(width, height, pixels);
    }

    public void Dispose() => _stream.Dispose();

    internal uint[]? ReadPalette(uint id)
    {
        var data = ReadFile(id);
        if (data == null || data.Length < 8) return null;
        var count = BitConverter.ToInt32(data, 4);
        if (count < 0 || 8 + (count * 4) > data.Length) return null;
        var colors = new uint[count];
        for (var i = 0; i < count; i++)
            colors[i] = BitConverter.ToUInt32(data, 8 + (i * 4));
        return colors;
    }

    /// <summary>Find a file in the B-tree and read its blocks.</summary>
    internal byte[]? ReadFile(uint id)
    {
        var node = _rootDirectory;
        while (node != 0)
        {
            var directory = ReadBlocks(node, DirectorySize);
            var count = BitConverter.ToInt32(directory, BranchCount * 4);
            var isLeaf = BitConverter.ToUInt32(directory, 0) == 0;
            var next = 0u;
            var descended = false;
            for (var i = 0; i < count; i++)
            {
                var entry = (BranchCount * 4) + 4 + (i * EntrySize);
                var entryId = BitConverter.ToUInt32(directory, entry + 4);
                if (entryId == id)
                    return ReadBlocks(BitConverter.ToUInt32(directory, entry + 8), BitConverter.ToInt32(directory, entry + 12));
                if (id < entryId)
                {
                    next = isLeaf ? 0 : BitConverter.ToUInt32(directory, i * 4);
                    descended = true;
                    break;
                }
            }
            if (!descended)
                next = isLeaf ? 0 : BitConverter.ToUInt32(directory, count * 4);
            node = next;
        }
        return null;
    }

    private byte[] ReadBlocks(uint offset, int size)
    {
        var buffer = new byte[size];
        var link = new byte[4];
        var written = 0;
        var block = offset;
        var payload = (int)_blockSize - 4;
        while (written < size)
        {
            _stream.Seek(block, SeekOrigin.Begin);
            ReadExactly(link, 4);
            var count = Math.Min(payload, size - written);
            ReadExactly(buffer, count, written);
            written += count;
            block = BitConverter.ToUInt32(link, 0);
            if (block == 0) break;
        }
        return buffer;
    }

    private void ReadExactly(byte[] buffer, int count, int offset = 0)
    {
        while (count > 0)
        {
            var read = _stream.Read(buffer, offset, count);
            if (read == 0) throw new EndOfStreamException("portal.dat ended inside a block.");
            offset += read;
            count -= read;
        }
    }

    private static void Set(byte[] pixels, int index, byte b, byte g, byte r, byte a)
    {
        pixels[index * 4] = b;
        pixels[(index * 4) + 1] = g;
        pixels[(index * 4) + 2] = r;
        pixels[(index * 4) + 3] = a;
    }

    private static void Premultiply(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 0xFF) continue;
            pixels[i] = (byte)(pixels[i] * a / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
        }
    }
}

/// <summary>A decoded game image as premultiplied BGRA, rows top to bottom.</summary>
public sealed class GameImage
{
    public GameImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }
}
