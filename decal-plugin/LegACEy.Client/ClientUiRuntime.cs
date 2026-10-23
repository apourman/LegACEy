using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Media;
using IOPath = System.IO.Path;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.InputRouter;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

namespace LegACEy.Client.DecalPlugin;

/// <summary>The client runtime: wires the Decal events the bridge raises to the netstandard client UI framework.</summary>
internal sealed class ClientUiRuntime : IClientUiHost, ILegACEyPluginHost, IClientEvents, IAppraisalGame
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(IntPtr cursor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    // WM_SETCURSOR, the hit-test code for the client area, and the standard size cursors' resource ids (MAKEINTRESOURCE).
    private const int WmSetCursor = 0x0020;
    private const int HtClient = 1;
    private const int IdcSizeWE = 32644;
    private const int IdcSizeNS = 32645;
    private const int IdcSizeNWSE = 32642;
    private const int IdcSizeNESW = 32643;
    private const int IdcSizeAll = 32646;
    // The retail cursors in the portal DAT, all 32×32 with the hotspot in the middle.
    private const uint DatCursorNS = 0x06005E66;
    private const uint DatCursorWE = 0x06006128;
    private const uint DatCursorNWSE = 0x06006126;
    private const uint DatCursorNESW = 0x06006127;
    private const uint DatCursorMove = 0x06006119;
    // Beside the DLL: while this file exists, retail's inventory panel stays retail's. Without it the LegACEy UI takes it over.
    private const string RetailPanelSwitchFile = "retail-inventory-native";
    // Beside the DLL: while this file exists, retail's examination panel stays retail's. Without it the appraisal window takes it over.
    // Creating it from the Settings card also hands an open appraisal to retail's panel.
    private const string RetailAppraisalSwitchFile = "retail-appraisal-native";
    // Beside the DLL: while this file exists, retail's toolbar stays retail's. Without it the Dereth toolbar replaces it.
    private const string RetailToolbarSwitchFile = "retail-toolbar-native";
    // Beside the DLL: while this file exists, retail's chat windows stay retail's. Without it the Dereth chat window replaces them.
    private const string RetailChatSwitchFile = "retail-chat-native";
    // Beside the DLL: while this file exists, retail's vitals windows stay retail's. Without it the Dereth vitals window replaces them.
    private const string RetailVitalsSwitchFile = "retail-vitals-native";
    // Beside the DLL: while this file exists, retail's radar stays retail's. Without it the Dereth radar replaces it.
    private const string RetailRadarSwitchFile = "retail-radar-native";
    // Beside the DLL: while this file exists, retail's attack panel stays retail's. Without it the Dereth attack bar replaces it in melee and missile.
    private const string RetailAttackSwitchFile = "retail-attack-native";
    // A window switch whose file could not be written or deleted this session: its state (true for LegACEy's) until the file can be changed.
    // Runtime only, so the next session reads the file again.
    private readonly Dictionary<string, bool> _switchHeld = new(StringComparer.Ordinal);

    // The /water setting, one for every character: percent and on (1) or off (0).
    private const string WaterKey = "client-water";
    private const int SystemChat = 5;
    private WaterChoice _water = WaterChoice.Default;
    // How big LegACEy windows show against their design size, in percent: the player's UI size from the Settings window, kept for every
    // character. Drag icons keep retail's 32 pixels. An Avalonia that cannot scale panels shows them
    // at design size rather than failing every window.
    private const string UiSizeKey = "client-ui-size";
    private const int DefaultUiSize = 85;
    private int _uiSize = DefaultUiSize;
    private bool _uiSizeChanged;
    // Set by Reset window positions in the Settings window, applied on the next frame, outside that window's click.
    private bool _positionsResetRequested;
    private double WindowScale => AvaloniaPanel.CanScale ? _uiSize / 100.0 : 1;

    // The release build stamps the client with the plugin version, so the menu shows which release is installed.
    private static readonly string MenuTitle = $"LegACEy {typeof(ClientUiRuntime).Assembly.GetName().Version?.ToString(3)}";
    private const string MenuWindowId = "plugin-menu";
    private const string SettingsWindowId = "dereth-settings";
    // The Settings window's last tab, kept for the install like the UI size.
    private const string SettingsTabKey = "settings-tab";
    // The per-character options kept in the position store. Reset window positions keeps these; every other row there is a window's place.
    private static readonly string[] PositionOptionKeys = { RetailChatWindows.TextSizeKey, RetailChatWindows.FadeKey };
    private const string TestFailureFile = "fail-post-ui-draw";
    // The stub's folder, which the bridge sets before the client starts: the log, settings and Plugins\ live there.
    internal static string PluginDirectory { get; set; } = ClientDirectory;
    // This client's own folder, the runtime folder beside the stub.
    private static string ClientDirectory => IOPath.GetDirectoryName(typeof(ClientUiRuntime).Assembly.Location)!;
    // Every client appends to the same log beside the DLL; the process id tells their lines apart.
    private static readonly int ProcessId = Environment.ProcessId;

    private IntPtr _device;
    private static readonly System.Drawing.Color ResizeOutline = System.Drawing.Color.FromArgb(0xC9, 0xA4, 0x5C);
    private PortalDat? _portal;
    private readonly Dictionary<ModelView, ModelRenderer> _modelRenderers = new();
    private readonly HashSet<ModelView> _failedModelViews = new();
    private readonly HashSet<ModelView> _drawnModelViews = new();
    private readonly List<ModelView> _releasedModelViews = new();
    private WindowManager? _windows;
    // Kept for the session, not rebuilt with the windows: its session overlay holds what a failed write could not save.
    private readonly FileWindowPositionStore _positionStore = new(IOPath.Combine(PluginDirectory, "window-positions.txt"));
    // The settings the open Settings window shows, so a UI size change made there reaches it.
    private ClientSettings? _settings;
    // Each plugin's one settings value, per character, in its own file: a row keyed by the plugin's name whose X is the value.
    private readonly FileWindowPositionStore _settingsStore = new(IOPath.Combine(PluginDirectory, "plugin-settings.txt"));
    // The chat tabs each window of each character has: a row per server, character and window, missing means the six defaults.
    private readonly FileRetailChatTabStore _chatTabs = new(IOPath.Combine(PluginDirectory, "chat-tabs.txt"), Log);
    private readonly ShortcutRowChoice _row2Choice;
    // The window whose frame shows a hovered corner, and the corner it shows.
    private string? _cornerWindowId;
    private DerethCorner _cornerApplied;
    private readonly Dictionary<uint, IntPtr> _datCursors = new();
    private readonly Dictionary<int, IntPtr> _systemCursors = new();
    private PostUiDrawHook? _postUiDrawHook;
    private bool _windowsEnabled;
    private bool _firstPostUiWindow = true;
    private DateTime _lastMeasurementLog = DateTime.UtcNow;
    private DateTime _lastTestFailureCheck;
    private readonly FrameSamples _uiFrameTimes = new();
    private readonly FrameSamples _postUiDrawTimes = new();
    // The parts of a frame, timed separately to show where the worst frames go.
    private readonly PhaseTimes _phaseTimes = new();
    private long _lastFrameEndBytes;
    private long _betweenFrameBytes;
    private long _lastAllocatedBytes = GC.GetTotalAllocatedBytes();
    private TimeSpan _lastGcPause = GC.GetTotalPauseDuration();
    private ScreenSurface? _hovered;
    private readonly InputRouterService _inputRouter = new();
    private Point _pointer;
    // The Dereth toolbar over retail's toolbar: its port, window, surface and takeover, held only while the switch is on.
    // Keeps only the selected object's answers, so combat traffic does not grow it.
    private readonly SelectionMeters _selectionMeters = new(() => (uint)Bridge.Fact(BridgeFact.CurrentSelection));
    private NativeRetailToolbarPort? _toolbarPort;
    private RetailToolbar? _toolbar;
    private ScreenSurface? _toolbarSurface;
    private RetailTakeoverLifecycle? _toolbarTakeover;
    private ToolbarPlacePort? _toolbarPlace;
    // Whether this character's saved toolbar place has been read since login.
    private bool _toolbarPlaceLoaded;
    private bool _toolbarFailed;
    // The offset from the toolbar's corner to the pointer while its frame is dragged; null when it is not.
    private Size? _toolbarDragOffset;
    private bool _toolbarSwitch;
    private bool _toolbarReleaseLogged;
    // The Dereth toolbar's surface id. It is not a managed window, so drops reach it by its surface.
    private const string ToolbarSurfaceId = "toolbar";
    // The selected stack's split tab: its own surface over the toolbar's plaque, shown while a stack is selected.
    private const string SplitTabSurfaceId = "toolbar-split";
    private StackSplitTab? _splitTab;
    private ScreenSurface? _splitTabSurface;
    // Shortcut rows 1 and 2, inside the toolbar under its buttons while the takeover holds, and their refresh on inventory changes.
    private ShortcutBar? _shortcutBar;
    private ShortcutBar? _shortcutRow2;
    // The slot a shortcut drag started from, shared by both rows: a drop on either row moves the shortcut.
    private readonly ShortcutDragSource _shortcutDrag = new();
    private Action? _unsubscribeShortcutItems;
    private bool _inventoryWindowOpen;
    private DateTime _lastToolbarSwitchCheck;
    // The Dereth chat window over retail's chat: its feature checks, port, window and the held retail chat windows. Held while the switch is on.
    private const string ChatWindowId = RetailChatWindows.MainId;
    private RetailChatNatives? _chatNatives;
    private NativeRetailChatPort? _chatPort;
    private RetailChatWindows? _chatWindows;
    private RetailHeldWindows? _chatTakeover;
    private bool _chatFailed;
    private bool _chatReleaseLogged;
    // Set when the port that listens while retail's chat is shown failed; logoff clears it.
    private bool _chatListenFailed;
    private bool _chatSwitch;
    private DateTime _lastChatSwitchCheck;
    // The Dereth vitals window over retail's vitals: held while its switch is on. Its natives are checked once; the player's clock times its trails.
    private RetailVitalsTakeover? _vitals;
    private RetailHudNatives? _hudNatives;
    private bool _vitalsSwitch;
    private DateTime _lastVitalsSwitchCheck;
    private readonly Stopwatch _vitalsClock = Stopwatch.StartNew();
    // The layout the player picked on the vitals gear, kept per character; the window reopens in it on the next frame.
    private readonly VitalsLayoutChoice _vitalsLayouts;
    private bool _vitalsLayoutPicked;
    private int? _loggedVitae;
    private bool _vitaeReadLogged;
    private TimeSpan _nextVitaeRead;
    // The Dereth radar over retail's radar: its natives, held root, port, window and ground. Held while the switch is on.
    private const string RadarWindowId = "dereth-radar";
    private const string RadarOptionsKey = "dereth-radar-options";
    private NativeRadarPort? _radarPort;
    // The chat's history kept over a UI size rebuild, for the chat windows that open next; a logoff drops it.
    private RetailChatHistory? _carriedChatHistory;
    // Set while a Ctrl+C that copied chat text is down, so its character and key-up are kept from the game too.
    private bool _eatingCtrlC;
    // Where retail's radar stood when it was held; the Dereth radar opens there. Null when retail's radar was not found.
    private Point? _radarPlace;
    private RetailHeldWindows? _radarTakeover;
    private RetailRadarWindow? _radar;
    private Task<GroundDat?>? _radarDatLoad;
    private GroundTiles? _radarTiles;
    private Task<IndoorDat?>? _radarIndoorLoad;
    private IndoorPlanCache? _radarPlans;
    // The server's name for the place the player is in, asked on each landblock visit; asked only when hello lists place.name.
    private readonly RadarPlaceNames _radarPlaceNames = new();
    private bool _radarFailed;
    private bool _radarReleaseLogged;
    private bool _radarSwitch;
    private DateTime _lastRadarSwitchCheck;
    // The Dereth attack bar over retail's attack panel: whether its natives matched, the held panel and window, and the port the window reads.
    private bool? _attackNatives;
    private RetailAttackTakeover? _attack;
    private NativeAttackBarPort? _attackPort;
    private bool _attackSwitch;
    private DateTime _lastAttackSwitchCheck;
    private readonly GameStatePort _gameState = new(new GameStateSnapshot("unknown-character", "unknown-server", 0, 0, 0, 0, 0, 0));
    private readonly GameStatePoller _gameStatePoller;
    private ClientUiFramework? _clientUi;
    private GameChannelTransport? _channelTransport;
    private ServerChannelClient? _serverChannel;
    private RetailItemDrag? _retailDrag;
    // Plugin takeovers of retail panels (the inventory), and the switch that turns them on; the examination takeover has its own. The switch is a marker file beside the DLL,
    // as the post-UI test failure is. Creating the switch file gives retail's panels back; deleting it turns the takeovers on.
    private readonly List<RetailPanelTakeover> _retailPanels = new();
    private bool? _retailPanelsValid;
    private bool? _retailToolbarValid;
    private bool _retailPanelSwitch;
    private DateTime _lastRetailPanelSwitchCheck;
    private bool _retailAppraisalSwitch;
    private DateTime _lastAppraisalSwitchCheck;
    // The Appraisal card was switched: once the switch is read, a turn to retail hands the open appraisal over to retail's panel.
    private bool _appraisalSwitched;
    private RetailExamine? _retailExamine;
    // Retail's examination panel, taken over for the session when the native UI check passes; each showing is an appraisal in our window.
    private RetailExaminationTakeover? _examination;
    private readonly BridgeInventoryPort _inventory = new(Log);
    private readonly AppraisalFeature _appraisal;
    private RetailBusyCursor? _busyCursor;
    private WorldEffects? _worldEffects;
    private RetailTextMode? _textMode;
    // The inventory as plugins and windows get it: the bridge port, with an Assess also asking for the appraisal window.
    private readonly AppraisingInventoryPort _appraisingInventory;
    private uint _retailDragItem;
    // The world object a LegACEy window's own drag carries (an inventory item), or 0.
    private uint _ownDragItem;
    private string _retailDragName = string.Empty;
    private int _loggedDrops;
    // A drag handed from a LegACEy window to retail, from the press posted to the client until retail's drag starts.
    private sealed class HandOff
    {
        public IntPtr List, Item;
        public uint ItemId;
        public Point Press;
        public bool Pressed;
        public int Tries;
    }
    private HandOff? _handOff;
    private IntPtr _gameWindow;
    private int _loggedHandOffs;
    private uint _handOffMissed;
    // The retail element under the pointer over the 3D world, seen in play (chat reports 0x10000011).
    private const uint WorldViewElement = 0x1000049A;
    private ScreenSurface? _dragIconSurface;
    // our copy of the retail drag icon, drawn while the item is over a LegACEy window (the client draws its own below them)
    private IDisposable? _retailDragIcon;
    // a LegACEy item being dragged out of a window: the inventory cell under it shows the client's drop indicator
    private bool _itemDragActive;
    private readonly Dictionary<string, ScreenSurface> _featureSurfaces = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, ScreenSurface>> _preparing = new();
    // Plugin windows the player closed: kept, texture and all, so reopening skips building the content again.
    // ponytail: kept until logoff, still subscribed and running (the paperdoll rebuilds on equip changes); add a cap,
    // or tell plugins when they are shown and hidden, once more plugins arrive
    private readonly Dictionary<string, ScreenSurface> _hiddenSurfaces = new(StringComparer.Ordinal);
    // Error handlers of windows opened by plugins; a failure in one turns off its plugin, not the client.
    private readonly Dictionary<string, Action<Exception>> _windowFailures = new(StringComparer.Ordinal);
    private PluginRegistry? _plugins;
    // Every object's look as the game sends it, so the paperdoll works on any server.
    private readonly LookCache _lookCache = new();
    private readonly GameLooks _looks;
    private bool _inGame;
    // Kept apart from the UI, which may have failed: the plugin's own updates still check, and tell the player in chat.
    private readonly PluginUpdates _updates = new(PluginDirectory);
    private bool _loggedIn;
    private bool _noticeFailLogged;
    private bool _failed;

    public ClientUiRuntime()
    {
        _looks = new GameLooks(_lookCache, () => ((ILegACEyPluginHost)this).ServerChannel, action => _plugins?.SupportsAction(action) == true,
            () => (uint)Bridge.Fact(BridgeFact.CharacterId));
        _gameStatePoller = new GameStatePoller(_gameState, Bridge.ReadGameState,
            error => Log($"Character stats temporarily unavailable; keeping UI active and retrying: {error.Message}"));
        _appraisal = new AppraisalFeature(this, Log, spells: () => _portal?.ReadSpells(Log) ?? new Dictionary<uint, SpellInfo>(), game: this,
            names: () => _portal?.ReadCharacterNames(Log) ?? CharacterNames.None, measure: (control, available) => AvaloniaPanel.MeasureAt(control, available, WindowScale));
        _appraisingInventory = new AppraisingInventoryPort(_inventory, _appraisal,
            () => SwitchOn(RetailAppraisalSwitchFile, ref _lastAppraisalSwitchCheck, ref _retailAppraisalSwitch));
        _row2Choice = new ShortcutRowChoice(_settingsStore);
        _vitalsLayouts = new VitalsLayoutChoice(_settingsStore);
        if (_settingsStore.Load("", "", UiSizeKey) is { Location.X: var size and > 0 })
            _uiSize = Math.Clamp(size, UiSizeChoice.Smallest, UiSizeChoice.Largest);
        if (_settingsStore.Load("", "", WaterKey) is { Location: var water })
            _water = new WaterChoice(water.Y != 0, Math.Clamp(water.X, 0, WaterChoice.Most));
    }

    public void Startup()
    {
        // Before the plugins load, which may need it; a failed start leaves no handler behind.
        AssemblyLoadContext.Default.Resolving += ResolveFromClientDirectory;
        try
        {
            NativeUi.Initialize(Log);
            // Only with the examination's addresses checked: otherwise retail's examination panel is left alone.
            if (NativeUi.Ready && RetailPanelCatalogueValid())
            {
                var examinationPort = new NativeExaminationPort();
                _examination = new RetailExaminationTakeover(examinationPort, () => (uint)Bridge.Fact(BridgeFact.CurrentSelection),
                    _appraisal, Log, examinationPort.VerifyRoot);
            }
            else Log("Retail's examination panel is left to retail: the native UI or its addresses are not ready.");
            try
            {
                _channelTransport = new GameChannelTransport(NativeUi.ReadMemory, () => _inGame, Log);
                _serverChannel = new ServerChannelClient(_channelTransport);
                _serverChannel.Subscribe(StormProtocol.Changed, ShowStorm);
                _serverChannel.Subscribe(WeatherProtocol.Changed, ShowWeather);
                _serverChannel.Subscribe(CombatTextProtocol.Hit, ShowHit);
                _serverChannel.Subscribe(PingProtocol.Placed, ShowPing);
            }
            catch (Exception exception) { Log($"LegACEy server channel disabled: {exception.Message}"); }
            try { _retailDrag = new RetailItemDrag(NativeUi.ReadMemory, Log); }
            catch (Exception exception) { Log($"Item drag and drop disabled: {exception.Message}"); }
            try { _retailExamine = new RetailExamine(NativeUi.ReadMemory, Log); }
            catch (Exception exception) { Log($"Switching the appraisal window to retail won't reopen the item in retail's: {exception.Message}"); }
            try { _busyCursor = new RetailBusyCursor(NativeUi.ReadMemory, Log); }
            catch (Exception exception) { Log($"The busy cursor while an appraisal loads is off: {exception.Message}"); }
            try { _textMode = new RetailTextMode(NativeUi.ReadMemory, Log); }
            catch (Exception exception) { Log($"Keys typed in LegACEy text boxes also reach the game: {exception.Message}"); }
            try { _inventory.TargetMode = new RetailTargetMode(NativeUi.ReadMemory, Log); }
            catch (Exception exception) { Log($"Using an item on a target in LegACEy windows is off: {exception.Message}"); }
            LoadPlugins();
        }
        catch
        {
            AssemblyLoadContext.Default.Resolving -= ResolveFromClientDirectory;
            throw;
        }
    }

    /// <summary>Starts the plugins in Plugins/ beside the client. Each plugin's failures stay with that plugin.</summary>
    private void LoadPlugins()
    {
        _plugins = new PluginRegistry(this, Log);
        try
        {
            foreach (var plugin in PluginLoader.Load(IOPath.Combine(PluginDirectory, "Plugins"), Log))
                _plugins.Add(plugin);
        }
        catch (Exception exception) { Log($"LegACEy plugins unavailable: {exception}"); }
    }

    public void Shutdown()
    {
        try
        {
            _updates.Dispose();
            _postUiDrawHook?.Dispose();
            _postUiDrawHook = null;
            _worldEffects?.Dispose();
            _worldEffects = null;
            TearDown();
            _serverChannel?.Dispose();
            _serverChannel = null;
        }
        finally { AssemblyLoadContext.Default.Resolving -= ResolveFromClientDirectory; }
    }

    /// <summary>
    /// An assembly the runtime could not find, such as one a plugin references: serve the copy shipped with the client when it is the
    /// same or newer, and log every redirect. A copy that can't be read or loaded is logged and left to the runtime's own failure.
    /// </summary>
    private static Assembly? ResolveFromClientDirectory(AssemblyLoadContext context, AssemblyName requested)
    {
        if (requested.Name is not { } name) return null;
        var path = IOPath.Combine(ClientDirectory, IOPath.GetFileName(name) + ".dll");
        if (!File.Exists(path))
            return null;
        try
        {
            var shipped = AssemblyName.GetAssemblyName(path);
            if (requested.Version != null && shipped.Version < requested.Version)
                return null;

            Log($"Assembly redirect: {requested.FullName} -> {shipped.Version}");
            return context.LoadFromAssemblyPath(path);
        }
        catch (Exception exception)
        {
            Log($"Assembly redirect to {path} failed: {exception.Message}");
            return null;
        }
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(IOPath.Combine(PluginDirectory, "legacey-avalonia.log"), $"{DateTime.Now:O} [pid {ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break assembly resolution or the client callback that triggered it.
        }
    }

    // The open container and the selection are part of the inventory snapshot, whoever changed them.
    public void ItemSelected() => _inventory.MarkStale();
    public void ContainerOpened() => _inventory.MarkStale();

    /// <summary>
    /// /legacey (or @legacey) opens or closes the LegACEy menu, so it can be reached without the Dereth toolbar. /water on, off or a percent
    /// (0 to 200) sets the water effect, and /water alone says how it is set; the line is eaten either way, so the server never sees it.
    /// </summary>
    public bool CommandLine(string text)
    {
        if (_failed || !_inGame) return false;
        var command = text.Trim();
        if (command.Equals("/legacey", StringComparison.OrdinalIgnoreCase) || command.Equals("@legacey", StringComparison.OrdinalIgnoreCase))
        {
            Guard(ToggleMenuWindow);
            return true;
        }
        var space = command.IndexOf(' ');
        var name = space < 0 ? command : command[..space];
        if (!name.Equals("/water", StringComparison.OrdinalIgnoreCase) && !name.Equals("@water", StringComparison.OrdinalIgnoreCase))
            return false;
        if (_water.Apply(space < 0 ? "" : command[space..]) is not { } water)
        {
            WaterChat("Water: /water on, /water off or /water 0 to 200 (percent; 100 is the usual look).");
            return true;
        }
        SetWater(water);
        WaterChat(water.ToString());
        return true;
    }

    /// <summary>
    /// The water setting from /water or the Settings window's World tab: applied at once, saved for every character, and shown in an open
    /// Settings window. Nothing happens when it is unchanged.
    /// </summary>
    private void SetWater(WaterChoice water)
    {
        if (water == _water) return;
        _water = water;
        _settingsStore.Save("", "", WaterKey, (new Point(water.Percent, water.On ? 1 : 0), null));
        if (_worldEffects != null) _worldEffects.WaterStrength = water.Strength;
        if (_settings != null) _settings.Water = WaterOptionFor(water);
    }

    /// <summary>The Settings window's test weather: drawn around the player at once, kept for this session only.</summary>
    private void SetTestWeather(WeatherKind weather)
    {
        if (_worldEffects == null) return;
        _worldEffects.TestWeather = weather;
        if (_settings != null) _settings.TestWeather = new OptionChoice<WeatherKind>(weather, SetTestWeather);
    }

    /// <summary>The Settings window's test fog: the storm's ring around the player's landblock at once, kept for this session only.</summary>
    private void SetTestFog(bool on)
    {
        if (_worldEffects == null) return;
        _worldEffects.TestFog = on;
        if (_settings != null) _settings.TestFog = new OptionChoice<bool>(on, SetTestFog);
    }

    /// <summary>The World tab's water: the setting, and the change it asks for, with the strength kept within the bounds.</summary>
    private WaterOption WaterOptionFor(WaterChoice water) => new(water.On, water.Percent,
        (on, percent) => SetWater(new WaterChoice(on, Math.Clamp(percent, 0, WaterChoice.Most))));

    /// <summary>
    /// Retail's Lock UI for the Settings window's switch. It is greyed out when the lock's natives did not check, and it is read as retail
    /// reads it.
    /// </summary>
    private LockUiChoice LockUiOption()
    {
        var available = LockAvailable;
        return new LockUiChoice(available && NativeUi.IsUiLocked, available, SetUiLock);
    }

    /// <summary>Retail's Lock UI is set only once the window can read it and the Lock list has checked its calls.</summary>
    private bool LockAvailable => NativeUi.Ready && HudNatives().Lock;

    /// <summary>
    /// Sets retail's Lock UI from the Settings window, as the radar's lock button does, and shows the state retail now reads, so a refused
    /// change does not leave the switch wrong.
    /// </summary>
    private void SetUiLock(bool locked)
    {
        if (!LockAvailable) return;
        NativeUi.SetUiLocked(locked);
        ShowLockUi();
    }

    /// <summary>Shows retail's Lock UI as it reads now on the open switch, which redraws the panel.</summary>
    private void ShowLockUi()
    {
        if (_settings != null && _settings.LockUi is { } option) _settings.LockUi = option with { Locked = NativeUi.IsUiLocked };
    }

    /// <summary>While Settings is open, retail's Lock UI can change from its own options or the radar's button, so the switch follows it.</summary>
    private void TickSettingsLock()
    {
        if (_settings?.LockUi is not { Enabled: true } option) return;
        if (NativeUi.IsUiLocked != option.Locked) ShowLockUi();
    }

    // A chat line that fails only goes to the log: it is no reason to switch the UI off.
    private static void WaterChat(string line)
    {
        try { Bridge.Chat(line, SystemChat); }
        catch (Exception exception) { Log($"Could not write '{line}' to chat: {exception.Message}"); }
    }

    public void InventoryChanged(uint id, uint container)
    {
        _inventory.ObjectChanged(id, container);
        _toolbarPort?.ObjectChanged(id);
    }

    // InventoryPutObjInContainer, WieldObject and InventoryPutObjectIn3D (ACE's GameEventType).
    private static readonly uint[] InventoryMoveEvents = { 0x0022, 0x0023, 0x019A };

    /// <summary>LegACEy channel replies and pushes arrive on the game thread with every other server message.</summary>
    public void ServerMessage(int type, ReadOnlySpan<byte> raw)
    {
        if (_failed)
            return;
        // The appraisal response is a game event like any other, so it is read whether or not the LegACEy server channel is up.
        var appraisal = GameChannelTransport.GameEventPayload(type, raw, AppraisalResponse.IdentifyEvent);
        if (appraisal != null) Guard(() => _appraisal.Receive(appraisal));
        // The selection plaque's health and mana, which retail asks the server for itself when the selection changes.
        _selectionMeters.Receive(type, raw);
        try { _lookCache.Receive(type, raw); }
        catch (Exception exception) { Log($"Could not read an object's look: {exception}"); }
        // An item put in a pack, wielded or put in the world: Decal raised no event for some of these (a split stack, a pickup), so the
        // inventory is read again on the next frame.
        foreach (var moved in InventoryMoveEvents)
            if (GameChannelTransport.GameEventPayload(type, raw, moved) != null)
                _inventory.MarkStale();
        if (_serverChannel == null || _channelTransport == null)
            return;
        byte[]? payload;
        try { payload = _channelTransport.ReadEvent(type, raw); }
        catch (Exception exception)
        {
            Log($"Could not read a server channel event: {exception}");
            return;
        }
        if (payload != null)
            Guard(() => _serverChannel.Receive(payload));
    }

    /// <summary>Enable character-specific windows once Decal has finished loading character identity.</summary>
    public void LoginComplete()
    {
        _loggedIn = true;
        // A Log out and update whose logout never came through is forgotten, so this session's own logout doesn't close the game.
        _quitPending = false;
        _updates.Check();
        if (_failed || !NativeUi.Ready)
            return;

        Guard(() =>
        {
            if (_portal == null)
                CreateUi();
            if (_windows == null)
                CreateWindowManager();
            _clientUi ??= new ClientUiFramework(this, _gameState, CurrentTheme(), _serverChannel);
            _inGame = true;
            _inventory.MarkStale();
            PublishGameState();
            RequestServerActions();
        });
    }

    /// <summary>
    /// Asks the server which actions it has. Until the answer arrives, and when there is none, every plugin that needs
    /// a server action stays hidden.
    /// </summary>
    private void RequestServerActions()
    {
        _plugins?.SetServerActions(null);
        _radarPlaceNames.Reset(null, Array.Empty<string>());
        _serverChannel?.Request(ChannelHello.Action, Array.Empty<byte>(), reply =>
        {
            var actions = ServerActionsFrom(reply).ToArray();
            _plugins?.SetServerActions(actions);
            _radarPlaceNames.Reset(_serverChannel, actions);
            if (actions.Contains(StormProtocol.Zone))
                _serverChannel?.Request(StormProtocol.Zone, Array.Empty<byte>(), zone => { if (zone.Ok) ShowStorm(zone.Body); });
            // Only a server that answered without them has no storm or weather; a failed hello keeps what was remembered.
            else if (reply.Ok)
            {
                if (_worldEffects != null) _worldEffects.StormZone = null;
                Forget(LastStormFile);
            }
            if (actions.Contains(WeatherProtocol.Zone))
                _serverChannel?.Request(WeatherProtocol.Zone, Array.Empty<byte>(), weather => { if (weather.Ok) ShowWeather(weather.Body); });
            else if (reply.Ok)
            {
                if (_worldEffects != null) _worldEffects.Weather = null;
                Forget(LastWeatherFile);
            }
        });
    }

    // The server's last storm and weather replies, kept so a fresh client draws them from its first frame: the server's reply comes a
    // couple of seconds after the world is drawn, and until then retail's own sky would show. The reply corrects them.
    private const string LastStormFile = "last-storm.bin", LastWeatherFile = "last-weather.bin";

    /// <summary>The storm fog test's zone, from the server: the ring of landblocks around it fills with fog.</summary>
    private void ShowStorm(byte[] body)
    {
        try
        {
            if (_worldEffects != null) _worldEffects.StormZone = StormProtocol.ReadZone(body);
            Remember(LastStormFile, body);
        }
        catch (Exception exception) { Log($"Could not read the storm zone: {exception.Message}"); }
    }

    /// <summary>The weather test's weather, from the server: snow, a blizzard or a thunderstorm on the 3×3 landblocks around its landblock.</summary>
    private void ShowWeather(byte[] body)
    {
        try
        {
            if (_worldEffects != null) _worldEffects.Weather = WeatherProtocol.Read(body);
            Remember(LastWeatherFile, body);
        }
        catch (Exception exception) { Log($"Could not read the weather: {exception.Message}"); }
    }

    private static void Remember(string file, byte[] body)
    {
        try { File.WriteAllBytes(IOPath.Combine(PluginDirectory, file), body); }
        catch (Exception exception) { Log($"Could not keep {file}: {exception.Message}"); }
    }

    private static void Forget(string file)
    {
        try { File.Delete(IOPath.Combine(PluginDirectory, file)); }
        catch (Exception exception) { Log($"Could not remove {file}: {exception.Message}"); }
    }

    /// <summary>The storm and weather the server last sent, read back at start.</summary>
    private void ShowRemembered()
    {
        foreach (var (file, show) in new (string, Action<byte[]>)[] { (LastStormFile, ShowStorm), (LastWeatherFile, ShowWeather) })
        {
            var path = IOPath.Combine(PluginDirectory, file);
            if (!File.Exists(path)) continue;
            try { show(File.ReadAllBytes(path)); }
            catch (Exception exception) { Log($"Could not read {file}: {exception.Message}"); }
        }
    }

    /// <summary>A hit anywhere in sight, from the server: its number rises over the target.</summary>
    private void ShowHit(byte[] body)
    {
        try
        {
            var hit = CombatTextProtocol.ReadHit(body);
            _worldEffects?.Combat.Add(hit.Target, hit.Amount, hit.DamageType, hit.Critical, hit.Periodic);
        }
        catch (Exception exception) { Log($"Could not read a hit: {exception.Message}"); }
    }

    /// <summary>
    /// Sends a radar ping to the server, which shows it to the fellowship and back to this radar. A server without pings leaves it to this
    /// radar alone.
    /// </summary>
    private void Ping(RadarPosition at)
    {
        if (_plugins?.SupportsAction(PingProtocol.Place) != true || _serverChannel == null)
        {
            _radar?.ShowPing(SessionCharacter(), at);
            return;
        }
        _serverChannel.Request(PingProtocol.Place, PingProtocol.WritePlace(at), reply =>
        {
            if (!reply.Ok) Log($"Ping not sent: {reply.Message}");
        });
    }

    private void ShowPing(byte[] body)
    {
        try
        {
            var (sender, at) = PingProtocol.ReadPlaced(body);
            _radar?.ShowPing(sender, at);
        }
        catch (Exception exception) { Log($"Could not read a ping: {exception.Message}"); }
    }

    private static IEnumerable<string> ServerActionsFrom(ChannelReply reply)
    {
        if (!reply.Ok) return Array.Empty<string>();
        try { return ChannelHello.Read(reply.Body).Actions; }
        catch (Exception exception)
        {
            Log($"Could not read the server's action list; plugins that need the server stay hidden: {exception.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The server has let the character go. After Log out and update, this closes the game, as its window's × does, never by ending the
    /// process from under it, and the updater waiting for it installs once it has gone. Still to be seen in game: that Decal raises this
    /// Authorized logoff after our log out, and that retail closes cleanly on the WM_CLOSE.
    /// </summary>
    public void LogoffAuthorized()
    {
        if (!_quitPending) return;
        _quitPending = false;
        const int WmClose = 0x0010;
        if (_gameWindow == IntPtr.Zero || !PostMessage(_gameWindow, WmClose, IntPtr.Zero, IntPtr.Zero))
            Log($"Could not close the game to update: window {_gameWindow}");
    }

    public void Logoff()
    {
        Guard(() => ApplyReset(_inputRouter.Route(new NativeInputMessage(InputRouterService.WmLogoff, IntPtr.Zero, IntPtr.Zero), GetInputSurfaces())));
        Guard(() => _hovered?.Panel.PointerLeave());
        _inGame = false;
        _loggedIn = false;
        _carriedChatHistory = null;
        _chatListenFailed = false;
        // The next character reads its own toolbar place; until then the toolbar stands where retail's window is.
        _toolbarPlaceLoaded = false;
        if (_toolbarPlace != null) _toolbarPlace.Location = null;
        _inventory.Clear();
        _lookCache.Clear();
        Guard(() => _appraisal.Clear());
        Guard(() => _busyCursor?.Show(false));
        if (_worldEffects != null)
        {
            _worldEffects.Selected = 0;
            // The storm and weather stay for the next login, as at start (LastStormFile).
            _worldEffects.Combat.Clear();
        }
        Guard(() => _textMode?.Hold(false));
        try { _clientUi?.EndSession(); }
        catch (Exception exception) { Log($"Could not clean up client UI at logoff: {exception}"); }
        _clientUi = null;
        _settings = null;
        // Before the plugins close their windows: giving the panel back tells the plugin its window is no longer held.
        try { EndRetailPanels(); }
        catch (Exception exception) { Log($"Could not give the retail panels back at logoff: {exception}"); }
        try { ReleaseRetailToolbar(); }
        catch (Exception exception) { Log($"Could not give the retail toolbar back at logoff: {exception}"); }
        try { ReleaseRetailChat(); }
        catch (Exception exception) { Log($"Could not give the retail chat back at logoff: {exception}"); }
        // The Dereth vitals window closes and retail's vitals come back; the next login holds them again while the switch is on.
        _vitals?.Release();
        // The attack bar closes and retail's attack panel comes back; the next melee or missile holds it again while the switch is on.
        _attack?.Release();
        try { ReleaseRetailRadar(); }
        catch (Exception exception) { Log($"Could not give the retail radar back at logoff: {exception}"); }
        _toast?.Hide();
        ReleaseFeatureWindow(ToastWindowId);
        DropUpdateToast();
        try { _plugins?.EndSession(); }
        catch (Exception exception) { Log($"Could not close plugin windows at logoff: {exception}"); }
        // Windows released their requests above; anything still outstanding fails as disconnected.
        Guard(() => _serverChannel?.Reset());
        foreach (var window in _windows?.ZOrder.ToArray() ?? Array.Empty<ManagedWindow>())
            _windows!.Close(window.Id);
        _windows = null;
        _hovered = null;
        _handOff = null;
        _handOffMissed = 0;
        _ownDragItem = 0;
        _retailDragItem = 0;
        SetMenuRing(false);
    }

    private void CreateUi()
    {
        _device = GameDevice.Open();
        var acclient = Process.GetCurrentProcess().MainModule!.FileName;
        _portal = new PortalDat(IOPath.Combine(IOPath.GetDirectoryName(acclient)!, "client_portal.dat"));
        GameArtImageExtension.CurrentSource = _portal;

        EnsurePostUiDrawHook();
        try
        {
            _worldEffects ??= WorldEffects.Create(_device, _portal, NativeUi.ReadMemory, Log);
            if (_worldEffects != null) _worldEffects.WaterStrength = _water.Strength;
            ShowRemembered();
        }
        catch (Exception exception) { Log($"The selection outline and storm fog are off: {exception.Message}"); }
        Log("LegACEy UI ready.");
        WarmUpDerethTheme();
    }

    /// <summary>Lays out a throwaway Dereth window at login, so the first real one opens without a long frame.</summary>
    private static void WarmUpDerethTheme()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var panel = AvaloniaPanel.Create(DerethWarmUp.Sample, DerethWarmUp.Width, DerethWarmUp.Height);
            panel.ApplyTheme(new DerethClientTheme());
            panel.Tick();
        }
        catch (Exception exception)
        {
            Log($"Dereth warm-up failed: {exception.Message}");
            return;
        }
        Log($"Dereth warm-up took {stopwatch.ElapsedMilliseconds} ms.");
    }

    private void CreateWindowManager()
    {
        if (_device == IntPtr.Zero) return;
        if (!AvaloniaPanel.CanScale) Log("This Avalonia cannot scale panels; LegACEy windows show at their design size.");
        _windows = new WindowManager(GameDevice.ViewportSize(_device), _positionStore, SessionServer(), SessionCharacter(), WindowScale, () => NativeUi.IsUiLocked,
            ToolbarSnapTargets);
        _windowsEnabled = EnsurePostUiDrawHook();
    }

    private bool EnsurePostUiDrawHook()
    {
        // Entry rendering needs this before LoginComplete creates character windows.
        _postUiDrawHook ??= new PostUiDrawHook(DrawAfterRetailUi, Disable, Log);
        try
        {
            if (_postUiDrawHook.Install()) return true;
        }
        catch (Exception exception)
        {
            _postUiDrawHook.Dispose();
            _postUiDrawHook = null;
            DisableWindows(exception);
            return false;
        }
        Log("Post-UI drawing unavailable: checked retail EndScene hook could not be installed. Retail replacements use pre-UI drawing.");
        return false;
    }

    private IClientTheme CurrentTheme() => _portal != null ? new AcClientTheme(_portal) : new SimpleClientTheme();

    /// <summary>Opens or closes the LegACEy launcher window: the plugins' menu entries and a Settings… button.</summary>
    private void ToggleMenuWindow()
    {
        if (!_windowsEnabled || _windows == null || _clientUi == null)
            return;
        if (_postUiDrawHook?.HasRun != true)
        {
            Log("LegACEy windows unavailable: the installed retail EndScene hook has not run.");
            return;
        }
        if (_windows.Get(MenuWindowId) != null)
        {
            _clientUi.CloseWindow(MenuWindowId);
            SetMenuRing(false);
            return;
        }
        var window = new DerethWindow(MenuTitle, null, new PluginMenuPanel(_plugins!, _portal, () => OpenSettings(LoadSettingsTab())));
        window.CloseRequested += (_, _) =>
        {
            _clientUi?.CloseWindow(MenuWindowId);
            SetMenuRing(false);
        };
        _clientUi.OpenWindow(new WindowDefinition(MenuWindowId, MenuWindowId, PluginMenuPanel.WindowWidth, PluginMenuPanel.WindowHeight,
            DerethWindow.TitleBarHeight, new DerethClientTheme()), window, new Point(260, 120));
        SetMenuRing(true);
    }

    /// <summary>
    /// Opens the Settings window on the tab the player last used. It does nothing while the window is open, and while the windows cannot open
    /// yet.
    /// </summary>
    private void OpenSettings(SettingsTab tab)
    {
        if (_clientUi == null || !CanOpenWindows) return;
        if (_windows?.Get(SettingsWindowId) != null)
        {
            _windows.BringToFront(SettingsWindowId);
            return;
        }
        var settings = new ClientSettings(AvaloniaPanel.CanScale ? new UiSizeChoice(_uiSize, SetUiSize) : null, UpdateCheck(), WindowChoices(), WaterOptionFor(_water), LockUiOption())
        {
            Vitals = new OptionChoice<VitalsLayout>(_vitalsLayouts.Load(SessionServer(), SessionCharacter()), ChooseVitalsLayout),
            VitalsRetailBars = new OptionChoice<bool>(_vitalsLayouts.RetailBars(SessionServer(), SessionCharacter()), SetVitalsRetailBars),
            ShortcutRow2 = new OptionChoice<bool>(_row2Choice.IsOn(SessionServer(), SessionCharacter()), SetShortcutRow2),
            ChatTextSize = new OptionChoice<int>(RetailChatWindows.LoadTextSize(_positionStore, SessionServer(), SessionCharacter()), SetChatTextSize),
            ChatFade = new OptionChoice<int>(RetailChatWindows.LoadFadeSeconds(_positionStore, SessionServer(), SessionCharacter()), SetChatFadeSeconds),
            ChatActiveOpacity = new OptionChoice<int>(RetailChatWindows.LoadOpacity(_positionStore, SessionServer(), SessionCharacter(), RetailChatWindows.ActiveOpacityKey, RetailChatWindows.DefaultActiveOpacity), SetChatActiveOpacity),
            ChatInactiveOpacity = new OptionChoice<int>(RetailChatWindows.LoadOpacity(_positionStore, SessionServer(), SessionCharacter(), RetailChatWindows.InactiveOpacityKey, RetailChatWindows.DefaultInactiveOpacity), SetChatInactiveOpacity),
            ChatNameColour = new OptionChoice<int>(RetailChatWindows.LoadNameColour(_positionStore, SessionServer(), SessionCharacter()), SetChatNameColour),
            Radar = new OptionChoice<RadarOptions>(LoadRadarOptions(), SetRadarOptions),
            TestWeather = _worldEffects != null ? new OptionChoice<WeatherKind>(_worldEffects.TestWeather, SetTestWeather) : null,
            TestFog = _worldEffects != null ? new OptionChoice<bool>(_worldEffects.TestFog, SetTestFog) : null,
            ResetPositions = () => _positionsResetRequested = true,
        };
        _settings = settings;
        var window = new DerethWindow("Settings", null, new SettingsPanel(settings, tab, SaveSettingsTab, _plugins));
        window.CloseRequested += (_, _) =>
        {
            _clientUi?.CloseWindow(SettingsWindowId);
            _settings = null;
        };
        // First opened beside the launcher (260 + its 220 width), not over it.
        _clientUi.OpenWindow(new WindowDefinition(SettingsWindowId, "Settings", SettingsPanel.WindowWidth, SettingsPanel.WindowHeight,
            DerethWindow.TitleBarHeight, new DerethClientTheme()), window, new Point(500, 120));
    }

    private SettingsTab LoadSettingsTab() =>
        _settingsStore.Load("", "", SettingsTabKey) is { Location.X: var tab } && Enum.IsDefined(typeof(SettingsTab), tab) ? (SettingsTab)tab : SettingsTab.General;

    private void SaveSettingsTab(SettingsTab tab) => _settingsStore.Save("", "", SettingsTabKey, (new Point((int)tab, 0), null));

    /// <summary>The Settings window's update row: the installed version and Check for updates, greyed out with the reason on an install that never updates.</summary>
    private UpdateChoice UpdateCheck()
    {
        var off = _updates.Off;
        return new UpdateChoice(off == null ? $"Version {_updates.Installed}" : $"Version {_updates.Installed} ({off})", off == null, () => _updates.CheckNow());
    }

    /// <summary>
    /// The windows the Settings window switches between LegACEy's and retail's, each by its switch file beside the DLL, so the choice holds for
    /// every character on this install. Custom is the default; a change is read on the next frame.
    /// </summary>
    private IReadOnlyList<UiWindowChoice> WindowChoices() => new[]
    {
        Choice("Inventory", RetailPanelSwitchFile, () => _lastRetailPanelSwitchCheck = default),
        Choice("Appraisal", RetailAppraisalSwitchFile, () => { _lastAppraisalSwitchCheck = default; _appraisalSwitched = true; }),
        Choice("Toolbar", RetailToolbarSwitchFile, () => _lastToolbarSwitchCheck = default),
        Choice("Chat", RetailChatSwitchFile, () => _lastChatSwitchCheck = default),
        Choice("Vitals", RetailVitalsSwitchFile, () => _lastVitalsSwitchCheck = default),
        Choice("Radar", RetailRadarSwitchFile, () => _lastRadarSwitchCheck = default),
        Choice("Attack bar", RetailAttackSwitchFile, () => _lastAttackSwitchCheck = default)
    };

    private UiWindowChoice Choice(string title, string file, Action recheck)
    {
        var path = IOPath.Combine(PluginDirectory, file);
        return new UiWindowChoice(title, () => SwitchCustom(path), custom =>
        {
            try
            {
                if (custom) File.Delete(path);
                else File.WriteAllText(path, string.Empty);
                _switchHeld.Remove(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The card keeps the chosen state for this session, so it doesn't snap back to the file's.
                Log($"Could not switch the {title} window: {exception.Message}");
                _switchHeld[path] = custom;
            }
            recheck();
        });
    }

    /// <summary>Whether a switch's window is on LegACEy's: the file's absence, unless a write this session could not change it.</summary>
    private bool SwitchCustom(string path) =>
        _switchHeld.TryGetValue(path, out var held) ? held : !File.Exists(path);

    /// <summary>
    /// The UI size set from the Settings window, kept within the bounds and snapped to the step: saved now, and applied on the next frame,
    /// outside the window's own click, since applying it closes the window.
    /// </summary>
    private void SetUiSize(int percent)
    {
        var size = Math.Clamp((int)Math.Round((double)percent / UiSizeChoice.Step) * UiSizeChoice.Step, UiSizeChoice.Smallest, UiSizeChoice.Largest);
        if (size == _uiSize) return;
        _uiSize = size;
        _settingsStore.Save("", "", UiSizeKey, (new Point(size, 0), null));
        if (_settings != null) _settings.UiSize = new UiSizeChoice(size, SetUiSize);
        _uiSizeChanged = true;
    }

    /// <summary>
    /// Rebuilds the LegACEy UI at the new size, as a relog does: every window closes and the session's windows, toolbar, bars and chat
    /// open again at the new size, the menu with them. Plugin windows the player had open close.
    /// </summary>
    private void ApplyUiSize()
    {
        _uiSizeChanged = false;
        if (!_inGame) return;
        Log($"UI size {_uiSize}%: rebuilding the LegACEy UI.");
        Rebuild(resetPositions: false);
    }

    /// <summary>
    /// Puts this character's windows back at their first-open places, from Reset window positions in the Settings window: the windows rebuild
    /// as a UI size change does, with their saved places forgotten, and the Settings window comes back on General.
    /// </summary>
    private void ResetPositions()
    {
        _positionsResetRequested = false;
        if (!_inGame) return;
        Log("Window positions reset: rebuilding the LegACEy UI.");
        Rebuild(resetPositions: true);
    }

    /// <summary>
    /// Closes every window and opens the session's again, as a relog does. With <paramref name="resetPositions"/> the saved window places are
    /// forgotten once the windows have closed, since closing saves where they stood, and the Settings window reopens on General rather than
    /// on the tab it was on.
    /// </summary>
    private void Rebuild(bool resetPositions)
    {
        var menuOpen = _windows?.Get(MenuWindowId) != null;
        var settingsOpen = _windows?.Get(SettingsWindowId) != null;
        // The chat's lines and sent text outlive the rebuild: it is the same character still playing.
        var chatHistory = _chatWindows?.History ?? _carriedChatHistory;
        Logoff();
        if (resetPositions) ForgetWindowPlaces();
        _carriedChatHistory = chatHistory;
        LoginComplete();
        if (menuOpen) Guard(ToggleMenuWindow);
        // The Settings window comes back on the tab it was on, which the tab store keeps; after a reset, on General.
        if (settingsOpen) Guard(() => OpenSettings(resetPositions ? SettingsTab.General : LoadSettingsTab()));
    }

    /// <summary>
    /// Forgets this character's saved window places and keeps its options. A failure is logged: if the position file can't be read, nothing
    /// changes; if it can't be written, the places are forgotten for this session only. The rebuild goes on either way.
    /// </summary>
    private void ForgetWindowPlaces()
    {
        try
        {
            if (_positionStore.ForgetPlaces(SessionServer(), SessionCharacter(), PositionOptionKeys) == false)
                Log("Could not reset the window positions: the position file can't be read or written. If only writing failed, they're forgotten for this session.");
        }
        catch (Exception exception) { Log($"Could not forget the saved window positions: {exception.Message}"); }
    }

    /// <summary>
    /// Runs the UI for one frame. It starts once retail's indicators show, at world entry.
    /// </summary>
    public void Frame()
    {
        // What this thread allocated since the last frame ended: hooks, the post-UI draw, Decal's events.
        if (_lastFrameEndBytes != 0) _betweenFrameBytes += GC.GetAllocatedBytesForCurrentThread() - _lastFrameEndBytes;
        var start = Stopwatch.GetTimestamp();
        if (_loggedIn)
        {
            // A failed line is tried again next frame, logged once per streak.
            try
            {
                _updates.PostNotices(PostUpdateNotice);
                _noticeFailLogged = false;
            }
            catch (Exception exception)
            {
                if (!_noticeFailLogged) Log($"Could not write the update notice to chat; retrying: {exception.Message}");
                _noticeFailLogged = true;
            }
        }
        try { FrameCore(); }
        finally
        {
            _uiFrameTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            RecordPerformanceMeasurement();
            _lastFrameEndBytes = GC.GetAllocatedBytesForCurrentThread();
        }
    }

    private void FrameCore()
    {
        // Once the whole UI has failed, a give-back of retail's toolbar that failed is retried each frame until it lands; logged once per streak.
        if (_failed && _toolbarTakeover != null)
        {
            try { ReleaseRetailToolbar(); }
            catch (Exception exception)
            {
                if (!_toolbarReleaseLogged) Log($"Could not give the retail toolbar back; retrying: {exception}");
                _toolbarReleaseLogged = true;
            }
        }
        // The same for retail's chat windows: ReleaseRetailChat logs a failed give-back once per streak itself.
        if (_failed && _chatTakeover != null)
        {
            try { ReleaseRetailChat(); }
            catch (Exception exception)
            {
                if (!_chatReleaseLogged) Log($"Could not give the retail chat back; retrying: {exception}");
                _chatReleaseLogged = true;
            }
        }
        // The same for retail's radar: a failed give-back is retried each frame, logged once per streak, as chat's is.
        if (_failed && _radarTakeover != null)
        {
            try { ReleaseRetailRadar(); }
            catch (Exception exception)
            {
                if (!_radarReleaseLogged) Log($"Could not give the retail radar back; retrying: {exception}");
                _radarReleaseLogged = true;
            }
        }
        // The same for retail's vitals: Release logs a failed give-back once per streak itself.
        if (_failed && _vitals?.Holds == true) _vitals.Release();
        // The same for retail's attack panel: Release logs a failed give-back once per streak itself.
        if (_failed && _attack?.Holds == true) _attack.Release();
        if (_failed || !NativeUi.Ready)
            return;

        if (_uiSizeChanged) Guard(ApplyUiSize);
        if (_positionsResetRequested) Guard(ResetPositions);
        // A failed rebuild has torn the UI down; the block below would build it again this frame.
        if (_failed) return;

        Guard(() =>
        {
            var start = PhaseTimes.Start();
            PublishGameState();
            _inventory.Flush(_inGame);
            _inventory.TargetMode?.Tick();
            _busyCursor?.Show(_inGame && _appraisal.Waiting);
            if (_worldEffects != null)
            {
                _worldEffects.Selected = _inGame ? (uint)Bridge.Fact(BridgeFact.CurrentSelection) : 0;
            }
            _textMode?.Hold(_inGame && SurfaceById(_inputRouter.FocusedSurfaceId)?.Panel.WantsKeyboard == true);
            _serverChannel?.Tick();
            if (_portal == null)
            {
                var element = NativeUi.GetElement(NativeUi.Indicators);
                if (element == IntPtr.Zero || !NativeUi.IsVisible(element))
                    return;
                CreateUi();
            }
            _phaseTimes.Add("state", start);
            start = PhaseTimes.Start();
            TickRetailToolbar();
            _phaseTimes.Add("toolbar", start);
            start = PhaseTimes.Start();
            TickRetailChat();
            _phaseTimes.Add("chat", start);
            start = PhaseTimes.Start();
            TickRetailVitals();
            _phaseTimes.Add("vitals", start);
            start = PhaseTimes.Start();
            TickRetailRadar();
            _phaseTimes.Add("radar", start);
            start = PhaseTimes.Start();
            TickRetailAttack();
            _phaseTimes.Add("attack", start);
            // Portal space at login comes before Decal's login complete: retail's inventory opened then is parked already,
            // and our window opens for it once the windows can.
            if (!_inGame) TickRetailPanels(loggingIn: true);
        });

        Guard(() =>
        {
            if (_failed || !_inGame || !_windowsEnabled)
                return;

            if (_windows != null)
            {
                var viewport = GameDevice.ViewportSize(_device);
                if (_windows.Screen != viewport)
                {
                    _windows.ResizeScreen(viewport);
                    SyncWindowLocations();
                }
            }

            var start = PhaseTimes.Start();
            TickRetailPanels();
            TickSettingsLock();
            UpdateRetailDrag();
            if (_itemDragActive)
                _retailDrag?.UpdateDropIndicator();
            _phaseTimes.Add("panels", start);
            start = PhaseTimes.Start();
            TickFeatureWindows();
            _phaseTimes.Add("windows", start);
            PlaceUpdateToast();
            if (_dragIconSurface != null)
                _dragIconSurface.Location = new Point(_pointer.X - 16, _pointer.Y - 16);
        });

        // Every window's state is up to date: one Avalonia step lays out, paints and captures them all, then each uploads.
        // Guard's work, without a delegate allocated every frame.
        if (_failed) return;
        try { AdvancePanels(); }
        catch (Exception exception) { Disable(exception); }
    }

    /// <summary>
    /// Runs Avalonia once for every LegACEy window (<see cref="PanelCompositor.Advance"/>), then uploads each shown window's frame. A window's
    /// own failure, in the shared step or its capture, or a capture over its budget, goes to that window's error handler; a failure in
    /// the shared step that no window raised, or the step over its budget, is thrown.
    /// </summary>
    private void AdvancePanels()
    {
        // Out of game or with the windows off, the LegACEy windows are neither captured nor uploaded: they keep their last frame.
        var held = !_inGame || !_windowsEnabled;
        foreach (var surface in _featureSurfaces.Values) surface.Held = held;
        if (_updateToastSurface != null) _updateToastSurface.Held = held;
        if (_dragIconSurface != null) _dragIconSurface.Held = held;

        var start = PhaseTimes.Start();
        PanelCompositor.Advance();
        _phaseTimes.Add("avalonia", start);

        start = PhaseTimes.Start();
        // A toolbar failure drops the split tab too, so the tab is read after it.
        if (PrepareSurface(_toolbarSurface) is { } toolbarError) ToolbarFailed(toolbarError);
        if (PrepareSurface(_splitTabSurface) is { } splitTabError) ToolbarFailed(splitTabError);
        // A failure handler may close windows mid-loop, so walk a copy; the buffer is reused frame to frame.
        _preparing.Clear();
        _preparing.AddRange(_featureSurfaces);
        foreach (var pair in _preparing)
        {
            // An earlier failure in this loop may have closed the window.
            if (_featureSurfaces.ContainsKey(pair.Key) && PrepareSurface(pair.Value) is { } windowError) _windowFailures[pair.Key](windowError);
        }
        if (PrepareSurface(_updateToastSurface) is { } toastError) UpdateToastFailed(toastError);
        if (PrepareSurface(_dragIconSurface) is { } dragIconError) Disable(dragIconError);
        _phaseTimes.Add("upload", start);
    }

    /// <summary>Uploads a surface's frame; what it threw, for the caller's error handler.</summary>
    private static Exception? PrepareSurface(ScreenSurface? surface)
    {
        try { surface?.Prepare(); }
        catch (Exception exception) { return exception; }
        return null;
    }

    /// <summary>
    /// Every 30 s, logs the UI frame and post-UI draw times (p50/p95/max ms), the GC, the frame's phases with what each allocated,
    /// the world effects, then one line per window that ran: its tick, texture upload and draw times, how many of its ticks changed
    /// pixels, its layout passes, the pixels it painted and its share of the shared step's painted pixels. Ticks that changed nothing
    /// are the waste to cut.
    /// </summary>
    private void RecordPerformanceMeasurement()
    {
        var now = DateTime.UtcNow;
        if (now - _lastMeasurementLog < TimeSpan.FromSeconds(30)) return;
        _lastMeasurementLog = now;
        try
        {
            using var process = Process.GetCurrentProcess();
            Log($"UI performance sample: frames={_uiFrameTimes.Count} uiFrame={_uiFrameTimes.TakeSummary()}ms postUiDraw={_postUiDrawTimes.TakeSummary()}ms surfaces={_featureSurfaces.Count} privateBytes={process.PrivateMemorySize64} virtualBytes={process.VirtualMemorySize64} managedBytes={GC.GetTotalMemory(false)} gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)}.");
            LogGcSample();
            Log($"UI performance phases: {_phaseTimes.TakeSummary()} betweenFrames={_betweenFrameBytes / 1024}KB.");
            _betweenFrameBytes = 0;
            if (_worldEffects != null) Log($"UI performance world: {_worldEffects.TakePerformanceSummary()}.");
            LogSurfaceSample("toolbar", _toolbarSurface);
            foreach (var pair in _featureSurfaces) LogSurfaceSample(pair.Key, pair.Value);
            LogSurfaceSample("split-tab", _splitTabSurface);
            LogSurfaceSample("update-toast", _updateToastSurface);
            LogSurfaceSample("drag-icon", _dragIconSurface);
        }
        catch (Exception exception)
        {
            Log($"Could not collect a UI performance sample: {exception.Message}");
        }
    }

    /// <summary>
    /// Why the GC runs: what was allocated and how long the game was paused since the last line, the memory load against the
    /// threshold where the GC turns aggressive, and the last full and background pauses.
    /// </summary>
    private void LogGcSample()
    {
        var allocated = GC.GetTotalAllocatedBytes();
        var pause = GC.GetTotalPauseDuration();
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var full = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var background = GC.GetGCMemoryInfo(GCKind.Background);
        const double MB = 1024 * 1024;
        Log($"UI performance gc: allocatedMB={(allocated - _lastAllocatedBytes) / MB:F1} pausedMs={(pause - _lastGcPause).TotalMilliseconds:F1} " +
            $"heapMB={info.HeapSizeBytes / MB:F1} fragmentedMB={info.FragmentedBytes / MB:F1} committedMB={info.TotalCommittedBytes / MB:F1} " +
            $"loadMB={info.MemoryLoadBytes / MB:F0} highLoadMB={info.HighMemoryLoadThresholdBytes / MB:F0} availableMB={info.TotalAvailableMemoryBytes / MB:F0} " +
            $"lastFullPauseMs={(full.Index == 0 ? "-" : full.PauseDurations[0].TotalMilliseconds.ToString("F1"))} " +
            $"lastBackgroundPauseMs={(background.Index == 0 ? "-" : background.PauseDurations[0].TotalMilliseconds.ToString("F1"))}.");
        _lastAllocatedBytes = allocated;
        _lastGcPause = pause;
    }

    private static void LogSurfaceSample(string name, ScreenSurface? surface)
    {
        if (surface == null || surface.TickTimes.Count == 0 && surface.DrawTimes.Count == 0) return;
        var ticks = surface.TickTimes.Count;
        // ponytail: the share is of the step's painted pixels, not its time: Avalonia lays out and paints every window in one shared call.
        var share = surface.StepPixels == 0 ? 0 : 100.0 * surface.PaintedPixels / surface.StepPixels;
        Log($"UI performance surface {name}: ticks={ticks} changed={surface.ChangedFrames} dirtyRects={surface.DirtyRectangles} layoutPasses={surface.LayoutPasses} paintedPixels={surface.PaintedPixels} share={share:F1}% tick={surface.TickTimes.TakeSummary()}ms upload={surface.UploadTimes.TakeSummary()}ms draw={surface.DrawTimes.TakeSummary()}ms.");
        surface.ChangedFrames = 0;
        surface.DirtyRectangles = 0;
        surface.LayoutPasses = 0;
        surface.PaintedPixels = 0;
        surface.StepPixels = 0;
    }

    /// <summary>
    /// Runs each LegACEy window's own time. A failure goes to the window's error handler: a plugin's window turns off that plugin, a client
    /// window disables the client UI.
    /// </summary>
    private void TickFeatureWindows()
    {
        // A failure handler may close windows mid-loop, so walk a copy; the buffer is reused frame to frame.
        _preparing.Clear();
        _preparing.AddRange(_featureSurfaces);
        foreach (var pair in _preparing)
        {
            // An earlier failure in this loop may have closed the window.
            if (!_featureSurfaces.ContainsKey(pair.Key)) continue;
            try
            {
                // the panels run no Avalonia timers: a window that keeps time (a toast's) is ticked here
                if (pair.Key == ToastWindowId) _toast?.Tick();
                else (pair.Value.Panel.Content as IFrameTick)?.Tick();
            }
            catch (Exception exception) { _windowFailures[pair.Key](exception); }
        }
    }

    /// <summary>Called by IDirect3DDevice9.EndScene after retail and Decal UI drawing.</summary>
    private void DrawAfterRetailUi()
    {
        var start = Stopwatch.GetTimestamp();
        try { DrawAfterRetailUiCore(); }
        finally { _postUiDrawTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }

    private void DrawAfterRetailUiCore()
    {
        if (_failed) return;
        ThrowIfTestFailureRequested();
        // Drawn after retail's UI, under the LegACEy windows: drawn before it, retail's portal space would paint over it on a teleport.
        Guard(() => _toolbarSurface?.DrawNow());
        var windows = !_failed && _windowsEnabled && _windows != null;
        if (windows)
        {
            _drawnModelViews.Clear();
            var order = _windows!.ZOrder;
            for (var i = order.Count - 1; i >= 0; i--)
            {
                var surface = SurfaceById(order[i].Id);
                surface?.DrawNow();
                DrawModelViews(surface);
                if (_firstPostUiWindow)
                {
                    _firstPostUiWindow = false;
                    Log("LegACEy post-UI window draw reached through retail RenderDeviceD3D.EndScene.");
                }
            }
            ReleaseModelRenderers(_drawnModelViews);
            if (_windows.Resizing is { } resizing && _device != IntPtr.Zero)
                Guard(() => ScreenSurface.DrawOutline(_device, resizing.Bounds, ResizeOutline));
        }
        // The split tab stands over the windows: it is up only while a stack is selected, and an inventory kept over the toolbar would hide it.
        if (!_failed) Guard(() => _splitTabSurface?.DrawNow());
        if (windows)
        {
            try { _updateToastSurface?.DrawNow(); }
            catch (Exception exception) { UpdateToastFailed(exception); }
        }
        // An item dragged out of a LegACEy window draws above everything.
        if (windows) Guard(() => _dragIconSurface?.DrawNow());
    }

    /// <summary>
    /// A tester creates a file named fail-post-ui-draw beside the DLL to make the next post-UI draw fail in game,
    /// exercising the real EndScene failure path. Checked at most once a second, not every frame.
    /// </summary>
    private void ThrowIfTestFailureRequested()
    {
        var now = DateTime.UtcNow;
        if (now - _lastTestFailureCheck < TimeSpan.FromSeconds(1)) return;
        _lastTestFailureCheck = now;
        var marker = IOPath.Combine(PluginDirectory, TestFailureFile);
        if (!File.Exists(marker)) return;
        File.Delete(marker);
        Log($"Test failure requested by {TestFailureFile}; throwing from the post-UI draw.");
        throw new InvalidOperationException($"Test failure requested by {TestFailureFile}.");
    }

    /// <summary>
    /// Every 3D view in a window goes on top of that window's frame, before any window above it. A failure turns off
    /// only that view, until its window closes.
    /// </summary>
    private void DrawModelViews(ScreenSurface? surface)
    {
        if (surface is not { Visible: true } || _device == IntPtr.Zero || surface.Panel.Content is not Avalonia.Visual window) return;
        // Walking each window's tree for its views every frame was most of the plugin's garbage; the views list themselves instead.
        var views = ModelView.Attached;
        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            if (!Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(window, view)) continue;
            _drawnModelViews.Add(view);
            if (view.Model is not { } model || !view.IsEffectivelyVisible || _failedModelViews.Contains(view)) continue;
            try
            {
                var origin = Avalonia.VisualExtensions.TranslatePoint(view, default, window);
                if (origin == null) continue;
                var bounds = view.Bounds;
                var scale = surface.Panel.Scale;
                var area = new Rectangle(surface.Location.X + (int)(origin.Value.X * scale), surface.Location.Y + (int)(origin.Value.Y * scale),
                    (int)(bounds.Width * scale), (int)(bounds.Height * scale));
                if (!_modelRenderers.TryGetValue(view, out var renderer))
                {
                    _modelRenderers[view] = renderer = new ModelRenderer(_device);
                    Log($"3D view first drawn: {model.TriangleCount} triangles, height {model.Top - model.Bottom:0.00} m, at {area} on a {D3D9.Viewport(_device)} viewport.");
                }
                renderer.Draw(model, area, view.Orientation, view.Zoom, view.Focus, view.Outlined, view.OutlineColour);
            }
            catch (Exception exception)
            {
                _failedModelViews.Add(view);
                Log($"A 3D view failed to draw; it stays off until its window closes: {exception}");
            }
        }
    }

    /// <summary>Frees the GPU resources of 3D views that are no longer in an open window (all of them for an empty set).</summary>
    private void ReleaseModelRenderers(ISet<ModelView> keep)
    {
        // Runs every frame: collect into a reused list rather than a fresh LINQ one.
        foreach (var view in _modelRenderers.Keys)
            if (!keep.Contains(view)) _releasedModelViews.Add(view);
        foreach (var view in _releasedModelViews)
        {
            _modelRenderers[view].Dispose();
            _modelRenderers.Remove(view);
        }
        _releasedModelViews.Clear();
        _failedModelViews.IntersectWith(keep);
    }

    /// <summary>Tells LegACEy drop targets about a drag in the retail UI, and where the pointer is over them.</summary>
    private void UpdateRetailDrag()
    {
        var retailItem = _retailDrag?.CurrentItem() ?? 0;
        // A LegACEy inventory drag is told to the other windows as a retail one is; the window it came from ignores its own.
        var item = retailItem != 0 ? retailItem : _ownDragItem;
        if (item != _retailDragItem)
        {
            _retailDragItem = item;
            _retailDragName = item == 0 ? string.Empty : ObjectName(item);
        }
        // A Vault withdraw carries no world object, but our inventory still shows where it would go.
        // A shortcut dragged off a bar carries no item, but the bars still show the slot it would land on.
        var top = item == 0 && !_itemDragActive && _shortcutDrag.Slot == null ? null : TopSurfaceAt(_pointer);
        if (top != null && retailItem != 0 && _retailDragIcon == null)
            _retailDragIcon = ShowDragIcon(ObjectIcon(item), 1);
        else if ((top == null || retailItem == 0) && _retailDragIcon != null)
        {
            _retailDragIcon.Dispose();
            _retailDragIcon = null;
        }
        // A failure handler may close windows mid-loop, so walk a copy; the buffer is reused frame to frame.
        _preparing.Clear();
        _preparing.AddRange(_featureSurfaces);
        foreach (var pair in _preparing)
        {
            // An earlier failure in this loop may have closed the window.
            if (!_featureSurfaces.ContainsKey(pair.Key)) continue;
            var surface = pair.Value;
            Avalonia.Point? over = top == pair.Key ? (Avalonia.Point?)surface.Panel.ToContent(_pointer.X - surface.Location.X, _pointer.Y - surface.Location.Y) : null;
            try
            {
                if (surface.Panel.Content is IRetailItemDropTarget target) target.RetailDragOver(item, _retailDragName, over);
                if (surface.Panel.Content is IInventoryDropZone zone) zone.WithdrawDragOver(_itemDragActive ? over : null);
            }
            catch (Exception exception) { _windowFailures[pair.Key](exception); }
        }
        // The toolbar is not a managed window: its shortcut rows light the slot a drag over them would land on.
        if (_toolbar is { } toolbar && _toolbarSurface is { } bar)
        {
            Avalonia.Point? over = top == ToolbarSurfaceId ? bar.Panel.ToContent(_pointer.X - bar.Location.X, _pointer.Y - bar.Location.Y) : null;
            try { toolbar.RetailDragOver(item, _retailDragName, over); }
            catch (Exception exception) { ToolbarFailed(exception); }
        }
    }

    /// <summary>
    /// A retail drag released over a LegACEy window: end the drag without delivering it, so the item never drops
    /// onto whatever retail element or world is under the window, then offer it to the window. The button-up still
    /// reaches the client, which now sees an ordinary release with no drag in progress.
    /// </summary>
    private void HandleRetailDrop(Point point)
    {
        var item = _retailDrag?.CurrentItem() ?? 0;
        if (item == 0) return;
        var id = TopSurfaceAt(point);
        if (id == null) return;
        _retailDrag!.Cancel();
        var surface = SurfaceById(id);
        var accepted = false;
        if (surface?.Panel.Content is IRetailItemDropTarget target)
        {
            try
            {
                accepted = target.RetailDrop(item, ObjectName(item), surface.Panel.ToContent(point.X - surface.Location.X, point.Y - surface.Location.Y));
            }
            catch (Exception exception) { DropFailed(id, exception); }
        }
        if (_loggedDrops++ < 5)
            Log($"Retail item 0x{item:X8} dropped on LegACEy window '{id}'; retail drag cancelled; accepted: {accepted}.");
        _retailDragItem = 0;
        UpdateRetailDrag();
    }

    /// <summary>
    /// Takes a handed-off drag one message further, before the client sees the message. The posted press goes through, so the client
    /// sees the left button held and records where it went down; the hidden item holds the mouse capture, so the press goes to it rather than
    /// to the retail element under the pointer. Retail would then drag whatever that press landed on (the world, as
    /// often as not), so that is forgotten on every move until the client's pointer, which is the one before this move, is 4 pixels
    /// from the press: then the item's own list starts retail's drag of it.
    /// </summary>
    private void ContinueHandOff(int message)
    {
        var handOff = _handOff!;
        var drag = _retailDrag!;
        if (message == InputRouterService.WmLButtonDown)
        {
            // Without this the press lands on whatever retail element is under the pointer: a bar shortcut, or a window's frame it then moves.
            drag.CapturePress(handOff.Item);
            handOff.Pressed = true;
        }
        else if (message == InputRouterService.WmLButtonUp)
        {
            drag.ReleasePress(handOff.Item);
            _handOff = null;
            drag.ClearPotentialDrag();
            LogHandOff($"Hand-off of 0x{handOff.ItemId:X8}: released before retail's drag began.");
        }
        else if (message == InputRouterService.WmMouseMove && handOff.Pressed)
        {
            // The client has seen the press: the capture that routed it to the item is given back before anything else.
            drag.ReleasePress(handOff.Item);
            drag.ClearPotentialDrag();
            int dx = _pointer.X - handOff.Press.X, dy = _pointer.Y - handOff.Press.Y;
            if (dx * dx + dy * dy < 16) return;
            var started = drag.BeginDrag(handOff.List, handOff.Item, handOff.ItemId, out var state);
            // The first try scrolls the hidden retail list to the item, and retail lays the list out again only on a later frame,
            // so a scrolled item is hit on a later move.
            if (!started && ++handOff.Tries < 10) return;
            _handOff = null;
            LogHandOff($"Hand-off of 0x{handOff.ItemId:X8} pressed at {handOff.Press}, client pointer {_pointer}, try {handOff.Tries + 1}: retail drag started: {started}" +
                       (started ? "." : $"; {state}, now dragging 0x{drag.CurrentItem():X8}."));
        }
    }

    private void LogHandOff(string message)
    {
        if (_loggedHandOffs++ < 40) Log(message);
    }

    /// <summary>
    /// Ticks each retail panel takeover once a frame. The switch is read once a second; without the windows able to open, the takeover
    /// is off, so a held panel is given back.
    /// </summary>
    private void TickRetailPanels(bool loggingIn = false)
    {
        if (_retailPanels.Count == 0 && _examination == null)
        {
            // Nothing to hand over to: a switch made now must not fire later.
            _appraisalSwitched = false;
            return;
        }
        // Logging in, the windows can't open yet; they will if the post-UI hook is in.
        var canOpen = loggingIn ? _postUiDrawHook?.IsInstalled == true : CanOpenWindows;
        var enabled = canOpen && SwitchOn(RetailPanelSwitchFile, ref _lastRetailPanelSwitchCheck, ref _retailPanelSwitch);
        foreach (var takeover in _retailPanels.ToArray())
            takeover.Tick(enabled, canShow: !loggingIn);
        var appraisalOn = SwitchOn(RetailAppraisalSwitchFile, ref _lastAppraisalSwitchCheck, ref _retailAppraisalSwitch);
        _examination?.Tick(canOpen && appraisalOn, canShow: !loggingIn);
        // Turned to retail: the item in our window opens in retail's panel, now given back. The other way, the takeover already parks an
        // open retail panel and examines the selection in our window.
        if (_appraisalSwitched)
        {
            _appraisalSwitched = false;
            if (!appraisalOn && _appraisal.HandOff() is var shown and not 0 && _retailExamine?.Examine(shown) != true)
                Log("The appraisal window was switched to retail; retail's window could not reopen the item.");
        }
    }

    /// <summary>
    /// Gives every retail panel back: logoff, unload, or the windows failing. A takeover keeps its plugin's handle, and takes its
    /// panel again at the next login.
    /// </summary>
    private void EndRetailPanels()
    {
        foreach (var takeover in _retailPanels.ToArray())
            takeover.EndSession();
        _examination?.EndSession();
    }

    /// <summary>A LegACEy window's content by its id, for the drop routing that the window manager's hit test drives.</summary>
    private object? ContentOf(string id) => SurfaceById(id)?.Panel.Content;

    /// <summary>A plugin's hold on a retail panel. Disposing it gives the panel back.</summary>
    private sealed class RetailPanelHandle : IRetailPanel
    {
        private readonly ClientUiRuntime _owner;
        private RetailPanelTakeover? _takeover;

        public RetailPanelHandle(ClientUiRuntime owner, RetailPanelTakeover takeover)
        {
            _owner = owner;
            _takeover = takeover;
        }

        public bool Holds => _takeover?.Holds ?? false;

        public void Close() => _takeover?.Close();

        public void Dispose()
        {
            var takeover = _takeover;
            if (takeover == null) return;
            _takeover = null;
            _owner._retailPanels.Remove(takeover);
            takeover.Dispose();
        }
    }

    /// <summary>What a plugin gets when the takeover cannot run: nothing happens, and the retail panel is left alone.</summary>
    private sealed class NoRetailPanel : IRetailPanel
    {
        public static readonly NoRetailPanel Instance = new();
        public bool Holds => false;
        public void Close() { }
        public void Dispose() { }
    }

    private string? TopSurfaceAt(Point point)
    {
        InputSurface? top = null;
        foreach (var surface in GetInputSurfaces())
            if (point.X >= surface.X && point.Y >= surface.Y && point.X < surface.X + surface.Width && point.Y < surface.Y + surface.Height &&
                (top == null || surface.ZOrder > top.ZOrder))
                top = surface;
        return top?.Id;
    }

    private static string ObjectName(uint id)
    {
        try { return Bridge.Item(id)?.Name ?? string.Empty; }
        catch (COMException) { return string.Empty; }
    }

    /// <summary>The object's icon as the retail UI draws it: underlay, the icon with its UI-effect outline, then the secondary overlay.</summary>
    private GameImage? ObjectIcon(uint id)
    {
        try
        {
            if (_portal == null || Bridge.Item(id) is not { } item) return null;
            var visual = item.Visual;
            // Decal has no secondary overlay key, so none is drawn.
            return ItemIcon.Draw(_portal, visual.Underlay, visual.Icon, visual.Overlay, 0, visual.UiEffects);
        }
        catch (COMException) { return null; }
    }

    /// <summary>Drag services for LegACEy windows: the floating icon and what lies under the pointer in the retail UI.</summary>
    private sealed class ItemDragHost : IItemDragHost
    {
        private readonly ClientUiRuntime _owner;
        public ItemDragHost(ClientUiRuntime owner) => _owner = owner;

        /// <summary>An item released outside every slot of a LegACEy window goes to the window under the pointer, if it takes retail drops.</summary>
        public bool DeliverAtPointer(uint itemId, string itemName)
        {
            // The toolbar is not a managed window, so a drop on it is delivered to its own surface.
            if (_owner.TopSurfaceAt(_owner._pointer) == ToolbarSurfaceId)
                return _owner.DeliverToToolbar((content, point) => content is IRetailItemDropTarget target && target.RetailDrop(itemId, itemName, point));
            if (_owner._windows is not { } windows) return false;
            var window = windows.HitTest(_owner._pointer);
            if (window == null) return false;
            // A shortcut dragged off a row is only the toolbar's to take: no window is offered it as an item.
            if (_owner._shortcutDrag.Slot != null) return false;
            try { return ItemDropRouting.DeliverTo(window, _owner.ContentOf, _owner._pointer, itemId, itemName); }
            catch (Exception exception)
            {
                if (_owner._windowFailures.TryGetValue(window.Id, out var failed)) failed(exception);
                else _owner.Disable(exception);
                return false;
            }
        }

        public bool DeliverManyAtPointer(IReadOnlyList<uint> itemIds)
        {
            if (_owner.TopSurfaceAt(_owner._pointer) == ToolbarSurfaceId)
                return _owner.DeliverToToolbar((content, point) => content is IItemSetDropTarget target && target.DropMany(itemIds, point));
            if (_owner._windows is not { } windows) return false;
            var window = windows.HitTest(_owner._pointer);
            if (window == null) return false;
            try { return ItemDropRouting.DeliverManyTo(window, _owner.ContentOf, _owner._pointer, itemIds); }
            catch (Exception exception)
            {
                if (_owner._windowFailures.TryGetValue(window.Id, out var failed)) failed(exception);
                else _owner.Disable(exception);
                return false;
            }
        }

        public IDisposable ShowDragIcon(GameImage? image, int count, bool retailDropIndicator, uint itemId = 0)
        {
            var icon = _owner.ShowDragIcon(image, count);
            _owner._ownDragItem = itemId;
            return new ItemDrag(_owner, icon, retailDropIndicator);
        }

        private sealed class ItemDrag : IDisposable
        {
            private ClientUiRuntime? _owner;
            private readonly IDisposable _icon;
            // Only a drag the retail inventory can take shows retail's drop indicator; the others leave retail's indicator alone.
            private readonly bool _retailIndicator;
            public ItemDrag(ClientUiRuntime owner, IDisposable icon, bool retailIndicator)
            {
                _owner = owner;
                _icon = icon;
                _retailIndicator = retailIndicator;
                if (retailIndicator) owner._itemDragActive = true;
            }
            public void Dispose()
            {
                if (_owner == null) return;
                if (_retailIndicator)
                {
                    _owner._itemDragActive = false;
                    _owner.Guard(() => _owner._retailDrag?.ClearDropIndicator());
                }
                _owner._ownDragItem = 0;
                _icon.Dispose();
                _owner = null;
            }
        }

        /// <summary>
        /// Outside every LegACEy surface, finds the item in the retail inventory's lists and posts the client a left press at the
        /// pointer, as if the press had happened there; <see cref="ContinueHandOff"/> starts retail's drag once the client has seen it.
        /// </summary>
        public bool HandToRetail(uint itemId)
        {
            var owner = _owner;
            if (owner._handOff != null || owner._gameWindow == IntPtr.Zero || owner._retailDrag is not { Available: true } drag ||
                owner.TopSurfaceAt(owner._pointer) != null)
                return false;
            var (list, item) = drag.FindItem(itemId);
            if (item == IntPtr.Zero)
            {
                // Asked on every move outside the window; said once per item.
                if (owner._handOffMissed == itemId) return false;
                owner._handOffMissed = itemId;
                owner.LogHandOff($"Hand-off of 0x{itemId:X8}: no retail list shows it; the drag stays ours.");
                return false;
            }
            var point = owner._pointer;
            const int MkLButton = 1;
            if (!PostMessage(owner._gameWindow, InputRouterService.WmLButtonDown, new IntPtr(MkLButton), new IntPtr((point.Y << 16) | (point.X & 0xffff))))
                return false;
            owner._handOff = new HandOff { List = list, Item = item, ItemId = itemId, Press = point };
            owner._inputRouter.ReleaseCapture();
            return true;
        }

        public (uint Container, int Position)? InventoryPlaceAtPointer() =>
            _owner._windows is { } windows ? ItemDropRouting.InventoryPlaceAt(windows, _owner.ContentOf, _owner._pointer) : null;

        public ItemDropTarget DropTargetAtPointer()
        {
            // A LegACEy inventory window is the inventory, with or without the takeover.
            if (_owner._windows is { } windows && ItemDropRouting.InventoryAt(windows, _owner.ContentOf, _owner._pointer) is { } inventory) return inventory;
            // Any other LegACEy window (the Vault, which refused the item) covers the world: a release there drops nothing.
            if (_owner.TopSurfaceAt(_owner._pointer) is { } surface)
            {
                if (_owner._loggedDrops++ < 5) Log($"Item dropped at {_owner._pointer} over LegACEy surface '{surface}': target: Elsewhere.");
                return ItemDropTarget.Elsewhere;
            }
            var drag = _owner._retailDrag;
            if (drag == null) return ItemDropTarget.Inventory;
            var over = drag.IsPointerOverInventory(out var exists, out var open, out var element);
            // Without the panel element, any drop outside LegACEy windows withdraws to the pack.
            var target = !exists ? ItemDropTarget.Inventory : !open ? ItemDropTarget.InventoryClosed : over ? ItemDropTarget.Inventory
                : element == WorldViewElement ? ItemDropTarget.World : ItemDropTarget.Elsewhere;
            if (_owner._loggedDrops++ < 5)
                Log($"Item dropped at {_owner._pointer}: retail element under pointer 0x{element:X8}, inventory open: {open}, target: {target}.");
            return target;
        }
    }

    /// <summary>The "×count" badge for a drag icon's corner: the count over a dark shadow, as the Vault draws its stack counts.</summary>
    private static Avalonia.Controls.Control CountBadge(int count)
    {
        static Avalonia.Controls.TextBlock Line(string text, Avalonia.Media.IBrush brush, Avalonia.Thickness margin) => new()
        {
            Text = text, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontFamily = DerethPalette.Body, Foreground = brush, Margin = margin
        };
        var badge = new Avalonia.Controls.Panel
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Avalonia.Thickness(0, 0, 3, 1)
        };
        badge.Children.Add(Line($"×{count}", Avalonia.Media.Brushes.Black, new Avalonia.Thickness(1, 1, 0, 0)));
        badge.Children.Add(Line($"×{count}", DerethPalette.TextBrush, new Avalonia.Thickness(0, 0, 1, 1)));
        return badge;
    }

    private IDisposable ShowDragIcon(GameImage? image, int count)
    {
        HideDragIcon();
        if (_device == IntPtr.Zero || _portal == null) return new DragIcon(this);
        var bitmap = GameArtImageExtension.CreateBitmap(image);
        var layers = new Grid { Width = 32, Height = 32 };
        if (bitmap != null)
            layers.Children.Add(new Avalonia.Controls.Image { Source = bitmap, Width = 32, Height = 32, Stretch = Stretch.None });
        // a selection's drag carries its count in the icon's corner
        if (count > 1)
            layers.Children.Add(CountBadge(count));
        RenderOptions.SetBitmapInterpolationMode(layers, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        layers.DetachedFromVisualTree += (_, _) => bitmap?.Dispose();
        var panel = ObservePanel(AvaloniaPanel.Create(() => layers, 32, 32));
        _dragIconSurface = new ScreenSurface(_device, panel) { Visible = true, Location = new Point(_pointer.X - 16, _pointer.Y - 16) };
        return new DragIcon(this);
    }

    private void HideDragIcon()
    {
        _dragIconSurface?.Dispose();
        _dragIconSurface = null;
    }

    private sealed class DragIcon : IDisposable
    {
        private ClientUiRuntime? _owner;
        public DragIcon(ClientUiRuntime owner) => _owner = owner;
        public void Dispose()
        {
            _owner?.HideDragIcon();
            _owner = null;
        }
    }

    IDisposable IClientUiHost.OpenWindow(WindowDefinition definition, Control content, Point requestedLocation)
    {
        if (!CanOpenWindows)
            throw new InvalidOperationException("LegACEy windows are unavailable until the post-UI renderer is ready.");
        OpenWindowCore(definition, content, requestedLocation, Disable);
        return new FeatureWindow(this, definition.Id);
    }

    private bool CanOpenWindows => _windowsEnabled && _windows != null && _device != IntPtr.Zero && _postUiDrawHook?.HasRun == true;

    private void OpenWindowCore(WindowDefinition definition, Control content, Point requestedLocation, Action<Exception> failed)
    {
        if (_featureSurfaces.ContainsKey(definition.Id))
            throw new InvalidOperationException($"A feature window named '{definition.Id}' is already registered.");
        // Created at its on-screen size; showing it fits it to the window manager's size again (a saved size, for one).
        var scale = PanelScaleOf(definition.Id);
        var panel = ObservePanel(AvaloniaPanel.Create(() => content, (int)Math.Round(definition.Width * scale), (int)Math.Round(definition.Height * scale), scale), failed);
        var surface = new ScreenSurface(_device, panel);
        ShowSurface(definition, surface, requestedLocation, failed);
    }

    private void ShowSurface(WindowDefinition definition, ScreenSurface surface, Point requestedLocation, Action<Exception> failed)
    {
        var opened = false;
        try
        {
            var window = _windows!.Open(definition, requestedLocation);
            opened = true;
            FitSurface(surface, window);
            surface.Visible = true;
            surface.Panel.ApplyTheme(definition.Theme ?? _clientUi?.Theme ?? CurrentTheme());
            _featureSurfaces.Add(definition.Id, surface);
            // a window whose shortcuts work from the start (the Market's Esc) takes the keyboard as it opens
            if (surface.Panel.Content is IWindowShortcuts { TakesFocusOnOpen: true }) _inputRouter.FocusSurface(definition.Id);
            _windowFailures[definition.Id] = failed;
        }
        catch
        {
            try { surface.Dispose(); }
            finally { if (opened) _windows!.Close(definition.Id); }
            throw;
        }
    }

    bool ILegACEyPluginHost.OpenWindow(WindowDefinition definition, Point location, Func<Action, Control> createContent, Action<Exception> failed, bool ownChrome)
    {
        if (!CanOpenWindows) return false;
        if (_hiddenSurfaces.TryGetValue(definition.Id, out var hidden))
        {
            _hiddenSurfaces.Remove(definition.Id);
            // Hiding runs inside the window's own click, so its hover and focus are cleared here instead. A focused
            // field left behind would take keys again after the next click in the window.
            hidden.Panel.PointerLeave();
            hidden.Panel.ClearFocus();
            ShowSurface(definition, hidden, location, failed);
            return true;
        }
        Action close = () => HideFeatureWindow(definition.Id);
        var content = createContent(close);
        try
        {
            if (ownChrome)
                OpenWindowCore(definition, content, location, failed);
            else
            {
                var chrome = new ThemeWindowChrome(_portal!, definition.Title, content);
                chrome.CloseRequested += (_, _) => close();
                OpenWindowCore(definition, chrome, location, failed);
            }
        }
        catch
        {
            // The window never opened, so nothing else releases the content the plugin built for it.
            (content as IDisposable)?.Dispose();
            throw;
        }
        return true;
    }

    IServerChannel ILegACEyPluginHost.ServerChannel => (IServerChannel?)_serverChannel ?? UnavailableServerChannel.Instance;
    ILookSource ILegACEyPluginHost.Looks => _looks;
    string ILegACEyPluginHost.PortalPath => _portal?.Path ?? string.Empty;
    IGameArtSource ILegACEyPluginHost.Art => (IGameArtSource?)_portal ?? throw new InvalidOperationException("Game art is unavailable until the client UI is ready.");
    IItemDragHost ILegACEyPluginHost.ItemDrag => new ItemDragHost(this);
    IInventoryPort ILegACEyPluginHost.Inventory => _appraisingInventory;

    IRetailPanel ILegACEyPluginHost.TakeOverRetailInventory(Action<bool> retailOpenChanged)
    {
        if (!NativeUi.Ready || !RetailPanelCatalogueValid()) return NoRetailPanel.Instance;
        // The LegACEy inventory window's open state, which the toolbar's backpack shows while the takeover holds retail's panel.
        var takeover = new RetailPanelTakeover(new NativeRetailPanelPort(), open => { _inventoryWindowOpen = open; retailOpenChanged(open); }, Log);
        _retailPanels.Add(takeover);
        return new RetailPanelHandle(this, takeover);
    }

    /// <summary>
    /// The panel takeover's own addresses. A mismatch, or a check that throws, turns only the takeover off; the bar and the Vault drag keep
    /// their gates.
    /// </summary>
    private bool RetailPanelCatalogueValid()
    {
        if (_retailPanelsValid is { } known) return known;
        try
        {
            _retailPanelsValid = NativeUiCatalogue.Validate(NativeUi.ReadMemory, Log, RetailPanelCatalogue.Entries);
        }
        catch (Exception exception)
        {
            Log($"Retail inventory takeover disabled: its native addresses could not be checked: {exception.Message}");
            _retailPanelsValid = false;
        }
        return _retailPanelsValid.Value;
    }

    /// <summary>
    /// The toolbar takeover's own addresses, checked once. A mismatch, or a check that throws, leaves retail's toolbar in place and logs
    /// one line; the bar, the Vault drag and the inventory keep their gates.
    /// </summary>
    private bool RetailToolbarCatalogueValid()
    {
        if (_retailToolbarValid is { } known) return known;
        try
        {
            _retailToolbarValid = NativeUiCatalogue.Validate(NativeUi.ReadMemory, Log, RetailToolbarCatalogue.Entries);
            if (_retailToolbarValid == false) Log("Dereth toolbar takeover is off: the running client does not match its native entries.");
        }
        catch (Exception exception)
        {
            Log($"Dereth toolbar takeover is off: its native addresses could not be checked: {exception.Message}");
            _retailToolbarValid = false;
        }
        return _retailToolbarValid.Value;
    }

    bool ILegACEyPluginHost.IsWindowOpen(string id) => _featureSurfaces.ContainsKey(id);

    bool ILegACEyPluginHost.Tell(string recipient, string text) =>
        RetailTell.Command(recipient, text) is { } command && _chatPort != null && _chatPort.Send(command);

    // The toast plugins show over the game (a sale): one small window at the bottom right, hidden when the toast hides itself.
    private const string ToastWindowId = "legacey-toast";
    private const int ToastHeight = 84;
    private DerethToast? _toast;

    void ILegACEyPluginHost.ShowToast(string title, string line)
    {
        if (!_inGame || !CanOpenWindows) return;
        if (!_featureSurfaces.ContainsKey(ToastWindowId))
        {
            var definition = new WindowDefinition(ToastWindowId, string.Empty, (int)DerethToast.CardWidth, ToastHeight, dragsFromContent: true);
            var scale = _windows!.ScaleOf(ToastWindowId);
            // ponytail: a fixed place above retail's bottom-right corner; a saved or chosen place if players want to move it
            var at = new Point(_windows.Screen.Width - (int)Math.Round((DerethToast.CardWidth + 20) * scale),
                _windows.Screen.Height - (int)Math.Round((ToastHeight + 140) * scale));
            // a hidden toast window comes back with its toast; a new one builds it
            if (!((ILegACEyPluginHost)this).OpenWindow(definition, at, close =>
                {
                    _toast = new DerethToast { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
                    _toast.Hidden += close;
                    return _toast;
                }, Disable, ownChrome: true))
                return;
        }
        _toast!.Show(title, line);
    }

    // The updater's toast at the bottom centre of the screen, over the windows. It is its own surface, as the split tab is, not a window: it
    // is never dragged, saved or focused. One shows at a time; a logoff drops it.
    private const string UpdateToastSurfaceId = "update-toast";
    // From the screen's bottom edge to the toast's, in DIPs: clear of the toolbar where it first stands.
    private const int UpdateToastLift = 110;
    // Between the toast and a toolbar row it is lifted clear of, in DIPs.
    private const int UpdateToastGap = 8;
    private DerethNoticeToast? _updateToast;
    private ScreenSurface? _updateToastSurface;
    // Log out and update asked to log out; the server's authorized logoff then closes the game.
    private bool _quitPending;

    /// <summary>
    /// The toast for an updater notice, with its lifetime. What's new opens the release's page with <paramref name="open"/>; Try again calls
    /// <paramref name="checkNow"/>, or <paramref name="install"/> again after a failed download. Ready's update button <paramref name="show"/>s a
    /// confirm in its place, whose Log out works only while <paramref name="peace"/> holds: it shows Downloading and calls <paramref name="install"/>,
    /// and the Downloaded that follows calls <paramref name="logOut"/> (false when it couldn't), or, out of peace by then, offers its own Log out.
    /// Cancel shows Ready again. × only hides a toast: a download asked for still logs out.
    /// </summary>
    internal static ToastNotice UpdateToast(UpdateNotice notice, Action<string> open, Action checkNow, Action<ToastNotice> show, Func<bool> peace,
        Action<Version> install, Func<bool> logOut) => notice.Kind switch
    {
        UpdateNoticeKind.Checking => new ToastNotice(ToastIcon.Search, "Checking for updates…", Busy: true),
        UpdateNoticeKind.Ready => ReadyToast(notice, open, show, peace, install),
        UpdateNoticeKind.Downloaded when peace() => LoggingOut(notice, logOut),
        UpdateNoticeKind.Downloaded => new ToastNotice(ToastIcon.New, $"LegACEy {notice.Version} is downloaded", Installs(notice),
            new[] { new ToastAction("Log out", () => show(LoggingOut(notice, logOut)), Unavailable: () => peace() ? null : "Not while in combat") }),
        UpdateNoticeKind.UpToDate => new ToastNotice(ToastIcon.Done, $"LegACEy {notice.Version} is up to date", Lifetime: TimeSpan.FromSeconds(4)),
        UpdateNoticeKind.Wait => new ToastNotice(ToastIcon.Wait, "Checked moments ago", "Try again in a minute. Another client on this PC may be checking too.",
            Lifetime: TimeSpan.FromSeconds(5)),
        _ when notice.Version is { } version => new ToastNotice(ToastIcon.Failed, $"Couldn't download LegACEy {version}",
            "The download failed or didn't verify. Nothing has changed.", new[] { new ToastAction("Try again", () => install(version)) }, TimeSpan.FromSeconds(8)),
        _ => new ToastNotice(ToastIcon.Failed, "Couldn't check for updates", "GitHub didn't answer. Try again later.",
            new[] { new ToastAction("Try again", checkNow) }, TimeSpan.FromSeconds(8)),
    };

    // With several clients open the update installs only once every one has closed, so the button says so.
    private static ToastNotice ReadyToast(UpdateNotice notice, Action<string> open, Action<ToastNotice> show, Func<bool> peace, Action<Version> install) =>
        new(ToastIcon.New, $"LegACEy {notice.Version} is available", "Nothing changes until you update.",
            new[]
            {
                new ToastAction(notice.SeveralClients ? "Update when all are closed" : "Log out and update", () => show(Confirm(notice, open, show, peace, install))),
                new ToastAction("What's new", () => open(PluginUpdates.ReleasePage(notice.Version!)), Quiet: true),
            }, TimeSpan.FromSeconds(12));

    private static ToastNotice Confirm(UpdateNotice notice, Action<string> open, Action<ToastNotice> show, Func<bool> peace, Action<Version> install)
    {
        var version = notice.Version!;
        var downloading = new ToastNotice(ToastIcon.New, $"Downloading LegACEy {version}…", "Your character logs out once it's ready.", Busy: true);
        return new ToastNotice(ToastIcon.Caution, notice.SeveralClients ? "Update when all are closed?" : "Log out and update?",
            notice.SeveralClients
                ? $"LegACEy {version} downloads, then your character logs out and this game closes. It installs once every other game window is closed too."
                : $"LegACEy {version} downloads, then your character logs out and the game closes while it installs. Then start it again from your launcher.",
            new[]
            {
                new ToastAction("Log out", () =>
                {
                    show(downloading);
                    install(version);
                }, Unavailable: () => peace() ? null : "Not while in combat"),
                new ToastAction("Cancel", () => show(ReadyToast(notice, open, show, peace, install)), Quiet: true),
            });
    }

    // Logs out for a downloaded update: the player asked for it, and the updater is now waiting for the game to close.
    private static ToastNotice LoggingOut(UpdateNotice notice, Func<bool> logOut) => logOut()
        ? new ToastNotice(ToastIcon.New, $"Logging out to install LegACEy {notice.Version}", Busy: true)
        : new ToastNotice(ToastIcon.Failed, "Couldn't log out", $"Log out yourself. {Installs(notice)}", Lifetime: TimeSpan.FromSeconds(8));

    private static string Installs(UpdateNotice notice) =>
        notice.SeveralClients ? "It installs once every game window is closed." : "It installs when you close the game.";

    /// <summary>An updater notice: its chat line, as before the toast, and its toast while the windows can show one.</summary>
    private void PostUpdateNotice(UpdateNotice notice)
    {
        if (notice.Chat != null) Bridge.Chat(notice.Chat, SystemChat);
        if (!_inGame || !CanOpenWindows) return;
        // A failed chat line throws, and the notice stays queued. A failed toast is logged and swallowed instead: the notice is used up, so
        // its chat line is not written again.
        try
        {
            if (_updateToastSurface == null)
            {
                var toast = new DerethNoticeToast();
                var panel = ObservePanel(AvaloniaPanel.Create(() => toast, 1, 1, WindowScale), UpdateToastFailed);
                panel.ApplyTheme(new DerethClientTheme());
                _updateToastSurface = new ScreenSurface(_device, panel);
                _updateToast = toast;
            }
            _updateToast!.Show(UpdateToast(notice, OpenPage, () => _updates.CheckNow(), shown => _updateToast?.Show(shown), InPeace,
                version => _updates.Install(version), LogOutToUpdate));
        }
        catch (Exception exception) { UpdateToastFailed(exception); }
    }

    /// <summary>Runs the toast's time, and sizes and places it at the bottom centre at the UI size while it shows.</summary>
    private void PlaceUpdateToast()
    {
        if (_updateToast is not { } toast || _updateToastSurface is not { } surface) return;
        try
        {
            toast.Tick();
            var shown = surface.Visible;
            surface.Visible = toast.IsVisible;
            if (surface.Visible)
            {
                var scale = surface.Panel.Scale;
                // Just unhidden, no layout pass has caught it up: every part, as FitToolbar does.
                if (!shown) Avalonia.Layout.LayoutHelper.InvalidateSelfAndChildrenMeasure(toast);
                toast.Measure(Avalonia.Size.Infinity);
                int width = WholePixels(toast.DesiredSize.Width * scale), height = WholePixels(toast.DesiredSize.Height * scale);
                if (surface.Panel.Frame.Width != width || surface.Panel.Frame.Height != height) surface.Panel.Resize(width, height);
                var screen = GameDevice.ViewportSize(_device);
                var spot = new Rectangle((screen.Width - width) / 2, screen.Height - height - (int)Math.Round(UpdateToastLift * scale), width, height);
                var bars = _toolbarSurface is { Visible: true } bar ? new[] { bar.Bounds } : Array.Empty<Rectangle>();
                var clear = ClearOf(spot, bars, (int)Math.Round(UpdateToastGap * scale));
                surface.Location = new Point(Math.Max(0, clear.X), Math.Max(0, clear.Y));
            }
        }
        catch (Exception exception) { UpdateToastFailed(exception); }
    }

    /// <summary>
    /// Where the toast stands clear of the toolbar and its rows: lifted, still centred, above every one it would cover, however many rows
    /// are open or wherever they were moved.
    /// </summary>
    internal static Rectangle ClearOf(Rectangle toast, IEnumerable<Rectangle> bars, int gap)
    {
        var covered = bars.Where(bar => bar.Left < toast.Right && toast.Left < bar.Right).OrderByDescending(bar => bar.Top).ToList();
        foreach (var bar in covered)
            if (bar.Top < toast.Bottom + gap && toast.Top < bar.Bottom) toast.Y = bar.Top - gap - toast.Height;
        return toast;
    }

    private void UpdateToastFailed(Exception exception)
    {
        Log($"The update toast failed and is dropped; its chat line stands: {exception}");
        DropUpdateToast();
    }

    private void DropUpdateToast()
    {
        if (_hovered == _updateToastSurface) _hovered = null;
        _updateToastSurface?.Dispose();
        _updateToastSurface = null;
        _updateToast = null;
    }

    // Peace stance stands in for "not in combat": a character can still be attacked in it, but the confirm has already asked.
    private static bool InPeace()
    {
        try { return (RetailStance)Bridge.Fact(BridgeFact.CombatMode) == RetailStance.Peace; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The confirm's Log out: logs the character out, and the logoff then closes the game so the waiting updater can install. False when
    /// retail's log out could not be asked for.
    /// </summary>
    private bool LogOutToUpdate()
    {
        if (_quitPending) return true;
        try { NativeUi.RequestLogOut(confirm: false); }
        catch (Exception exception)
        {
            Log($"Could not log out to update: {exception}");
            return false;
        }
        _quitPending = true;
        return true;
    }

    /// <summary>Opens a web page in the player's browser.</summary>
    private static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception exception) { Log($"Could not open {url}: {exception.Message}"); }
    }

    void ILegACEyPluginHost.WriteChat(string text)
    {
        if (text.Length == 0) return;
        // 0 is retail's broadcast type, which it draws green
        try { Bridge.Chat(text, 0); }
        catch (COMException exception) { Log($"Could not write a chat line: {exception.Message}"); }
    }
    void ILegACEyPluginHost.HideWindow(string id) => HideFeatureWindow(id);
    void ILegACEyPluginHost.CloseWindow(string id) => ReleaseFeatureWindow(id);

    // The appraisal window's game: the character and the inventory it reads, the inscription action through the server channel's send,
    // and a silent ID request (Bridge.RequestId, which selects nothing and opens no window).
    string IAppraisalGame.CharacterName => SessionCharacter();

    bool IAppraisalGame.IsCarried(uint itemId) => _inventory.Snapshot.Carries(itemId);

    bool IAppraisalGame.SetInscription(uint itemId, string text) =>
        _channelTransport?.SendAction(InscriptionAction.Opcode, InscriptionAction.Payload(itemId, text)) ?? false;

    void IAppraisalGame.RequestAppraisal(uint itemId) => Bridge.RequestId(itemId);

    // A released object reads as no slots and no coverage: the window then shows no "Covers" line.
    uint IAppraisalGame.ValidLocationsOf(uint itemId) => WorldRead(() => Bridge.ValidLocations(itemId));

    uint IAppraisalGame.CoverageOf(uint itemId) => WorldRead(() => Bridge.CoverageOf(itemId));

    uint IAppraisalGame.DescriptionFlagsOf(uint itemId) => WorldRead(() => Bridge.DescriptionFlagsOf(itemId));

    InventoryObjectRead? IAppraisalGame.WorldObjectOf(uint itemId)
    {
        try { return Bridge.Item(itemId); }
        catch (COMException) { return null; }
    }

    string IAppraisalGame.PluralNameOf(uint itemId)
    {
        try { return Bridge.PluralName(itemId); }
        catch (COMException) { return string.Empty; }
    }

    private static uint WorldRead(Func<uint> read)
    {
        try { return read(); }
        catch (COMException) { return 0; }
    }

    void ILegACEyPluginHost.Inspect(uint objectId, string name, ItemVisual? visual) =>
        _appraisal.Inspect(objectId, objectId == 0 && name.Length == 0 ? SessionCharacter() : name, visual);

    void ILegACEyPluginHost.ShowAppraisal(byte[] appraisal, string name, ItemVisual? visual, int stackCount) =>
        _appraisal.ShowStored(appraisal, name, visual, stackCount);

    void ILegACEyPluginHost.SelectForExamination(Action? examine) => _appraisal.SelectStored(examine);

    int? ILegACEyPluginHost.LoadPluginSettings(string plugin) => _settingsStore.Load(SessionServer(), SessionCharacter(), plugin)?.Location.X;

    void ILegACEyPluginHost.SavePluginSettings(string plugin, int value) =>
        _settingsStore.Save(SessionServer(), SessionCharacter(), plugin, (new Point(value, 0), null));

    private void HideFeatureWindow(string id)
    {
        if (!_featureSurfaces.TryGetValue(id, out var surface)) return;
        RemoveFeatureWindow(id, surface);
        _hiddenSurfaces[id] = surface;
    }

    private void ReleaseFeatureWindow(string id)
    {
        if (_hiddenSurfaces.TryGetValue(id, out var hidden))
        {
            _hiddenSurfaces.Remove(id);
            hidden.Dispose();
        }
        if (!_featureSurfaces.TryGetValue(id, out var surface)) return;
        RemoveFeatureWindow(id, surface);
        surface.Dispose();
    }

    private void RemoveFeatureWindow(string id, ScreenSurface surface)
    {
        if (_hovered == surface) _hovered = null;
        // A hidden or closed window must not reopen with its corner lit.
        if (_cornerWindowId == id)
        {
            SetWindowCorner(id, DerethCorner.None);
            _cornerWindowId = null;
            _cornerApplied = DerethCorner.None;
        }
        surface.Visible = false;
        _featureSurfaces.Remove(id);
        _windowFailures.Remove(id);
        _windows?.Close(id);
    }

    /// <summary>Shows the LegACEy menu's open state on the toolbar's menu button.</summary>
    private void SetMenuRing(bool open) => _toolbar?.SetMenuOpen(open);

    /// <summary>
    /// Holds retail's toolbar and draws the Dereth toolbar at its place, unless the switch file is beside the DLL. Off, retail's toolbar is
    /// given back and nothing new draws. A failure gives retail's toolbar back and stops the Dereth toolbar until the switch is turned off and on.
    /// </summary>
    private void TickRetailToolbar()
    {
        try
        {
            var switchOn = SwitchOn(RetailToolbarSwitchFile, ref _lastToolbarSwitchCheck, ref _toolbarSwitch);
            // A give-back that failed is retried every frame until it lands, whatever state the toolbar is in: retail's toolbar is never left hidden.
            // The toolbar needs only the post-UI draw, not the windows LoginComplete makes, so it takes over from portal space at login on.
            var canDraw = _postUiDrawHook?.IsInstalled == true;
            if (_toolbarTakeover != null && (!canDraw || _toolbarFailed || !switchOn))
            {
                ReleaseRetailToolbar();
                return;
            }
            // Without the post-UI draw the toolbar's surface cannot draw; DisableWindows has already given retail's toolbar back.
            if (!canDraw) return;
            if (!switchOn)
            {
                _toolbarFailed = false;
                ReleaseRetailToolbar();
                return;
            }
            if (_toolbarFailed) return;
            if (_toolbarSurface == null && !CreateRetailToolbar()) return;
            LoadToolbarPlace();
            _toolbarPort!.Poll();
            _toolbarTakeover!.Tick(_toolbarDragOffset != null, GameDevice.ViewportSize(_device));
            // Until login completes, retail's toolbar stays held but ours is not drawn: its saved place is read only then, so it never
            // shows at retail's place first and jumps.
            if (!_inGame)
            {
                _toolbarSurface!.Visible = false;
                if (_splitTabSurface != null) _splitTabSurface.Visible = false;
                CloseShortcutBar();
                return;
            }
            // The bar stands beside the toolbar only while the takeover holds retail's element; otherwise it closes with it.
            if (_toolbarTakeover.Holds) EnsureShortcutBar();
            else CloseShortcutBar();
            PlaceSplitTab();
        }
        catch (Exception exception)
        {
            ToolbarFailed(exception);
        }
    }

    /// <summary>
    /// Shows the split tab while the toolbar shows and a stack is selected, at its content's size, centred over the plaque and standing on
    /// the toolbar's top edge; below the toolbar when there is no room above it.
    /// </summary>
    private void PlaceSplitTab()
    {
        if (_splitTab is not { } tab || _splitTabSurface is not { } surface || _toolbar is not { } toolbar || _toolbarSurface is not { } bar) return;
        var shown = surface.Visible;
        surface.Visible = bar.Visible && tab.IsShown;
        if (surface.Visible)
        {
            var scale = surface.Panel.Scale;
            // Just unhidden, no layout pass has caught it up: every part, as FitToolbar does.
            if (!shown) Avalonia.Layout.LayoutHelper.InvalidateSelfAndChildrenMeasure(tab);
            tab.Measure(Avalonia.Size.Infinity);
            int width = WholePixels(tab.DesiredSize.Width * scale), height = WholePixels(tab.DesiredSize.Height * scale);
            if (surface.Panel.Frame.Width != width || surface.Panel.Frame.Height != height) surface.Panel.Resize(width, height);
            var plaque = Avalonia.VisualExtensions.TranslatePoint(toolbar.Plaque, new Avalonia.Point(toolbar.Plaque.Bounds.Width / 2, 0), toolbar) ?? default;
            var screen = GameDevice.ViewportSize(_device);
            var x = bar.Location.X + (int)Math.Round(plaque.X * scale) - width / 2;
            var y = bar.Location.Y - height;
            surface.Location = new Point(Math.Max(0, Math.Min(screen.Width - width, x)), y >= 0 ? y : bar.Bounds.Bottom);
        }
    }

    private const string ToolbarPlaceId = "dereth-toolbar";

    /// <summary>Once per toolbar taken over, puts it at this character's saved place, kept on screen. Until then it stands where retail's did.</summary>
    private void LoadToolbarPlace()
    {
        if (_toolbarPlaceLoaded || !_inGame || _toolbarPlace == null || _toolbarSurface == null) return;
        _toolbarPlaceLoaded = true;
        ScaleToolbar();
        if (_positionStore.Load(SessionServer(), SessionCharacter(), ToolbarPlaceId) is not { } saved) return;
        _toolbarPlace.Location = OnScreen(saved.Location, _toolbarSurface.Bounds.Size);
    }

    /// <summary>
    /// Draws the toolbar at this character's toolbar scale, the window scale times its own size, and lays it out again at its new size. Its
    /// top-left corner stays, within the screen. The split tab over it takes the scale too.
    /// </summary>
    private void ScaleToolbar()
    {
        if (_windows == null || _toolbarSurface is not { } surface) return;
        var scale = PanelScaleOf(ToolbarPlaceId);
        if (surface.Panel.Scale == scale) return;
        surface.Panel.Resize(surface.Panel.Frame.Width, surface.Panel.Frame.Height, scale);
        if (_splitTabSurface is { } split) split.Panel.Resize(split.Panel.Frame.Width, split.Panel.Frame.Height, scale);
        FitToolbar();
    }

    /// <summary>
    /// Sizes the toolbar's panel to its content at its scale, after a new scale or a shortcut row shown or hidden. In the lower half of the
    /// screen its bottom edge stays, elsewhere its top edge, within the screen; so a row shown and hidden again puts it back where it was.
    /// </summary>
    private void FitToolbar()
    {
        if (_toolbar is not { } toolbar || _toolbarSurface is not { } surface) return;
        var before = surface.Bounds;
        var scale = surface.Panel.Scale;
        // Every part, not just the toolbar: outside a layout pass a measure skips a part last measured at the same size, though a row
        // added inside it changed it. A shown window's layout pass catches it up; the toolbar's is hidden until its first draw.
        Avalonia.Layout.LayoutHelper.InvalidateSelfAndChildrenMeasure(toolbar);
        toolbar.Measure(Avalonia.Size.Infinity);
        surface.Panel.Resize(WholePixels(toolbar.DesiredSize.Width * scale), WholePixels(toolbar.DesiredSize.Height * scale));
        var size = surface.Bounds.Size;
        var lower = before.Top + before.Height / 2 > GameDevice.ViewportSize(_device).Height / 2;
        var kept = OnScreen(new Point(before.X, lower ? before.Bottom - size.Height : before.Y), size);
        if (kept != surface.Location) _toolbarTakeover?.MoveTo(kept);
    }

    /// <summary>A place moved just enough that something this size there is on screen.</summary>
    private Point OnScreen(Point location, Size size)
    {
        var screen = GameDevice.ViewportSize(_device);
        return new Point(Math.Max(0, Math.Min(screen.Width - size.Width, location.X)), Math.Max(0, Math.Min(screen.Height - size.Height, location.Y)));
    }

    /// <summary>The scale a window's panel draws at: its own, or 1 where this Avalonia cannot scale panels.</summary>
    private double PanelScaleOf(string id) => AvaloniaPanel.CanScale ? _windows!.ScaleOf(id) : 1;

    private void SaveToolbarPlace(Point location)
    {
        if (_inGame) _positionStore.Save(SessionServer(), SessionCharacter(), ToolbarPlaceId, (location, null));
    }

    /// <summary>Whether a screen point is on the toolbar's top edge, where a press moves it as a window's top edge does.</summary>
    private bool OnToolbarMoveEdge(Point point) =>
        _toolbarSurface is { } surface && _toolbarTakeover?.Holds == true && point.Y - surface.Location.Y < WindowManager.ResizeBand;

    /// <summary>The toolbar's frame was pressed: it follows the pointer until the button goes up. Refused while retail's UI is locked.</summary>
    private void BeginToolbarDrag()
    {
        if (_toolbarTakeover?.CanDrag != true || _toolbarSurface is not { } surface) return;
        _toolbarDragOffset = new Size(_pointer.X - surface.Location.X, _pointer.Y - surface.Location.Y);
    }

    /// <summary>Moves the toolbar with the pointer, snapped to the windows and kept on screen. Stops when the takeover no longer lets it move.</summary>
    private void DragToolbar(Point pointer)
    {
        if (_toolbarTakeover?.CanDrag != true || _toolbarSurface is not { } surface)
        {
            _toolbarDragOffset = null;
            return;
        }
        var screen = GameDevice.ViewportSize(_device);
        var size = surface.Bounds.Size;
        var offset = _toolbarDragOffset!.Value;
        var wanted = new Point(pointer.X - offset.Width, pointer.Y - offset.Height);
        if (_windows != null) wanted = _windows.Snap(wanted, size);
        surface.Location = new Point(
            Math.Max(0, Math.Min(screen.Width - size.Width, wanted.X)),
            Math.Max(0, Math.Min(screen.Height - size.Height, wanted.Y)));
    }

    /// <summary>
    /// The button went up: the Dereth toolbar stays where it was dropped, and the place is saved for this character with the LegACEy
    /// windows. A failure gives retail's toolbar back.
    /// </summary>
    private void EndToolbarDrag()
    {
        _toolbarDragOffset = null;
        if (_toolbarSurface is not { } surface) return;
        try
        {
            if (_toolbarTakeover?.MoveTo(surface.Location) == true) Log($"Moved the Dereth toolbar to {surface.Location}.");
        }
        catch (Exception exception) { ToolbarFailed(exception); }
    }

    /// <summary>Makes the toolbar window and takes retail's toolbar. False when the native addresses are not ready, so retail's toolbar is left alone.</summary>
    private bool CreateRetailToolbar()
    {
        if (!NativeUi.Ready || _device == IntPtr.Zero || !RetailPanelCatalogueValid() || !RetailToolbarCatalogueValid()) return false;
        _toolbarPort = new NativeRetailToolbarPort(Log, () => _retailPanels.Any(panel => panel.Holds) ? _inventoryWindowOpen : (bool?)null,
            () => _inGame, _selectionMeters, _appraisal, _inventory.TargetMode);
        // A move of the selected stack splits by retail's amount while the toolbar that shows the amount is up, and only then.
        _inventory.Split ??= new StackSplitter(new NativeRetailSplitPort());
        var toolbar = new RetailToolbar(_toolbarPort, ToggleMenuWindow, MoveToMainPack, BeginToolbarDrag, _portal);
        // Drawn at LegACEy's window scale, as the other LegACEy windows are. The Dereth theme makes it larger than it measures
        // without one, so it is measured again under the theme and the panel takes that size, rounded up so no edge is cut.
        var scale = WindowScale;
        var panel = ObservePanel(AvaloniaPanel.Create(() => toolbar, 1, 1, scale), ToolbarFailed);
        panel.ApplyTheme(new DerethClientTheme());
        _toolbar = toolbar;
        _toolbarSurface = new ScreenSurface(_device, panel);
        FitToolbar();
        _splitTab = new StackSplitTab(_toolbarPort);
        var splitPanel = ObservePanel(AvaloniaPanel.Create(() => _splitTab, 1, 1, scale), ToolbarFailed);
        splitPanel.ApplyTheme(new DerethClientTheme());
        _splitTabSurface = new ScreenSurface(_device, splitPanel);
        _toolbarPlace = new ToolbarPlacePort(new NativeRootPort(NativeUi.ToolbarWindow), () => _toolbarSurface?.Bounds.Size ?? Size.Empty, SaveToolbarPlace);
        _toolbarTakeover = new RetailTakeoverLifecycle(_toolbarPlace, new SurfaceTakeoverPort(_toolbarSurface));
        Log($"Dereth toolbar replacement ready; retail's toolbar window {(NativeUi.GetElement(NativeUi.ToolbarWindow) != IntPtr.Zero ? "found" : "absent")}, " +
            $"its toolbar {(NativeUi.GetElement(NativeUi.Toolbar) != IntPtr.Zero ? "found" : "absent")}.");
        return true;
    }

    /// <summary>Puts shortcut row 1 in the toolbar, and row 2 when it is on for the character, once the takeover holds the toolbar.</summary>
    private void EnsureShortcutBar()
    {
        if (_shortcutBar != null || !CanOpenWindows) return;
        _shortcutBar = NewShortcutRow(0);
        if (_row2Choice.IsOn(SessionServer(), SessionCharacter())) _shortcutRow2 = NewShortcutRow(ShortcutBar.SlotCount);
        ShowShortcutRows();
        Action refresh = () => { _shortcutBar?.Refresh(); _shortcutRow2?.Refresh(); };
        _inventory.Changed += refresh;
        _unsubscribeShortcutItems = () => _inventory.Changed -= refresh;
    }

    /// <summary>Takes both shortcut rows out of the toolbar, stops their refresh and frees their icons.</summary>
    private void CloseShortcutBar()
    {
        _unsubscribeShortcutItems?.Invoke();
        _unsubscribeShortcutItems = null;
        if (_shortcutBar == null) return;
        var rows = new[] { _shortcutBar, _shortcutRow2 };
        _shortcutBar = _shortcutRow2 = null;
        ShowShortcutRows();
        foreach (var row in rows) row?.Dispose();
    }

    /// <summary>
    /// Turns row 2 on or off, and keeps the choice for the character. Row 2 sits under row 1, so it shows only while row 1 is up; the choice is
    /// kept either way, and row 1 brings row 2 when it is on.
    /// </summary>
    private void SetShortcutRow2(bool on)
    {
        if (on && _shortcutBar != null && _shortcutRow2 == null)
        {
            // Row 2 starts at slot 9, the first slot after row 1's nine.
            _shortcutRow2 = NewShortcutRow(ShortcutBar.SlotCount);
            ShowShortcutRows();
        }
        else if (!on && _shortcutRow2 is { } row2)
        {
            _shortcutRow2 = null;
            ShowShortcutRows();
            row2.Dispose();
        }
        _row2Choice.Save(SessionServer(), SessionCharacter(), on);
        if (_settings != null) _settings.ShortcutRow2 = new OptionChoice<bool>(on, SetShortcutRow2);
    }

    /// <summary>Shows the rows there are in the toolbar and sizes the toolbar to them.</summary>
    private void ShowShortcutRows()
    {
        _toolbar?.SetShortcutRows(new[] { _shortcutBar, _shortcutRow2 }.OfType<ShortcutBar>());
        FitToolbar();
    }

    /// <summary>A shortcut row for the toolbar, its slots from <paramref name="firstIndex"/>. It has no frame of its own.</summary>
    private ShortcutBar NewShortcutRow(int firstIndex) =>
        new(firstIndex, _toolbarPort!, ShortcutItemOf, _portal, new ItemDragHost(this), _shortcutDrag);

    /// <summary>The icon and stack count of an object the player holds, from the inventory snapshot; null when it is not held.</summary>
    private ShortcutItem? ShortcutItemOf(uint id)
    {
        var snapshot = _inventory.Snapshot;
        foreach (var item in snapshot.Items)
            if (item.Id == id) return new ShortcutItem(item.Visual, item.StackCount);
        foreach (var worn in snapshot.Wielded)
            if (worn.Id == id) return new ShortcutItem(worn.Visual, worn.StackCount);
        // A pack bound to a slot opens it, as retail's shortcut to a pack does; the main pack is the character itself.
        foreach (var pack in snapshot.SidePacks.Prepend(snapshot.MainPack))
            if (pack.Id == id) return new ShortcutItem(pack.Visual, 1);
        return null;
    }

    /// <summary>
    /// A LegACEy window's item or a shortcut released on the toolbar, as the toolbar's content takes it: a shortcut row binds it to a slot,
    /// and the backpack takes an item. A shortcut released elsewhere on the toolbar is only removed from its row.
    /// </summary>
    private bool DeliverToToolbar(Func<object, Avalonia.Point, bool> deliver)
    {
        if (_toolbarSurface is not { } surface) return false;
        var point = surface.Panel.ToContent(_pointer.X - surface.Location.X, _pointer.Y - surface.Location.Y);
        try { return deliver(surface.Panel.Content, point); }
        catch (Exception exception)
        {
            ToolbarFailed(exception);
            return false;
        }
    }

    /// <summary>
    /// Moves the items to the main pack's free slots in order, as retail's backpack takes them. An item already in the main pack is skipped,
    /// and the rest stop when the pack is full. True when one moved.
    /// </summary>
    private bool MoveToMainPack(IReadOnlyList<uint> itemIds)
    {
        var snapshot = _inventory.Snapshot;
        var main = snapshot.MainPack;
        using var free = snapshot.FreeSlots(main).GetEnumerator();
        var moved = false;
        foreach (var itemId in itemIds)
        {
            if (snapshot.Items.Any(item => item.Id == itemId && item.Container == main.Id)) continue;
            if (!free.MoveNext()) break;
            _inventory.MoveToContainer(itemId, main.Id, free.Current);
            moved = true;
        }
        return moved;
    }

    /// <summary>Gives retail's toolbar back and drops the Dereth toolbar. A give-back that fails is logged and retried on the next call.</summary>
    private void ReleaseRetailToolbar()
    {
        _inventory.Split = null;
        CloseShortcutBar();
        if (_toolbarTakeover != null)
        {
            try { _toolbarTakeover.Dispose(); }
            catch (Exception exception)
            {
                // With the switch off this runs every frame: the failure is logged once, and the give-back is retried each frame.
                if (!_toolbarReleaseLogged) Log($"Could not give the retail toolbar back; retrying: {exception}");
                _toolbarReleaseLogged = true;
                return;
            }
            _toolbarTakeover = null;
            _toolbarPlace = null;
            // The next toolbar (Custom again, after Native) starts from the saved place, not from where retail's stands.
            _toolbarPlaceLoaded = false;
            _toolbarReleaseLogged = false;
        }
        _toolbarSurface?.Dispose();
        _toolbarSurface = null;
        _toolbar = null;
        _splitTabSurface?.Dispose();
        _splitTabSurface = null;
        _splitTab = null;
        _toolbarPort = null;
    }

    /// <summary>
    /// A failure while a window takes a retail drop goes to that window's error handler. The toolbar is not a managed window, so its
    /// failure goes to the toolbar; a window with no handler disables the client UI, as a failed delivery does.
    /// </summary>
    private void DropFailed(string id, Exception exception)
    {
        if (id is ToolbarSurfaceId or SplitTabSurfaceId) ToolbarFailed(exception);
        else if (_windowFailures.TryGetValue(id, out var failed)) failed(exception);
        else Disable(exception);
    }

    private void ToolbarFailed(Exception exception)
    {
        _toolbarFailed = true;
        Log($"Dereth toolbar disabled; retail's toolbar is back: {exception}");
        try { ReleaseRetailToolbar(); }
        catch (Exception releaseError) { Log($"Could not give the retail toolbar back: {releaseError}"); }
    }

    private const int VkReturn = 0x0D;
    // WM_KEYDOWN's lParam bit 24: an extended key, the keypad's Enter for VK_RETURN.
    private const int ExtendedKey = 1 << 24;

    /// <summary>
    /// Gives the chat window's box the keyboard when retail's chat is held and neither a field of ours nor one of retail's has it. True when
    /// it did, so the Enter is eaten. Never without the Enter entries: retail's hidden entry could not be let go then. A failure turns the
    /// chat window off, not the whole UI.
    /// </summary>
    private bool TryFocusRetailChat()
    {
        try
        {
            if (_chatNatives?.Enter != true || _chatWindows?.Main is not { } main || _chatTakeover?.Holds != true) return false;
            var focused = SurfaceById(_inputRouter.FocusedSurfaceId);
            if (focused?.Panel.WantsKeyboard == true || _chatPort?.RetailFieldHasKeyboard() != false) return false;
            main.FocusInput();
            _inputRouter.FocusSurface(ChatWindowId);
            return true;
        }
        catch (Exception exception)
        {
            ChatFailed(exception);
            return false;
        }
    }

    private void TickRetailChat()
    {
        try
        {
            var switchOn = SwitchOn(RetailChatSwitchFile, ref _lastChatSwitchCheck, ref _chatSwitch);
            // Turning the switch off and on again retries a chat window that failed.
            if (!switchOn) _chatFailed = false;
            // Retail's chat windows are given back if the switch goes off, the post-UI draw is gone, the LegACEy windows failed or it failed.
            var canDraw = _postUiDrawHook?.IsInstalled == true && !(_windows != null && !_windowsEnabled);
            if (_chatTakeover != null && (!canDraw || _chatFailed || !switchOn))
            {
                // The same character is still playing, so the lines and sent text come back with the Dereth chat; logoff forgets them.
                _carriedChatHistory = _chatWindows?.History ?? _carriedChatHistory;
                ReleaseRetailChat();
                if (_chatTakeover != null) return;
            }
            if (!canDraw || !switchOn || _chatFailed)
            {
                ListenToRetailChat();
                return;
            }
            // Retail's chat is held from portal space at login on, like the toolbar; the window opens once the windows can, and the port
            // keeps the lines that come before it. Logoff gives it back, and character select holds it again.
            if (_chatTakeover == null && !HoldRetailChat()) return;
            _chatTakeover!.Tick(GameDevice.ViewportSize(_device));
            if (!_inGame) return;
            if (_chatWindows == null && !OpenRetailChat()) return;
            _chatPort!.Poll(typing: SurfaceById(_inputRouter.FocusedSurfaceId)?.Panel.WantsKeyboard == true);
            _chatWindows?.Refresh();
        }
        catch (Exception exception)
        {
            ChatFailed(exception);
        }
    }

    /// <summary>
    /// Holds retail's chat windows hidden and starts the port, once its native addresses match. False when they do not, so retail's chat is
    /// left alone and the next frame tries again. A port whose registrar is not ready yet attaches when the window opens.
    /// </summary>
    private bool HoldRetailChat()
    {
        if (!NativeUi.Ready || _device == IntPtr.Zero) return false;
        var natives = ChatNatives();
        if (!natives.ChatWindow) return false;
        var roots = new List<IRetailTakeoverPort>();
        foreach (var id in new[] { RetailChatCatalogue.MainChatWindow }.Concat(RetailChatCatalogue.FloatingChats))
            roots.Add(new RetailHeldWindows.RootPort(id));
        // A port that listened while retail's chat was shown carries on, so no line falls between the two.
        _chatPort ??= NewChatPort(natives);
        _chatTakeover = new RetailHeldWindows(roots);
        try { _chatPort.Attach(); }
        catch (InvalidOperationException) { }
        Log($"Retail's chat windows held: {roots.Count}.");
        return true;
    }

    /// <summary>
    /// Opens the Dereth chat window over the held retail chat. False when the windows cannot open yet, so the next frame tries again.
    /// </summary>
    private bool OpenRetailChat()
    {
        if (!CanOpenWindows) return false;
        var port = _chatPort!;
        var server = SessionServer();
        var character = SessionCharacter();
        var chats = new RetailChatWindows(port, _windows!, _positionStore, _chatTabs, server, character, () => _pointer, (definition, content, location) => OpenWindowCore(definition, content, location, ChatFailed), ReleaseFeatureWindow,
            copy: CopyToClipboard, paste: PasteFromClipboard, carried: _carriedChatHistory);
        // A history that listened while retail's chat was shown stops taking lines: the windows' own history takes them from here.
        _carriedChatHistory?.Dispose();
        _carriedChatHistory = null;
        // The chat's own text size control moves the Settings window's value too.
        chats.TextSizeChanged += size => { if (_settings != null) _settings.ChatTextSize = new OptionChoice<int>(size, SetChatTextSize); };
        // The main window starts at the bottom left, above the toolbar, until the player moves it; its place is then kept per character.
        var screen = _windows!.Screen;
        var beside = new Point(12, Math.Max(0, screen.Height - (int)Math.Round(RetailChatWindows.DesignHeight * _windows!.ScaleOf(ChatWindowId)) - 150));
        _chatWindows = chats;
        try
        {
            chats.Open(beside);
            port.Attach();
        }
        catch
        {
            ReleaseRetailChat();
            throw;
        }
        Log($"Dereth chat window ready with {chats.Windows.Count} window(s).");
        return true;
    }

    private NativeRetailChatPort NewChatPort(RetailChatNatives natives) =>
        new(Log, natives, (opcode, payload) => _channelTransport?.SendAction(opcode, payload) ?? false);

    /// <summary>
    /// While retail's chat is shown in a session that had the Dereth chat, a port keeps the Dereth chat's history filling, so the lines that
    /// come meanwhile are there when it comes back. Retail's chat is left as it is. A port that cannot listen is logged once and left off
    /// until logoff.
    /// </summary>
    private void ListenToRetailChat()
    {
        if (!_inGame || _chatListenFailed || _carriedChatHistory is not { } carried) return;
        try
        {
            if (_chatPort == null)
            {
                var natives = ChatNatives();
                if (!NativeUi.Ready || !natives.ChatWindow) return;
                // Kept before it attaches, so a failure after retail registered its handler still stops it.
                _chatPort = NewChatPort(natives);
                carried.Dispose();
                _carriedChatHistory = new RetailChatHistory(_chatPort, carried);
            }
            // Retail's notice registrar may not be ready yet; the next frame tries again.
            try { _chatPort.Attach(); }
            catch (InvalidOperationException) { return; }
            _chatPort.Poll(holdsEntry: false);
        }
        catch (Exception exception)
        {
            _chatListenFailed = true;
            Log($"Dereth chat stopped listening while retail's chat is shown; lines until it comes back are not kept: {exception}");
            ReleaseRetailChat();
        }
    }

    private RetailChatNatives ChatNatives() => _chatNatives ??= ValidateChatNatives();

    private RetailChatNatives ValidateChatNatives()
    {
        try
        {
            var natives = RetailChatCatalogue.Validate(NativeUi.ReadMemory, Log);
            if (!natives.ChatWindow) Log("Dereth chat window is off: the running client does not match its native entries.");
            return natives;
        }
        catch (Exception exception)
        {
            Log($"Dereth chat window is off: its native addresses could not be checked: {exception.Message}");
            return new RetailChatNatives(false, false, false, false, false, false, false);
        }
    }

    /// <summary>
    /// Gives retail's chat windows back, stops the port and closes the Dereth chat window. A give-back that fails is logged and retried on the
    /// next call, so retail's chat is never left hidden.
    /// </summary>
    private void ReleaseRetailChat()
    {
        if (_chatTakeover != null)
        {
            try { _chatTakeover.Release(); }
            catch (Exception exception)
            {
                if (!_chatReleaseLogged) Log($"Could not give the retail chat back; retrying: {exception}");
                _chatReleaseLogged = true;
                return;
            }
            _chatTakeover = null;
            _chatReleaseLogged = false;
        }
        // Cleared first, so a window that throws on close cannot leave them half released; the port is stopped whatever the windows do.
        var windows = _chatWindows;
        var port = _chatPort;
        _chatWindows = null;
        _chatPort = null;
        try { windows?.Dispose(); }
        finally { port?.Dispose(); }
    }

    /// <summary>
    /// Ctrl+C while text is selected in a chat log copies it, and the game sees none of the C's down, character or up, so it does not act on
    /// the key. A press outside the chat windows lets go of the selection, so the key is the game's again. True eats the message. A field of
    /// ours with the keyboard gets its keys as before: the chat box copies the log's selection itself.
    /// </summary>
    private bool CopyChatSelection(int message, int wParam, int lParam)
    {
        const int VkControl = 0x11, VkC = 0x43, CtrlC = 0x03;
        if (_chatWindows is not { } chats) return false;
        if (message is InputRouterService.WmLButtonDown or InputRouterService.WmRButtonDown)
        {
            var at = new Point((short)(lParam & 0xffff), (short)((lParam >> 16) & 0xffff));
            Guard(() => chats.ClearLogSelections(_windows?.HitTest(at)?.Id));
            return false;
        }
        if (_eatingCtrlC && (message == InputRouterService.WmChar && wParam == CtrlC || message == InputRouterService.WmKeyUp && wParam == VkC))
        {
            if (message == InputRouterService.WmKeyUp) _eatingCtrlC = false;
            return true;
        }
        if (message != InputRouterService.WmKeyDown || wParam != VkC || (GetKeyState(VkControl) & 0x8000) == 0) return false;
        if (SurfaceById(_inputRouter.FocusedSurfaceId)?.Panel.WantsKeyboard == true || chats.LogSelection is not { Length: > 0 } selection) return false;
        CopyToClipboard(selection);
        _eatingCtrlC = true;
        return true;
    }

    /// <summary>
    /// Puts text on the Windows clipboard as Unicode text, for text copied from the chat log. A clipboard another program holds is logged and
    /// the copy is dropped; it never fails the chat.
    /// </summary>
    private void CopyToClipboard(string text)
    {
        const uint GmemMoveable = 0x0002;
        const uint CfUnicodeText = 13;
        if (!OpenClipboard(_gameWindow))
        {
            Log($"Could not copy chat text: the clipboard is busy ({Marshal.GetLastWin32Error()}).");
            return;
        }
        var memory = IntPtr.Zero;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
            if (memory == IntPtr.Zero) return;
            var target = GlobalLock(memory);
            if (target == IntPtr.Zero) return;
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
            GlobalUnlock(memory);
            // The clipboard owns the memory once it takes it.
            if (SetClipboardData(CfUnicodeText, memory) != IntPtr.Zero) memory = IntPtr.Zero;
        }
        finally
        {
            if (memory != IntPtr.Zero) GlobalFree(memory);
            CloseClipboard();
        }
    }

    /// <summary>The Windows clipboard's Unicode text, for Ctrl+V in a chat box; null when it holds none or another program holds it.</summary>
    private string? PasteFromClipboard()
    {
        const uint CfUnicodeText = 13;
        if (!OpenClipboard(_gameWindow))
        {
            Log($"Could not paste into the chat: the clipboard is busy ({Marshal.GetLastWin32Error()}).");
            return null;
        }
        try
        {
            var memory = GetClipboardData(CfUnicodeText);
            if (memory == IntPtr.Zero) return null;
            var source = GlobalLock(memory);
            if (source == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(source); }
            finally { GlobalUnlock(memory); }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private void ChatFailed(Exception exception)
    {
        _chatFailed = true;
        Log($"Dereth chat window disabled; retail's chat is back: {exception}");
        try { ReleaseRetailChat(); }
        catch (Exception releaseError) { Log($"Could not give the retail chat back: {releaseError}"); }
    }

    /// <summary>
    /// Holds retail's radar and draws the Dereth radar over it unless the <c>retail-radar-native</c> file is beside the DLL. Off, retail's
    /// radar is left as it is and nothing new draws. A failure gives retail's radar back and stops the Dereth radar until the switch is turned off and on.
    /// </summary>
    private void TickRetailRadar()
    {
        try
        {
            var switchOn = SwitchOn(RetailRadarSwitchFile, ref _lastRadarSwitchCheck, ref _radarSwitch);
            // Turning the switch off and on again retries a radar that failed.
            if (!switchOn) _radarFailed = false;
            // Retail's radar is given back if the switch goes off, the post-UI draw is gone, or the LegACEy windows failed.
            var canDraw = _postUiDrawHook?.IsInstalled == true && !(_windows != null && !_windowsEnabled);
            if (_radarTakeover != null && (!canDraw || _radarFailed || !switchOn))
                ReleaseRetailRadar();
            // Switched off, the ground's DATs go too once no radar reads them; switching on loads them again.
            if (!switchOn && _radar == null && _radarDatLoad != null) DisposeRadarGround();
            if (!canDraw || !switchOn || _radarFailed) return;
            if (_radarTakeover == null && !HoldRetailRadar()) return;
            _radarTakeover!.Tick(GameDevice.ViewportSize(_device));
            if (!_inGame) return;
            if (_radar == null && !OpenRetailRadar()) return;
            _radar?.Refresh();
        }
        catch (Exception exception)
        {
            RadarFailed(exception);
        }
    }

    /// <summary>Holds retail's radar hidden and starts the radar's port, once its native addresses match. False when they do not.</summary>
    private bool HoldRetailRadar()
    {
        if (!NativeUi.Ready || _device == IntPtr.Zero || _portal == null) return false;
        var natives = HudNatives();
        if (!natives.RadarWindow) return false;
        _radarPort = new NativeRadarPort(natives, ObjectName, Bridge.SelectItem);
        // Read before the radar is held, so the Dereth radar opens where retail's was.
        var retail = NativeUi.GetElement(RetailHudCatalogue.Radar);
        _radarPlace = retail == IntPtr.Zero ? null : NativeUi.GetBounds(retail).Location;
        _radarTakeover = new RetailHeldWindows(RetailHudCatalogue.RadarWindows);
        // The ground's DATs open off the UI thread; the radar draws dots on dark until its first tiles are built.
        _radarDatLoad ??= LoadRadarGround(_portal);
        _radarIndoorLoad ??= LoadRadarIndoors(_portal.Path);
        Log("Retail's radar held.");
        return true;
    }

    private static Task<GroundDat?> LoadRadarGround(PortalDat portal) => Task.Run(() =>
    {
        try { return new GroundDat(portal); }
        catch (Exception exception)
        {
            Log($"Dereth radar has no ground; it draws dots on dark: {exception.Message}");
            return null;
        }
    });

    // The ground's tiles, once its DATs have opened; null before that or without them.
    private GroundTiles? RadarGround()
    {
        if (_radarTiles != null) return _radarTiles;
        if (_radarDatLoad is not { IsCompletedSuccessfully: true } load || load.Result is not { } dat) return null;
        return _radarTiles = new GroundTiles(dat, Log);
    }

    // The indoor plans' DATs open beside the portal, as the ground's do. Off the UI thread; a failure leaves the radar drawing dots on dark indoors.
    private static Task<IndoorDat?> LoadRadarIndoors(string portalPath) => Task.Run(() =>
    {
        try { return new IndoorDat(portalPath, IOPath.Combine(IOPath.GetDirectoryName(portalPath)!, "client_cell_1.dat"), Log); }
        catch (Exception exception)
        {
            Log($"Dereth radar has no indoor plans; it draws dots on dark indoors: {exception.Message}");
            return null;
        }
    });

    // The indoor plans, once their DATs have opened; null before that or without them.
    private IndoorPlanCache? RadarPlans()
    {
        if (_radarPlans != null) return _radarPlans;
        if (_radarIndoorLoad is not { IsCompletedSuccessfully: true } load || load.Result is not { } dat) return null;
        return _radarPlans = new IndoorPlanCache(dat.ReadPlan, Log, owned: dat);
    }

    /// <summary>Opens the Dereth radar over the held retail radar. False when the windows cannot open yet, so the next frame tries again.</summary>
    private bool OpenRetailRadar()
    {
        if (!CanOpenWindows || _radarPort == null) return false;
        var screen = _windows!.Screen;
        var at = _radarPlace ?? new Point(Math.Max(0, screen.Width - (int)Math.Round(RetailRadarWindow.DesignWidth * _windows!.ScaleOf(RadarWindowId)) - 12), 12);
        var port = _radarPort;
        var window = new RetailRadarWindow(port, RadarGround, RadarPlans, LoadRadarOptions(), SaveRadarOptions,
            () => (uint)Bridge.Fact(BridgeFact.CurrentSelection), () => _windows?.BeginMove(RadarWindowId, _pointer), _radarPlaceNames.NameOf);
        window.CoordinatesClicked += text => _chatWindows?.Main?.Insert(text);
        window.PingRequested += Ping;
        // The radar has no title bar: it drags from its frame, so its title bar is only one pixel tall.
        var definition = new WindowDefinition(RadarWindowId, "Radar", RetailRadarWindow.DesignWidth, RetailRadarWindow.DesignHeight, titleBarHeight: 1,
            theme: new DerethClientTheme(), resizing: RetailRadarWindow.Resizing(() => port.UiLocked), dragsFromContent: true);
        _radar = window;
        try { OpenWindowCore(definition, window, at, RadarFailed); }
        catch
        {
            _radar = null;
            throw;
        }
        Log("Dereth radar ready.");
        return true;
    }

    /// <summary>The radar's options for this character, or the defaults before they are saved.</summary>
    private RadarOptions LoadRadarOptions() =>
        _settingsStore.Load(SessionServer(), SessionCharacter(), RadarOptionsKey) is { Location: var saved }
            ? RadarOptions.Decode(saved.X, saved.Y)
            : new RadarOptions();

    private void SaveRadarOptions(RadarOptions options)
    {
        var (flags, zoom) = options.Encode();
        _settingsStore.Save(SessionServer(), SessionCharacter(), RadarOptionsKey, (new Point(flags, zoom), null));
        if (_settings != null) _settings.Radar = new OptionChoice<RadarOptions>(options, SetRadarOptions);
    }

    /// <summary>The radar's options set from the Settings window: kept for the character, and an open radar redraws with them.</summary>
    private void SetRadarOptions(RadarOptions options)
    {
        SaveRadarOptions(options);
        _radar?.SetOptions(options);
    }

    /// <summary>Gives retail's radar back and closes the Dereth radar. A give-back that fails is logged and retried on the next call.</summary>
    private void ReleaseRetailRadar()
    {
        if (_radarTakeover != null)
        {
            try { _radarTakeover.Release(); }
            catch (Exception exception)
            {
                if (!_radarReleaseLogged) Log($"Could not give the retail radar back; retrying: {exception}");
                _radarReleaseLogged = true;
                return;
            }
            _radarTakeover = null;
            _radarReleaseLogged = false;
        }
        // Cleared first, as chat's is, so a radar that throws on close cannot leave the port and window half released.
        var radar = _radar;
        _radarPort = null;
        _radar = null;
        radar?.Dispose();
        ReleaseFeatureWindow(RadarWindowId);
    }

    /// <summary>
    /// Drops the ground's and the indoor plans' DATs at unload or switch-off, after the radar that reads them is gone. A load still running is
    /// disposed when it lands; the plans' DAT is disposed once their reads in flight have finished.
    /// </summary>
    private void DisposeRadarGround()
    {
        if (_radarDatLoad is { } load) DisposeWhenLoaded(load);
        if (_radarPlans != null) _radarPlans.Dispose();
        else if (_radarIndoorLoad is { } indoors) DisposeWhenLoaded(indoors);
        _radarDatLoad = null;
        _radarTiles = null;
        _radarIndoorLoad = null;
        _radarPlans = null;
    }

    private static void DisposeWhenLoaded<T>(Task<T?> load) where T : class, IDisposable
    {
        if (load.IsCompletedSuccessfully) load.Result?.Dispose();
        else _ = load.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result?.Dispose(); }, TaskScheduler.Default);
    }

    private void RadarFailed(Exception exception)
    {
        _radarFailed = true;
        Log($"Dereth radar disabled; retail's radar is back: {exception}");
        try { ReleaseRetailRadar(); }
        catch (Exception releaseError) { Log($"Could not give the retail radar back: {releaseError}"); }
    }

    /// <summary>
    /// Holds retail's attack panel in melee and missile and draws the Dereth attack bar over it, unless the <c>retail-attack-native</c> file is
    /// beside the DLL; with it retail's panel is left as it is. The bar's port is polled each frame while its window is open.
    /// </summary>
    private void TickRetailAttack()
    {
        _attack ??= new RetailAttackTakeover(AttackNatives, HoldRetailAttack, RetailAttackPlace, OpenAttackBar, Log);
        try
        {
            var switchOn = SwitchOn(RetailAttackSwitchFile, ref _lastAttackSwitchCheck, ref _attackSwitch);
            // Retail's panel is given back if the post-UI draw is gone or the LegACEy windows failed, as the radar's is.
            var canDraw = _postUiDrawHook?.IsInstalled == true && !(_windows != null && !_windowsEnabled);
            var stance = switchOn && _inGame ? (RetailStance)Bridge.Fact(BridgeFact.CombatMode) : RetailStance.Peace;
            _attack.Tick(switchOn, canDraw, _inGame, stance, GameDevice.ViewportSize(_device));
            _attackPort?.Poll();
        }
        catch (Exception exception)
        {
            AttackFailed(exception);
        }
    }

    /// <summary>
    /// The attack bar's natives, checked once a session; null until the UI can be read. A check that cannot complete turns every feature off.
    /// </summary>
    private bool? AttackNatives()
    {
        if (!NativeUi.Ready || _device == IntPtr.Zero) return null;
        return _attackNatives ??= ValidateAttackNatives();
    }

    private bool ValidateAttackNatives()
    {
        try { return RetailAttackCatalogue.Validate(NativeUi.ReadMemory, Log); }
        catch (Exception exception)
        {
            Log($"Dereth attack bar is off: its native addresses could not be checked: {exception.Message}");
            return false;
        }
    }

    /// <summary>Retail's attack panel, held by its placed id: hidden while the takeover holds it.</summary>
    private static RetailHeldWindows HoldRetailAttack() => new(new[] { RetailAttackCatalogue.CombatPanel });

    /// <summary>
    /// Where the bar opens: centred over retail's attack panel. With the panel not laid out, at the bottom centre of the screen. The window keeps
    /// the place the character left it at once it has one.
    /// </summary>
    private Point RetailAttackPlace()
    {
        const int ScreenBottomGap = 120;
        var screen = _windows?.Screen ?? Size.Empty;
        // Centred at the size the bar first draws at: its design size times the window scale.
        var scale = _windows == null ? 1 : PanelScaleOf(DerethAttackBarWindow.WindowId);
        int width = (int)Math.Round(DerethAttackBarArt.DesignWidth * scale), height = (int)Math.Round(DerethAttackBarArt.DesignHeight * scale);
        var element = NativeUi.GetElement(RetailAttackCatalogue.CombatPanel);
        if (element == IntPtr.Zero)
            return new Point(Math.Max(0, (screen.Width - width) / 2), Math.Max(0, screen.Height - height - ScreenBottomGap));
        var bounds = NativeUi.GetBounds(element);
        return new Point(bounds.X + (bounds.Width - width) / 2, bounds.Y + (bounds.Height - height) / 2);
    }

    /// <summary>
    /// Opens the Dereth attack bar at <paramref name="at"/> with its port. Null while the windows cannot open yet, so the next frame asks
    /// again. The takeover opens it only in melee and missile, where the bar is always shown, and closes it outside them.
    /// </summary>
    private IDisposable? OpenAttackBar(Point at)
    {
        if (!CanOpenWindows) return null;
        var port = new NativeAttackBarPort(HudNatives().Lock);
        var window = new DerethAttackBarWindow(port, () => _windows?.BeginMove(DerethAttackBarWindow.WindowId, _pointer));
        var definition = DerethAttackBarWindow.DefinitionOf(() => port.UiLocked);
        try { OpenWindowCore(definition, window, at, AttackFailed); }
        catch
        {
            window.Dispose();
            throw;
        }
        _attackPort = port;
        return new AttackWindowHandle(this, window, definition.Id);
    }

    private void AttackFailed(Exception exception) => _attack?.Fail(exception);

    private sealed class AttackWindowHandle : IDisposable
    {
        private readonly ClientUiRuntime _owner;
        private readonly DerethAttackBarWindow _window;
        private readonly string _windowId;

        public AttackWindowHandle(ClientUiRuntime owner, DerethAttackBarWindow window, string windowId)
        {
            _owner = owner;
            _window = window;
            _windowId = windowId;
        }

        public void Dispose()
        {
            _owner._attackPort = null;
            _window.Dispose();
            _owner.ReleaseFeatureWindow(_windowId);
        }
    }

    /// <summary>
    /// Whether a feature's switch is on: on while its file is missing beside the DLL, or while a write this session could not change it
    /// holds (see <see cref="SwitchCustom"/>). The file is looked for once a second.
    /// </summary>
    private bool SwitchOn(string file, ref DateTime lastCheck, ref bool on)
    {
        var now = DateTime.UtcNow;
        if (now - lastCheck >= TimeSpan.FromSeconds(1))
        {
            lastCheck = now;
            on = SwitchCustom(IOPath.Combine(PluginDirectory, file));
        }
        return on;
    }

    /// <summary>
    /// Holds retail's vitals windows and draws the Dereth vitals window at their place, unless the <c>retail-vitals-native</c> file is beside
    /// the DLL; with it nothing is held and nothing draws. Each frame the takeover is given the state, and the player's clock.
    /// </summary>
    private void TickRetailVitals()
    {
        _vitals ??= new RetailVitalsTakeover(HoldRetailVitals, RetailVitalsPlace, OpenVitals, Log);
        var switchOn = SwitchOn(RetailVitalsSwitchFile, ref _lastVitalsSwitchCheck, ref _vitalsSwitch);
        // Retail's vitals are held from portal space at login on, like chat; the window opens in game.
        var canDraw = _postUiDrawHook?.IsInstalled == true && !(_windows != null && !_windowsEnabled);
        // The player picked the other layout: the window closes now, and the Tick below opens the chosen one where it was left.
        if (_vitalsLayoutPicked)
        {
            _vitalsLayoutPicked = false;
            _vitals.CloseWindow();
        }
        _vitals.Tick(switchOn, canDraw, _inGame, GameDevice.ViewportSize(_device), _vitalsClock.Elapsed);
        if (_inGame && _vitals.Holds) LogVitae();
    }

    /// <summary>Retail's vitals windows, held, once the running client's addresses match. Null leaves retail's alone; the next frame asks again.</summary>
    private RetailHeldWindows? HoldRetailVitals()
    {
        if (!NativeUi.Ready || _device == IntPtr.Zero) return null;
        return HudNatives().Vitals ? new RetailHeldWindows(RetailHudCatalogue.VitalsWindows, oneShows: true) : null;
    }

    /// <summary>
    /// Where retail's vitals stand: the place of whichever of its two windows is shown. The window opens there until the player moves it. With
    /// neither shown, the first that exists.
    /// </summary>
    private static Point RetailVitalsPlace()
    {
        var first = IntPtr.Zero;
        foreach (var id in RetailHudCatalogue.VitalsWindows)
        {
            var element = NativeUi.GetElement(id);
            if (element == IntPtr.Zero) continue;
            if (NativeUi.IsVisible(element)) return NativeUi.GetBounds(element).Location;
            if (first == IntPtr.Zero) first = element;
        }
        return first == IntPtr.Zero ? Point.Empty : NativeUi.GetBounds(first).Location;
    }

    /// <summary>
    /// Opens the Dereth vitals window in the character's layout at <paramref name="at"/>, in the window store's place for this character once it
    /// has one. Null while the windows cannot open yet, so the next frame asks again.
    /// </summary>
    private IRetailVitalsWindow? OpenVitals(Point at)
    {
        if (!CanOpenWindows) return null;
        var layout = _vitalsLayouts.Load(SessionServer(), SessionCharacter());
        var definition = DerethVitalsWindow.DefinitionOf(layout, () => NativeUi.IsUiLocked);
        var view = new DerethVitalsWindow(_gameState, () => NativeUi.IsUiLocked, () => _windows?.BeginMove(definition.Id, _pointer), layout, ChooseVitalsLayout,
            _vitalsLayouts.RetailBars(SessionServer(), SessionCharacter()));
        OpenWindowCore(definition, view, at, VitalsFailed);
        return new VitalsWindowHandle(this, view, definition.Id);
    }

    /// <summary>
    /// Keeps the layout the player picked for the character. The window reopens in it on the next frame, not inside the click. The Settings
    /// window's layout is this same change.
    /// </summary>
    private void ChooseVitalsLayout(VitalsLayout layout)
    {
        _vitalsLayouts.Save(SessionServer(), SessionCharacter(), layout);
        _vitalsLayoutPicked = true;
        if (_settings != null) _settings.Vitals = new OptionChoice<VitalsLayout>(layout, ChooseVitalsLayout);
    }

    /// <summary>Keeps whether the character's vitals bars are retail-style; the window reopens with them on the next frame, as for a layout.</summary>
    private void SetVitalsRetailBars(bool on)
    {
        _vitalsLayouts.SaveRetailBars(SessionServer(), SessionCharacter(), on);
        _vitalsLayoutPicked = true;
        if (_settings != null) _settings.VitalsRetailBars = new OptionChoice<bool>(on, SetVitalsRetailBars);
    }

    /// <summary>
    /// The chat's text size set from the Settings window: applied to the open chat, which then publishes the value itself; or kept for the
    /// chat to open with when the chat is closed, and published here.
    /// </summary>
    private void SetChatTextSize(int percent)
    {
        if (_chatWindows != null)
        {
            _chatWindows.SetTextSize(percent);
            return;
        }
        var size = RetailChatWindows.ClampTextSize(percent);
        RetailChatWindows.SaveTextSize(_positionStore, SessionServer(), SessionCharacter(), size);
        if (_settings != null) _settings.ChatTextSize = new OptionChoice<int>(size, SetChatTextSize);
    }

    /// <summary>
    /// The chat's fade delay set from the Settings window: applied to the open chat, or kept for the chat to open with when it is closed; either
    /// way the value is published back to the Settings window.
    /// </summary>
    private void SetChatFadeSeconds(int seconds)
    {
        var fade = RetailChatWindows.FadeOrDefault(seconds);
        if (_chatWindows != null) _chatWindows.SetFadeSeconds(fade);
        else RetailChatWindows.SaveFadeSeconds(_positionStore, SessionServer(), SessionCharacter(), fade);
        if (_settings != null) _settings.ChatFade = new OptionChoice<int>(fade, SetChatFadeSeconds);
    }

    /// <summary>
    /// The chat log backdrop's active opacity set from the Settings window: applied to the open chat, or kept for the chat to open with when it is
    /// closed; either way the value is published back to the Settings window.
    /// </summary>
    private void SetChatActiveOpacity(int percent)
    {
        var opacity = RetailChatWindows.ClampOpacity(percent);
        if (_chatWindows != null) _chatWindows.SetActiveOpacity(opacity);
        else RetailChatWindows.SaveOpacity(_positionStore, SessionServer(), SessionCharacter(), RetailChatWindows.ActiveOpacityKey, opacity);
        if (_settings != null) _settings.ChatActiveOpacity = new OptionChoice<int>(opacity, SetChatActiveOpacity);
    }

    /// <summary>The chat log backdrop's inactive opacity set from the Settings window; see <see cref="SetChatActiveOpacity"/>.</summary>
    private void SetChatInactiveOpacity(int percent)
    {
        var opacity = RetailChatWindows.ClampOpacity(percent);
        if (_chatWindows != null) _chatWindows.SetInactiveOpacity(opacity);
        else RetailChatWindows.SaveOpacity(_positionStore, SessionServer(), SessionCharacter(), RetailChatWindows.InactiveOpacityKey, opacity);
        if (_settings != null) _settings.ChatInactiveOpacity = new OptionChoice<int>(opacity, SetChatInactiveOpacity);
    }

    /// <summary>The chat's name colour set from the Settings window; see <see cref="SetChatActiveOpacity"/>.</summary>
    private void SetChatNameColour(int index)
    {
        var choice = RetailChatWindows.NameColourOrDefault(index);
        if (_chatWindows != null) _chatWindows.SetNameColour(choice);
        else RetailChatWindows.SaveNameColour(_positionStore, SessionServer(), SessionCharacter(), choice);
        if (_settings != null) _settings.ChatNameColour = new OptionChoice<int>(choice, SetChatNameColour);
    }

    private void VitalsFailed(Exception exception) => _vitals?.Fail(exception);

    private RetailHudNatives HudNatives() => _hudNatives ??= ValidateHudNatives();

    private RetailHudNatives ValidateHudNatives()
    {
        try { return RetailHudCatalogue.Validate(NativeUi.ReadMemory, Log); }
        catch (Exception exception)
        {
            Log($"Dereth HUD is off: its native addresses could not be checked: {exception.Message}");
            return new RetailHudNatives(false, false, false, false, false);
        }
    }

    /// <summary>
    /// Logs the raw vitae value when it changes, so the play-test can settle its unit. The vitals draw no hatching from it until the unit is
    /// known. It is read at most once a second, and only while retail's vitals are held.
    /// </summary>
    private void LogVitae()
    {
        var now = _vitalsClock.Elapsed;
        if (now < _nextVitaeRead) return;
        _nextVitaeRead = now + TimeSpan.FromSeconds(1);
        int vitae;
        try { vitae = Bridge.Fact(BridgeFact.Vitae); }
        catch (Exception exception)
        {
            // Logged once, not every frame: the vitals keep drawing without the vitae value.
            if (!_vitaeReadLogged) Log($"Could not read the vitae fact: {exception.Message}");
            _vitaeReadLogged = true;
            return;
        }
        if (vitae == _loggedVitae) return;
        _loggedVitae = vitae;
        Log($"Dereth vitals: CharacterFilter.Vitae is {vitae} (unit unconfirmed; no vitae hatching is drawn).");
    }

    private sealed class VitalsWindowHandle : IRetailVitalsWindow
    {
        private readonly ClientUiRuntime _owner;
        private readonly DerethVitalsWindow _view;
        private readonly string _windowId;

        public VitalsWindowHandle(ClientUiRuntime owner, DerethVitalsWindow view, string windowId)
        {
            _owner = owner;
            _view = view;
            _windowId = windowId;
        }

        public void Refresh(TimeSpan now) => _view.Refresh(now);
        public void Dispose() => _owner.ReleaseFeatureWindow(_windowId);
    }

    /// <summary>Translate Decal's raw messages into router decisions and Avalonia.Headless input. True eats the message.</summary>
    public bool WindowMessage(int hwnd, int message, int wParam, int lParam)
    {
        if (_failed || !_inGame || _portal == null)
            return false;
        // Enter in the game, with retail's chat held and no field of ours taking keys, opens the Dereth chat window's box and eats the key.
        // The keypad's Enter (an extended key) stays retail's, as the spike recommends.
        if (message == InputRouterService.WmKeyDown && wParam == VkReturn && (lParam & ExtendedKey) == 0 && TryFocusRetailChat()) return true;
        if (CopyChatSelection(message, wParam, lParam)) return true;

        var eat = false;
        if (message == WmSetCursor)
        {
            // Over a resize edge, or while moving a window, our cursor replaces the game's; the eaten message keeps the game from resetting it.
            if ((lParam & 0xffff) == HtClient)
                Guard(() =>
                {
                    var cursor = WindowCursorAt(_pointer);
                    if (cursor == IntPtr.Zero) return;
                    SetCursor(cursor);
                    eat = true;
                });
            return eat;
        }
        _gameWindow = new IntPtr(hwnd);
        if (_handOff != null)
            Guard(() => ContinueHandOff(message));
        if (message == InputRouterService.WmMouseMove)
            _pointer = new Point((short)(lParam & 0xffff), (short)((lParam >> 16) & 0xffff));
        if (message == InputRouterService.WmLButtonUp && _inputRouter.CapturedSurfaceId == null)
            Guard(() => HandleRetailDrop(new Point((short)(lParam & 0xffff), (short)((lParam >> 16) & 0xffff))));
        if (message == InputRouterService.WmMouseWheel)
        {
            var point = new NativePoint
            {
                X = unchecked((short)(lParam & 0xffff)),
                Y = unchecked((short)((lParam >> 16) & 0xffff))
            };
            var window = GetForegroundWindow();
            if (window != IntPtr.Zero && ScreenToClient(window, ref point))
                lParam = (point.X & 0xffff) | (point.Y << 16);
        }

        var route = _inputRouter.Route(
            new NativeInputMessage(message, new IntPtr(wParam), new IntPtr(lParam)),
            GetInputSurfaces());
        GuardInput(route, () =>
        {
            var target = route.SurfaceId == null ? null : SurfaceById(route.SurfaceId);
            switch (route.Action)
            {
                case InputAction.PointerMove:
                    var point = new Point((short)(lParam & 0xffff), (short)((lParam >> 16) & 0xffff));
                    _pointer = point;
                    if (target != _hovered)
                    {
                        _hovered?.Panel.PointerLeave();
                        _hovered = target;
                    }
                    if (_windows?.IsDragging == true)
                    {
                        _windows.Move(point);
                        SyncWindowLocations();
                    }
                    else if (_toolbarDragOffset != null)
                        DragToolbar(point);
                    else
                    {
                        var handingOff = _handOff == null;
                        target?.Panel.PointerMove(route.X, route.Y);
                        // While retail waits for a target, a move over a LegACEy window stays ours: retail would pick the world object
                        // behind the window as the one under the pointer, over the item the window reported, and judge that for its cursor.
                        if (target != null && _inventory.TargetMode?.Waiting == true) eat = true;
                        // A drag handed to retail on this move takes the button-up to retail, so the panel lets go of the button now.
                        if (handingOff && _handOff != null) target?.Panel.PointerUp(-1, -1);
                    }
                    break;
                case InputAction.PointerDown:
                    // The window that had focus loses it to this one, and so does retail's focused element (a split amount box).
                    SurfaceById(route.ClearFocusSurfaceId)?.Panel.ClearFocus();
                    if (route.SurfaceId != UpdateToastSurfaceId) _retailDrag?.ClearKeyboardFocus();
                    _pointer = new Point(route.X + target!.Location.X, route.Y + target.Location.Y);
                    if (route.SurfaceId != null && _windows?.Get(route.SurfaceId) != null)
                    {
                        _windows.Press(_pointer);
                        SyncWindowLocations();
                    }
                    // The toolbar's top edge moves it, and the press goes no further: a button under the edge is not pressed.
                    if (route.SurfaceId == ToolbarSurfaceId && OnToolbarMoveEdge(_pointer))
                    {
                        BeginToolbarDrag();
                        eat = route.Eat;
                        break;
                    }
                    target.Panel.PointerDown(route.X, route.Y, ToKeyModifiers(route.Modifiers));
                    eat = route.Eat;
                    break;
                case InputAction.RightClick:
                    target!.Panel.RightClick(route.X, route.Y);
                    eat = route.Eat;
                    break;
                case InputAction.PointerUp:
                    target!.Panel.PointerUp(route.X, route.Y);
                    if (_windows?.IsDragging == true)
                    {
                        _windows.Release();
                        SyncWindowLocations();
                        // A chat failure here turns the chat window off, not the whole UI.
                        try { _chatWindows?.Released(); }
                        catch (Exception exception) { ChatFailed(exception); }
                    }
                    if (_toolbarDragOffset != null)
                        EndToolbarDrag();
                    eat = route.Eat;
                    break;
                case InputAction.MouseWheel:
                    // Ctrl and the wheel over a LegACEy window or the toolbar make it bigger or smaller, and its content does not scroll. Over
                    // chat they keep changing its text size.
                    if ((route.Modifiers & InputModifiers.Control) != 0 && Rescalable(route.SurfaceId))
                        RescaleWindow(route.SurfaceId!, Math.Sign(route.WheelDelta));
                    else
                        target!.Panel.MouseWheel(route.X, route.Y, 0, route.WheelDelta / 120.0, ToKeyModifiers(route.Modifiers));
                    eat = route.Eat;
                    break;
                case InputAction.KeyDown:
                    target!.Panel.KeyDown(Win32KeyMap.ToAvaloniaKey(route.KeyCode), ToKeyModifiers(route.Modifiers));
                    eat = route.Eat;
                    break;
                case InputAction.KeyUp:
                    target!.Panel.KeyUp(Win32KeyMap.ToAvaloniaKey(route.KeyCode), ToKeyModifiers(route.Modifiers));
                    eat = route.Eat;
                    break;
                case InputAction.TextInput:
                    // Control characters (Ctrl+V's 0x16, Backspace's 0x08) are keys the box has had already, not text to type.
                    if (route.KeyCode >= 0x20 && route.KeyCode != 0x7F) target!.Panel.TextInput(((char)route.KeyCode).ToString());
                    eat = route.Eat;
                    break;
                case InputAction.ClearFocus:
                    SurfaceById(route.ClearFocusSurfaceId)?.Panel.ClearFocus();
                    break;
                case InputAction.Reset:
                    ApplyReset(route);
                    break;
                case InputAction.None when message == InputRouterService.WmMouseMove:
                    _hovered?.Panel.PointerLeave();
                    _hovered = null;
                    break;
            }
            if (message == InputRouterService.WmMouseMove)
                UpdateWindowCorner(route.SurfaceId, _pointer);
            if (route.Eat)
                eat = true;
        });
        return eat;
    }

    /// <summary>
    /// Brightens the corner under the pointer on its window's frame. The resize cursor itself is set on WM_SETCURSOR, which
    /// the game sends before each mouse move, so the game's own handler cannot put its cursor back over an edge.
    /// </summary>
    private void UpdateWindowCorner(string? surfaceId, Point point)
    {
        if (_windows == null) return;
        var hover = surfaceId == "bar" ? default : _windows.HoverAt(point);
        var corner = CornerOf(hover.Edges);
        if (hover.WindowId == _cornerWindowId && corner == _cornerApplied) return;
        SetWindowCorner(_cornerWindowId, DerethCorner.None);
        SetWindowCorner(hover.WindowId, corner);
        _cornerWindowId = hover.WindowId;
        _cornerApplied = corner;
    }

    private void SetWindowCorner(string? windowId, DerethCorner corner)
    {
        var surface = SurfaceById(windowId);
        if (surface == null) return;
        foreach (var frame in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(surface.Panel.Content).OfType<DerethFrame>())
            frame.HoveredCorner = corner;
    }

    private static DerethCorner CornerOf(WindowEdges edges)
    {
        if (edges == (WindowEdges.Left | WindowEdges.Top)) return DerethCorner.TopLeft;
        if (edges == (WindowEdges.Right | WindowEdges.Top)) return DerethCorner.TopRight;
        if (edges == (WindowEdges.Left | WindowEdges.Bottom)) return DerethCorner.BottomLeft;
        if (edges == (WindowEdges.Right | WindowEdges.Bottom)) return DerethCorner.BottomRight;
        return DerethCorner.None;
    }

    /// <summary>
    /// The cursor for a point: the move cursor while a window is being moved, the resize cursor over a window edge or corner,
    /// or zero where the game's own cursor stands.
    /// </summary>
    private IntPtr WindowCursorAt(Point point)
    {
        if (_toolbarDragOffset != null || _windows?.Moving != null) return RetailCursor(DatCursorMove, IdcSizeAll);
        // The pointer goes to a capture, else to the topmost surface under it, as the router decides. Only the window that owns
        // it gets the move or resize cursor, so the retail bar and any other surface keep the game's cursor.
        var owner = _inputRouter.CapturedSurfaceId ?? InputRouterService.SurfaceAt(point.X, point.Y, GetInputSurfaces());
        if (owner == ToolbarSurfaceId && OnToolbarMoveEdge(point)) return RetailCursor(DatCursorMove, IdcSizeAll);
        if (_windows == null) return IntPtr.Zero;
        if (owner != null && _windows.MoveEdgeAt(point) == owner) return RetailCursor(DatCursorMove, IdcSizeAll);
        var hover = _windows.HoverAt(point);
        if (hover.WindowId == null || owner != hover.WindowId) return IntPtr.Zero;
        var horizontal = hover.Edges & (WindowEdges.Left | WindowEdges.Right);
        var vertical = hover.Edges & (WindowEdges.Top | WindowEdges.Bottom);
        if (horizontal == WindowEdges.None && vertical == WindowEdges.None) return IntPtr.Zero;
        if (vertical == WindowEdges.None) return RetailCursor(DatCursorWE, IdcSizeWE);
        if (horizontal == WindowEdges.None) return RetailCursor(DatCursorNS, IdcSizeNS);
        return hover.Edges == (WindowEdges.Left | WindowEdges.Top) || hover.Edges == (WindowEdges.Right | WindowEdges.Bottom)
            ? RetailCursor(DatCursorNWSE, IdcSizeNWSE)
            : RetailCursor(DatCursorNESW, IdcSizeNESW);
    }

    /// <summary>A retail cursor from the DAT, made once; the standard Windows cursor if the DAT image can't be read.</summary>
    private IntPtr RetailCursor(uint imageId, int fallback)
    {
        if (!_datCursors.TryGetValue(imageId, out var cursor))
        {
            try { cursor = _portal?.ReadImage(imageId) is { } image ? CreateCursor(image) : IntPtr.Zero; }
            catch (Exception exception)
            {
                Log($"Cursor 0x{imageId:X8} could not be made from the DAT: {exception.Message}");
                cursor = IntPtr.Zero;
            }
            _datCursors.Add(imageId, cursor);
        }
        if (cursor != IntPtr.Zero) return cursor;
        if (!_systemCursors.TryGetValue(fallback, out cursor))
            _systemCursors.Add(fallback, cursor = LoadCursor(IntPtr.Zero, (IntPtr)fallback));
        return cursor;
    }

    /// <summary>An alpha cursor from a premultiplied BGRA image, its hotspot in the middle.</summary>
    private static IntPtr CreateCursor(GameImage image)
    {
        // The colour bitmap carries the alpha; the 1-bit mask is all zero, so it adds nothing.
        var color = CreateBitmap(image.Width, image.Height, 1, 32, image.Pixels);
        var mask = CreateBitmap(image.Width, image.Height, 1, 1, new byte[(image.Width + 15) / 16 * 2 * image.Height]);
        try
        {
            var info = new IconInfo { IsIcon = false, HotspotX = image.Width / 2, HotspotY = image.Height / 2, Mask = mask, Color = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    private void GuardInput(InputRoute route, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Disable(exception);
        }
    }

    private AvaloniaPanel ObservePanel(AvaloniaPanel panel, Action<Exception>? failed = null)
    {
        panel.Error += failed ?? new Action<Exception>(Disable);
        if (panel.LastError is { } initialError)
        {
            panel.Dispose();
            throw initialError;
        }
        return panel;
    }

    /// <summary>The virtual keys a window takes as shortcuts while it has the focus, from its content; null for a window with none.</summary>
    private static Func<int, bool>? ShortcutsOf(AvaloniaPanel panel)
    {
        if (panel.Content is not IWindowShortcuts shortcuts) return null;
        return virtualKey => shortcuts.WantsShortcut(Win32KeyMap.ToAvaloniaKey(virtualKey));
    }

    /// <summary>The toolbar and its split tab, while shown: windows snap to them though the window manager does not place them.</summary>
    private IEnumerable<Rectangle> ToolbarSnapTargets()
    {
        if (_toolbarSurface is { Visible: true } bar) yield return bar.Bounds;
        if (_splitTabSurface is { Visible: true } split) yield return split.Bounds;
    }

    private InputSurface[] GetInputSurfaces()
    {
        var surfaces = new System.Collections.Generic.List<InputSurface>(4);
        if (_toolbarSurface is { Visible: true })
            surfaces.Add(new InputSurface(ToolbarSurfaceId, _toolbarSurface.Location.X, _toolbarSurface.Location.Y, _toolbarSurface.Bounds.Width, _toolbarSurface.Bounds.Height, 0, _toolbarSurface.Panel.WantsKeyboard));
        // Above every window, as it draws.
        if (_splitTabSurface is { Visible: true } split)
            surfaces.Add(new InputSurface(SplitTabSurfaceId, split.Location.X, split.Location.Y, split.Bounds.Width, split.Bounds.Height, (_windows?.ZOrder.Count ?? 0) + 1, split.Panel.WantsKeyboard));
        // The update toast is above them all. A press on it leaves the focus where it was, so typing goes on where it was going.
        if (_updateToastSurface is { Visible: true } toast)
            surfaces.Add(new InputSurface(UpdateToastSurfaceId, toast.Location.X, toast.Location.Y, toast.Bounds.Width, toast.Bounds.Height, (_windows?.ZOrder.Count ?? 0) + 2,
                takesFocus: false));
        if (_windows != null)
        {
            var z = _windows.ZOrder.Count;
            foreach (var window in _windows.ZOrder)
            {
                var surface = SurfaceById(window.Id);
                if (surface is not { Visible: true }) continue;
                surfaces.Add(new InputSurface(window.Id, window.Location.X, window.Location.Y, window.Width, window.Height, z--, surface.Panel.WantsKeyboard,
                    ShortcutsOf(surface.Panel)));
            }
        }
        return surfaces.ToArray();
    }

    private ScreenSurface? SurfaceById(string? id)
    {
        if (id == ToolbarSurfaceId) return _toolbarSurface;
        if (id == SplitTabSurfaceId) return _splitTabSurface;
        if (id == UpdateToastSurfaceId) return _updateToastSurface;
        if (id != null && _featureSurfaces.TryGetValue(id, out var surface)) return surface;
        return null;
    }

    private void ApplyReset(InputRoute route)
    {
        SurfaceById(route.ReleaseCaptureSurfaceId)?.Panel.PointerUp(-1, -1);
        SurfaceById(route.ClearFocusSurfaceId)?.Panel.ClearFocus();
        _hovered?.Panel.PointerLeave();
        _hovered = null;
        _toolbarDragOffset = null;
        _windows?.Release();
    }

    private void SyncWindowLocations()
    {
        if (_windows == null) return;
        // A window being resized keeps its surface until release: relaying it out on every mouse move stalls the game,
        // so an outline shows the new bounds meanwhile.
        foreach (var window in _windows.ZOrder)
            if (window != _windows.Resizing)
                FitSurface(SurfaceById(window.Id)!, window);
    }

    /// <summary>
    /// A measured length rounded up to whole units, ignoring float noise: a scaled layout measures to whole pixels, which come back as 540.0000001,
    /// and a plain ceiling would add a pixel of empty edge.
    /// </summary>
    private static int WholePixels(double length) => (int)Math.Ceiling(length - 0.001);

    /// <summary>Whether Ctrl and the wheel over a surface resize it: the held toolbar, or a LegACEy window other than chat.</summary>
    private bool Rescalable(string? id) => id == ToolbarSurfaceId
        ? _toolbarTakeover?.Holds == true
        : id != null && _windows?.Get(id) != null && !RetailChatWindows.IsChat(id);

    // How long a window's size shows over it after a wheel notch.
    private static readonly TimeSpan ScaleNoteTime = TimeSpan.FromSeconds(1.2);

    /// <summary>
    /// Makes a window or the toolbar a step bigger or smaller, from Ctrl and the wheel over it. The toolbar, with its shortcut rows, lays out
    /// again; any other window's panel draws at the new scale in place.
    /// </summary>
    private void RescaleWindow(string id, int steps)
    {
        if (!AvaloniaPanel.CanScale || _windows!.Locked) return;
        var placeId = id == ToolbarSurfaceId ? ToolbarPlaceId : id;
        if (_windows.Rescale(placeId, steps))
        {
            if (id == ToolbarSurfaceId) ScaleToolbar();
            else SyncWindowLocations();
        }
        // The window's size shows over it while the wheel turns, at a limit too, as the Settings slider shows the UI size.
        SurfaceById(id)?.Panel.ShowNote($"{_windows.SizePercentOf(placeId)}%", ScaleNoteTime);
    }

    /// <summary>Puts a window's surface where the window manager has it, at the window's size and scale. A new size re-lays out the panel.</summary>
    private static void FitSurface(ScreenSurface surface, ManagedWindow window)
    {
        surface.Location = window.Location;
        var scale = AvaloniaPanel.CanScale ? window.Scale : 1;
        if (surface.Panel.Frame.Width != window.Width || surface.Panel.Frame.Height != window.Height || surface.Panel.Scale != scale)
            surface.Panel.Resize(window.Width, window.Height, scale);
    }

    private static KeyModifiers ToKeyModifiers(InputModifiers modifiers)
    {
        var result = KeyModifiers.None;
        if ((modifiers & InputModifiers.Shift) != 0) result |= KeyModifiers.Shift;
        if ((modifiers & InputModifiers.Control) != 0) result |= KeyModifiers.Control;
        if ((modifiers & InputModifiers.Alt) != 0) result |= KeyModifiers.Alt;
        if ((modifiers & InputModifiers.Meta) != 0) result |= KeyModifiers.Meta;
        return result;
    }

    /// <summary>Run UI work from a game callback; any exception disables the replacement instead of escaping.</summary>
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Disable(exception);
        }
    }

    /// <summary>Stop drawing, give the player retail's UI back, and log why.</summary>
    public void Disable(Exception exception)
    {
        // Every panel drains the shared Avalonia dispatcher, so a plugin's failure can surface on another panel.
        if (_plugins?.TryFailOwner(exception) == true) return;
        _failed = true;
        Log($"LegACEy UI disabled: {exception}");
        TearDown();
    }

    /// <summary>Disable only the post-UI windows when EndScene fails; retail surfaces continue.</summary>
    private void DisableWindows(Exception exception)
    {
        // The takeover stands in for a window: without windows, retail's panel must come back now.
        try { EndRetailPanels(); }
        catch (Exception cleanupError) { Log($"Could not give the retail panels back after a window failure: {cleanupError}"); }
        // The toolbar draws in the post-UI pass too: without windows, retail's toolbar must come back.
        try { ReleaseRetailToolbar(); }
        catch (Exception cleanupError) { Log($"Could not give the retail toolbar back after a window failure: {cleanupError}"); }
        _windowsEnabled = false;
        Log($"LegACEy windows disabled: {exception}");
        SetMenuRing(false);
        foreach (var window in _windows?.ZOrder.ToArray() ?? Array.Empty<ManagedWindow>())
            _windows!.Close(window.Id);
        try { _clientUi?.EndSession(); }
        catch (Exception cleanupError) { Log($"Could not clean up feature UI after window failure: {cleanupError}"); }
        _clientUi = null;
        // Plugin windows are not in the client UI framework; release them here too.
        ReleaseFeatureWindows();
        DropUpdateToast();
    }

    private void ReleaseFeatureWindows()
    {
        foreach (var id in _featureSurfaces.Keys.Concat(_hiddenSurfaces.Keys).ToArray())
            ReleaseFeatureWindow(id);
    }

    private static string SessionCharacter() => Bridge.Text(BridgeText.CharacterName);
    private static string SessionServer() => Bridge.Text(BridgeText.ServerName);

    private void PublishGameState() => _gameStatePoller.Poll(_inGame);

    private sealed class FeatureWindow : IDisposable
    {
        private readonly ClientUiRuntime _owner;
        private readonly string _id;
        public FeatureWindow(ClientUiRuntime owner, string id) { _owner = owner; _id = id; }
        public void Dispose() => _owner.ReleaseFeatureWindow(_id);
    }

    /// <summary>A retail root window by its id, for a takeover: hidden, shown and moved through retail's own element.</summary>
    private sealed class NativeRootPort : IRetailTakeoverPort
    {
        private readonly uint _rootId;
        public NativeRootPort(uint rootId) => _rootId = rootId;
        private IntPtr Element => NativeUi.GetElement(_rootId);
        public bool Exists => Element != IntPtr.Zero;
        public IntPtr ElementIdentity => Element;
        public bool IsVisible { get { var element = Element; return element != IntPtr.Zero && NativeUi.IsVisible(element); } }
        public Rectangle GetBounds() => NativeUi.GetBounds(Required);
        public void SetVisible(bool visible) { var element = Element; if (element != IntPtr.Zero) NativeUi.SetVisible(element, visible); }
        public void SetSaveLocation(bool save) => NativeUi.SetSaveLocation(Required, save);
        public void MoveTo(Point location) => NativeUi.MoveTo(Required, location);
        public bool IsUiLocked => NativeUi.IsUiLocked;
        private IntPtr Required => Element is var element && element != IntPtr.Zero ? element : throw new InvalidOperationException($"Retail element 0x{_rootId:X8} disappeared.");
    }

    /// <summary>
    /// Retail's toolbar window, held hidden, with the Dereth toolbar's own place and size: retail keeps its taller window (both shortcut
    /// rows) on screen by its own height, which stopped the slimmer Dereth toolbar well short of the screen's bottom. A move is the
    /// toolbar's alone and is saved with the LegACEy windows; retail's window stays where retail left it.
    /// </summary>
    private sealed class ToolbarPlacePort : IRetailTakeoverPort
    {
        private readonly NativeRootPort _native;
        private readonly Func<Size> _size;
        private readonly Action<Point> _moved;
        public ToolbarPlacePort(NativeRootPort native, Func<Size> size, Action<Point> moved) { _native = native; _size = size; _moved = moved; }
        /// <summary>The toolbar's own place; null until it is moved or loaded, when it stands where retail's window is.</summary>
        public Point? Location { get; set; }
        public bool Exists => _native.Exists;
        public IntPtr ElementIdentity => _native.ElementIdentity;
        public bool IsVisible => _native.IsVisible;
        public Rectangle GetBounds() => new(Location ?? _native.GetBounds().Location, _size());
        public void SetVisible(bool visible) => _native.SetVisible(visible);
        public void SetSaveLocation(bool save) { }
        public void MoveTo(Point location)
        {
            Location = location;
            _moved(location);
        }
        public bool IsUiLocked => _native.IsUiLocked;
    }

    private sealed class SurfaceTakeoverPort : IRetailTakeoverSurface
    {
        private readonly ScreenSurface _surface;
        public SurfaceTakeoverPort(ScreenSurface surface) => _surface = surface;
        public Point Location => _surface.Location;
        public Size Size => _surface.Bounds.Size;
        public bool Visible { get => _surface.Visible; set => _surface.Visible = value; }
        public void SetLocation(Point location) => _surface.Location = location;
    }

    private void TearDown()
    {
        // First: retail's panel must be given back before anything else is torn down.
        try { EndRetailPanels(); }
        catch (Exception exception) { Log($"Could not give the retail panels back during unload: {exception}"); }
        // Unload runs no more frames, so a give-back that failed gets one more try here before the player is told.
        try
        {
            ReleaseRetailChat();
            if (_chatTakeover != null) ReleaseRetailChat();
        }
        catch (Exception exception) { Log($"Could not give the retail chat back during unload: {exception}"); }
        if (_chatTakeover != null) Log("Retail's chat windows are still held after two tries at unload; they stay hidden until the client restarts.");
        // The vitals window closes before the windows do, and retail's vitals come back. Unload runs no more frames, so a give-back that
        // failed gets one more try here before the player is told, as chat does.
        try
        {
            _vitals?.Release();
            if (_vitals?.Holds == true) _vitals.Release();
        }
        catch (Exception exception) { Log($"Could not give the retail vitals back during unload: {exception}"); }
        if (_vitals?.Holds == true) Log("Retail's vitals windows are still held after two tries at unload; they stay hidden until the client restarts.");
        // Kept while still held, so a disabled UI's frames go on retrying the give-back.
        else _vitals = null;
        try { ReleaseRetailToolbar(); }
        catch (Exception exception) { Log($"Could not give the retail toolbar back during unload: {exception}"); }
        // The radar gets the same two tries as chat.
        try
        {
            ReleaseRetailRadar();
            if (_radarTakeover != null) ReleaseRetailRadar();
        }
        catch (Exception exception) { Log($"Could not give the retail radar back during unload: {exception}"); }
        if (_radarTakeover != null) Log("Retail's radar is still held after two tries at unload; it stays hidden until the client restarts.");
        try
        {
            _attack?.Release();
            if (_attack?.Holds == true) _attack?.Release();
        }
        catch (Exception exception) { Log($"Could not give the retail attack panel back during unload: {exception}"); }
        if (_attack?.Holds == true) Log("Retail's attack panel is still held after two tries at unload; it stays hidden until the client restarts.");
        // Kept while still held, so a disabled UI's frames go on retrying the give-back.
        else _attack = null;
        DisposeRadarGround();
        if (_itemDragActive)
            try { _retailDrag?.ClearDropIndicator(); } catch (Exception exception) { Log($"Could not clear the inventory drop indicator: {exception.Message}"); }
        _itemDragActive = false;
        _retailDragIcon = null;
        HideDragIcon();
        try { _clientUi?.EndSession(); }
        catch (Exception exception) { Log($"Could not clean up feature UI during unload: {exception}"); }
        _clientUi = null;
        try { _serverChannel?.Reset(); }
        catch (Exception exception) { Log($"Could not fail outstanding server channel requests: {exception}"); }
        _hovered = null;
        _postUiDrawHook?.Dispose();
        _postUiDrawHook = null;
        _windows = null;
        ReleaseFeatureWindows();
        DropUpdateToast();
        _windowFailures.Clear();
        _windowsEnabled = false;
        _portal?.Dispose();
        _portal = null;
        foreach (var cursor in _datCursors.Values)
            if (cursor != IntPtr.Zero) DestroyCursor(cursor);
        _datCursors.Clear();
        _drawnModelViews.Clear();
        ReleaseModelRenderers(_drawnModelViews);
        GameArtImageExtension.CurrentSource = null;
    }
}
