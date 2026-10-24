using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace LegACEy.Updater;

// LegACEy.Updater.exe <install> <stage>: once every game client exits, applies <stage>, or the newest ready stage beside it.
// LegACEy.Updater.exe world <install> <game> <stage>: once every game client exits, applies the world patch staged in <stage> to <game>'s dats.
// Run from a copy outside the install root (the client starts it from the stage folder), so replacing the install never replaces the
// running exe. The two modes hold their own mutex, so a plugin update and a world update waiting together both apply.
// Exit codes: 0 applied, 1 failed and rolled back, 2 bad arguments or no ready stage, 3 another updater of the same kind is running.
internal static class Program
{
    private static int Main(string[] args)
    {
        // The world apply's dat library and its dependencies travel inside the exe, which is copied on its own.
        AppDomain.CurrentDomain.AssemblyResolve += EmbeddedAssembly;
        var world = args.Length == 4 && args[0] == "world";
        if (!world && args.Length != 2) return 2;
        string install, stage, game = "";
        try
        {
            install = Full(args[world ? 1 : 0]);
            stage = Full(args[world ? 3 : 1]);
            if (world) game = Full(args[2]);
        }
        catch (Exception) { return 2; }

        using var mutex = new Mutex(false, world ? WorldFiles.UpdaterMutex : @"Local\LegACEy.Updater");
        try
        {
            if (!mutex.WaitOne(0))
            {
                StageApply.Log(install, world ? "Another world updater is already running" : "Another updater is already running");
                return 3;
            }
        }
        catch (AbandonedMutexException) { }

        try
        {
            StageApply.Log(install, world
                ? $"Waiting for every game client to exit, then applying the world patch in {stage} to {game}"
                : $"Waiting for every game client to exit, then applying {stage} or a newer stage");
            while (GameRunning()) Thread.Sleep(2000);
            return world ? (int)WorldApply.Apply(install, game, stage) : (int)StageApply.Apply(install, StageApply.Newest(install, stage));
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static string Full(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private static bool GameRunning()
    {
        var clients = Process.GetProcessesByName("acclient");
        foreach (var client in clients) client.Dispose();
        return clients.Length > 0;
    }

    // Loaded once per name whatever version is asked for: the exe's binding redirects stay behind in its .config when it is copied.
    private static readonly System.Collections.Generic.Dictionary<string, Assembly> Embedded = new();

    private static Assembly? EmbeddedAssembly(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        lock (Embedded)
        {
            if (Embedded.TryGetValue(name, out var loaded)) return loaded;
            using var stream = typeof(Program).Assembly.GetManifestResourceStream(name + ".dll");
            if (stream == null) return null;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return Embedded[name] = Assembly.Load(bytes.ToArray());
        }
    }
}
