//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.ListNames
{
    /// <summary>
    /// The link a <see cref="ListNamesClient"/> walk runs over: it hands out the asker's Flags 1
    /// numbers, sends one request datagram and waits for the matching reply.
    /// </summary>
    /// <remarks>
    /// The secure ACKs in both directions belong to the link, not to the walk: every request and
    /// every reply of the captured walk is ACKed as for any other datagram.
    /// </remarks>
    public interface IListNamesExchange
    {
        /// <summary>
        /// Returns the asker's next outgoing Flags 1 number and advances it.
        /// </summary>
        /// <returns>
        /// The Flags 1 for the next request.
        /// </returns>
        ushort NextFlags1();

        /// <summary>
        /// Sends one request datagram and waits for the reply to it.
        /// </summary>
        /// <param name="request">
        /// The whole request datagram from byte 0.
        /// </param>
        /// <param name="timeout">
        /// How long to wait for the reply.
        /// </param>
        /// <param name="reply">
        /// When this method returns <c>true</c>, the reply datagram; otherwise <c>null</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when a reply arrived in time; <c>false</c> on timeout.
        /// </returns>
        bool TryExchange(byte[] request, TimeSpan timeout, out XmsgFrame? reply);
    }
}
