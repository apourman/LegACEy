using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The Dereth magic window in place of retail's magic panel (Spellbook, Components), held as the quests panel is: retail's key and toolbar
/// button open and close our window, and its close box closes retail's panel. The spellbook comes from the server's messages
/// (<see cref="MagicState"/>) and the portal's spell and component tables; the filters, Delete and component levels send retail's own game
/// actions, and the spell bar takes spells through retail's own notice (<see cref="MagicNatives"/>). Call everything on the game thread.
/// </summary>
internal sealed class MagicFeature : IMagicGame, IDisposable
{
    // ACE's GameActionType: SpellbookFilter, RemoveSpellC2S and SetDesiredComponentLevel.
    private const uint SpellbookFilterAction = 0x0286, RemoveSpellAction = 0x01A8, SetDesiredComponentAction = 0x0224;
    // ACE's Player.FociWCIDs: the focus that lets each school (ACE's MagicSchool) cast with scarabs and prismatic tapers.
    private static readonly Dictionary<uint, int> Foci = new() { [1] = 15271, [2] = 15270, [3] = 15269, [4] = 15268, [5] = 43173 };
    private static readonly Dictionary<uint, string> SchoolNames = new()
    {
        [1] = "War Magic", [2] = "Life Magic", [3] = "Item Enchantment", [4] = "Creature Enchantment", [5] = "Void Magic"
    };

    private readonly ILegACEyPluginHost _host;
    private readonly RetailPanelTakeover? _panel;
    private readonly Func<IReadOnlyDictionary<uint, SpellInfo>> _spells;
    private readonly Func<SpellComponentTable> _components;
    private readonly Func<GameChannelTransport?> _transport;
    private readonly Func<InventorySnapshot> _inventory;
    private readonly Func<AppraisalContent, bool> _examine;
    private readonly Func<bool> _overSpellBar;
    private readonly Action _endTargetMode;
    private readonly Action<string> _log;
    private MagicWindow? _view;
    private Action? _hide;
    private bool _shown;

    public MagicFeature(ILegACEyPluginHost host, IRetailPanelPort? port, Func<IReadOnlyDictionary<uint, SpellInfo>> spells,
        Func<SpellComponentTable> components, Func<GameChannelTransport?> transport, Func<InventorySnapshot> inventory,
        Func<AppraisalContent, bool> examine, Func<bool> overSpellBar, Action endTargetMode, Action<string> log)
    {
        _endTargetMode = endTargetMode;
        _host = host;
        _spells = spells;
        _components = components;
        _transport = transport;
        _inventory = inventory;
        _examine = examine;
        _overSpellBar = overSpellBar;
        _log = log;
        if (port != null) _panel = new RetailPanelTakeover(port, OpenChanged, log);
        State.Changed += Refresh;
    }

    public MagicState State { get; } = new();

    /// <summary>Once a frame in game. With <paramref name="enabled"/> false retail's panel is given back, and our window closes.</summary>
    public void Tick(bool enabled, bool canShow) => _panel?.Tick(enabled, canShow);

    /// <summary>The packs changed: the Components tab's counts and the formulas' foci follow them.</summary>
    public void Refresh()
    {
        if (_shown) _view?.Refresh();
    }

    /// <summary>Logoff, unload or the windows failing: gives retail's panel back and drops the window.</summary>
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
            _log($"The magic window could not open: {exception.Message}");
            _panel?.Close();
        }
    }

    private void Open()
    {
        IGameArtSource? art = null;
        try { art = _host.Art; }
        catch (InvalidOperationException) { }
        var fresh = _view == null;
        _view ??= new MagicWindow(State, this, _spells, _components, art, _host.ItemDrag);
        var view = _view;
        var place = _panel?.HeldPlace ?? new Point(200, 150);
        var definition = new WindowDefinition(MagicWindow.WindowId, "Magic", MagicWindow.Width, MagicWindow.Height,
            DerethWindow.TitleBarHeight, new DerethClientTheme(), MagicWindow.Sizing,
            sharesPlaceWith: RetailPanelSlot.WindowPlace);
        var opened = _host.OpenWindow(definition, place, close =>
        {
            _hide = close;
            view.Close = () =>
            {
                if (_panel?.Holds == true) _panel.Close();
                if (!_shown) return;
                _shown = false;
                _hide?.Invoke();
            };
            return view.Window;
        }, error =>
        {
            _log($"The magic window failed: {error.Message}");
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
        if (!fresh) view.Refresh();
    }

    private bool Send(uint action, byte[] payload, string what)
    {
        var sent = _transport()?.SendAction(action, payload) == true;
        if (!sent) _log($"Could not {what}: the game connection is not ready.");
        return sent;
    }

    // The game, as the window reads it.

    IReadOnlyDictionary<uint, int> IMagicGame.CarriedComponents()
    {
        var components = _components();
        var carried = new Dictionary<uint, int>();
        foreach (var item in _inventory().Items)
        {
            var wcid = (uint)item.Wcid;
            if (components.ByWcid(wcid) == null) continue;
            carried[wcid] = (carried.TryGetValue(wcid, out var count) ? count : 0) + Math.Max(1, item.StackCount);
        }
        return carried;
    }

    bool IMagicGame.SendFilters(uint filters) => Send(SpellbookFilterAction, BitConverter.GetBytes(filters), "change the spellbook filters");

    bool IMagicGame.DeleteSpell(uint spell) => Send(RemoveSpellAction, BitConverter.GetBytes(spell), "delete the spell");

    bool IMagicGame.SendDesired(uint wcid, int amount) =>
        Send(SetDesiredComponentAction, BitConverter.GetBytes(wcid).Concat(BitConverter.GetBytes(amount)).ToArray(), "set the component amount");

    bool IMagicGame.AddToSpellBar(uint spell)
    {
        var added = MagicNatives.AddToSpellBar(spell, _log);
        if (!added) _log("The spell bar could not take the spell: switch the Magic window to retail to add it there.");
        return added;
    }

    bool IMagicGame.DropOnSpellBar(uint spell) => _overSpellBar() && ((IMagicGame)this).AddToSpellBar(spell);

    void IMagicGame.Examine(uint spell)
    {
        _endTargetMode();
        Examine(spell);
    }

    /// <summary>Shows the spell in the appraisal window, as a click in the spellbook does. False for a spell the portal does not know.</summary>
    public bool Examine(uint spell) => _spells().TryGetValue(spell, out var info) && _examine(Examination(info));

    /// <summary>
    /// The spell as the appraisal window shows it: its school and level, its mana and duration, its description, and the components the
    /// player casts it with: the account's tapers, or scarabs and prismatic tapers when a focus or Infused augmentation allows them.
    /// </summary>
    private AppraisalContent Examination(SpellInfo spell)
    {
        var focus = State.Infused.Contains(spell.School)
            || Foci.TryGetValue(spell.School, out var wcid) && _inventory().Items.Any(i => i.Wcid == wcid);
        var formula = focus ? SpellFormula.ScarabOnly(spell) : SpellFormula.ForAccount(spell, MagicNatives.AccountName(_log));
        var table = _components().Components;
        var icons = formula.Select(c => table.TryGetValue(c, out var component) ? component.Icon : 0).Where(i => i != 0).ToList();
        var level = SpellFormula.Level(spell);
        var school = SchoolNames.TryGetValue(spell.School, out var name) ? name : string.Empty;
        var type = level > 0 ? string.Format(CultureInfo.InvariantCulture, "Level {0} {1}", level, school) : school;
        var figures = new List<string> { string.Format(CultureInfo.InvariantCulture, "Mana Cost: {0}.", spell.BaseMana) };
        if (spell.Duration > 0)
            figures.Add(spell.Duration >= 60
                ? string.Format(CultureInfo.InvariantCulture, "Duration: {0} min.", (int)(spell.Duration / 60))
                : string.Format(CultureInfo.InvariantCulture, "Duration: {0} sec.", (int)spell.Duration));
        return new AppraisalContent(0, spell.Name, new ItemVisual(spell.Icon, 0, 0, 0), type, Array.Empty<AppraisalFigure>(),
            Array.Empty<AppraisalSection>(), spell.Description, null, Subtitle: string.Join("  ", figures), Formula: icons);
    }
}
