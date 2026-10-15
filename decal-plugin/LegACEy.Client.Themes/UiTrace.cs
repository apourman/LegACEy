using System;

namespace LegACEy.Client.Themes;

// ponytail: diagnostic for the toolbar release crash; remove once the cause is known.
public static class UiTrace
{
    public static Action<string>? Log;
}
