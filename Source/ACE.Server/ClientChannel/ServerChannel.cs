using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using log4net;

using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClientChannel
{
    /// <summary>
    /// The LegACEy in-band server channel: client UI features send named actions over the game connection and get one reply each,
    /// and the server can push named topics to clients that use the channel. The game session is the authentication.
    /// Handlers run on the world thread, as game actions do; slow work leaves it and replies later from any thread.
    /// Everything sent goes back through the world thread, so game event sequence numbers stay in send order.
    /// </summary>
    public static class ServerChannel
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const int RateLimitRequests = 30;

        public static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(10);

        private static readonly ConcurrentDictionary<string, Action<ChannelContext>> handlers = new ConcurrentDictionary<string, Action<ChannelContext>>(StringComparer.Ordinal);

        /// <summary>
        /// Sessions that have sent at least one channel request. Only they are sent pushes, so retail clients and other tools never see LegACEy events.
        /// </summary>
        private static readonly ConditionalWeakTable<Session, SessionState> sessions = new ConditionalWeakTable<Session, SessionState>();

        /// <summary>
        /// Registers the handler for an action, replacing any earlier one
        /// </summary>
        public static void Register(string action, Action<ChannelContext> handler)
        {
            if (string.IsNullOrEmpty(action) || Encoding.UTF8.GetByteCount(action) > ChannelWire.MaxNameBytes)
                throw new ArgumentException("Channel action names are 1 to 64 UTF-8 bytes.", nameof(action));

            handlers[action] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// The names of every registered action, in ordinal order
        /// </summary>
        public static IReadOnlyList<string> ActionNames() => handlers.Keys.OrderBy(action => action, StringComparer.Ordinal).ToArray();

        /// <summary>
        /// A channel game action from the client, on the world thread
        /// </summary>
        public static void Receive(Session session, BinaryReader payload)
        {
            var player = session.Player;

            if (player == null)
                return;

            if (!ChannelWire.TryReadRequest(payload, out var request, out var error))
            {
                log.Warn($"[CHANNEL] Bad request from {player.Name}: {error}");
                return;
            }

            var state = sessions.GetValue(session, _ => new SessionState());

            if (!state.TryTake(DateTime.UtcNow))
            {
                Send(session, ChannelEventKind.Reply, ChannelStatus.RateLimited, request.RequestId, request.Action, Text("Too many requests; try again shortly."));
                return;
            }

            var context = new ChannelContext(session, player, request);

            if (!handlers.TryGetValue(request.Action, out var handler))
            {
                context.Fail(ChannelStatus.UnknownAction, $"The server has no '{request.Action}' action.");
                return;
            }

            try
            {
                handler(context);
            }
            catch (Exception ex)
            {
                log.Error($"[CHANNEL] {request.Action} for {player.Name} threw: {ex}");
                context.Fail(ChannelStatus.Error, "The server could not complete the request.");
            }
        }

        /// <summary>
        /// True if the player's client has used the channel this session
        /// </summary>
        public static bool IsConnected(Player player)
        {
            var session = player?.Session;
            return session != null && sessions.TryGetValue(session, out _);
        }

        /// <summary>
        /// Pushes a topic to the player if their client uses the channel. Safe from any thread.
        /// </summary>
        public static void Push(Player player, string topic, byte[] body)
        {
            var session = player?.Session;

            if (session == null || !sessions.TryGetValue(session, out _))
                return;

            Send(session, ChannelEventKind.Push, ChannelStatus.Ok, 0, topic, body);
        }

        internal static void Send(Session session, ChannelEventKind kind, ChannelStatus status, uint requestId, string topic, byte[] body)
        {
            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                // gone, or logged into another character since the request
                if (session.Player == null)
                    return;

                try
                {
                    session.Network.EnqueueSend(new GameEventLegaceyChannel(session, kind, status, requestId, topic, body));
                }
                catch (Exception ex)
                {
                    log.Error($"[CHANNEL] Could not send {kind} '{topic}' to {session.Player?.Name}: {ex}");
                }
            }));
        }

        internal static byte[] Text(string message) => ChannelWire.Body(w => ChannelWire.WriteString(w, message));

        private sealed class SessionState
        {
            private readonly object sync = new object();
            private DateTime windowStart;
            private int count;

            public bool TryTake(DateTime now)
            {
                lock (sync)
                {
                    if (now - windowStart >= RateLimitWindow)
                    {
                        windowStart = now;
                        count = 0;
                    }

                    return ++count <= RateLimitRequests;
                }
            }
        }
    }

    /// <summary>
    /// One request being handled. Exactly one reply is sent; later calls are ignored. Reply and Fail are safe from any thread.
    /// </summary>
    public sealed class ChannelContext
    {
        private int replied;

        public Session Session { get; }

        public Player Player { get; }

        public ChannelRequest Request { get; }

        public ChannelContext(Session session, Player player, ChannelRequest request)
        {
            Session = session;
            Player = player;
            Request = request;
        }

        public BinaryReader Body() => new BinaryReader(new MemoryStream(Request.Body, writable: false), Encoding.UTF8);

        public void Reply(byte[] body) => Send(ChannelStatus.Ok, body);

        public void Fail(ChannelStatus status, string message) => Send(status, ServerChannel.Text(message));

        private void Send(ChannelStatus status, byte[] body)
        {
            if (Interlocked.Exchange(ref replied, 1) != 0)
                return;

            ServerChannel.Send(Session, ChannelEventKind.Reply, status, Request.RequestId, Request.Action, body);
        }
    }
}
