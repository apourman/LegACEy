using System;
using LegACEy.Client.DecalPlugin;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class SessionRecordTests
{
    // Real wevtutil output, cut down: retail's fault (with a pid), .NET's two records of another crash (without), and a different program.
    private const string Events = """
        Event[0]
          Source: Application Error
          Date: 2026-10-26T18:41:06.1230000Z
          Description:
        Faulting application name: acclient.exe, version: 0.0.11.6096, time stamp: 0x557a956c
        Faulting module name: acclient.exe, version: 0.0.11.6096, time stamp: 0x557a956c
        Exception code: 0xc0000005
        Fault offset: 0x0013916a
        Faulting process id: 0xB2A4

        Event[1]
          Source: .NET Runtime
          Date: 2026-10-26T18:54:45.0840000Z
          Description:
        Application: acclient.exe
        Exception Info: exception code c0000005, exception address 0053D3C2
        Stack:
           at LegACEy.Client.DecalPlugin.WorldEffects.OnLandscapeDraw(IntPtr)

        Event[2]
          Source: .NET Runtime
          Date: 2026-10-26T18:54:45.0710000Z
          Description: 
        Application: acclient.exe
        Exception Info: exception code c0000005, exception address 0053D3C2
        Stack:

        Event[3]
          Source: Application Error
          Date: 2026-10-26T18:50:00.0000000Z
          Description:
        Faulting application name: notepad.exe, version: 1.0
        Faulting process id: 0x10

        """;

    [Fact]
    public void A_gone_client_is_described_by_its_own_fault_then_by_an_unclaimed_dotnet_record_then_as_unrecorded()
    {
        var events = CrashEvent.Parse(Events.Replace("\n", "\r\n"));
        Assert.Equal(3, events.Count);

        var faulted = SessionRecord.Describe(0xB2A4, new DateTime(2026, 10, 26, 18, 38, 55), events);
        Assert.Contains("crashed after 2.2 min", faulted);
        Assert.Contains("0xc0000005 in acclient.exe at +0x0013916a", faulted);

        var dotnet = SessionRecord.Describe(39916, new DateTime(2026, 10, 26, 18, 54, 10), events);
        Assert.Contains("probably", dotnet);
        Assert.Contains("exception address 0053D3C2 ← LegACEy.Client.DecalPlugin.WorldEffects.OnLandscapeDraw(IntPtr)", dotnet);

        // Both of .NET's records went to the first client, so a second one started before them gets neither.
        Assert.Contains("Windows recorded no crash", SessionRecord.Describe(1, new DateTime(2026, 10, 26, 18, 50, 0), events));
    }
}
