using System.Linq;
using LegACEy.Client.Themes;
using Xunit;

public class XbrTests
{
    [Fact]
    public void A_diagonal_edge_is_smoothed_and_flat_colour_stays_flat()
    {
        // white above the diagonal, black below it
        const int size = 8;
        var art = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var shade = (byte)(x >= y ? 255 : 0);
            art[(y * size + x) * 4] = art[(y * size + x) * 4 + 1] = art[(y * size + x) * 4 + 2] = shade;
            art[(y * size + x) * 4 + 3] = 255;
        }

        var doubled = Xbr.Double(art, size);

        var blues = Enumerable.Range(0, size * size * 4).Select(p => doubled[p * 4]).ToArray();
        Assert.Contains(blues, shade => shade is > 0 and < 255);
        Assert.Equal(255, blues[(0 * size * 2 + size * 2 - 1)]);
        Assert.Equal(0, blues[((size * 2 - 1) * size * 2)]);
    }
}
