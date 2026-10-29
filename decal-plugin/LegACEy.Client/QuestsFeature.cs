using System;
using System.Collections.Generic;
using System.Drawing;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The Dereth quests window in place of retail's quest panel (Contracts, Journal, Page List), held as the social panel is: retail's key and
/// toolbar button open and close our window, and its close box closes retail's panel. The contracts come from the server's messages
/// (<see cref="QuestState"/>) and the portal's contract table; Abandon sends retail's own game action. The journal is the character's own
/// file beside the plugin (<see cref="QuestJournal"/>), loaded at the first open of a session and saved as it changes. Call everything on the
/// game thread.
/// </summary>
internal sealed class QuestsFeature : IQuestsGame, IDisposable
{
    private const long RefreshMs = 1000;
    // ACE's GameActionType.AbandonContract.
    private const uint AbandonContractAction = 0x0316;

    private readonly ILegACEyPluginHost _host;
    private readonly RetailPanelTakeover? _panel;
    private readonly Func<IReadOnlyDictionary<uint, ContractInfo>> _contracts;
    private readonly Func<GameChannelTransport?> _transport;
    private readonly Func<RadarPosition?> _player;
    private readonly Func<(string Server, string Character)> _session;
    private readonly string _directory;
    private readonly Action<string> _log;
    private QuestsWindow? _view;
    private QuestJournal? _journal;
    private string? _journalPath;
    private Action? _hide;
    private bool _shown;
    private long _refreshedAt;

    public QuestsFeature(ILegACEyPluginHost host, IRetailPanelPort? port, Func<IReadOnlyDictionary<uint, ContractInfo>> contracts,
        Func<GameChannelTransport?> transport, Func<RadarPosition?> player, Func<(string Server, string Character)> session, string directory,
        Action<string> log)
    {
        _host = host;
        _contracts = contracts;
        _transport = transport;
        _player = player;
        _session = session;
        _directory = directory;
        _log = log;
        if (port != null) _panel = new RetailPanelTakeover(port, OpenChanged, log);
        State.Changed += () => { if (_shown) _view?.Refresh(); };
    }

    public QuestState State { get; } = new();

    /// <summary>Once a frame in game. With <paramref name="enabled"/> false retail's panel is given back, and our window closes.</summary>
    public void Tick(bool enabled, bool canShow)
    {
        _panel?.Tick(enabled, canShow);
        // Timers count down with no message, so an open window draws again about once a second, and an edited journal is saved then.
        var now = Environment.TickCount64;
        if (now - _refreshedAt < RefreshMs) return;
        _refreshedAt = now;
        if (_shown) _view?.Refresh();
        SaveJournal();
    }

    /// <summary>Logoff, unload or the windows failing: saves the journal, gives retail's panel back and drops the window.</summary>
    public void EndSession()
    {
        SaveJournal();
        _panel?.EndSession();
        _view = null;
        _journal = null;
        _hide = null;
        _shown = false;
    }

    public void Dispose() => _panel?.Dispose();

    private void OpenChanged(bool open)
    {
        if (open == _shown) return;
        if (!open)
        {
            Hide();
            return;
        }
        try { Open(); }
        catch (Exception exception)
        {
            _log($"The quests window could not open: {exception.Message}");
            _panel?.Close();
        }
    }

    private void Hide()
    {
        _shown = false;
        _hide?.Invoke();
        SaveJournal();
    }

    private void Open()
    {
        if (_journal == null)
        {
            var (server, character) = _session();
            _journalPath = QuestJournal.OwnPath(_directory, server, character);
            _journal = QuestJournal.Load(_journalPath, QuestJournal.RetailPath(server, character), _log);
        }
        IGameArtSource? art = null;
        try { art = _host.Art; }
        catch (InvalidOperationException) { }
        var fresh = _view == null;
        _view ??= new QuestsWindow(State, _journal, this, _contracts, art);
        var view = _view;
        var place = _panel?.HeldPlace ?? new Point(200, 150);
        var definition = new WindowDefinition(QuestsWindow.WindowId, "Quests", QuestsWindow.Width, QuestsWindow.Height,
            DerethWindow.TitleBarHeight, new DerethClientTheme(), QuestsWindow.Sizing);
        var opened = _host.OpenWindow(definition, place, close =>
        {
            _hide = close;
            view.Close = () =>
            {
                if (_panel?.Holds == true) _panel.Close();
                if (_shown) Hide();
            };
            return view.Window;
        }, error =>
        {
            _log($"The quests window failed: {error.Message}");
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

    private void SaveJournal()
    {
        if (_journal is { Dirty: true } journal && _journalPath != null) journal.Save(_journalPath, _log);
    }

    // The game, as the window reads it.

    double IQuestsGame.Time => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    (double North, double East)? IQuestsGame.Coordinates => _player() is { } here ? (here.North, here.East) : null;

    bool IQuestsGame.Abandon(uint contract)
    {
        var sent = _transport()?.SendAction(AbandonContractAction, BitConverter.GetBytes(contract)) == true;
        if (!sent) _log("Could not abandon the contract: the game connection is not ready.");
        return sent;
    }
}
