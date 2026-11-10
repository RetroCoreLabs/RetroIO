//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Protocols.Xmsg.ListNames
{
    /// <summary>
    /// How a LIST-NAMES walk ended.
    /// </summary>
    public enum ListNamesWalkStatus
    {
        /// <summary>
        /// The remote XROUT sent the end-of-table reply; the list is the whole table.
        /// </summary>
        Complete = 0,

        /// <summary>
        /// A reply did not arrive in time; the list holds the rows received before that.
        /// </summary>
        TimedOut = 1,

        /// <summary>
        /// A reply could not be read, or its magic number was below the one asked for (which would
        /// make the walk go round forever); the list holds the rows received before that.
        /// </summary>
        BadReply = 2,
    }

    /// <summary>
    /// The outcome of a <see cref="ListNamesClient.Walk"/>: the rows received, in the order the
    /// remote XROUT sent them, and how the walk ended.
    /// </summary>
    public sealed class ListNamesWalkResult
    {
        /// <summary>
        /// Initialises a walk result.
        /// </summary>
        /// <param name="status">
        /// How the walk ended.
        /// </param>
        /// <param name="entries">
        /// The rows received.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="entries"/> is null.
        /// </exception>
        public ListNamesWalkResult(ListNamesWalkStatus status, IReadOnlyList<NameTableEntry> entries)
        {
            Status = status;
            Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        }

        /// <summary>
        /// Gets how the walk ended.
        /// </summary>
        public ListNamesWalkStatus Status { get; }

        /// <summary>
        /// Gets the rows received, in the order they arrived (ascending magic number).
        /// </summary>
        public IReadOnlyList<NameTableEntry> Entries { get; }
    }
}
