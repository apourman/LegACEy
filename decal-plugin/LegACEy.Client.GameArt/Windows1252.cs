namespace LegACEy.Client.GameArt;

/// <summary>
/// Windows-1252 text as the server and the spell table write it. Bytes 0x80-0x9F are the code page's symbols (the apostrophe in
/// "Asheron's" is 0x92), where Latin-1 has control characters.
/// </summary>
public static class Windows1252
{
    // The code page for bytes 0x80-0x9F. The five undefined bytes keep their control characters, as the code page does.
    private const string High =
        "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F" +
        "\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    /// <summary>The character a byte stands for in Windows-1252.</summary>
    public static char ToChar(byte value) => value is >= 0x80 and <= 0x9F ? High[value - 0x80] : (char)value;
}
