using System.Threading;

using ACE.Server.ClientChannel;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// How far past a station's use radius the player can walk before the session ends
        /// </summary>
        public const float StationRangeMargin = 1.5f;

        private StationSession stationSession;

        /// <summary>
        /// Starts the player's session at the station, ending any session they already have first
        /// </summary>
        public void StartStation(WorldObject station)
        {
            var session = new StationSession(station);
            var previous = Interlocked.Exchange(ref stationSession, session);

            if (previous != null)
                PushStationClose(previous);

            ServerChannel.Push(this, StationChannelActions.Open, StationChannelActions.Body(session.Name, session.StationGuid));
        }

        /// <summary>
        /// Ends the player's station session, if they have one
        /// </summary>
        public void EndStation()
        {
            var session = Interlocked.Exchange(ref stationSession, null);

            if (session != null)
                PushStationClose(session);
        }

        /// <summary>
        /// True while the player has an open session at a station with this name
        /// </summary>
        public bool HasStation(string station)
        {
            var session = Volatile.Read(ref stationSession);

            return session != null && session.Name == station;
        }

        /// <summary>
        /// Ends the session if the station has left the world, or the player has walked past its use radius plus <see cref="StationRangeMargin"/>. Called every tick, so with no session it is one read.
        /// </summary>
        public void CheckStationRange()
        {
            var session = Volatile.Read(ref stationSession);

            if (session == null)
                return;

            var station = session.Station;

            if (station.CurrentLandblock != null && IsWithinUseRadiusOf(station, (station.UseRadius ?? 0.6f) + StationRangeMargin))
                return;

            if (Interlocked.CompareExchange(ref stationSession, null, session) == session)
                PushStationClose(session);
        }

        private void PushStationClose(StationSession session)
        {
            ServerChannel.Push(this, StationChannelActions.Close, StationChannelActions.Body(session.Name, session.StationGuid));
        }

        private sealed class StationSession
        {
            public StationSession(WorldObject station)
            {
                Station = station;
                Name = station.LegaceyStation;
                StationGuid = station.Guid.Full;
            }

            /// <summary>
            /// The world object the player is using, for the range check
            /// </summary>
            public WorldObject Station { get; }

            public string Name { get; }

            public uint StationGuid { get; }
        }
    }
}
