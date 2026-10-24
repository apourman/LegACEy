namespace ACE.Server.Network
{
    /// <summary>What happens to a client after the DDD interrogation compares its dats with the server's.</summary>
    public enum DatVerdict
    {
        LetIn,
        /// <summary>DAT patching is on: ACE's DDD sends the missing files.</summary>
        Patch,
        /// <summary>Newer dats than the server's: kicked at connect, as ACE always has.</summary>
        KickNewer,
        /// <summary>Older dats and no DAT patching: reaches character select, where the LegACEy plugin updates the game folder, and is
        /// refused at Enter.</summary>
        RefuseAtEnter,
        /// <summary>As <see cref="RefuseAtEnter"/> with refuse_older_dats off: enters with a chat warning.</summary>
        WarnAtEnter,
    }

    public static class DatGate
    {
        /// <summary>The server setting, on by default: refuse clients with older dats at Enter rather than letting them in with a warning.</summary>
        public const string RefuseOlderKey = "refuse_older_dats";

        public const string OlderMessage = "your game files are older than this server's world. Launch the game with the LegACEy plugin to update them, then log in again";

        public static DatVerdict Decide(bool clientIsMissingIterations, bool clientHasExtraIterations, bool datPatching, bool refuseOlder)
        {
            if (clientHasExtraIterations)
                return DatVerdict.KickNewer;
            if (!clientIsMissingIterations)
                return DatVerdict.LetIn;
            if (datPatching)
                return DatVerdict.Patch;
            return refuseOlder ? DatVerdict.RefuseAtEnter : DatVerdict.WarnAtEnter;
        }
    }
}
