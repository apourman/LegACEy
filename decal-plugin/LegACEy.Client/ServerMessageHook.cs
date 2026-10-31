using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X86;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The server's messages, from retail itself. PacketController::AddReceivedBlobToQueue takes every reassembled message once, as it arrives
/// and before the SmartBox or the UI queue handles it, which is where Decal's ServerDispatch saw it too. The client gets each message it
/// reads in Decal's shape: its type, and its bytes from the type on. (RynthCore hooks ACSmartBox::DispatchSmartBoxEvent and
/// UIQueueManager::ProcessNetBlobData instead; the first never sees game events and the second gets them with the header cut off.)
/// </summary>
internal sealed unsafe class ServerMessageHook : IDisposable
{
    private const string Source = "acclient.pdb (C:\\Turbine\\Asheron's Call, a near build) for names; our objdump read of installed acclient.exe";
    private const uint AddReceivedBlobToQueue = 0x00542070;
    // NetBlob's message buffer and its length, as UIQueueManager::ProcessEphemeralNetBlob and ACSmartBox::DispatchSmartBoxEvent read them.
    private const int BufferOffset = 0x2C, SizeOffset = 0x30;

    private static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("PacketController::AddReceivedBlobToQueue", AddReceivedBlobToQueue,
            NativeUiCatalogue.Bytes("56 8B 74 24 08 85 F6 57 0F 84 EA 00 00 00 66 8B 46 3E 66 3D 0C 00"), Source,
            "ThisCall (PacketController*, NetBlob*) -> int, ret 4; the queue id at +0x3E; called once, from 0x0054AD90"),
        new NativeUiEntry("UIQueueManager::ProcessEphemeralNetBlob reads the NetBlob's size (+0x30) and buffer (+0x2C)", 0x0055DD83,
            NativeUiCatalogue.Bytes("8B 44 24 14 56 8B 70 30 57 8B F9 8B 48 2C"), Source, "instruction reference"),
    };

    [Function(CallingConventions.MicrosoftThiscall)]
    private delegate int AddReceivedBlobFn(IntPtr controller, IntPtr blob);

    private readonly ServerMessageReceiver _receive;
    private readonly Action<Exception> _failed;
    private readonly Action<string> _log;
    private IHook<AddReceivedBlobFn>? _hook;

    internal delegate void ServerMessageReceiver(int type, ReadOnlySpan<byte> raw);

    private ServerMessageHook(ServerMessageReceiver receive, Action<Exception> failed, Action<string> log)
    {
        _receive = receive;
        _failed = failed;
        _log = log;
    }

    /// <summary>The hook in place, or null with the reason logged when the running client doesn't match.</summary>
    public static ServerMessageHook? Place(ServerMessageReceiver receive, Action<Exception> failed, Action<string> log)
    {
        var hook = new ServerMessageHook(receive, failed, log);
        hook._hook = RetailHook.Place<AddReceivedBlobFn>(AddReceivedBlobToQueue, hook.OnAddReceivedBlob, Entries,
            "LegACEy reads no server messages: the running client does not match the message hook's entries.", log);
        return hook._hook != null ? hook : null;
    }

    /// <summary>Hands a message buffer, from its type on, to <paramref name="receive"/> when it is one the client reads.</summary>
    internal static void Deliver(byte* buffer, int size, ServerMessageReceiver receive)
    {
        if (buffer == null || size < 4) return;
        var message = new ReadOnlySpan<byte>(buffer, size);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(message);
        if (Reads(type)) receive((int)type, message);
    }

    /// <summary>
    /// The messages the client reads: game events (0xF7B0) carry the server channel; DDD_EndDDD (0xF7EA) ends character select's DDD
    /// exchange, after any world note; Login_WorldInfo (0xF7E1) names the server, Login_CreatePlayer (0xF746) is the character entering the
    /// world and Login_LogOffCharacter (0xF653) the server letting it go (SessionIdentity); 0xF625 to 0xF7DB are objects' looks,
    /// for the paperdoll (LookCache); 0x02CD to 0x02E7 are the private int, int64, skill, attribute and vital updates (CharacterSheet); 0x0024
    /// (an item leaves a container) and 0x02CE (a stack's size changes) are what the container window follows besides the objects.
    /// </summary>
    private static bool Reads(uint type) => type is 0xF7B0 or 0xF7EA or 0xF7E1 or 0xF746 or 0xF653 or 0xF625 or 0xF745 or 0xF747 or 0xF749 or 0xF74A or 0xF7DB
        or 0x02CD or 0x02CF or 0x02DD or 0x02E3 or 0x02E7 or 0x0024 or 0x02CE;

    private int OnAddReceivedBlob(IntPtr controller, IntPtr blob)
    {
        // Read before retail queues it: the caller lets go of the blob as soon as this returns.
        if (blob != IntPtr.Zero)
        {
            try { Deliver(*(byte**)(blob + BufferOffset), *(int*)(blob + SizeOffset), _receive); }
            catch (Exception exception)
            {
                try { _failed(exception); }
                catch { /* Reporting must never escape the native callback. */ }
            }
        }
        return _hook!.OriginalFunction(controller, blob);
    }

    public void Dispose() => RetailHook.Remove(_hook, "server message", _log);
}
