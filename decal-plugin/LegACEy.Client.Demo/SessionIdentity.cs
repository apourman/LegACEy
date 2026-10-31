using System;

namespace LegACEy.Client.Demo;

/// <summary>What a server message did to the session.</summary>
public enum SessionChange
{
    None,
    /// <summary>The described character entered the world (Login_CreatePlayer), before portal space: its session starts.</summary>
    Entered,
    /// <summary>The server let the character go (Login_LogOffCharacter), at the end of the logout animation. A refused logoff sends none.</summary>
    LoggedOff
}

/// <summary>
/// The character playing and its server, from the server's own messages as retail receives them. ACE sends the world name with the
/// character list (Login_WorldInfo), the character's PlayerDescription before Login_CreatePlayer, and Login_LogOffCharacter once it has
/// let the character go. The values outlive a logoff until the next character's description replaces them, so the windows closing at
/// logoff still save under the character that left.
/// </summary>
public sealed class SessionIdentity
{
    public const int WorldInfoMessage = 0xF7E1, PlayerCreateMessage = 0xF746, LogOffMessage = 0xF653;
    private const int GameEventMessage = 0xF7B0;
    private const uint NameProperty = 1, GenderProperty = 113, HeritageProperty = 188;
    private const uint IntTable = 0x01, Int64Table = 0x80, BoolTable = 0x02, DoubleTable = 0x04, StringTable = 0x10;

    // Heritage names by the server's HeritageGroup, as OpenAC's CharacterIdentityText names them (MIT).
    internal static readonly string[] HeritageNames =
    {
        string.Empty, "Aluvian", "Gharu'ndim", "Sho", "Viamontian", "Umbraen", "Gearknight", "Tumerok", "Lugian", "Empyrean", "Penumbraen",
        "Undead", "Olthoi", "Olthoi",
    };

    public uint CharacterId { get; private set; }
    public string CharacterName { get; private set; } = string.Empty;
    public string Server { get; private set; } = string.Empty;
    /// <summary>The heritage, such as "Viamontian"; empty when unknown.</summary>
    public string Race { get; private set; } = string.Empty;
    public string Gender { get; private set; } = string.Empty;

    /// <summary>Reads a server message, from its type on, as the message hook delivers it. A message cut short changes nothing.</summary>
    public SessionChange Receive(int messageType, ReadOnlySpan<byte> raw)
    {
        if (messageType is not (WorldInfoMessage or PlayerCreateMessage or LogOffMessage or GameEventMessage) || raw.Length < 4) return SessionChange.None;
        var reader = new MessageReader(raw.Slice(4));
        try
        {
            switch (messageType)
            {
                case WorldInfoMessage:
                    reader.Skip(8); // the players online and the most allowed
                    Server = reader.String();
                    return SessionChange.None;
                case PlayerCreateMessage:
                    // Only the described character: the windows load its places and settings, never the last one's.
                    return reader.UInt32() == CharacterId && CharacterName.Length > 0 ? SessionChange.Entered : SessionChange.None;
                case LogOffMessage:
                    return SessionChange.LoggedOff;
                default:
                    var character = reader.UInt32();
                    reader.Skip(4); // the event sequence
                    if (reader.UInt32() == CharacterSheet.PlayerDescriptionEvent) ReadDescription(character, reader);
                    return SessionChange.None;
            }
        }
        catch (ArgumentOutOfRangeException) { return SessionChange.None; }
    }

    // The description's property tables as ACE's GameEventPlayerDescription writes them; read whole before anything is set.
    private void ReadDescription(uint character, MessageReader reader)
    {
        var tables = reader.UInt32();
        reader.Skip(4); // the weenie type
        int heritage = 0, gender = 0;
        var name = string.Empty;
        if ((tables & IntTable) != 0)
            for (var count = reader.Header(); count > 0; count--)
            {
                var property = reader.UInt32();
                var value = reader.Int32();
                if (property == HeritageProperty) heritage = value;
                else if (property == GenderProperty) gender = value;
            }
        if ((tables & Int64Table) != 0) reader.Skip(reader.Header() * 12);
        if ((tables & BoolTable) != 0) reader.Skip(reader.Header() * 8);
        if ((tables & DoubleTable) != 0) reader.Skip(reader.Header() * 12);
        if ((tables & StringTable) != 0)
            for (var count = reader.Header(); count > 0; count--)
            {
                var property = reader.UInt32();
                var value = reader.String();
                if (property == NameProperty) name = value;
            }
        CharacterId = character;
        CharacterName = name;
        Race = heritage > 0 && heritage < HeritageNames.Length ? HeritageNames[heritage] : string.Empty;
        Gender = gender switch { 1 => "Male", 2 => "Female", _ => string.Empty };
    }
}
