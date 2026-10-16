using System;
using System.Linq;
using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class DatModelsDecodeTests
{
    private const uint Dxt1 = 0x31545844;

    [Fact]
    public void Sixteen_bit_and_dxt1_surfaces_decode_to_straight_alpha_bgra()
    {
        // R5G6B5: red, green, blue, white.
        var rgb565 = Decode(Surface(4, 1, 23, Words(0xF800, 0x07E0, 0x001F, 0xFFFF)));
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }, rgb565.Pixels);

        // A4R4G4B4: opaque green, then red at alpha 8 of 15 (136 of 255).
        var argb4444 = Decode(Surface(2, 1, 26, Words(0xF0F0, 0x8F00)));
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 0, 255, 136 }, argb4444.Pixels);

        // DXT1: one 4 x 4 block whose two colours are red and black, every texel indexing red.
        var dxt1 = Decode(Surface(4, 4, Dxt1, Words(0xF800, 0x0000, 0x0000, 0x0000)));
        Assert.Equal(Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 16).SelectMany(p => p).ToArray(), dxt1.Pixels);
    }

    private static GameImage Decode(byte[] data) =>
        DatModels.DecodeSurface(data, _ => null, palette => palette, clipped: false) ?? throw new InvalidOperationException("The surface did not decode.");

    /// <summary>A RenderSurface file: its 24-byte header, then the body bytes.</summary>
    private static byte[] Surface(int width, int height, uint format, byte[] body)
    {
        var data = new byte[24 + body.Length];
        BitConverter.GetBytes(width).CopyTo(data, 8);
        BitConverter.GetBytes(height).CopyTo(data, 12);
        BitConverter.GetBytes(format).CopyTo(data, 16);
        BitConverter.GetBytes(body.Length).CopyTo(data, 20);
        body.CopyTo(data, 24);
        return data;
    }

    private static byte[] Words(params ushort[] words) => words.SelectMany(w => BitConverter.GetBytes(w)).ToArray();
}
