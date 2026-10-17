//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Api;

namespace RetroIO.Protocols.Xmsg.Chat
{
    /// <summary>
    /// Lists the chat rooms on this node and how much space each has left.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a room list is not just "the names"</b></para>
    /// <para>
    /// The name table holds every server on the node - a file server, a terminal service, whatever
    /// else has registered. A chat client wanting to offer somebody a choice of rooms needs the
    /// chat ones and not the rest, so rooms are recognised by a PREFIX their servers register
    /// under. That is a convention of ours, chosen because the alternative - asking each name what
    /// it is - would mean sending a message to every server on the node just to draw a menu.
    /// </para>
    /// <para><b>Free seats come from XROUT, not from the room</b></para>
    /// <para>
    /// The count is the free-connection count XROUT keeps, which is the same number that decides
    /// whether a join is forwarded at all. Asking the room itself would be both slower and less
    /// true: a room can be told its own membership, but XROUT is what actually turns people away.
    /// </para>
    /// <para><b>What this is NOT</b></para>
    /// <para>
    /// It does not work across the wire. No capture in the corpus shows a name-listing exchange
    /// between machines, so a remote SINTRAN user cannot ask us for this and nothing here pretends
    /// otherwise.
    /// </para>
    /// </remarks>
    public static class ChatRooms
    {
        /// <summary>
        /// The prefix a chat room's name carries, so rooms can be told from other services.
        /// </summary>
        public const string NamePrefix = "CHAT-";

        /// <summary>
        /// Builds the full registered name for a room.
        /// </summary>
        /// <param name="room">
        /// The room's short name, for example <c>LOBBY</c>.
        /// </param>
        /// <returns>
        /// The name to register with XROUT, for example <c>CHAT-LOBBY</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="room"/> is null.
        /// </exception>
        public static string NameFor(string room)
        {
            if (room == null) { throw new ArgumentNullException(nameof(room)); }

            return NamePrefix + room.ToUpperInvariant();
        }

        /// <summary>
        /// Lists the chat rooms registered on a node.
        /// </summary>
        /// <param name="directory">
        /// The name table to read.
        /// </param>
        /// <returns>
        /// One entry per room, with the SHORT name and the free-seat count.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="directory"/> is null.
        /// </exception>
        public static IReadOnlyList<XroutNameEntry> List(XroutDirectory directory)
        {
            if (directory == null) { throw new ArgumentNullException(nameof(directory)); }

            XroutNameEntry[] all = directory.CopyNames();
            List<XroutNameEntry> rooms = new List<XroutNameEntry>();

            for (int i = 0; i < all.Length; i++)
            {
                string name = all[i].Name;
                if (name.Length <= NamePrefix.Length)
                {
                    continue;
                }

                if (!name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The short name is what a person types and what the list should show; the prefix
                // is plumbing.
                rooms.Add(new XroutNameEntry(
                    name.Substring(NamePrefix.Length), all[i].FreeConnections));
            }

            return rooms;
        }
    }
}
