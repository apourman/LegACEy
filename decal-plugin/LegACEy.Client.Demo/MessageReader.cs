using System;
using System.Buffers.Binary;

namespace LegACEy.Client.Demo;

/// <summary>A little-endian reader over a server message that throws ArgumentOutOfRangeException past its end.</summary>
internal ref struct MessageReader
{
    private readonly ReadOnlySpan<byte> _bytes;
    private int _at;

    public MessageReader(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes;
        _at = 0;
    }

    public void Skip(int count)
    {
        if (count < 0 || _at + count > _bytes.Length) throw new ArgumentOutOfRangeException(nameof(count));
        _at += count;
    }

    public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    /// <summary>A packed hash table's header: its count, then a bucket size the reader does not need.</summary>
    public int Header()
    {
        var count = UInt16();
        Skip(2);
        return count;
    }

    /// <summary>ACE's String16L: a 16-bit length, the Windows-1252 bytes, then padding to a multiple of four with the length.</summary>
    public string String()
    {
        var length = UInt16();
        var bytes = Take(length);
        Skip((4 - (2 + length) % 4) % 4);
        var chars = new char[length];
        // Names are plain ASCII; the rest of 1252 is close enough to Latin-1 for what the windows show.
        for (var i = 0; i < length; i++) chars[i] = (char)bytes[i];
        return new string(chars);
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (_at + count > _bytes.Length) throw new ArgumentOutOfRangeException(nameof(count));
        var taken = _bytes.Slice(_at, count);
        _at += count;
        return taken;
    }
}
