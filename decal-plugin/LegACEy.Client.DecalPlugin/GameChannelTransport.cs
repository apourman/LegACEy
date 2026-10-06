using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Decal.Adapter;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// The LegACEy server channel over the game connection. Requests go out as a game action (0xF7B1) with the LegACEy
/// opcode, sent through the retail client's own <c>Proto_UI::SendToWeenie</c> exactly as retail actions such as
/// <c>CM_Communication::Event_Talk</c> build theirs: <c>[0xF7B1][UI counter][opcode][payload]</c> in a buffer from
/// the client's allocator, which the NetBlob then owns. Replies and pushes arrive as a game event (0xF7B0) with the
/// LegACEy event type, read from Decal's EchoFilter; retail acclient drops event types it does not know.
/// Call everything on the game thread.
/// </summary>
internal sealed class GameChannelTransport : IServerChannelTransport
{
    private const uint GameActionMessage = 0xF7B1;
    private const int GameEventMessage = 0xF7B0;
    private const string Source = "Chorizite AcClient bindings; our capstone read of installed end-of-retail acclient.exe";

    internal static readonly IReadOnlyList<NativeUiEntry> Entries = new[]
    {
        new NativeUiEntry("Proto_UI::SendToWeenie", 0x005473D0, Bytes("53 32 DB E8 98 A3 FF FF 85 C0 74 51"), Source, "Cdecl (char* buf, int size) -> bool; NetBlob takes the buffer"),
        new NativeUiEntry("Proto_UI::GetNextUICounter", 0x005473A0, Bytes("A1 38 6F 84 00 40 A3 38 6F 84 00 C3"), Source, "Cdecl () -> uint"),
        new NativeUiEntry("Proto_UI::UICounterFailedSend", 0x005473B0, Bytes("FF 0D 38 6F 84 00 C3"), Source, "Cdecl ()"),
        new NativeUiEntry("operator new[]", 0x005DF0F0, Bytes("E9 00 00 00 00 56 8B 74 24 08"), Source, "Cdecl (size) -> void*; the allocator Event_Talk uses"),
        new NativeUiEntry("CM_Communication::Event_Talk calls SendToWeenie", 0x006A546E, Bytes("57 E8 5C 1F EA FF"), Source, "reference: retail action send path")
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendToWeenieFn(IntPtr buffer, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetNextUiCounterFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UiCounterFailedSendFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OperatorNewFn(uint size);

    private readonly Func<bool> _sessionReady;
    private readonly Action<string> _log;
    private readonly bool _verified;
    private readonly SendToWeenieFn? _sendToWeenie;
    private readonly GetNextUiCounterFn? _nextCounter;
    private readonly UiCounterFailedSendFn? _counterFailed;
    private readonly OperatorNewFn? _allocate;
    private bool _loggedFirstSend;
    private bool _loggedFirstReceive;

    public GameChannelTransport(Func<uint, int, byte[]> readMemory, Func<bool> sessionReady, Action<string> log)
    {
        _sessionReady = sessionReady;
        _log = log;
        _verified = NativeUiCatalogue.Validate(readMemory, log, Entries);
        if (!_verified)
        {
            log("LegACEy server channel disabled: the running client does not match its native send entries.");
            return;
        }
        _sendToWeenie = Function<SendToWeenieFn>(0x005473D0);
        _nextCounter = Function<GetNextUiCounterFn>(0x005473A0);
        _counterFailed = Function<UiCounterFailedSendFn>(0x005473B0);
        _allocate = Function<OperatorNewFn>(0x005DF0F0);
        log("LegACEy server channel ready: native send entries verified.");
    }

    public bool IsAvailable => _verified && _sessionReady();
    public int Sent { get; private set; }
    public int Received { get; private set; }

    public bool Send(byte[] requestPayload)
    {
        if (!IsAvailable) return false;
        var size = 12 + requestPayload.Length;
        var buffer = _allocate!((uint)size);
        if (buffer == IntPtr.Zero) return false;
        var counter = _nextCounter!();
        Marshal.WriteInt32(buffer, 0, unchecked((int)GameActionMessage));
        Marshal.WriteInt32(buffer, 4, unchecked((int)counter));
        Marshal.WriteInt32(buffer, 8, unchecked((int)ChannelWire.GameActionOpcode));
        Marshal.Copy(requestPayload, 0, buffer + 12, requestPayload.Length);
        var sent = _sendToWeenie!(buffer, size) != 0;
        if (!sent) _counterFailed!();
        else Sent++;
        if (!_loggedFirstSend || !sent)
        {
            _log($"Server channel send: {size} bytes, UI counter {counter}, accepted by client: {sent}.");
            _loggedFirstSend = true;
        }
        return sent;
    }

    /// <summary>
    /// The LegACEy event payload (after the event type) from a Decal server message, or null for any other message.
    /// Decal parses 0xF7B0 as character, sequence and event, so an unknown event type still reaches plugins.
    /// </summary>
    public byte[]? ReadEvent(NetworkMessageEventArgs e)
    {
        var message = e.Message;
        if (message.Type != GameEventMessage) return null;
        int eventType;
        try { eventType = message.Value<int>("event"); }
        catch (Exception) { return null; }
        if (unchecked((uint)eventType) != ChannelWire.GameEventType) return null;
        var raw = message.RawData;
        // Decal's raw bytes may or may not start with the message type; find the event type either way.
        var offset = raw.Length >= 16 && BitConverter.ToUInt32(raw, 0) == GameEventMessage && BitConverter.ToUInt32(raw, 12) == ChannelWire.GameEventType ? 16
            : raw.Length >= 12 && BitConverter.ToUInt32(raw, 8) == ChannelWire.GameEventType ? 12 : -1;
        if (!_loggedFirstReceive || offset < 0)
        {
            _log($"Server channel event: {raw.Length} raw bytes, payload offset {offset}.");
            _loggedFirstReceive = true;
        }
        if (offset < 0) return null;
        Received++;
        var payload = new byte[raw.Length - offset];
        Buffer.BlockCopy(raw, offset, payload, 0, payload.Length);
        return payload;
    }

    private static T Function<T>(uint address) where T : Delegate =>
        (T)Marshal.GetDelegateForFunctionPointer(new IntPtr(address), typeof(T));

    private static byte[] Bytes(string bytes)
    {
        var parts = bytes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++) result[i] = Convert.ToByte(parts[i], 16);
        return result;
    }
}
