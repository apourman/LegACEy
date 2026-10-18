using LegACEy.Client.Demo;
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

    [Fact]
    public void An_inspect_reply_gives_the_look_then_what_is_worn()
    {
        var body = ChannelWire.Body(w =>
        {
            w.Write(0x02000001u); w.Write(0x0400007Eu);
            w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write(0x80000123u); w.Write(0x00000200u | 0x00000800u);
            w.Write(0x06001234u); w.Write(0x06000001u); w.Write(0u); w.Write(1u);
            ChannelWire.WriteString(w, "Nariyid Breastplate");
        });

        var (look, gear) = PaperdollProtocol.ReadInspect(body);

        Assert.Equal(0x02000001u, look.SetupId);
        var worn = Assert.Single(gear);
        Assert.Equal((0x80000123u, "Nariyid Breastplate", 0x06001234u, 0x06000001u, 1u), (worn.Id, worn.Name, worn.Visual.Icon, worn.Visual.Underlay, worn.Visual.UiEffects));
        Assert.Equal(new[] { PaperdollSlot.UpperArms, PaperdollSlot.Chest }, InventorySnapshotBuilder.SlotsFor(worn.Location));
    }
}
