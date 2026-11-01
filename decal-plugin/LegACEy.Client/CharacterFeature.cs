using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The Dereth character window in place of retail's character panel (Attributes, Skills, Titles). Retail's panel is parked while it is
/// open, as the inventory's is, so retail's key and toolbar button open and close our window; its close box closes retail's panel. The
/// character comes from the server's messages (<see cref="CharacterSheet"/>) and the values the player sees from the game. Raises, trains
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
        // Buffs come and go with no message the sheet reads, so an open window reads the game's values again about once a second.
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
            sharesPlaceWith: RetailPanelSlot.WindowPlace);
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

    string ICharacterGame.Name => ClientUiRuntime.Identity.CharacterName;
    string ICharacterGame.Race => ClientUiRuntime.Identity.Race;
    string ICharacterGame.Gender => ClientUiRuntime.Identity.Gender;

    // The values as retail's character window shows them: base without enchantments, buffed with them (and vitae, for skills and vitals).
    // A value the client cannot read is null, and the window shows the server's.
    StatValue? ICharacterGame.Value(StatKind kind, uint id) =>
        Read(kind, id, raw: true, out var @base) && Read(kind, id, raw: false, out var buffed) ? new StatValue(@base, buffed) : null;

    // A vital is its maximum, by ACE's id: 1, 3 or 5.
    private bool Read(StatKind kind, uint id, bool raw, out int value) => kind switch
    {
        StatKind.Vital => NativeVitals.TryRead(id, raw, _log, out value),
        StatKind.Attribute => NativeCharacterStats.TryReadAttribute(id, raw, _log, out value),
        _ => NativeCharacterStats.TryReadSkill(id, raw, _log, out value)
    };

    // A vital's current value: the maximum's id is one below it in ACE's numbering.
    int ICharacterGame.Current(uint vital) => NativeVitals.TryRead(vital + 1, raw: false, _log, out var current) ? current : 0;

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
}
