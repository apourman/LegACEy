using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LegACEy.Updater;

// LegACEy.Updater.exe <install> <stage>: once every game client exits, applies <stage>, or the newest ready stage beside it.
// Run from a copy outside the install root (the client starts it from <install>\update\<version>\), so replacing the install
// never replaces the running exe.
// Exit codes: 0 applied, 1 failed and rolled back, 2 bad arguments or no ready stage, 3 another updater is running.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        string install, stage;
        try
        {
            install = Path.GetFullPath(args[0]).TrimEnd('\\', '/');
            stage = Path.GetFullPath(args[1]).TrimEnd('\\', '/');
        }
        catch (Exception) { return 2; }

        using var mutex = new Mutex(false, @"Local\LegACEy.Updater");
        try
        {
            if (!mutex.WaitOne(0))
            {
                StageApply.Log(install, "Another updater is already running");
                return 3;
            }
        }
        catch (AbandonedMutexException) { }

        try
        {
            StageApply.Log(install, $"Waiting for every game client to exit, then applying {stage} or a newer stage");
            while (GameRunning()) Thread.Sleep(2000);
            return (int)StageApply.Apply(install, StageApply.Newest(install, stage));
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool GameRunning()
    {
        var clients = Process.GetProcessesByName("acclient");
        foreach (var client in clients) client.Dispose();
        return clients.Length > 0;
    }
}
