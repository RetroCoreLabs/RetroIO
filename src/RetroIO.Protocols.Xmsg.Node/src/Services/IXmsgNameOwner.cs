//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node.Services
{
    /// <summary>
    /// A server that answers letters for names other than its own <see cref="IXmsgServer.Name"/>.
    /// </summary>
    /// <remarks>
    /// A server written as SINTRAN tasks run (the chat server, the request-reply library) claims
    /// its names in an XROUT name table of its own, not in the node's server list. A bridge server
    /// that carries those tasks implements this, so a letter from a machine on the wire, addressed
    /// to one of those names, is handed to the bridge. Without it the node finds only names in its
    /// own server list (RetroIO BUGS.md, the <c>XmsgKernelServer.Deliver</c> entry, 2026-11-10).
    /// </remarks>
    public interface IXmsgNameOwner
    {
        /// <summary>
        /// Says whether a letter to this name belongs to this server.
        /// </summary>
        /// <param name="name">
        /// The name the letter is addressed to, exactly as it carries it.
        /// </param>
        /// <returns>
        /// True when this server takes letters for the name.
        /// </returns>
        bool AnswersName(string name);
    }
}
