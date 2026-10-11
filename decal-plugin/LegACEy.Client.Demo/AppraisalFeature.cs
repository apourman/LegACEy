using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The appraisal window and the responses behind it. It keeps the server's appraisal of each object it has seen, and opens the window for
/// the item the player asked to appraise (right-click, through <see cref="AppraisingInventoryPort"/>) when that item's response arrives.
/// Any other response, including a plugin's silent ID, only caches; it refreshes the window when the window already shows that object.
/// </summary>
public sealed class AppraisalFeature
{
    /// <summary>The window's id, and the one window the feature reuses for every appraisal.</summary>
    public const string WindowId = "appraisal";

    // The cache is for the current character only; logoff clears it, and it starts over when it grows past this many objects.
    private const int CacheLimit = 64;
    // A request is answered by the server at once. One that has not been answered this long is stale, and its item's later
    // response (a plugin's silent ID, say) does not open the window.
    private const long RequestTimeoutMs = 10_000;
    private static readonly Point DefaultLocation = new(240, 100);

    private readonly ILegACEyPluginHost _host;
    private readonly Action<string> _log;
    private readonly Func<long> _clock;
    private readonly IAppraisalGame _game;
    private readonly Dictionary<uint, AppraisalResponse> _responses = new();
    private uint _requested;
    private long _requestedAt;
    private uint _shownId;
    private AppraisalWindow? _view;

    /// <param name="clock">The time in milliseconds, for timing requests out. Null uses the system's tick count.</param>
    /// <param name="game">The character, the inventory and the inscription action. Null means none: nothing is editable and nothing is sent.</param>
    public AppraisalFeature(ILegACEyPluginHost host, Action<string> log, Func<long>? clock = null, IAppraisalGame? game = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? new Func<long>(() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        _game = game ?? NoAppraisalGame.Instance;
    }

    /// <summary>What the window shows: the content of the last appraisal the window opened for, or null when it has opened for none.</summary>
    public AppraisalContent? Shown { get; private set; }

    /// <summary>The last response for an object this session, or null when none has arrived.</summary>
    public AppraisalResponse? Response(uint objectId) => _responses.TryGetValue(objectId, out var response) ? response : null;

    /// <summary>The player asked to appraise this item. A new request replaces the one still waiting.</summary>
    public void Request(uint objectId)
    {
        _requested = objectId;
        _requestedAt = _clock();
    }

    /// <summary>
    /// Takes one 0x00C9 payload: the object's id, then its appraisal. The first response for the requested item opens the window and clears
    /// the request. Nothing thrown while reading or showing it escapes: it is logged, and the client's other features carry on.
    /// </summary>
    public void Receive(byte[] payload)
    {
        try
        {
            var response = AppraisalResponse.Read(payload);
            if (_responses.Count >= CacheLimit) _responses.Clear();
            _responses[response.ObjectId] = response;
            var requested = response.ObjectId == _requested && _clock() - _requestedAt <= RequestTimeoutMs;
            if (response.ObjectId == _requested) _requested = 0;
            if (requested || (response.ObjectId == _shownId && _host.IsWindowOpen(WindowId))) Show(response);
        }
        catch (Exception exception)
        {
            _log($"Could not show an appraisal: {exception.Message}");
        }
    }

    /// <summary>Logoff: the object ids and the window's content belong to the character that left, so the window closes too.</summary>
    public void Clear()
    {
        _responses.Clear();
        _requested = 0;
        _shownId = 0;
        Shown = null;
        _view = null;
        _host.CloseWindow(WindowId);
    }

    private void Show(AppraisalResponse response)
    {
        var content = AppraisalContent.From(response, _game.CharacterName, _game.IsCarried(response.ObjectId));
        var art = _host.Art;
        // One window serves the session: it takes the next appraisal in place while the content fits its height. A height change
        // releases the window and builds a new one, because a window's size cannot change once it is open.
        if (_view == null || !_view.TryShow(content, art, Inscribe))
        {
            _host.CloseWindow(WindowId);
            _view = AppraisalWindow.Create(content, art, Inscribe);
        }
        var view = _view;
        // An open window already shows the content. A hidden one comes back as it was, with the content just swapped in.
        if (!_host.IsWindowOpen(WindowId))
        {
            var definition = new WindowDefinition(WindowId, "Appraisal", AppraisalWindow.Width, view.Height, DerethWindow.TitleBarHeight,
                new DerethClientTheme());
            var opened = _host.OpenWindow(definition, DefaultLocation, close =>
            {
                // The close box ends any request still waiting: the player has stopped looking at the window.
                view.Close = () =>
                {
                    _requested = 0;
                    close();
                };
                return view.Window;
            }, error => _log($"The appraisal window failed: {error.Message}"), ownChrome: true);
            // The window never opened, so the view is dropped: it was not handed to the client.
            if (!opened)
            {
                _view = null;
                return;
            }
        }
        _shownId = response.ObjectId;
        Shown = content;
    }

    /// <summary>
    /// Saves an inscription on an item and returns whether it was sent. The server sends no reply, so the item is appraised again: that
    /// response shows the saved text, or the old text when the server refused it.
    /// </summary>
    private bool Inscribe(uint itemId, string text)
    {
        // Like Receive, nothing thrown while sending escapes: it is logged, and the window keeps the typed text.
        try
        {
            if (!_game.SetInscription(itemId, text))
            {
                _log("Could not send the inscription: the server channel is not available.");
                return false;
            }
            _game.RequestAppraisal(itemId);
            return true;
        }
        catch (Exception exception)
        {
            _log($"Could not save the inscription: {exception.Message}");
            return false;
        }
    }
}

/// <summary>
/// What the appraisal window needs from the game: the character's name and whether an item is carried, for the inscription rule, and the
/// two things it asks of the game to save an inscription.
/// </summary>
public interface IAppraisalGame
{
    /// <summary>The character's name, as the server writes it as a scribe's name.</summary>
    string CharacterName { get; }
    /// <summary>Whether the item is in the character's packs or worn, as the inventory has it.</summary>
    bool IsCarried(uint itemId);
    /// <summary>Sends retail's set-inscription action for an item. Returns false when it could not be sent.</summary>
    bool SetInscription(uint itemId, string text);
    /// <summary>Asks the server to appraise an item again, silently: the item is not selected and no window is asked for.</summary>
    void RequestAppraisal(uint itemId);
}

/// <summary>The game when there is none: no character, nothing carried, nothing sent.</summary>
internal sealed class NoAppraisalGame : IAppraisalGame
{
    public static readonly NoAppraisalGame Instance = new();

    private NoAppraisalGame()
    {
    }

    public string CharacterName => string.Empty;
    public bool IsCarried(uint itemId) => false;
    public bool SetInscription(uint itemId, string text) => false;
    public void RequestAppraisal(uint itemId) { }
}

/// <summary>
/// The inventory port as the client gives it to plugins and windows. Every command is the bridge port's; an Assess also asks the appraisal
/// window to show the item when its response arrives, so a right-click opens the window and nothing else does.
/// </summary>
public sealed class AppraisingInventoryPort : IInventoryPort
{
    private readonly IInventoryPort _inner;
    private readonly AppraisalFeature _appraisals;

    public AppraisingInventoryPort(IInventoryPort inner, AppraisalFeature appraisals)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _appraisals = appraisals ?? throw new ArgumentNullException(nameof(appraisals));
    }

    public InventorySnapshot Snapshot => _inner.Snapshot;

    public event Action? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public void OpenContainer(uint containerId) => _inner.OpenContainer(containerId);
    public void Select(uint itemId) => _inner.Select(itemId);
    public void Use(uint itemId) => _inner.Use(itemId);

    public void Assess(uint itemId)
    {
        _appraisals.Request(itemId);
        _inner.Assess(itemId);
    }

    public void DropOnGround(uint itemId) => _inner.DropOnGround(itemId);
    public void MoveToContainer(uint itemId, uint containerId, int slotIndex) => _inner.MoveToContainer(itemId, containerId, slotIndex);
    public uint WieldMask(uint itemId, PaperdollSlot slot) => _inner.WieldMask(itemId, slot);
    public void Wield(uint itemId, PaperdollSlot slot) => _inner.Wield(itemId, slot);
    public void MergeStack(uint itemId, uint targetStackId) => _inner.MergeStack(itemId, targetStackId);
}
