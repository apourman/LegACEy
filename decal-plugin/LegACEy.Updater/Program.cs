using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LegACEy.Updater;

// LegACEy.Updater.exe <install> <stage>: run from a copy outside the install root (the client starts it from
// <install>\update\<version>\), so replacing the install never replaces the running exe.
// Exit codes: 0 applied, 1 failed and rolled back, 2 bad arguments or no ready stage, 3 another updater is running.
internal static class Program
{
    private static int Main(string[] args)
    {
        string install, stage;
        try
        {
            install = Path.GetFullPath(args[0]).TrimEnd('\\', '/');
            stage = Path.GetFullPath(args[1]).TrimEnd('\\', '/');
            if (args.Length != 2) return 2;
        }
        catch (Exception) { return 2; }

        using var mutex = new Mutex(false, @"Local\LegACEy.Updater");
        try
        {
            if (!mutex.WaitOne(0)) return 3;
        }
        catch (AbandonedMutexException) { }

        StageApply.Log(install, $"Waiting for every game client to exit, then applying {stage}");
        while (Process.GetProcessesByName("acclient").Length > 0) Thread.Sleep(2000);

        var result = StageApply.Apply(install, stage);
        var update = Path.Combine(install, "update");
        if (result == ApplyResult.Applied && Directory.Exists(update))
        {
            // This exe sits in update\ and can't delete itself: a hidden shell removes the folder once it has exited.
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rd /s /q \"{update}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            catch (Exception error) { StageApply.Log(install, $"Could not remove {update}: {error.Message}"); }
        }
        mutex.ReleaseMutex();
        return (int)result;
    }
}
