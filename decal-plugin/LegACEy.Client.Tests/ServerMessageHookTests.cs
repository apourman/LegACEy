using LegACEy.Client.DecalPlugin;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

public sealed unsafe class ServerMessageHookTests
{
    [Fact]
    public void A_message_retail_queues_reaches_the_parsers_from_its_type_on()
    {
        // A PrivateUpdatePropertyInt (0x02CD) as the hook took it from retail's queue in game: sequence 0, Age (125) = 56346, then the
        // same message with Level (25) = 126 in its place, which the character sheet reads.
        var message = Convert.FromHexString("CD020000007D0000001ADC0000");
        var delivered = (Type: 0, Bytes: Array.Empty<byte>());
        fixed (byte* bytes = message) ServerMessageHook.Deliver(bytes, message.Length, (type, raw) => delivered = (type, raw.ToArray()));
        Assert.Equal(0x02CD, delivered.Type);
        Assert.Equal(message, delivered.Bytes);

        message[5] = 25;
        message[9] = 126;
        message[10] = 0;
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
