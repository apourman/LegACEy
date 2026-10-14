using System;

namespace LegACEy.Client.Demo;

/// <summary>
/// Whether shortcut row 2 is shown: one saved value per character, in the store the client keeps plugin settings in. A character with
/// no saved value has row 2 on.
/// </summary>
public sealed class ShortcutRowChoice
{
    private const string Key = "client-shortcut-row-2";
    private readonly IWindowPositionStore _store;

    public ShortcutRowChoice(IWindowPositionStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public bool IsOn(string server, string character) => _store.Load(server, character, Key) is not { Location.X: 0 };

    /// <summary>Saves whether row 2 is on for the character.</summary>
    public void Save(string server, string character, bool on) =>
        _store.Save(server, character, Key, (new System.Drawing.Point(on ? 1 : 0, 0), null));
}
