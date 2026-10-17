//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node
{
    /// <summary>
    /// Remembers the envelope SEED learned for each remote node, so a link to a machine we have
    /// met before can be opened without waiting to be spoken to.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a seed can be remembered at all</b></para>
    /// <para>
    /// The seed is a per-link CONSTANT. That is not an assumption: it was verified across the whole
    /// capture corpus - <c>0x14</c> for 100 to and from 102 through every session, reconnect and
    /// REBOOT in it - and <c>XmsgServerHost.EnsureLink</c> already relies on it, learning the seed
    /// once and refusing to overwrite it afterwards. A value that survives the machine rebooting
    /// survives being written to a file.
    /// </para>
    /// <para><b>What it unblocks</b></para>
    /// <para>
    /// Our node could not originate to a peer that had not spoken to us since we started, because
    /// the seed is learned from an inbound datagram and never derived. That is fine for a server
    /// and useless for a daemon: the folder-watch sync sat with a file queued, reporting "the XMSG
    /// layer cannot address node 100 yet", until somebody typed a command on the far machine.
    /// </para>
    /// <para><b>What it does NOT claim</b></para>
    /// <para>
    /// It does not claim the seed can be computed, guessed or defaulted. Nothing here invents a
    /// seed: a node we have never heard from still cannot be addressed, and that is honest. This
    /// only stops us forgetting one we were told.
    /// </para>
    /// </remarks>
    public interface ILinkSeedStore
    {
        /// <summary>
        /// Loads the seed learned for a remote node.
        /// </summary>
        /// <param name="remoteNode">
        /// The remote system (node) number, for example 100.
        /// </param>
        /// <param name="seed">
        /// The stored seed when this returns <see langword="true"/>; otherwise zero.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when a seed is known for that node.
        /// </returns>
        bool TryLoadSeed(ushort remoteNode, out byte seed);

        /// <summary>
        /// Records the seed learned for a remote node.
        /// </summary>
        /// <param name="remoteNode">
        /// The remote system (node) number.
        /// </param>
        /// <param name="seed">
        /// The seed just learned from an inbound datagram.
        /// </param>
        void SaveSeed(ushort remoteNode, byte seed);
    }
}
