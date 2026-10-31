using LegACEy.Client.DecalPlugin;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

public sealed unsafe class ServerMessageHookTests
{
    [Fact]
    public void A_message_retail_queues_reaches_the_parsers_from_its_type_on()
    {
        // PrivateUpdatePropertyInt (0x02CD): sequence 1, Level (25) = 126.
        var message = new byte[] { 0xCD, 0x02, 0, 0, 1, 25, 0, 0, 0, 126, 0, 0, 0 };
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
