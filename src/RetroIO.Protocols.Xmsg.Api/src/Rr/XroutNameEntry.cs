//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// One registered name and how many free connections it still has.
    /// </summary>
    /// <remarks>
    /// A readonly struct because a listing builds one per name and they never outlive the call.
    /// </remarks>
    public readonly struct XroutNameEntry
    {
        /// <summary>
        /// Creates an entry.
        /// </summary>
        /// <param name="name">
        /// The registered name.
        /// </param>
        /// <param name="freeConnections">
        /// How many free connections remain, or a negative value when the name has no connection
        /// port at all.
        /// </param>
        public XroutNameEntry(string name, int freeConnections)
            : this(name, freeConnections, XmsgMagicNumber.None)
        {
        }

        /// <summary>
        /// Creates an entry that also carries the magic number the name was claimed with.
        /// </summary>
        /// <param name="name">
        /// The registered name.
        /// </param>
        /// <param name="freeConnections">
        /// How many free connections remain, or a negative value when the name has no connection
        /// port at all.
        /// </param>
        /// <param name="magic">
        /// The magic number the name resolves to.
        /// </param>
        public XroutNameEntry(string name, int freeConnections, XmsgMagicNumber magic)
        {
            Name = name;
            FreeConnections = freeConnections;
            Magic = magic;
        }

        /// <summary>
        /// Gets the magic number the name resolves to; <see cref="XmsgMagicNumber.None"/> when the
        /// listing that made this entry did not carry it.
        /// </summary>
        public XmsgMagicNumber Magic { get; }

        /// <summary>
        /// Gets the registered name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets how many free connections remain.
        /// </summary>
        /// <remarks>
        /// Negative means the name was registered WITHOUT a connection port - it can be written to
        /// but has no seats, so "full" does not apply to it. That is a different thing from zero,
        /// which means a room that has seats and none left.
        /// </remarks>
        public int FreeConnections { get; }

        /// <summary>
        /// Gets whether this name has seats and at least one is free.
        /// </summary>
        public bool HasRoom
        {
            get { return FreeConnections > 0; }
        }
    }
}
