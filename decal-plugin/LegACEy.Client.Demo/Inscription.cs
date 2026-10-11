using System;

namespace LegACEy.Client.Demo;

/// <summary>Whether the inscription field takes text: written by the character, or read only.</summary>
public enum InscriptionMode { ReadOnly, Editable }

/// <summary>
/// The inscription field at the foot of the appraisal window: its mode, the text on the item and the name of who wrote it. A null
/// <see cref="AppraisalContent.Inscription"/> means there is no field.
/// </summary>
public sealed record AppraisalInscription(InscriptionMode Mode, string Text, string Scribe)
{
    /// <summary>
    /// Retail's maximum inscription length: MaxCharacters (property 0x1E) of the inscription field, element 0x1000013E of the examination
    /// layout 0x2100006B in the installed client_local_English.dat. The server does not check the length.
    /// </summary>
    public const int MaxLength = 300;

    // The server's property ids: PropertyBool.Inscribable, PropertyString.Inscription and PropertyString.ScribeName.
    private const uint InscribableBool = 22, InscriptionString = 7, ScribeNameString = 8;

    /// <summary>
    /// The field for an appraisal, or null when the item is neither inscribable nor inscribed. It follows the server's rule for the
    /// set-inscription action (GameActionSetInscription and Player_Inventory.HandleActionSetInscription): the item must be carried (in the
    /// character's packs or worn), inscribable, and either unscribed or scribed by this character. The server also lets admins overwrite
    /// any inscription; the client does not offer that. The server's account check is not in the appraisal, so the scribe's name stands
    /// in for it.
    /// </summary>
    public static AppraisalInscription? From(AppraisalResponse response, string characterName, bool carried)
    {
        var inscribable = response.Bools.TryGetValue(InscribableBool, out var flag) && flag;
        var text = response.Strings.TryGetValue(InscriptionString, out var inscription) ? inscription : string.Empty;
        var scribe = response.Strings.TryGetValue(ScribeNameString, out var name) ? name : string.Empty;
        if (!inscribable && text.Length == 0 && scribe.Length == 0) return null;
        var editable = carried && inscribable
            && (string.IsNullOrWhiteSpace(scribe) || string.Equals(scribe, characterName, StringComparison.Ordinal));
        return new AppraisalInscription(editable ? InscriptionMode.Editable : InscriptionMode.ReadOnly, text, scribe);
    }
}

/// <summary>
/// Retail's Writing_SetInscription game action (0x00BF): the item's id, then the text as a 16-bit length string padded to a multiple of four
/// bytes, as the server reads it (GameActionSetInscription). The game action frame around it is <c>GameChannelTransport.GameActionFrame</c>.
/// </summary>
public static class InscriptionAction
{
    public const uint Opcode = 0x00BF;

    /// <summary>
    /// The payload for an item and its new text. Empty text erases the inscription. The text is one byte a character, as the appraisal
    /// reader reads it; a character past Latin-1 is written as '?'.
    /// </summary>
    public static byte[] Payload(uint itemId, string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++) bytes[i] = text[i] <= 0xFF ? (byte)text[i] : (byte)'?';
        // The padding makes the length and the text a multiple of four bytes: the length's two bytes count.
        var padding = (4 - (2 + bytes.Length) % 4) % 4;
        var payload = new byte[4 + 2 + bytes.Length + padding];
        payload[0] = (byte)itemId;
        payload[1] = (byte)(itemId >> 8);
        payload[2] = (byte)(itemId >> 16);
        payload[3] = (byte)(itemId >> 24);
        payload[4] = (byte)bytes.Length;
        payload[5] = (byte)(bytes.Length >> 8);
        Array.Copy(bytes, 0, payload, 6, bytes.Length);
        return payload;
    }
}
