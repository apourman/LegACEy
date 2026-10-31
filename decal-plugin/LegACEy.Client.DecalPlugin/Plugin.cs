using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using IOPath = System.IO.Path;

// Decal keys a plugin's registration by its assembly GUID. setup.exe registers under this one (the key a manual
// registration of 0.6.1 got); pinned so a manual registration lands on the same key.
[assembly: Guid("00156974-B6EE-4990-A850-4E3B55871654")]

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The thin Decal stub. It starts the LegACEy client's .NET 10 runtime from runtime\ beside this DLL and hands it the bridge: Decal
/// events go in through the client's table, and the client's Decal reads and actions come back out through <see cref="DecalCalls"/>. The
/// client hooks its own frame, input, server messages and chat lines.
/// Anything that fails here is logged and leaves the game untouched; nothing is thrown into Decal.
/// </summary>
[FriendlyName("LegACEy Decal Plugin")]
public sealed unsafe class Plugin : FilterBase
{
    /// <summary>The folder holding this DLL: the log, settings and Plugins\ live there.</summary>
    internal static readonly string Directory = IOPath.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
    private static readonly int ProcessId = CurrentProcessId();

    // Process lifetime: the runtime can't be unloaded, and the client keeps pointers into both tables.
    private static delegate* unmanaged[Stdcall]<BridgeEvents*, BridgeCalls*, int> _init;
    private static BridgeEvents* _events;
    private static BridgeCalls* _calls;
    private bool _running;

    protected override void Startup()
    {
        try
        {
            Log($"LegACEy stub starting: {FreeAddressSpace()}.");
            if (_init == null) _init = StartRuntime();
            if (_events == null)
            {
                _events = (BridgeEvents*)Marshal.AllocHGlobal(sizeof(BridgeEvents));
                *_events = default;
                _calls = DecalCalls.Create();
            }
            var watch = Stopwatch.StartNew();
            if (_init(_events, _calls) != 0)
            {
                Log("LegACEy is off: the client did not start (see the lines above, or runtime\\legacey-avalonia.log if it could not read this folder).");
                return;
            }
            // The client is running from here on, so Shutdown must reach it whatever fails next.
            _running = true;
            Log($"LegACEy client started in {watch.ElapsedMilliseconds} ms: {FreeAddressSpace()}.");
            CoreManager.Current.FilterInitComplete += OnFilterInitComplete;
        }
        catch (Exception exception) { Log(_running ? $"LegACEy could not wait for Decal's filters: {exception}" : $"LegACEy is off: {exception}"); }
    }

    protected override void Shutdown()
    {
        if (!_running) return;
        _running = false;
        // Each group on its own, so one failing filter doesn't keep the rest subscribed.
        Unsubscribe(() =>
        {
            var core = CoreManager.Current;
            core.FilterInitComplete -= OnFilterInitComplete;
            core.ContainerOpened -= OnContainerOpened;
        });
        Unsubscribe(() =>
        {
            var world = CoreManager.Current.WorldFilter;
            world.CreateObject -= OnCreated;
            world.ChangeObject -= OnChanged;
            world.ReleaseObject -= OnReleased;
            world.MoveObject -= OnMoved;
        });
        _events->Shutdown();
    }

    private static void Unsubscribe(Action unsubscribe)
    {
        try { unsubscribe(); }
        catch (Exception exception) { Log($"Could not unsubscribe from Decal at shutdown: {exception}"); }
    }

    // The client's event entry points catch their own exceptions, so only the stub's own Decal reads need a guard here.

    private void OnFilterInitComplete(object? sender, EventArgs e)
    {
        try
        {
            var core = CoreManager.Current;
            core.FilterInitComplete -= OnFilterInitComplete;
            // The open container is part of the inventory snapshot, whoever changed it.
            core.ContainerOpened += OnContainerOpened;
            var world = core.WorldFilter;
            world.CreateObject += OnCreated;
            world.ChangeObject += OnChanged;
            world.ReleaseObject += OnReleased;
            world.MoveObject += OnMoved;
        }
        catch (Exception exception) { Log($"LegACEy could not subscribe to Decal: {exception}"); }
    }

    // Bridge.Raise on the client side catches every exception, so these plain forwards can't unwind into Decal.
    private void OnContainerOpened(object? sender, ContainerOpenedEventArgs e) => _events->ContainerOpened();

    private void OnCreated(object? sender, CreateObjectEventArgs e) => InventoryChanged(e.New);
    private void OnChanged(object? sender, ChangeObjectEventArgs e) => InventoryChanged(e.Changed);
    private void OnReleased(object? sender, ReleaseObjectEventArgs e) => InventoryChanged(e.Released);
    private void OnMoved(object? sender, MoveObjectEventArgs e) => InventoryChanged(e.Moved);

    private void InventoryChanged(WorldObject? item)
    {
        if (item == null) return;
        try { _events->InventoryChanged(item.Id, item.Container); }
        catch (Exception exception) { Log($"Could not pass an inventory change to LegACEy: {exception.Message}"); }
    }

    /// <summary>Starts CoreCLR from runtime\ and returns the client's Bridge.Init. Throws with the reason when it can't.</summary>
    private static delegate* unmanaged[Stdcall]<BridgeEvents*, BridgeCalls*, int> StartRuntime()
    {
        var runtime = IOPath.Combine(Directory, "runtime");
        var coreclrPath = IOPath.Combine(runtime, "coreclr.dll");
        if (!File.Exists(coreclrPath)) throw new FileNotFoundException($"The .NET runtime is missing: {coreclrPath} was not found.");

        var watch = Stopwatch.StartNew();
        var coreclr = LoadLibraryEx(coreclrPath, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (coreclr == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            throw error == ErrorBadExeFormat
                ? new BadImageFormatException($"{coreclrPath} is not an x86 runtime.")
                : new Win32Exception(error, $"Could not load {coreclrPath}: {new Win32Exception(error).Message}");
        }
        var initialize = (delegate* unmanaged[Stdcall]<byte*, byte*, int, byte**, byte**, IntPtr*, uint*, int>)Export(coreclr, "coreclr_initialize");
        var createDelegate = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, byte*, byte*, void**, int>)Export(coreclr, "coreclr_create_delegate");

        var assemblies = System.IO.Directory.GetFiles(runtime, "*.dll").Where(IsManaged).ToArray();
        var keys = new[] { "TRUSTED_PLATFORM_ASSEMBLIES", "NATIVE_DLL_SEARCH_DIRECTORIES", "APP_CONTEXT_BASE_DIRECTORY", "System.GC.Concurrent", "System.Globalization.Invariant" }
            .Select(value => (IntPtr)Utf8(value)).ToArray();
        var values = new[] { string.Join(";", assemblies), runtime + ";", runtime + "\\", "true", "true" }.Select(value => (IntPtr)Utf8(value)).ToArray();
        IntPtr host;
        uint domain;
        void* init;
        fixed (IntPtr* keyPointers = keys)
        fixed (IntPtr* valuePointers = values)
            Check(initialize(Utf8(GameExecutable()), Utf8("LegACEy"), keys.Length,
                (byte**)keyPointers, (byte**)valuePointers, &host, &domain), "coreclr_initialize");
        Check(createDelegate(host, domain, Utf8("LegACEy.Client"), Utf8("LegACEy.Client.DecalPlugin.Bridge"), Utf8("Init"), &init),
            "coreclr_create_delegate for Bridge.Init");
        Log($"CoreCLR up in {watch.ElapsedMilliseconds} ms with {assemblies.Length} assemblies: {FreeAddressSpace()}.");
        return (delegate* unmanaged[Stdcall]<BridgeEvents*, BridgeCalls*, int>)init;
    }

    private static int CurrentProcessId()
    {
        using var process = Process.GetCurrentProcess();
        return process.Id;
    }

    private static string GameExecutable()
    {
        using var process = Process.GetCurrentProcess();
        return process.MainModule!.FileName;
    }

    private const int LoadWithAlteredSearchPath = 0x8;
    private const int ErrorBadExeFormat = 193;

    private static void* Export(IntPtr module, string name) =>
        GetProcAddress(module, name) is var pointer && pointer != IntPtr.Zero ? (void*)pointer : throw new EntryPointNotFoundException($"coreclr.dll has no {name}.");

    private static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"{what} failed: 0x{result:X8}.");
    }

    /// <summary>A NUL-terminated UTF-8 copy for CoreCLR. ponytail: never freed; a handful of strings, once per process.</summary>
    private static byte* Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        var copy = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, copy, bytes.Length);
        return (byte*)copy;
    }

    private static bool IsManaged(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException) { return false; }
    }

    /// <summary>Appends to the client's log beside this DLL; the process id tells several clients' lines apart.</summary>
    internal static void Log(string message)
    {
        try { File.AppendAllText(IOPath.Combine(Directory, "legacey-avalonia.log"), $"{DateTime.Now:O} [pid {ProcessId}] {message}{Environment.NewLine}"); }
        catch
        {
            // Logging must never break the Decal callback that triggered it.
        }
    }

    /// <summary>Free 32-bit address space, as RynthCore's LoaderFreeAddressSpaceMb walks it.</summary>
    private static string FreeAddressSpace()
    {
        long free = 0, largest = 0, address = 0;
        while (VirtualQuery(new IntPtr(unchecked((int)(uint)address)), out var region, (IntPtr)sizeof(MemoryRegion)) != IntPtr.Zero)
        {
            var size = (long)(uint)region.RegionSize;
            if (size == 0) break;
            if (region.State == MemFree)
            {
                free += size;
                largest = Math.Max(largest, size);
            }
            address = (uint)region.BaseAddress + size;
            if (address > uint.MaxValue) break;
        }
        return $"{free >> 20} MB free, largest {largest >> 20} MB";
    }

    private const uint MemFree = 0x10000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQuery(IntPtr address, out MemoryRegion region, IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
