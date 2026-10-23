using System;

namespace LegACEy.Client.Themes;

/// <summary>
/// Hyllian's xBR pixel-art scaler at 2x: each art pixel becomes a 2x2 block, and where the art has a diagonal edge the block's corners take
/// a share of the neighbour across it, so outlines come out smooth and the pixels inside them stay hard.
/// </summary>
public static class Xbr
{
    // A corner's neighbourhood, as offsets from the pixel E, for the bottom-right corner; the other three corners are this turned a quarter.
    //        A1 B1 C1
    //     A0 A  B  C  C4
    //     D0 D  E  F  F4
    //     G0 G  H  I  I4
    //        G5 H5 I5
    // The last three entries are the corner's sub-pixel and the sub-pixels beside it (right of it in the top row, below it in the left
    // column), as quadrants of the 2x2 block.
    private static readonly (int X, int Y)[] Corner =
    {
        (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1), (2, 1), (1, 2),
        (1, 1), (1, -1), (-1, 1),
    };
    private const int A = 0, B = 1, C = 2, D = 3, F = 4, G = 5, H = 6, I = 7, I4 = 8, I5 = 9, Here = 10, Side = 11, Below = 12;
    private static readonly (int X, int Y)[][] Turns = new (int X, int Y)[4][];

    static Xbr()
    {
        for (var turn = 0; turn < 4; turn++)
        {
            Turns[turn] = new (int X, int Y)[Corner.Length];
            for (var n = 0; n < Corner.Length; n++)
            {
                var offset = Corner[n];
                for (var t = 0; t < turn; t++) offset = (offset.Y, -offset.X);
                Turns[turn][n] = offset;
            }
        }
    }

    /// <summary>Square 32-bit art (4 bytes a pixel, blue first) at twice its size.</summary>
    public static byte[] Double(byte[] art, int size)
    {
        var count = size * size;
        var pixels = new uint[count];
        var yuva = new float[count * 4];
        for (var p = 0; p < count; p++)
        {
            int b = art[p * 4], g = art[p * 4 + 1], r = art[p * 4 + 2];
            pixels[p] = BitConverter.ToUInt32(art, p * 4);
            yuva[p * 4] = 0.299f * r + 0.587f * g + 0.114f * b;
            yuva[p * 4 + 1] = -0.169f * r - 0.331f * g + 0.5f * b;
            yuva[p * 4 + 2] = 0.5f * r - 0.419f * g - 0.081f * b;
            yuva[p * 4 + 3] = art[p * 4 + 3];
        }

        var wide = size * 2;
        var result = new uint[wide * wide];
        var near = new int[Corner.Length];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var e = y * size + x;
            result[y * 2 * wide + x * 2] = result[y * 2 * wide + x * 2 + 1] = result[(y * 2 + 1) * wide + x * 2] = result[(y * 2 + 1) * wide + x * 2 + 1] = pixels[e];
            foreach (var turn in Turns)
            {
                for (var n = 0; n < Here; n++)
                    near[n] = At(x, y, turn[n], size);
                for (var n = Here; n < Corner.Length; n++)
                    near[n] = (y * 2 + (turn[n].Y + 1) / 2) * wide + x * 2 + (turn[n].X + 1) / 2;
                BlendCorner(pixels, yuva, e, near, result);
            }
        }

        var bytes = new byte[result.Length * 4];
        Buffer.BlockCopy(result, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    // The art pixel at an offset from (x, y), the edge repeated past the art's sides.
    private static int At(int x, int y, (int X, int Y) offset, int size) =>
        Math.Min(Math.Max(y + offset.Y, 0), size - 1) * size + Math.Min(Math.Max(x + offset.X, 0), size - 1);

    private static void BlendCorner(uint[] pixels, float[] yuva, int e, int[] n, uint[] result)
    {
        if (pixels[e] == pixels[n[H]] || pixels[e] == pixels[n[F]]) return;

        var across = Diff(yuva, e, n[C]) + Diff(yuva, e, n[G]) + Diff(yuva, n[I], n[I5]) + Diff(yuva, n[I], n[I4]) + 4 * Diff(yuva, n[H], n[F]);
        var along = Diff(yuva, n[H], n[D]) + Diff(yuva, n[H], n[I5]) + Diff(yuva, n[F], n[I4]) + Diff(yuva, n[F], n[B]) + 4 * Diff(yuva, e, n[I]);
        if (across > along) return;

        var colour = pixels[Diff(yuva, e, n[F]) <= Diff(yuva, e, n[H]) ? n[F] : n[H]];
        if (across < along && (!Alike(yuva, n[F], n[B]) && !Alike(yuva, n[H], n[D])
            || Alike(yuva, e, n[I]) && !Alike(yuva, n[F], n[I4]) && !Alike(yuva, n[H], n[I5]) || Alike(yuva, e, n[G]) || Alike(yuva, e, n[C])))
        {
            var ke = Diff(yuva, n[F], n[G]);
            var ki = Diff(yuva, n[H], n[C]);
            var shallow = 2 * ke <= ki && pixels[e] != pixels[n[G]] && pixels[n[D]] != pixels[n[G]];
            var steep = ke >= 2 * ki && pixels[e] != pixels[n[C]] && pixels[n[B]] != pixels[n[C]];
            Blend(result, n[Here], colour, shallow || steep ? (shallow && steep ? 224 : 192) : 128);
            if (shallow) Blend(result, n[Below], colour, 64);
            if (steep) Blend(result, n[Side], colour, 64);
        }
        else Blend(result, n[Here], colour, 128);
    }

    // How far apart two pixels look: the sum of their brightness, colour and alpha differences.
    private static float Diff(float[] yuva, int a, int b) =>
        Math.Abs(yuva[a * 4] - yuva[b * 4]) + Math.Abs(yuva[a * 4 + 1] - yuva[b * 4 + 1]) + Math.Abs(yuva[a * 4 + 2] - yuva[b * 4 + 2])
        + Math.Abs(yuva[a * 4 + 3] - yuva[b * 4 + 3]);

    private static bool Alike(float[] yuva, int a, int b) => Diff(yuva, a, b) < 155;

    private static void Blend(uint[] result, int at, uint colour, int share)
    {
        uint mixed = 0, was = result[at];
        for (var shift = 0; shift < 32; shift += 8)
        {
            int from = (int)(was >> shift) & 0xFF, to = (int)(colour >> shift) & 0xFF;
            mixed |= (uint)((from + (to - from) * share / 256) & 0xFF) << shift;
        }
        result[at] = mixed;
    }
}
