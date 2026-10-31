using System;
using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>
/// How opaque each LegACEy window draws, in percent: one saved value per window and character, in the store the client keeps plugin settings
/// in. A window with no value of its own (a plugin's, or one never set) draws at the All windows value. The draw reads it every frame, so
/// values are kept once read.
/// </summary>
public sealed class WindowOpacities
{
    public const int Faintest = 10, Opaque = 100, Step = 5;
    private const string AllKey = "window-opacity-all", KeyPrefix = "window-opacity:";
    private readonly IWindowPositionStore _store;
    private readonly Dictionary<(string Server, string Character, string Key), int?> _read = new();

    public WindowOpacities(IWindowPositionStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>The All windows value, opaque before one is saved.</summary>
    public int All(string server, string character) => Read(server, character, AllKey) ?? Opaque;

    /// <summary>The window's own value, or the All windows value.</summary>
    public int Of(string server, string character, string windowId) => Read(server, character, KeyPrefix + windowId) ?? All(server, character);

    public void Set(string server, string character, string windowId, int percent) => Write(server, character, KeyPrefix + windowId, percent);

    /// <summary>Sets the All windows value. Windows with their own value keep it until the caller sets them too.</summary>
    public void SetAll(string server, string character, int percent) => Write(server, character, AllKey, percent);

    /// <summary>Whether a row of the store is an opacity, so a UI layout can carry these rows and none of the store's other settings.</summary>
    public static bool IsKey(string key) => key == AllKey || key.StartsWith(KeyPrefix, StringComparison.Ordinal);

    /// <summary>Drops the values read so far, after something other than this class changed the store's opacity rows.</summary>
    public void Forget() => _read.Clear();

    /// <summary>An opacity kept within the bounds and rounded to the step.</summary>
    public static int Clamp(int percent) => Math.Max(Faintest, Math.Min(Opaque, (int)Math.Round(percent / (double)Step) * Step));

    private int? Read(string server, string character, string key)
    {
        if (_read.TryGetValue((server, character, key), out var kept)) return kept;
        var value = _store.Load(server, character, key) is { Location.X: var saved } ? Clamp(saved) : (int?)null;
        _read[(server, character, key)] = value;
        return value;
    }

    private void Write(string server, string character, string key, int percent)
    {
        var value = Clamp(percent);
        _read[(server, character, key)] = value;
        _store.Save(server, character, key, (new System.Drawing.Point(value, 0), null));
    }
}
