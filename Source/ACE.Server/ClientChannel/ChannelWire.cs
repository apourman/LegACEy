using System;
using System.IO;
using System.Text;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The LegACEy in-band channel's wire format, version 1. Little-endian, as the rest of the game protocol.
    /// A request is the payload of game action <see cref="Network.GameAction.GameActionType.LegaceyChannel"/>:
    /// u16 version, u32 request id, string action, u32 body length, body.
    /// A reply or push is the payload of game event <see cref="Network.GameEvent.GameEventType.LegaceyChannel"/>:
    /// u16 version, u8 kind, u8 status, u32 request id (0 for a push), string topic, u32 body length, body.
    /// Strings are a u16 byte count and UTF-8. The Decal plugin's ChannelWire must match.
    /// </summary>
    public static class ChannelWire
    {
        public const ushort Version = 1;

        public const int MaxNameBytes = 64;

        public const int MaxRequestBody = 4096;

        public const int MaxReplyBody = 256 * 1024;

        public static bool TryReadRequest(BinaryReader reader, out ChannelRequest request, out string error)
        {
            request = null;

            try
            {
                var version = reader.ReadUInt16();

                if (version != Version)
                {
                    error = $"unsupported version {version}";
                    return false;
                }

                var requestId = reader.ReadUInt32();
                var action = ReadString(reader, MaxNameBytes);
                var length = reader.ReadUInt32();

                if (length > MaxRequestBody)
                {
                    error = $"body of {length} bytes is over the {MaxRequestBody} byte limit";
                    return false;
                }

                var body = reader.ReadBytes((int)length);

                if (body.Length != length)
                {
                    error = "body is truncated";
                    return false;
                }

                if (requestId == 0 || action.Length == 0)
                {
                    error = "request id and action are required";
                    return false;
                }

                request = new ChannelRequest(requestId, action, body);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException || ex is ArgumentException)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void WriteEvent(BinaryWriter writer, ChannelEventKind kind, ChannelStatus status, uint requestId, string topic, byte[] body)
        {
            body ??= Array.Empty<byte>();

            if (body.Length > MaxReplyBody)
                throw new ArgumentException($"Channel body of {body.Length} bytes is over the {MaxReplyBody} byte limit.", nameof(body));

            writer.Write(Version);
            writer.Write((byte)kind);
            writer.Write((byte)status);
            writer.Write(requestId);
            WriteString(writer, topic, MaxNameBytes);
            writer.Write((uint)body.Length);
            writer.Write(body);
        }

        public static string ReadString(BinaryReader reader, int maxBytes = ushort.MaxValue)
        {
            var length = reader.ReadUInt16();

            if (length > maxBytes)
                throw new InvalidDataException($"string of {length} bytes is over the {maxBytes} byte limit");

            var bytes = reader.ReadBytes(length);

            if (bytes.Length != length)
                throw new EndOfStreamException();

            return Encoding.UTF8.GetString(bytes);
        }

        public static void WriteString(BinaryWriter writer, string value, int maxBytes = ushort.MaxValue)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);

            if (bytes.Length > maxBytes)
                throw new ArgumentException($"String of {bytes.Length} bytes is over the {maxBytes} byte limit.", nameof(value));

            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        /// <summary>
        /// A body built with a BinaryWriter
        /// </summary>
        public static byte[] Body(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                write(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }

    public enum ChannelEventKind : byte
    {
        Reply = 1,
        Push = 2,
    }

    public enum ChannelStatus : byte
    {
        Ok = 0,
        Error = 1,
        UnknownAction = 2,
        RateLimited = 3,
        BadRequest = 4,
        Unavailable = 5,
    }

    public sealed class ChannelRequest
    {
        public uint RequestId { get; }

        public string Action { get; }

        public byte[] Body { get; }

        public ChannelRequest(uint requestId, string action, byte[] body)
        {
            RequestId = requestId;
            Action = action;
            Body = body;
        }
    }
}
