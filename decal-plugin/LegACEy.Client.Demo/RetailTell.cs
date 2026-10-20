namespace LegACEy.Client.Demo;

/// <summary>
/// The retail tell command a plugin sends: "/t recipient, text". A recipient or text that is blank, has a line break, or a recipient with a comma,
/// gives no command, so nothing is sent.
/// </summary>
public static class RetailTell
{
    private static readonly char[] LineBreaks = { '\r', '\n' };

    /// <returns>the command to send, or null when the pair can't be sent as a tell</returns>
    public static string? Command(string? recipient, string? text)
    {
        if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(text)) return null;
        var name = recipient!.Trim();
        var body = text!.Trim();
        if (name.IndexOf(',') >= 0 || name.IndexOfAny(LineBreaks) >= 0 || body.IndexOfAny(LineBreaks) >= 0) return null;
        return $"/t {name}, {body}";
    }
}
