using LegACEy.Client.DecalPlugin;

namespace LegACEy.Client.Tests;

public sealed class RetailAttackCatalogueTests
{
    [Fact]
    public void A_client_that_matches_every_list_turns_the_whole_attack_bar_on()
    {
        Assert.Equal(new RetailAttackNatives(true, true, true, true, true), RetailAttackCatalogue.Validate(Client(), _ => { }));
    }

    [Fact]
    public void A_patched_height_setter_turns_only_the_attack_height_off_and_says_so()
    {
        var messages = new List<string>();

        var natives = RetailAttackCatalogue.Validate(Client(patched: RetailAttackCatalogue.SetRequestedAttackHeight), messages.Add);

        Assert.Equal(new RetailAttackNatives(false, true, true, true, true), natives);
        Assert.Contains(messages, message => message.Contains("attack bar height is off"));
    }

    // A running client that matches the catalogue, with one address's first byte changed as a runtime patch would.
    private static Func<uint, int, byte[]> Client(uint? patched = null)
    {
        var bytes = new Dictionary<uint, byte[]>();
        foreach (var entry in new[]
                 {
                     RetailAttackCatalogue.Height, RetailAttackCatalogue.SetPoint, RetailAttackCatalogue.Charge,
                     RetailAttackCatalogue.Options, RetailAttackCatalogue.Hold
                 }.SelectMany(list => list))
            // The PlayerSystem pointer has no expected bytes: any four readable bytes pass.
            bytes[entry.Address.Value] = entry.ExpectedBytes.Length == 0 ? new byte[4] : entry.ExpectedBytes;
        return (address, count) =>
        {
            var copy = bytes[address].Take(count).ToArray();
            if (address == patched) copy[0] = 0xE9;
            return copy;
        };
    }
}
