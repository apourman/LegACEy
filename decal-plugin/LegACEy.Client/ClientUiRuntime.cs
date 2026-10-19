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
    // Beside the DLL: while this file exists, retail's inventory and examination panels stay retail's. Without it the LegACEy UI takes them
    // over; the examination takeover follows the same switch.
    private const string RetailPanelSwitchFile = "retail-inventory-native";
    // Beside the DLL: while this file exists, retail's toolbar stays retail's. Without it the Dereth toolbar replaces it.
    private const string RetailToolbarSwitchFile = "retail-toolbar-native";
    // Beside the DLL: while this file exists, retail's chat windows stay retail's. Without it the Dereth chat window replaces them.
    private const string RetailChatSwitchFile = "retail-chat-native";
    // Beside the DLL: while this file exists, retail's vitals windows stay retail's. Without it the Dereth vitals window replaces them.
    private const string RetailVitalsSwitchFile = "retail-vitals-native";
    // Beside the DLL: while this file exists, retail's radar stays retail's. Without it the Dereth radar replaces it.
    private const string RetailRadarSwitchFile = "retail-radar-native";

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
    private double WindowScale => AvaloniaPanel.CanScale ? _uiSize / 100.0 : 1;

    // The release build stamps the client with the plugin version, so the menu shows which release is installed.
    private static readonly string MenuTitle = $"LegACEy {typeof(ClientUiRuntime).Assembly.GetName().Version?.ToString(3)}";
    private const string MenuWindowId = "plugin-menu";
    private const string SettingsWindowId = "dereth-settings";
    // The Settings window's last tab, kept for the install like the UI size.
    private const string SettingsTabKey = "settings-tab";
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
    private WindowManager? _windows;
    private IWindowPositionStore? _positionStore;
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
    private TimeSpan _maxUiFrame;
    private TimeSpan _maxPostUiDraw;
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
    // Shortcut rows 1 and 2: their own windows beside the toolbar, held while the takeover is, and their refresh on inventory changes.
    private const string ShortcutRowWindowId = "shortcut-row-1";
    private const string ShortcutRow2WindowId = "shortcut-row-2";
    // The Dereth toolbar's surface id. It is not a managed window, so drops reach it by its surface.
    private const string ToolbarSurfaceId = "toolbar";
    // The selected stack's split tab: its own surface over the toolbar's plaque, shown while a stack is selected.
    private const string SplitTabSurfaceId = "toolbar-split";
    private StackSplitTab? _splitTab;
    private ScreenSurface? _splitTabSurface;
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
    private bool _radarFailed;
    private bool _radarReleaseLogged;
    private bool _radarSwitch;
    private DateTime _lastRadarSwitchCheck;
    private readonly GameStatePort _gameState = new(new GameStateSnapshot("unknown-character", "unknown-server", 0, 0, 0, 0, 0, 0));
    private readonly GameStatePoller _gameStatePoller;
    private ClientUiFramework? _clientUi;
    private GameChannelTransport? _channelTransport;
    private ServerChannelClient? _serverChannel;
    private RetailItemDrag? _retailDrag;
    // Plugin takeovers of retail panels (the inventory), and the switch that turns them on, which also turns the examination takeover on or off. The switch is a marker file beside the DLL,
    // as the post-UI test failure is. Creating the switch file gives retail's panels back; deleting it turns the takeovers on.
    private readonly List<RetailPanelTakeover> _retailPanels = new();
    private bool? _retailPanelsValid;
    private bool? _retailToolbarValid;
    private bool _retailPanelSwitch;
    private DateTime _lastRetailPanelSwitchCheck;
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
    // Plugin windows the player closed: kept, texture and all, so reopening skips building the content again.
    // ponytail: kept until logoff, still subscribed and running (the paperdoll rebuilds on equip changes); add a cap,
    // or tell plugins when they are shown and hidden, once more plugins arrive
    private readonly Dictionary<string, ScreenSurface> _hiddenSurfaces = new(StringComparer.Ordinal);
    // Error handlers of windows opened by plugins; a failure in one turns off its plugin, not the client.
    private readonly Dictionary<string, Action<Exception>> _windowFailures = new(StringComparer.Ordinal);
    private PluginRegistry? _plugins;
    private bool _inGame;
    // Kept apart from the UI, which may have failed: the plugin's own updates still check, and tell the player in chat.
    private readonly PluginUpdates _updates = new(PluginDirectory);
    private bool _loggedIn;
    private bool _noticeFailLogged;
    private bool _failed;

    public ClientUiRuntime()
    {
        _gameStatePoller = new GameStatePoller(_gameState, Bridge.ReadGameState,
            error => Log($"Character stats temporarily unavailable; keeping UI active and retrying: {error.Message}"));
        _appraisal = new AppraisalFeature(this, Log, spells: () => _portal?.ReadSpells(Log) ?? new Dictionary<uint, SpellInfo>(), game: this,
            names: () => _portal?.ReadCharacterNames(Log) ?? CharacterNames.None, measure: (control, available) => AvaloniaPanel.MeasureAt(control, available, WindowScale),
            canInspect: () => _plugins?.SupportsAction(PaperdollProtocol.Inspect) == true);
        _appraisingInventory = new AppraisingInventoryPort(_inventory, _appraisal);
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
            }
            catch (Exception exception) { Log($"LegACEy server channel disabled: {exception.Message}"); }
            try { _retailDrag = new RetailItemDrag(NativeUi.ReadMemory, Log); }
            catch (Exception exception) { Log($"Item drag and drop disabled: {exception.Message}"); }
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
        _serverChannel?.Request(ChannelHello.Action, Array.Empty<byte>(), reply =>
        {
            var actions = ServerActionsFrom(reply).ToArray();
            _plugins?.SetServerActions(actions);
            if (actions.Contains(StormProtocol.Zone))
                _serverChannel?.Request(StormProtocol.Zone, Array.Empty<byte>(), zone => { if (zone.Ok) ShowStorm(zone.Body); });
            if (actions.Contains(WeatherProtocol.Zone))
                _serverChannel?.Request(WeatherProtocol.Zone, Array.Empty<byte>(), weather => { if (weather.Ok) ShowWeather(weather.Body); });
        });
    }

    /// <summary>The storm fog test's zone, from the server: the ring of landblocks around it fills with fog.</summary>
    private void ShowStorm(byte[] body)
    {
        try
        {
            if (_worldEffects != null) _worldEffects.StormZone = StormProtocol.ReadZone(body);
        }
        catch (Exception exception) { Log($"Could not read the storm zone: {exception.Message}"); }
    }

    /// <summary>The weather test's weather, from the server: snow, a blizzard or a thunderstorm on the 3×3 landblocks around its landblock.</summary>
    private void ShowWeather(byte[] body)
    {
        try
        {
            if (_worldEffects != null) _worldEffects.Weather = WeatherProtocol.Read(body);
        }
        catch (Exception exception) { Log($"Could not read the weather: {exception.Message}"); }
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

    public void Logoff()
    {
        Guard(() => ApplyReset(_inputRouter.Route(new NativeInputMessage(InputRouterService.WmLogoff, IntPtr.Zero, IntPtr.Zero), GetInputSurfaces())));
        Guard(() => _hovered?.Panel.PointerLeave());
        _inGame = false;
        _loggedIn = false;
        _carriedChatHistory = null;
        // The next character reads its own toolbar place; until then the toolbar stands where retail's window is.
        _toolbarPlaceLoaded = false;
        if (_toolbarPlace != null) _toolbarPlace.Location = null;
        _inventory.Clear();
        Guard(() => _appraisal.Clear());
        Guard(() => _busyCursor?.Show(false));
        if (_worldEffects != null)
        {
            _worldEffects.Selected = 0;
            _worldEffects.StormZone = null;
            _worldEffects.Weather = null;
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
        try { ReleaseRetailRadar(); }
        catch (Exception exception) { Log($"Could not give the retail radar back at logoff: {exception}"); }
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
        _positionStore = new FileWindowPositionStore(IOPath.Combine(PluginDirectory, "window-positions.txt"));
        if (!AvaloniaPanel.CanScale) Log("This Avalonia cannot scale panels; LegACEy windows show at their design size.");
        _windows = new WindowManager(GameDevice.ViewportSize(_device), _positionStore, SessionServer(), SessionCharacter(), WindowScale, () => NativeUi.IsUiLocked);
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
        var window = new DerethWindow(MenuTitle, null, new PluginMenuPanel(_plugins!, _portal, OpenSettings));
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
    private void OpenSettings()
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
            ShortcutRow2 = new OptionChoice<bool>(_row2Choice.IsOn(SessionServer(), SessionCharacter()), SetShortcutRow2),
            ChatTextSize = new OptionChoice<int>(RetailChatWindows.LoadTextSize(_positionStore!, SessionServer(), SessionCharacter()), SetChatTextSize),
            Radar = new OptionChoice<RadarOptions>(LoadRadarOptions(), SetRadarOptions),
        };
        _settings = settings;
        var window = new DerethWindow("Settings", null, new SettingsPanel(settings, LoadSettingsTab(), SaveSettingsTab, _plugins));
        window.CloseRequested += (_, _) =>
        {
            _clientUi?.CloseWindow(SettingsWindowId);
            _settings = null;
        };
        _clientUi.OpenWindow(new WindowDefinition(SettingsWindowId, "Settings", SettingsPanel.WindowWidth, SettingsPanel.WindowHeight,
            DerethWindow.TitleBarHeight, new DerethClientTheme()), window, new Point(260, 120));
    }

    private SettingsTab LoadSettingsTab() =>
        _settingsStore.Load("", "", SettingsTabKey) is { Location.X: var tab } && Enum.IsDefined(typeof(SettingsTab), tab) ? (SettingsTab)tab : SettingsTab.General;

    private void SaveSettingsTab(SettingsTab tab) => _settingsStore.Save("", "", SettingsTabKey, (new Point((int)tab, 0), null));

    /// <summary>The Settings window's update row: the installed version and Check for updates, greyed out with the reason on an install that never updates.</summary>
    private UpdateChoice UpdateCheck()
    {
        var off = _updates.Off;
        return new UpdateChoice(off == null ? $"Version {_updates.Installed}" : $"Version {_updates.Installed} ({off})", off == null, _updates.CheckNow);
    }

    /// <summary>
    /// The windows the Settings window switches between LegACEy's and retail's, each by its switch file beside the DLL, so the choice holds for
    /// every character on this install. Custom is the default; a change is read on the next frame.
    /// </summary>
    private IReadOnlyList<UiWindowChoice> WindowChoices() => new[]
    {
        Choice("Inventory", RetailPanelSwitchFile, () => _lastRetailPanelSwitchCheck = default),
        Choice("Toolbar", RetailToolbarSwitchFile, () => _lastToolbarSwitchCheck = default),
        Choice("Chat", RetailChatSwitchFile, () => _lastChatSwitchCheck = default),
        Choice("Vitals", RetailVitalsSwitchFile, () => _lastVitalsSwitchCheck = default),
        Choice("Radar", RetailRadarSwitchFile, () => _lastRadarSwitchCheck = default)
    };

    private static UiWindowChoice Choice(string title, string file, Action recheck)
    {
        var path = IOPath.Combine(PluginDirectory, file);
        return new UiWindowChoice(title, () => !File.Exists(path), custom =>
        {
            try
            {
                if (custom) File.Delete(path);
                else File.WriteAllText(path, string.Empty);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log($"Could not switch the {title} window: {exception.Message}");
            }
            recheck();
        });
    }

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
        var menuOpen = _windows?.Get(MenuWindowId) != null;
        var settingsOpen = _windows?.Get(SettingsWindowId) != null;
        Log($"UI size {_uiSize}%: rebuilding the LegACEy UI.");
        // The chat's lines and sent text outlive the rebuild: it is the same character still playing.
        var chatHistory = _chatWindows?.History ?? _carriedChatHistory;
        Logoff();
        _carriedChatHistory = chatHistory;
        LoginComplete();
        if (menuOpen) Guard(ToggleMenuWindow);
        // The Settings window comes back on the tab it was on, which the tab store keeps.
        if (settingsOpen) Guard(OpenSettings);
    }

    /// <summary>
    /// Runs the UI for one frame. It starts once retail's indicators show, at world entry.
    /// </summary>
    public void Frame()
    {
        var timer = Stopwatch.StartNew();
        if (_loggedIn)
        {
            // A failed line is tried again next frame, logged once per streak.
            try
            {
                _updates.PostNotices();
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
            if (timer.Elapsed > _maxUiFrame) _maxUiFrame = timer.Elapsed;
            RecordPerformanceMeasurement();
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
        if (_failed || !NativeUi.Ready)
            return;

        if (_uiSizeChanged) ApplyUiSize();

        Guard(() =>
        {
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
            TickRetailToolbar();
            TickRetailChat();
            TickRetailVitals();
            TickRetailRadar();
            // Portal space at login comes before Decal's login complete: retail's inventory opened then is parked already,
            // and our window opens for it once the windows can.
            if (!_inGame) TickRetailPanels(loggingIn: true);
        });

        if (_failed || !_inGame || !_windowsEnabled)
            return;

        Guard(() =>
        {
            if (_windows != null)
            {
                var viewport = GameDevice.ViewportSize(_device);
                if (_windows.Screen != viewport)
                {
                    _windows.ResizeScreen(viewport);
                    SyncWindowLocations();
                }
            }

            TickRetailPanels();
            TickSettingsLock();
            UpdateRetailDrag();
            if (_itemDragActive)
                _retailDrag?.UpdateDropIndicator();
            PrepareFeatureWindows();
            if (_dragIconSurface != null)
            {
                _dragIconSurface.Location = new Point(_pointer.X - 16, _pointer.Y - 16);
                _dragIconSurface.Prepare();
            }
        });
    }

    /// <summary>Logs the longest frame and post-UI draw since the last sample, then starts counting again.</summary>
    private void RecordPerformanceMeasurement()
    {
        var now = DateTime.UtcNow;
        if (now - _lastMeasurementLog < TimeSpan.FromSeconds(30)) return;
        _lastMeasurementLog = now;
        var uiFrame = _maxUiFrame;
        var postUiDraw = _maxPostUiDraw;
        _maxUiFrame = TimeSpan.Zero;
        _maxPostUiDraw = TimeSpan.Zero;
        try
        {
            var surfaces = new List<ScreenSurface>();
            surfaces.AddRange(_featureSurfaces.Values);
            using var process = Process.GetCurrentProcess();
            var maxTick = surfaces.Count == 0 ? 0 : surfaces.Max(surface => surface.LastTickMilliseconds);
            var maxUpload = surfaces.Count == 0 ? 0 : surfaces.Max(surface => surface.LastUploadMilliseconds);
            var dirtyRectangles = surfaces.Sum(surface => surface.LastDirtyRectangleCount);
            Log($"UI performance sample: maxUiFrame={uiFrame.TotalMilliseconds:F2}ms maxPostUiDraw={postUiDraw.TotalMilliseconds:F2}ms surfaces={surfaces.Count} maxPanelTick={maxTick:F2}ms maxTextureUpload={maxUpload:F2}ms dirtyRects={dirtyRectangles} privateBytes={process.PrivateMemorySize64} virtualBytes={process.VirtualMemorySize64} managedBytes={GC.GetTotalMemory(false)}.");
        }
        catch (Exception exception)
        {
            Log($"Could not collect a UI performance sample: {exception.Message}");
        }
    }

    /// <summary>
    /// Ticks each LegACEy window. A failure, including a panel over its tick budget, goes to the window's error handler:
    /// a plugin's window turns off that plugin, a client window disables the client UI.
    /// </summary>
    private void PrepareFeatureWindows()
    {
        foreach (var pair in _featureSurfaces.ToArray())
        {
            // An earlier failure in this loop may have closed the window.
            if (!_featureSurfaces.ContainsKey(pair.Key)) continue;
            try { pair.Value.Prepare(); }
            catch (Exception exception) { _windowFailures[pair.Key](exception); }
        }
    }

    /// <summary>Called by IDirect3DDevice9.EndScene after retail and Decal UI drawing.</summary>
    private void DrawAfterRetailUi()
    {
        var timer = Stopwatch.StartNew();
        try { DrawAfterRetailUiCore(); }
        finally { if (timer.Elapsed > _maxPostUiDraw) _maxPostUiDraw = timer.Elapsed; }
    }

    private void DrawAfterRetailUiCore()
    {
        if (_failed) return;
        ThrowIfTestFailureRequested();
        Guard(() => _toolbarSurface?.DrawNow());
        var windows = !_failed && _windowsEnabled && _windows != null;
        if (windows)
        {
            _drawnModelViews.Clear();
            foreach (var window in _windows!.ZOrder.Reverse())
            {
                var surface = SurfaceById(window.Id);
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
        // ponytail: walks the window's visual tree every frame; cache the views per window if a big window makes it show up in the frame budget
        foreach (var view in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<ModelView>())
        {
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
                renderer.Draw(model, area, view.Orientation, view.Zoom, view.Focus);
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
        foreach (var view in _modelRenderers.Keys.Where(view => !keep.Contains(view)).ToList())
        {
            _modelRenderers[view].Dispose();
            _modelRenderers.Remove(view);
        }
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
        foreach (var pair in _featureSurfaces.ToArray())
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
        if (_retailPanels.Count == 0 && _examination == null) return;
        // Logging in, the windows can't open yet; they will if the post-UI hook is in.
        var enabled = (loggingIn ? _postUiDrawHook?.IsInstalled == true : CanOpenWindows) && SwitchOn(RetailPanelSwitchFile, ref _lastRetailPanelSwitchCheck, ref _retailPanelSwitch);
        foreach (var takeover in _retailPanels.ToArray())
            takeover.Tick(enabled, canShow: !loggingIn);
        _examination?.Tick(enabled, canShow: !loggingIn);
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
            // A shortcut dragged off a bar is only a bar's to take: no other window is offered it as an item.
            try
            {
                return _owner._shortcutDrag.Slot != null
                    ? ItemDropRouting.DeliverShortcutTo(window, _owner.ContentOf, _owner._pointer, itemId, windows.Scale)
                    : ItemDropRouting.DeliverTo(window, _owner.ContentOf, _owner._pointer, itemId, itemName, windows.Scale);
            }
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
            try { return ItemDropRouting.DeliverManyTo(window, _owner.ContentOf, _owner._pointer, itemIds, windows.Scale); }
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
        var scale = _windows!.Scale;
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
            // Drawn after retail's UI, under the LegACEy windows: drawn before it, retail's portal space would paint over it on a teleport.
            _toolbarSurface!.Prepare();
            PrepareSplitTab();
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
    private void PrepareSplitTab()
    {
        if (_splitTab is not { } tab || _splitTabSurface is not { } surface || _toolbar is not { } toolbar || _toolbarSurface is not { } bar) return;
        surface.Visible = bar.Visible && tab.IsShown;
        if (surface.Visible)
        {
            var scale = WindowScale;
            tab.Measure(Avalonia.Size.Infinity);
            int width = WholePixels(tab.DesiredSize.Width * scale), height = WholePixels(tab.DesiredSize.Height * scale);
            if (surface.Panel.Frame.Width != width || surface.Panel.Frame.Height != height) surface.Panel.Resize(width, height);
            var plaque = Avalonia.VisualExtensions.TranslatePoint(toolbar.Plaque, new Avalonia.Point(toolbar.Plaque.Bounds.Width / 2, 0), toolbar) ?? default;
            var screen = GameDevice.ViewportSize(_device);
            var x = bar.Location.X + (int)Math.Round(plaque.X * scale) - width / 2;
            var y = bar.Location.Y - height;
            surface.Location = new Point(Math.Max(0, Math.Min(screen.Width - width, x)), y >= 0 ? y : bar.Bounds.Bottom);
        }
        surface.Prepare();
    }

    private const string ToolbarPlaceId = "dereth-toolbar";

    /// <summary>Once per toolbar taken over, puts it at this character's saved place, kept on screen. Until then it stands where retail's did.</summary>
    private void LoadToolbarPlace()
    {
        if (_toolbarPlaceLoaded || !_inGame || _positionStore == null || _toolbarPlace == null || _toolbarSurface == null) return;
        _toolbarPlaceLoaded = true;
        if (_positionStore.Load(SessionServer(), SessionCharacter(), ToolbarPlaceId) is not { } saved) return;
        var screen = GameDevice.ViewportSize(_device);
        var size = _toolbarSurface.Bounds.Size;
        _toolbarPlace.Location = new Point(Math.Max(0, Math.Min(screen.Width - size.Width, saved.Location.X)),
            Math.Max(0, Math.Min(screen.Height - size.Height, saved.Location.Y)));
    }

    private void SaveToolbarPlace(Point location)
    {
        if (_inGame && _positionStore != null) _positionStore.Save(SessionServer(), SessionCharacter(), ToolbarPlaceId, (location, null));
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

    /// <summary>Moves the toolbar with the pointer, kept on screen. Stops when the takeover no longer lets it move.</summary>
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
        surface.Location = new Point(
            Math.Max(0, Math.Min(screen.Width - size.Width, pointer.X - offset.Width)),
            Math.Max(0, Math.Min(screen.Height - size.Height, pointer.Y - offset.Height)));
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
        var toolbar = new RetailToolbar(_toolbarPort, ToggleMenuWindow, MoveToMainPack, BeginToolbarDrag);
        // Drawn at LegACEy's window scale, as the other LegACEy windows are. The Dereth theme makes it larger than it measures
        // without one, so it is measured again under the theme and the panel takes that size, rounded up so no edge is cut.
        var scale = WindowScale;
        var panel = ObservePanel(AvaloniaPanel.Create(() => toolbar, 1, 1, scale), ToolbarFailed);
        panel.ApplyTheme(new DerethClientTheme());
        toolbar.Measure(Avalonia.Size.Infinity);
        panel.Resize(WholePixels(toolbar.DesiredSize.Width * scale), WholePixels(toolbar.DesiredSize.Height * scale));
        _toolbar = toolbar;
        _toolbarSurface = new ScreenSurface(_device, panel);
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

    /// <summary>
    /// Shows shortcut row 1 as its own window beside the toolbar, once the takeover holds the toolbar and the windows can open. Its
    /// position is saved like any LegACEy window's; where it first opens is beside the toolbar.
    /// </summary>
    private void EnsureShortcutBar()
    {
        if (_shortcutBar != null || !CanOpenWindows) return;
        _shortcutBar = OpenShortcutRow(1, 0, ShortcutRowWindowId, ToggleShortcutRow2);
        if (_row2Choice.IsOn(SessionServer(), SessionCharacter())) ShowShortcutRow2();
        else SetRow2TagTip();
        Action refresh = () => { _shortcutBar?.Refresh(); _shortcutRow2?.Refresh(); };
        _inventory.Changed += refresh;
        _unsubscribeShortcutItems = () => _inventory.Changed -= refresh;
    }

    /// <summary>Closes both shortcut rows, stops their refresh and frees their icons. Their saved positions stay where the player left them.</summary>
    private void CloseShortcutBar()
    {
        _unsubscribeShortcutItems?.Invoke();
        _unsubscribeShortcutItems = null;
        HideShortcutRow2();
        if (_shortcutBar == null) return;
        var bar = _shortcutBar;
        _shortcutBar = null;
        ReleaseFeatureWindow(ShortcutRowWindowId);
        bar.Dispose();
    }

    /// <summary>Row 1's tag: turns row 2 off or on and saves the choice for the character.</summary>
    private void ToggleShortcutRow2() => SetShortcutRow2(_shortcutRow2 == null);

    /// <summary>
    /// Turns row 2 on or off, and keeps the choice for the character. Row 2 sits beside row 1, so it opens only while row 1 is up; the choice is
    /// kept either way, and row 1 opens row 2 when it is on.
    /// </summary>
    private void SetShortcutRow2(bool on)
    {
        if (on && _shortcutBar != null) ShowShortcutRow2();
        else HideShortcutRow2();
        _row2Choice.Save(SessionServer(), SessionCharacter(), on);
        if (_settings != null) _settings.ShortcutRow2 = new OptionChoice<bool>(on, SetShortcutRow2);
    }

    private void ShowShortcutRow2()
    {
        if (_shortcutRow2 != null) return;
        // Row 2 starts at slot 9, the first slot after row 1's nine.
        _shortcutRow2 = OpenShortcutRow(2, ShortcutBar.SlotCount, ShortcutRow2WindowId, null);
        SetRow2TagTip();
    }

    private void HideShortcutRow2()
    {
        if (_shortcutRow2 == null) return;
        var bar = _shortcutRow2;
        _shortcutRow2 = null;
        ReleaseFeatureWindow(ShortcutRow2WindowId);
        bar.Dispose();
        SetRow2TagTip();
    }

    private void SetRow2TagTip() => _shortcutBar?.SetTagTip(_shortcutRow2 != null ? "Hide shortcut row 2" : "Show shortcut row 2");

    /// <summary>
    /// Opens a shortcut row as its own window. It has no title bar: the frame and the row tag drag it. Without a saved position it sits beside
    /// the toolbar (row 1) or 4 DIP above row 1 (row 2).
    /// </summary>
    private ShortcutBar OpenShortcutRow(int row, int firstIndex, string windowId, Action? tagClick)
    {
        var bar = new ShortcutBar(row, firstIndex, _toolbarPort!, ShortcutItemOf, _portal, () => NativeUi.IsUiLocked,
            () => _windows?.BeginMove(windowId, _pointer), () => _pointer, tagClick, new ItemDragHost(this), _shortcutDrag);
        // Measured at the window's scale, where its slots round to whole pixels, so the window holds all nine.
        var measured = AvaloniaPanel.MeasureAt(bar, Avalonia.Size.Infinity, WindowScale);
        var size = new Size(DipsFor(WholePixels(measured.Width * WindowScale), WindowScale), DipsFor(WholePixels(measured.Height * WindowScale), WindowScale));
        // No title bar: the bar drags from its content (DragsFromContent), so titleBarHeight is not used for a drag. It is set to the bar's
        // height only because a window's title bar must be at least 1 and at most its height.
        var definition = new WindowDefinition(windowId, $"Shortcut row {row}", size.Width, size.Height,
            titleBarHeight: size.Height, theme: new DerethClientTheme(), dragsFromContent: true);
        var beside = ShortcutBar.DefaultLocation(_toolbarSurface!.Bounds, size, WindowScale);
        // Row 2 goes above row 1 where row 1 is now, which the player may have moved.
        var location = row == 1 ? beside : ShortcutBar.AboveRow(_windows?.Get(ShortcutRowWindowId)?.Location ?? beside, size.Height, WindowScale);
        OpenWindowCore(definition, bar, location, ToolbarFailed);
        return bar;
    }

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
            if (pack.Id == id) return new ShortcutItem(new ItemVisual(pack.Icon, 0, 0, 0), 1);
        return null;
    }

    /// <summary>
    /// A LegACEy window's item released on the toolbar, as the toolbar's content takes it: only its backpack takes one. A shortcut dragged
    /// off a bar is not an item drop: released on the backpack it is only removed from its bar.
    /// </summary>
    private bool DeliverToToolbar(Func<object, Avalonia.Point, bool> deliver)
    {
        if (_shortcutDrag.Slot != null || _toolbarSurface is not { } surface) return false;
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
                ReleaseRetailChat();
                return;
            }
            if (!canDraw || !switchOn || _chatFailed) return;
            // Retail's chat is held from portal space at login on, like the toolbar; the window opens once the windows can, and the port
            // keeps the lines that come before it. Logoff gives it back, and character select holds it again.
            if (_chatTakeover == null && !HoldRetailChat()) return;
            _chatTakeover!.Tick(GameDevice.ViewportSize(_device));
            if (!_inGame) return;
            if (_chatWindows == null && !OpenRetailChat()) return;
            _chatPort!.Poll();
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
        _chatPort = new NativeRetailChatPort(Log, natives, (opcode, payload) => _channelTransport?.SendAction(opcode, payload) ?? false);
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
        var chats = new RetailChatWindows(port, _windows!, _positionStore!, _chatTabs, server, character, () => _pointer, (definition, content, location) => OpenWindowCore(definition, content, location, ChatFailed), ReleaseFeatureWindow,
            copy: CopyToClipboard, paste: PasteFromClipboard, carried: _carriedChatHistory);
        _carriedChatHistory = null;
        // The chat's own text size control moves the Settings window's value too.
        chats.TextSizeChanged += size => { if (_settings != null) _settings.ChatTextSize = new OptionChoice<int>(size, SetChatTextSize); };
        // The main window starts at the bottom left, above the toolbar, until the player moves it; its place is then kept per character.
        var screen = _windows!.Screen;
        var beside = new Point(12, Math.Max(0, screen.Height - (int)Math.Round(RetailChatWindows.DesignHeight * WindowScale) - 150));
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

    /// <summary>Opens the Dereth radar over the held retail radar. False when the windows cannot open yet, so the next frame tries again.</summary>
    private bool OpenRetailRadar()
    {
        if (!CanOpenWindows || _radarPort == null) return false;
        var screen = _windows!.Screen;
        var at = _radarPlace ?? new Point(Math.Max(0, screen.Width - (int)Math.Round(RetailRadarWindow.DesignWidth * WindowScale) - 12), 12);
        var port = _radarPort;
        var window = new RetailRadarWindow(port, RadarGround, LoadRadarOptions(), SaveRadarOptions, () => (uint)Bridge.Fact(BridgeFact.CurrentSelection),
            () => _windows?.BeginMove(RadarWindowId, _pointer));
        window.CoordinatesClicked += text => _chatWindows?.Main?.Insert(text);
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

    /// <summary>Drops the ground's DATs at unload or switch-off, after the radar that reads them is gone. A load still running is disposed when it lands.</summary>
    private void DisposeRadarGround()
    {
        if (_radarDatLoad is { } load)
        {
            if (load.IsCompletedSuccessfully) load.Result?.Dispose();
            else _ = load.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result?.Dispose(); }, TaskScheduler.Default);
        }
        _radarDatLoad = null;
        _radarTiles = null;
    }

    private void RadarFailed(Exception exception)
    {
        _radarFailed = true;
        Log($"Dereth radar disabled; retail's radar is back: {exception}");
        try { ReleaseRetailRadar(); }
        catch (Exception releaseError) { Log($"Could not give the retail radar back: {releaseError}"); }
    }

    /// <summary>
    /// Whether a feature's switch is on: on while its file is missing beside the DLL. The file is looked for once a second.
    /// </summary>
    private static bool SwitchOn(string file, ref DateTime lastCheck, ref bool on)
    {
        var now = DateTime.UtcNow;
        if (now - lastCheck >= TimeSpan.FromSeconds(1))
        {
            lastCheck = now;
            on = !File.Exists(IOPath.Combine(PluginDirectory, file));
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
        var view = new DerethVitalsWindow(_gameState, () => NativeUi.IsUiLocked, () => _windows?.BeginMove(definition.Id, _pointer), layout, ChooseVitalsLayout);
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
        RetailChatWindows.SaveTextSize(_positionStore!, SessionServer(), SessionCharacter(), size);
        if (_settings != null) _settings.ChatTextSize = new OptionChoice<int>(size, SetChatTextSize);
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
                    _retailDrag?.ClearKeyboardFocus();
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

    private InputSurface[] GetInputSurfaces()
    {
        var surfaces = new System.Collections.Generic.List<InputSurface>(4);
        if (_toolbarSurface is { Visible: true })
            surfaces.Add(new InputSurface(ToolbarSurfaceId, _toolbarSurface.Location.X, _toolbarSurface.Location.Y, _toolbarSurface.Bounds.Width, _toolbarSurface.Bounds.Height, 0, _toolbarSurface.Panel.WantsKeyboard));
        // Above every window, as it draws.
        if (_splitTabSurface is { Visible: true } split)
            surfaces.Add(new InputSurface(SplitTabSurfaceId, split.Location.X, split.Location.Y, split.Bounds.Width, split.Bounds.Height, (_windows?.ZOrder.Count ?? 0) + 1, split.Panel.WantsKeyboard));
        if (_windows != null)
        {
            var z = _windows.ZOrder.Count;
            foreach (var window in _windows.ZOrder)
            {
                var surface = SurfaceById(window.Id);
                if (surface is not { Visible: true }) continue;
                surfaces.Add(new InputSurface(window.Id, window.Location.X, window.Location.Y, window.Width, window.Height, z--, surface.Panel.WantsKeyboard));
            }
        }
        return surfaces.ToArray();
    }

    private ScreenSurface? SurfaceById(string? id)
    {
        if (id == ToolbarSurfaceId) return _toolbarSurface;
        if (id == SplitTabSurfaceId) return _splitTabSurface;
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

    /// <summary>
    /// The whole DIPs a window definition gives for a window <paramref name="pixels"/> long, as the window manager rounds them at the scale; a
    /// length in DIPs rounded up from a measure can open a pixel larger than the content, which then stretches into it.
    /// </summary>
    private static int DipsFor(int pixels, double scale)
    {
        var dips = (int)Math.Ceiling((pixels - 0.5) / scale);
        while (Math.Round(dips * scale) < pixels) dips++;
        return dips;
    }

    /// <summary>Puts a window's surface where the window manager has it, at the window's size. A new size re-lays out the panel.</summary>
    private static void FitSurface(ScreenSurface surface, ManagedWindow window)
    {
        surface.Location = window.Location;
        if (surface.Panel.Frame.Width != window.Width || surface.Panel.Frame.Height != window.Height)
            surface.Panel.Resize(window.Width, window.Height);
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
