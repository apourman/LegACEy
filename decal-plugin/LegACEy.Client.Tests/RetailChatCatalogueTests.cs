using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class RetailChatCatalogueTests
{
    [Fact]
    public void PatchedSendCallTurnsOnlySendAndTheWindowOff()
    {
        var messages = new List<string>();

        var natives = RetailChatCatalogue.Validate(Client(patched: RetailChatCatalogue.ProcessCommandSendCall), messages.Add);

        Assert.Equal(new RetailChatNatives(true, true, false, true, true, true, true), natives);
        Assert.False(natives.ChatWindow);
        Assert.Contains(messages, message => message.Contains("send is off"));
    }

    [Fact]
    public void PatchedOptionalEntryLeavesTheWindowOn()
    {
        var natives = RetailChatCatalogue.Validate(Client(patched: RetailChatCatalogue.DeactivateChatEntry), _ => { });

        Assert.False(natives.Enter);
        Assert.True(natives.ChatWindow);
    }

    // A running client that matches the catalogue, with one address's first byte changed as a runtime patch would.
    private static Func<uint, int, byte[]> Client(uint? patched = null)
    {
        var bytes = new Dictionary<uint, byte[]>();
        foreach (var entry in new[]
                 {
                     RetailChatCatalogue.Takeover, RetailChatCatalogue.Incoming, RetailChatCatalogue.Send, RetailChatCatalogue.Timestamp,
                     RetailChatCatalogue.Channels, RetailChatCatalogue.Fellowship, RetailChatCatalogue.Enter
                 }.SelectMany(list => list).Where(entry => entry.Address != null))
            bytes[entry.Address!.Value] = entry.ExpectedBytes;
        return (address, count) =>
        {
            var copy = bytes[address].Take(count).ToArray();
            if (address == patched) copy[0] = 0xE9;
            return copy;
        };
    }
}
