//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// A file that exists in the remote user's directory, as a listing reports it.
    /// </summary>
    /// <remarks>
    /// <para><b>Name and type, and no content</b></para>
    /// <para>
    /// A listing says what is there, never what is in it. That is enough for the two questions the
    /// planner asks of the remote side - does this file already exist, so is the push a create or
    /// an overwrite, and is this one of the types we bring back. Deciding whether a pulled file
    /// actually differs from the local copy needs its content, which means reading it, which is
    /// work the plan authorises rather than work the plan does.
    /// </para>
    /// </remarks>
    public sealed class RemoteFileState
    {
        /// <summary>
        /// Creates a remote file state.
        /// </summary>
        /// <param name="name">
        /// The SINTRAN file name, without the type.
        /// </param>
        /// <param name="type">
        /// The SINTRAN file type without the colon, or an empty string when the file has none.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> or <paramref name="type"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> is empty.
        /// </exception>
        public RemoteFileState(string name, string type)
        {
            if (name == null) { throw new ArgumentNullException(nameof(name)); }
            if (type == null) { throw new ArgumentNullException(nameof(type)); }

            if (name.Length == 0)
            {
                throw new ArgumentException("A remote file name cannot be empty.", nameof(name));
            }

            // Upper-cased because that is how SINTRAN reports names, and comparing them is the
            // whole job. Doing it here means every comparison downstream is a plain string match.
            Name = name.ToUpperInvariant();
            Type = type.TrimStart(':').ToUpperInvariant();
        }

        /// <summary>
        /// Gets the file name, upper-cased.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the file type without the colon, upper-cased, or an empty string.
        /// </summary>
        public string Type { get; }
    }
}
