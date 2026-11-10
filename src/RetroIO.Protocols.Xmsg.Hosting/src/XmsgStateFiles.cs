//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//


using System;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Hosting
{
    /// <summary>
    /// The names of the small state files an XMSG node keeps between runs, and where they go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of them used to sit beside the runner's executable. A service must keep them in its
    /// data folder instead, so every path here is built from a folder the caller names.
    /// </para>
    /// <para>
    /// <b>Losing the sequence file is not harmless.</b> The ND keeps the next datagram sequence it
    /// expects from us for every remote system. If we start again at zero it drops our frames
    /// without an error, which looks exactly like a dead link. Move these files with the node.
    /// </para>
    /// </remarks>
    public static class XmsgStateFiles
    {
        /// <summary>
        /// The next outgoing datagram sequence per remote system.
        /// </summary>
        public const string Sequence = "xmsg-sequence.state";

        /// <summary>
        /// The envelope seed learned per remote system.
        /// </summary>
        public const string LinkSeed = "xmsg-link-seed.state";

        /// <summary>
        /// Where the file server's next block of connection numbers starts.
        /// </summary>
        public const string FaConnection = "fa-connection.state";

        /// <summary>
        /// What the folder sync has already carried to the machine.
        /// </summary>
        public const string SyncLedger = "xmsg-sync-ledger.state";

        /// <summary>
        /// The optional sync password list (<c>USER=password</c> lines).
        /// </summary>
        public const string SyncCredentials = "sync-credentials.txt";

        /// <summary>
        /// Builds the full path of one state file in a state folder.
        /// </summary>
        /// <param name="stateDirectory">
        /// The folder that holds the node's state.
        /// </param>
        /// <param name="fileName">
        /// One of the names above.
        /// </param>
        /// <returns>
        /// The combined path.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when either argument is null.
        /// </exception>
        public static string PathOf(string stateDirectory, string fileName)
        {
            if (stateDirectory == null)
            {
                throw new ArgumentNullException(nameof(stateDirectory));
            }

            if (fileName == null)
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            return Path.Combine(stateDirectory, fileName);
        }
    }
}
