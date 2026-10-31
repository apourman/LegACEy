using LegACEy.Client.DecalPlugin;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

public sealed unsafe class ServerMessageHookTests
{
    // One message of each type the client reads, as the hook took them from retail's queue in game (the smallest of each seen). No
    // 0xF7DB arrived while they were taken.
    [Theory]
    [InlineData("CD020000007D0000001ADC0000")]
    [InlineData("CF02000000010000000000000000000000")]
    [InlineData("DD020000000600000000000100010000000000000000000000000000000000000000000000")]
    [InlineData("E30200000001000000000000003700000000000000")]
    [InlineData("E702000000010000000000000000000000000000001C000000")]
    [InlineData("25F60000720300E0110101017E00EF05A806004020BF0C008604000000000100")]
    [InlineData("45F70000DA1D00E011000000011802001404000065000000140000202B0000345C0D00020000000000000000000000000000000000000000187021100300557374000000A650BA2600000020120000000A00000008000000010001000800F05F000000000A000200")]
    [InlineData("46F700000800F05F")]
    [InlineData("47F70000781500E000000000")]
    [InlineData("49F700000800F05FE31D00E002000000030000008E000100")]
    [InlineData("4AF70000D40700E000000100")]
    [InlineData("B0F700000800F05F01000000474C000001000200000000000D006C6567616365792E7265616C6D0400000000000000")]
    [InlineData("EAF70000")]
    public void Every_type_the_client_reads_gets_through_the_hook_whole(string captured)
    {
        var message = Convert.FromHexString(captured);
        var delivered = (Type: 0, Bytes: Array.Empty<byte>());
        fixed (byte* bytes = message) ServerMessageHook.Deliver(bytes, message.Length, (type, raw) => delivered = (type, raw.ToArray()));
        Assert.Equal(BitConverter.ToInt32(message), delivered.Type);
        Assert.Equal(message, delivered.Bytes);
    }

    [Fact]
    public void A_message_retail_queues_reaches_the_parsers_from_its_type_on()
    {
        // The captured PrivateUpdatePropertyInt (0x02CD) above, sequence 0, with Level (25) = 126 in place of Age (125) = 56346: the
        // character sheet reads Level.
        var message = Convert.FromHexString("CD02000000190000007E000000");
        var sheet = new CharacterSheet();
        fixed (byte* bytes = message) ServerMessageHook.Deliver(bytes, message.Length, sheet.Receive);
        Assert.Equal(126, sheet.Level);

        // A message the client doesn't read, such as a position update (0xF748), never reaches it.
        var position = new byte[] { 0x48, 0xF7, 0, 0, 1, 2, 3, 4 };
        var seen = 0;
        fixed (byte* bytes = position) ServerMessageHook.Deliver(bytes, position.Length, (_, _) => seen++);
        Assert.Equal(0, seen);
    }
}
