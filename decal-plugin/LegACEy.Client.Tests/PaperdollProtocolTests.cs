using LegACEy.Client.Demo;
using LegACEy.Plugin.Paperdoll;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class PaperdollProtocolTests
{
    [Fact]
    public void Clothing_palette_ids_that_ACE_cut_to_16_bits_get_their_file_type_back()
    {
        var body = ChannelWire.Body(w =>
        {
            w.Write(0x02000001u); w.Write(0x0400007Eu);
            w.Write((ushort)1); w.Write(0x00001234u); w.Write((ushort)12); w.Write((ushort)4);
            w.Write((ushort)0);
            w.Write((ushort)0);
        });

        var look = PaperdollProtocol.ReadLook(body);

        Assert.Equal(0x04001234u, look.SubPalettes[0].PaletteId);
        Assert.Equal(0x0400007Eu, look.PaletteId);
    }
}
