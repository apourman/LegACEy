using System.Threading;

using ACE.Server.Market;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        private VaultChannel vaultChannel;

        /// <summary>
        /// The Vault item the player last inspected, created from its row but in no container or landblock. Their appraisal of it is answered from this.
        /// </summary>
        public WorldObject VaultInspected { get; set; }

        /// <summary>
        /// The Vault deposit or withdrawal this player is channelling, or null
        /// </summary>
        public VaultChannel ActiveVaultChannel => Volatile.Read(ref vaultChannel);

        /// <summary>
        /// True while the player channels a Vault deposit or withdrawal. Checked everywhere PKLogout is, and it also blocks trading, giving, dropping and recalls.
        /// </summary>
        public bool IsVaultChannelling => ActiveVaultChannel != null;

        /// <summary>
        /// Cancels a Vault channel, if any. Called by the PK timer update (a landed player attack), death and logout.
        /// </summary>
        public void CancelVaultChannel()
        {
            if (IsVaultChannelling)
                VaultChannel.Cancel(this);
        }

        /// <summary>
        /// Makes the channel the player's, unless they already have one
        /// </summary>
        internal bool TryStartVaultChannel(VaultChannel channel)
        {
            return Interlocked.CompareExchange(ref vaultChannel, channel, null) == null;
        }

        /// <summary>
        /// Ends whatever channel the player has and returns it. Atomic, so two cancels can't both end the same channel.
        /// </summary>
        internal VaultChannel EndVaultChannel()
        {
            return Interlocked.Exchange(ref vaultChannel, null);
        }

        /// <summary>
        /// Ends the channel if it is still this one
        /// </summary>
        internal bool TryEndVaultChannel(VaultChannel channel)
        {
            return Interlocked.CompareExchange(ref vaultChannel, null, channel) == channel;
        }
    }
}
