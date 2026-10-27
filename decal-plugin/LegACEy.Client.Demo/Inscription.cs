using System;
using System.Text;

namespace LegACEy.Client.Demo;

/// <summary>Whether the inscription field takes text: written by the character, or read only.</summary>
public enum InscriptionMode { ReadOnly, Editable }

/// <summary>
/// The inscription field at the foot of the appraisal window: its mode, the text on the item and the name of who wrote it, and for an
/// editable field <see cref="Writer"/>, the character who would sign it. A null <see cref="AppraisalContent.Inscription"/> means there is
/// no field.
/// </summary>
public sealed record AppraisalInscription(InscriptionMode Mode, string Text, string Scribe, string Writer = "")
{
    /// <summary>
    /// Retail's maximum inscription length, a constant confirmed against MaxCharacters (property 0x1E) of the inscription field, element
    /// 0x1000013E of the examination layout 0x2100006B in the installed client_local_English.dat. It is not read from the DAT at runtime.
    /// The server does not check the length.
    /// </summary>
    public const int MaxLength = 300;

    // The server's property ids: PropertyBool.Inscribable, PropertyString.Inscription and PropertyString.ScribeName.
    private const uint InscribableBool = 22, InscriptionString = 7, ScribeNameString = 8;
    // ObjectDescriptionFlag.Inscribable, from the object's create message: ACE's appraisal never sends PropertyBool.Inscribable (it is not an
    // assessment property), so this flag is how retail, and the window, know a blank item can be inscribed.
    public const uint InscribableFlag = 0x2;

    /// <summary>
    /// The field for an appraisal, or null when the item is neither inscribable nor inscribed. It follows the server's rule for the
    /// set-inscription action (GameActionSetInscription and Player_Inventory.HandleActionSetInscription): the item must be carried (in the
    /// character's packs or worn), inscribable, and either unscribed or scribed by this character. The server also lets admins overwrite
    /// any inscription; the client does not offer that. The server's account check is not in the appraisal (it drops ScribeAccount for a
    /// non-admin examiner), so the scribe's name stands in for it. A same-name character on another account is refused silently by the
    /// server (HandleActionSetInscription sends no reply for a refusal), so the field stays editable and the save does nothing.
    /// </summary>
    public static AppraisalInscription? From(AppraisalResponse response, string characterName, bool carried, uint descriptionFlags = 0)
    {
        var inscribable = (descriptionFlags & InscribableFlag) != 0 || (response.Bools.TryGetValue(InscribableBool, out var flag) && flag);
        var text = response.Strings.TryGetValue(InscriptionString, out var inscription) ? inscription : string.Empty;
        var scribe = response.Strings.TryGetValue(ScribeNameString, out var name) ? name : string.Empty;
        if (!inscribable && text.Length == 0 && scribe.Length == 0) return null;
        var editable = carried && inscribable
            && (string.IsNullOrWhiteSpace(scribe) || string.Equals(scribe, characterName, StringComparison.Ordinal));
        return editable
            ? new AppraisalInscription(InscriptionMode.Editable, text, scribe, characterName)
            : new AppraisalInscription(InscriptionMode.ReadOnly, text, scribe);
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
    /// The payload for an item and its new text. Empty text erases the inscription. The text is UTF-8 and the length is its number of
    /// characters: the server reads it with a UTF-8 BinaryReader and ReadChars(length), so a byte count would read past the text when a
    /// character takes more than one byte. Every character is kept.
    /// </summary>
    public static byte[] Payload(uint itemId, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        // The padding makes the length and the text a multiple of four bytes: the length's two bytes count.
        var padding = (4 - (2 + bytes.Length) % 4) % 4;
        var payload = new byte[4 + 2 + bytes.Length + padding];
        payload[0] = (byte)itemId;
        payload[1] = (byte)(itemId >> 8);
        payload[2] = (byte)(itemId >> 16);
        payload[3] = (byte)(itemId >> 24);
        payload[4] = (byte)text.Length;
        payload[5] = (byte)(text.Length >> 8);
        Array.Copy(bytes, 0, payload, 6, bytes.Length);
        return payload;
    }
}
