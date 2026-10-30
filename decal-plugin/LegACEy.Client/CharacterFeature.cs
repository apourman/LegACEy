using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The Dereth character window in place of retail's character panel (Attributes, Skills, Titles). Retail's panel is parked while it is
/// open, as the inventory's is, so retail's key and toolbar button open and close our window; its close box closes retail's panel. The
/// character comes from the server's messages (<see cref="CharacterSheet"/>) and the values the player sees from Decal. Raises, trains
/// and the display title go out as retail's own game actions. Call everything on the game thread.
/// </summary>
internal sealed class CharacterFeature : ICharacterGame, IDisposable
{
    // ACE's GameActionType: RaiseVital, RaiseAttribute, RaiseSkill, TrainSkill and TitleSet.
    private const uint RaiseVitalAction = 0x0044, RaiseAttributeAction = 0x0045, RaiseSkillAction = 0x0046, TrainSkillAction = 0x0047, SetTitleAction = 0x002C;
    private const string FoldsKey = "client-character-folds";
    private const long RefreshMs = 1000;

    private readonly ILegACEyPluginHost _host;
    private readonly RetailPanelTakeover? _panel;
    private readonly Func<CharacterTables> _tables;
    private readonly Func<IReadOnlyDictionary<uint, string>> _titleNames;
    private readonly Func<GameChannelTransport?> _transport;
    private readonly IWindowPositionStore _store;
    private readonly Func<(string Server, string Character)> _who;
    private readonly Action<string> _log;
    private CharacterWindow? _view;
    private Action? _hide;
    private bool _shown;
    private long _refreshedAt;
    private bool _statFailed;

    public CharacterFeature(ILegACEyPluginHost host, IRetailPanelPort? port, Func<CharacterTables> tables, Func<IReadOnlyDictionary<uint, string>> titleNames,
        Func<GameChannelTransport?> transport, IWindowPositionStore store, Func<(string Server, string Character)> who, Action<string> log)
    {
        _host = host;
        _tables = tables;
        _titleNames = titleNames;
        _transport = transport;
        _store = store;
        _who = who;
        _log = log;
        if (port != null) _panel = new RetailPanelTakeover(port, OpenChanged, log);
        Sheet.Changed += () => _view?.SheetChanged();
    }

    public CharacterSheet Sheet { get; } = new();

    /// <summary>Once a frame in game. With <paramref name="enabled"/> false retail's panel is given back, and our window closes.</summary>
    public void Tick(bool enabled, bool canShow)
    {
        _panel?.Tick(enabled, canShow);
        // Buffs come and go with no message the sheet reads, so an open window reads Decal's values again about once a second.
        var now = Environment.TickCount64;
        if (_shown && _view != null && now - _refreshedAt >= RefreshMs)
        {
            _refreshedAt = now;
            _view.Refresh();
        }
    }

    /// <summary>Logoff, unload or the windows failing: gives retail's panel back and drops the window. The sheet is the caller's to clear.</summary>
    public void EndSession()
    {
        _panel?.EndSession();
        _view = null;
        _hide = null;
        _shown = false;
    }

    public void Dispose() => _panel?.Dispose();

    private void OpenChanged(bool open)
    {
        if (open == _shown) return;
        if (!open)
        {
            _shown = false;
            _hide?.Invoke();
            return;
        }
        try { Open(); }
        catch (Exception exception)
        {
            _log($"The character window could not open: {exception.Message}");
            _panel?.Close();
        }
    }

    private void Open()
    {
        var art = (IGameArtSource?)null;
        try { art = _host.Art; }
        catch (InvalidOperationException) { }
        // A window the client released is neither open nor hidden: the view is stale, so a new one is built.
        _view ??= new CharacterWindow(Sheet, this, art, _tables, _titleNames, LoadFolds(), SaveFolds);
        var view = _view;
        var place = _panel?.HeldPlace ?? new Point(1500, 150);
        var definition = new WindowDefinition(CharacterWindow.WindowId, "Character", CharacterWindow.Width, CharacterWindow.Height,
            DerethWindow.TitleBarHeight, new DerethClientTheme(), CharacterWindow.Sizing,
            sharesPlaceWith: RetailPanelSlot.WindowPlace, dragsFromContent: true);
        var opened = _host.OpenWindow(definition, place, close =>
        {
            _hide = close;
            // The close box closes retail's panel, whose report hides the window; without the takeover it hides the window itself.
            view.Close = () =>
            {
                if (_panel?.Holds == true) _panel.Close();
                if (_shown)
                {
                    _shown = false;
                    _hide?.Invoke();
                }
            };
            return view.Window;
        }, error =>
        {
            _log($"The character window failed: {error.Message}");
            if (ReferenceEquals(_view, view)) _view = null;
            _shown = false;
            _panel?.Close();
        }, ownChrome: true);
        if (!opened)
        {
            _panel?.Close();
            return;
        }
        _shown = true;
        _refreshedAt = Environment.TickCount64;
        view.Refresh();
    }

    private IEnumerable<CharacterGroup> LoadFolds()
    {
        var (server, character) = _who();
        var bits = _store.Load(server, character, FoldsKey)?.Location.X ?? 0;
        return Enum.GetValues(typeof(CharacterGroup)).Cast<CharacterGroup>().Where(group => (bits & (1 << (int)group)) != 0);
    }

    private void SaveFolds(IReadOnlyCollection<CharacterGroup> folded)
    {
        var (server, character) = _who();
        var bits = folded.Aggregate(0, (sum, group) => sum | (1 << (int)group));
        try { _store.Save(server, character, FoldsKey, (new Point(bits, 0), null)); }
        catch (Exception exception) { _log($"The character window's folds could not be saved: {exception.Message}"); }
    }

    // The game, as the window reads it.

    string ICharacterGame.Name => Text(BridgeText.CharacterName);
    string ICharacterGame.Race => Text(BridgeText.Race);
    string ICharacterGame.Gender => Text(BridgeText.Gender);

    StatValue? ICharacterGame.Value(StatKind kind, uint id)
    {
        // A vital's maximum (ACE's 1, 3 or 5) from the client, as retail's character window shows it: base without spells, buffed with them.
        if (kind == StatKind.Vital && NativeVitals.TryRead(id, raw: true, _log, out var unbuffed) && NativeVitals.TryRead(id, raw: false, _log, out var buffedMaximum))
            return new StatValue(unbuffed, buffedMaximum);
        try
        {
            // Decal numbers the vitals' maximums 2, 4 and 6; ACE 1, 3 and 5.
            var (@base, buffed) = Bridge.Stat((int)kind, kind == StatKind.Vital ? id + 1 : id);
            return new StatValue(@base, buffed);
        }
        catch (COMException exception)
        {
            if (!_statFailed) _log($"Decal could not read a character value ({kind} {id}); the window shows the server's: {exception.Message}");
            _statFailed = true;
            return null;
        }
    }

    int ICharacterGame.Current(uint vital)
    {
        try
        {
            return Bridge.Fact(vital switch { 1 => BridgeFact.Health, 3 => BridgeFact.Stamina, _ => BridgeFact.Mana });
        }
        catch (COMException) { return 0; }
    }

    bool ICharacterGame.Raise(StatKind kind, uint id, uint experience) =>
        Send(kind switch { StatKind.Attribute => RaiseAttributeAction, StatKind.Vital => RaiseVitalAction, _ => RaiseSkillAction }, id, experience);

    bool ICharacterGame.Train(uint skill, int credits) => Send(TrainSkillAction, skill, (uint)credits);

    bool ICharacterGame.SetTitle(uint title) => Send(SetTitleAction, title);

    private bool Send(uint action, params uint[] fields)
    {
        var payload = new byte[fields.Length * 4];
        for (var i = 0; i < fields.Length; i++) BitConverter.GetBytes(fields[i]).CopyTo(payload, i * 4);
        var sent = _transport()?.SendAction(action, payload) == true;
        if (!sent) _log($"Could not send character action 0x{action:X4}: the game connection is not ready.");
        return sent;
    }

    private static string Text(BridgeText text)
    {
        try { return Bridge.Text(text); }
        catch (COMException) { return string.Empty; }
    }
}
