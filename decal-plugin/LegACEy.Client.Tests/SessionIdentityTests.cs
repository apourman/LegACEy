using LegACEy.Client.Demo;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class SessionIdentityTests
{
    private const uint Character = 0x50000001, Other = 0x50000002;

    internal static byte[] WorldInfo(string server) =>
        new CharacterWindowTests.Writer().U32(SessionIdentity.WorldInfoMessage).U32(3).U32(-1).String(server).ToArray();

    /// <summary>A PlayerDescription as ACE writes it, cut to the tables before the strings plus a title after the name.</summary>
    internal static byte[] Description(uint character, string name, int heritage = 4, int gender = 2)
    {
        var w = new CharacterWindowTests.Writer();
        w.U32(0xF7B0).U32(character).U32(1).U32(CharacterSheet.PlayerDescriptionEvent);
        w.U32(0x01 | 0x80 | 0x02 | 0x04 | 0x10).U32(0x2B);
        w.Header(3).U32(25).U32(5).U32(188).U32(heritage).U32(113).U32(gender);
        w.Header(1).U32(1).I64(11056);
        w.Header(1).U32(104).U32(1);
        w.Header(1).U32(2).F64(1.5);
        w.Header(2).U32(1).String(name).U32(5).String("Adventurer");
        return w.ToArray();
    }

    private static byte[] PlayerCreate(uint character) => new CharacterWindowTests.Writer().U32(SessionIdentity.PlayerCreateMessage).U32(character).ToArray();

    [Fact]
    public void The_described_character_enters_on_its_server_and_the_server_letting_it_go_ends_the_session()
    {
        var identity = new SessionIdentity();
        Assert.Equal(SessionChange.None, identity.Receive(SessionIdentity.WorldInfoMessage, WorldInfo("Coldeve")));
        // Login_CreatePlayer before any description starts nothing: the windows would load no one's places.
        Assert.Equal(SessionChange.None, identity.Receive(SessionIdentity.PlayerCreateMessage, PlayerCreate(Character)));

        Assert.Equal(SessionChange.None, identity.Receive(0xF7B0, Description(Character, "+Aerbax")));
        Assert.Equal((Character, "+Aerbax", "Coldeve", "Viamontian", "Female"),
            (identity.CharacterId, identity.CharacterName, identity.Server, identity.Race, identity.Gender));
        Assert.Equal(SessionChange.Entered, identity.Receive(SessionIdentity.PlayerCreateMessage, PlayerCreate(Character)));

        // The names outlive the logoff, so the windows closing then save under the character that left.
        Assert.Equal(SessionChange.LoggedOff, identity.Receive(SessionIdentity.LogOffMessage, new byte[] { 0x53, 0xF6, 0, 0 }));
        Assert.Equal("+Aerbax", identity.CharacterName);

        // A relog onto another character enters only once that character is described.
        Assert.Equal(SessionChange.None, identity.Receive(SessionIdentity.PlayerCreateMessage, PlayerCreate(Other)));
        identity.Receive(0xF7B0, Description(Other, "Rand", heritage: 1, gender: 1));
        Assert.Equal(SessionChange.Entered, identity.Receive(SessionIdentity.PlayerCreateMessage, PlayerCreate(Other)));
        Assert.Equal(("Rand", "Aluvian", "Male"), (identity.CharacterName, identity.Race, identity.Gender));
    }

    [Fact]
    public void A_description_cut_short_changes_nothing()
    {
        var identity = new SessionIdentity();
        identity.Receive(0xF7B0, Description(Character, "Aerbax"));
        var cut = Description(Other, "Rand");
        Assert.Equal(SessionChange.None, identity.Receive(0xF7B0, cut[..^4]));
        Assert.Equal((Character, "Aerbax"), (identity.CharacterId, identity.CharacterName));
    }
}
