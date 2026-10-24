using System.Collections.Generic;

namespace ACE.Common
{
    public class GameConfiguration
    {
        public string WorldName { get; set; } = "ACEmulator";

        public NetworkSettings Network { get; set; } = new NetworkSettings();

        public AccountDefaults Accounts { get; set; } = new AccountDefaults();

        public string DatFilesDirectory { get; set; } = "c:\\ACE\\Dats\\";

        /// <summary>
        /// The LegACEy world these dats hold, shown to players by the LegACEy plugin; empty uses WorldName.
        /// </summary>
        public string LegaceyWorldName { get; set; } = "";

        /// <summary>
        /// The address of the LegACEy world's update list. When set, the server tells the LegACEy plugin at character select which world
        /// it runs, so the plugin brings the game folder's dats to the server's. Empty: no world of its own, and no note is sent.
        /// </summary>
        public string LegaceyWorldUpdateUrl { get; set; } = "";

        public string ModsDirectory { get; set; }

        /// <summary>
        /// The amount of seconds to wait before turning off the server. Default value is 60 (for 1 minute).
        /// </summary>
        public uint ShutdownInterval { get; set; } = 60;

        public bool ServerPerformanceMonitorAutoStart { get; set; } = false;

        public ThreadConfiguration Threading { get; set; } = new ThreadConfiguration();

        /// <summary>
        /// The amount of minutes to keep a player object from shard database in memory. Default value is 31 minutes.
        /// </summary>
        public uint ShardPlayerBiotaCacheTime { get; set; } = 31;

        /// <summary>
        /// The amount of minutes to keep a non player object from shard database in memory. Default value is 11 minutes.
        /// </summary>
        public uint ShardNonPlayerBiotaCacheTime { get; set; } = 11;

        public bool WorldDatabasePrecaching { get; set; } = false;

        public bool LandblockPreloading { get; set; } = true;

        public List<PreloadedLandblocks> PreloadedLandblocks { get; set; } = new List<PreloadedLandblocks>()
        {
            new PreloadedLandblocks()
            {
                Id                  = "E74EFFFF",
                Description         = "Hebian-To (Global Events)",
                Permaload           = true,
                IncludeAdjacents    = false,
                Enabled             = true
            },
            new PreloadedLandblocks()
            {
                Id                  = "A9B4FFFF",
                Description         = "Holtburg",
                Permaload           = true,
                IncludeAdjacents    = true,
                Enabled             = false
            },
            new PreloadedLandblocks()
            {
                Id                  = "DA55FFFF",
                Description         = "Shoushi",
                Permaload           = true,
                IncludeAdjacents    = true,
                Enabled             = false
            },
            new PreloadedLandblocks()
            {
                Id                  = "7D64FFFF",
                Description         = "Yaraq",
                Permaload           = true,
                IncludeAdjacents    = true,
                Enabled             = false
            },
            new PreloadedLandblocks()
            {
                Id                  = "0007FFFF",
                Description         = "Town Network",
                Permaload           = true,
                IncludeAdjacents    = false,
                Enabled             = false
            },
            new PreloadedLandblocks()
            {
                Id                  = "00000000",
                Description         = "Apartment Landblocks",
                Permaload           = true,
                IncludeAdjacents    = false,
                Enabled             = false
            }
        };
    }
}
