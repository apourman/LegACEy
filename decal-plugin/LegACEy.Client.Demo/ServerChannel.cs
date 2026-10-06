using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LegACEy.Client.Demo;

/// <summary>Reply status. Values below 100 come from the server (ACE.Server.ClientChannel.ChannelStatus); the rest are local.</summary>
public enum ChannelStatus : byte
{
    Ok = 0,
    Error = 1,
    UnknownAction = 2,
    RateLimited = 3,
    BadRequest = 4,
    Unavailable = 5,
    TimedOut = 100,
    Disconnected = 101,
    NotSent = 102
}

public sealed class ChannelReply
{
    public ChannelReply(ChannelStatus status, byte[] body, TimeSpan roundTrip)
    {
        Status = status;
        Body = body ?? Array.Empty<byte>();
        RoundTrip = roundTrip;
    }

    public ChannelStatus Status { get; }
    public byte[] Body { get; }
    public TimeSpan RoundTrip { get; }
    public bool Ok => Status == ChannelStatus.Ok;

    /// <summary>The reason for a failed reply: the server's text, or a local description.</summary>
    public string Message
    {
        get
        {
            if (Ok) return string.Empty;
            if (Body.Length > 0)
                try { return ChannelWire.ReadString(ChannelWire.Reader(Body)); } catch (EndOfStreamException) { }
            return Status switch
            {
                ChannelStatus.TimedOut => "The server did not answer in time.",
                ChannelStatus.Disconnected => "The game connection ended.",
                ChannelStatus.NotSent => "The request could not be sent.",
                _ => Status.ToString()
            };
        }
    }
}

/// <summary>
/// Feature-facing LegACEy server channel: named requests with one reply each, and server-pushed topics.
/// Callbacks run on the host's render thread; dispose a request or subscription to stop its callback.
/// </summary>
public interface IServerChannel
{
    bool IsAvailable { get; }
    IDisposable Request(string action, byte[] body, Action<ChannelReply> completed, TimeSpan? timeout = null);
    IDisposable Subscribe(string topic, Action<byte[]> handler);
}

/// <summary>Host transport: sends one encoded request as the payload of the LegACEy game action.</summary>
public interface IServerChannelTransport
{
    bool IsAvailable { get; }
    bool Send(byte[] requestPayload);
}

/// <summary>
/// Request correlation, timeouts and push routing over a host transport. Not thread-safe: the host calls
/// Request, Receive, Tick and Reset on its render thread, which is also where Decal raises network events.
/// </summary>
public sealed class ServerChannelClient : IServerChannel, IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IServerChannelTransport _transport;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<uint, Pending> _pending = new();
    private readonly Dictionary<string, List<Subscription>> _subscriptions = new(StringComparer.Ordinal);
    private uint _nextId;

    public ServerChannelClient(IServerChannelTransport transport, Func<DateTime>? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public bool IsAvailable => _transport.IsAvailable;
    public int PendingCount => _pending.Count;
    /// <summary>Events the client could not decode or match; kept for diagnostics.</summary>
    public int Discarded { get; private set; }

    public IDisposable Request(string action, byte[] body, Action<ChannelReply> completed, TimeSpan? timeout = null)
    {
        if (completed == null) throw new ArgumentNullException(nameof(completed));
        var id = unchecked(++_nextId);
        if (id == 0) id = ++_nextId;
        var now = _clock();
        var pending = new Pending(this, id, completed, now, now + (timeout ?? DefaultTimeout));
        bool sent;
        try { sent = _transport.IsAvailable && _transport.Send(ChannelWire.EncodeRequest(id, action, body)); }
        catch (Exception) { sent = false; }
        if (!sent)
        {
            completed(new ChannelReply(ChannelStatus.NotSent, Array.Empty<byte>(), TimeSpan.Zero));
            return pending;
        }
        _pending.Add(id, pending);
        return pending;
    }

    public IDisposable Subscribe(string topic, Action<byte[]> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (!_subscriptions.TryGetValue(topic, out var list)) _subscriptions.Add(topic, list = new List<Subscription>());
        var subscription = new Subscription(this, topic, handler);
        list.Add(subscription);
        return subscription;
    }

    /// <summary>A LegACEy game event payload (after the event type) from the host.</summary>
    public void Receive(byte[] eventPayload)
    {
        if (!ChannelWire.TryDecodeEvent(eventPayload, out var message))
        {
            Discarded++;
            return;
        }
        if (message.Kind == ChannelEventKind.Reply)
        {
            if (!_pending.TryGetValue(message.RequestId, out var pending))
            {
                // A reply for a request that timed out or was cancelled.
                Discarded++;
                return;
            }
            _pending.Remove(message.RequestId);
            pending.Complete(new ChannelReply(message.Status, message.Body, _clock() - pending.Sent));
            return;
        }
        if (!_subscriptions.TryGetValue(message.Topic, out var list)) return;
        foreach (var subscription in list.ToArray())
            if (subscription.Active) subscription.Handler(message.Body);
    }

    /// <summary>Fails requests whose timeout has passed.</summary>
    public void Tick()
    {
        if (_pending.Count == 0) return;
        var now = _clock();
        List<Pending>? expired = null;
        foreach (var pending in _pending.Values)
            if (now >= pending.Deadline) (expired ??= new List<Pending>()).Add(pending);
        if (expired == null) return;
        foreach (var pending in expired)
        {
            _pending.Remove(pending.Id);
            pending.Complete(new ChannelReply(ChannelStatus.TimedOut, Array.Empty<byte>(), now - pending.Sent));
        }
    }

    /// <summary>The game session ended: every outstanding request fails as disconnected.</summary>
    public void Reset()
    {
        var pending = new List<Pending>(_pending.Values);
        _pending.Clear();
        var now = _clock();
        foreach (var request in pending)
            request.Complete(new ChannelReply(ChannelStatus.Disconnected, Array.Empty<byte>(), now - request.Sent));
    }

    public void Dispose()
    {
        _pending.Clear();
        _subscriptions.Clear();
    }

    private sealed class Pending : IDisposable
    {
        private readonly ServerChannelClient _owner;
        private Action<ChannelReply>? _completed;

        public Pending(ServerChannelClient owner, uint id, Action<ChannelReply> completed, DateTime sent, DateTime deadline)
        { _owner = owner; Id = id; _completed = completed; Sent = sent; Deadline = deadline; }

        public uint Id { get; }
        public DateTime Sent { get; }
        public DateTime Deadline { get; }

        public void Complete(ChannelReply reply)
        {
            var completed = _completed;
            _completed = null;
            completed?.Invoke(reply);
        }

        public void Dispose()
        {
            _completed = null;
            _owner._pending.Remove(Id);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ServerChannelClient _owner;
        private readonly string _topic;

        public Subscription(ServerChannelClient owner, string topic, Action<byte[]> handler)
        { _owner = owner; _topic = topic; Handler = handler; }

        public Action<byte[]> Handler { get; }
        public bool Active { get; private set; } = true;

        public void Dispose()
        {
            if (!Active) return;
            Active = false;
            if (_owner._subscriptions.TryGetValue(_topic, out var list)) list.Remove(this);
        }
    }
}

/// <summary>Channel used when the host has no server connection (tests, failed native checks).</summary>
public sealed class UnavailableServerChannel : IServerChannel
{
    public static readonly UnavailableServerChannel Instance = new();
    public bool IsAvailable => false;

    public IDisposable Request(string action, byte[] body, Action<ChannelReply> completed, TimeSpan? timeout = null)
    {
        completed(new ChannelReply(ChannelStatus.NotSent, Array.Empty<byte>(), TimeSpan.Zero));
        return Nothing.Instance;
    }

    public IDisposable Subscribe(string topic, Action<byte[]> handler) => Nothing.Instance;

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}

public enum ChannelEventKind : byte { Reply = 1, Push = 2 }

public sealed class ChannelEvent
{
    public ChannelEvent(ChannelEventKind kind, ChannelStatus status, uint requestId, string topic, byte[] body)
    { Kind = kind; Status = status; RequestId = requestId; Topic = topic; Body = body; }
    public ChannelEventKind Kind { get; }
    public ChannelStatus Status { get; }
    public uint RequestId { get; }
    public string Topic { get; }
    public byte[] Body { get; }
}

/// <summary>
/// The channel's wire format, version 1; it must match ACE.Server.ClientChannel.ChannelWire.
/// Request: u16 version, u32 request id, string action, u32 body length, body.
/// Event: u16 version, u8 kind, u8 status, u32 request id, string topic, u32 body length, body.
/// Strings are a u16 byte count and UTF-8; everything is little-endian.
/// </summary>
public static class ChannelWire
{
    public const ushort Version = 1;
    /// <summary>The LegACEy game action opcode (0xF7B1) and game event type (0xF7B0).</summary>
    public const uint GameActionOpcode = 0x4C47;
    public const uint GameEventType = 0x4C47;
    public const int MaxNameBytes = 64;
    public const int MaxRequestBody = 4096;
    public const int MaxEventBody = 256 * 1024;

    public static byte[] EncodeRequest(uint requestId, string action, byte[]? body)
    {
        body ??= Array.Empty<byte>();
        if (body.Length > MaxRequestBody) throw new ArgumentException($"Request body is over {MaxRequestBody} bytes.", nameof(body));
        return Body(writer =>
        {
            writer.Write(Version);
            writer.Write(requestId);
            WriteString(writer, action, MaxNameBytes);
            writer.Write((uint)body.Length);
            writer.Write(body);
        });
    }

    public static byte[] EncodeEvent(ChannelEventKind kind, ChannelStatus status, uint requestId, string topic, byte[]? body) => Body(writer =>
    {
        body ??= Array.Empty<byte>();
        writer.Write(Version);
        writer.Write((byte)kind);
        writer.Write((byte)status);
        writer.Write(requestId);
        WriteString(writer, topic, MaxNameBytes);
        writer.Write((uint)body.Length);
        writer.Write(body);
    });

    public static bool TryDecodeRequest(byte[] payload, out uint requestId, out string action, out byte[] body)
    {
        requestId = 0; action = string.Empty; body = Array.Empty<byte>();
        try
        {
            var reader = Reader(payload);
            if (reader.ReadUInt16() != Version) return false;
            requestId = reader.ReadUInt32();
            action = ReadString(reader, MaxNameBytes);
            var length = reader.ReadUInt32();
            if (length > MaxRequestBody) return false;
            body = reader.ReadBytes((int)length);
            return body.Length == length && requestId != 0 && action.Length != 0;
        }
        catch (Exception error) when (error is EndOfStreamException || error is InvalidDataException) { return false; }
    }

    public static bool TryDecodeEvent(byte[] payload, out ChannelEvent message)
    {
        message = null!;
        try
        {
            var reader = Reader(payload);
            if (reader.ReadUInt16() != Version) return false;
            var kind = (ChannelEventKind)reader.ReadByte();
            var status = (ChannelStatus)reader.ReadByte();
            var requestId = reader.ReadUInt32();
            var topic = ReadString(reader, MaxNameBytes);
            var length = reader.ReadUInt32();
            if (length > MaxEventBody || (kind != ChannelEventKind.Reply && kind != ChannelEventKind.Push)) return false;
            var body = reader.ReadBytes((int)length);
            if (body.Length != length) return false;
            message = new ChannelEvent(kind, status, requestId, topic, body);
            return true;
        }
        catch (Exception error) when (error is EndOfStreamException || error is InvalidDataException) { return false; }
    }

    public static BinaryReader Reader(byte[] bytes) => new(new MemoryStream(bytes, writable: false), Encoding.UTF8);

    public static byte[] Body(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    public static string ReadString(BinaryReader reader, int maxBytes = ushort.MaxValue)
    {
        var length = reader.ReadUInt16();
        if (length > maxBytes) throw new InvalidDataException($"String of {length} bytes is over {maxBytes}.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    public static void WriteString(BinaryWriter writer, string? value, int maxBytes = ushort.MaxValue)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > maxBytes) throw new ArgumentException($"String of {bytes.Length} bytes is over {maxBytes}.", nameof(value));
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }
}
