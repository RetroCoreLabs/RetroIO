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
    /// One row of an XROUT name table, as one XROUT <c>XSGNI</c> ("get next name") reply carries it.
    /// </summary>
    /// <remarks>
    /// VERIFIED 2026-11-10 from a complete SINTRAN LIST-NAMES walk (D102 asking D100, decoded byte for
    /// byte from the COSMOS Ethernet hub):
    /// <list type="bullet">
    ///   <item><description><see cref="Magic"/> is reply parameter 1, 32 bits: the system number in the high half and the port word in the low half.</description></item>
    ///   <item><description><see cref="Name"/> is reply parameter 2, a string.</description></item>
    ///   <item><description><see cref="FreeConnections"/> is reply parameter 3, 16 bits, present only for a connection port (*XFTRA, *FA-FSA and *FA-FSA-I in the capture). A plain named port and a system name carry no parameter 3.</description></item>
    /// </list>
    /// A system name is the row whose port word is <c>0x0001</c> (D100 is <c>0x00640001</c>).
    /// </remarks>
    public readonly struct NameTableEntry
    {
        /// <summary>
        /// Initialises a row with no free-connections count (a plain named port or a system name).
        /// </summary>
        /// <param name="magic">
        /// The 32-bit magic number: system in the high 16 bits, port word in the low 16 bits.
        /// </param>
        /// <param name="name">
        /// The registered name, for example <c>*XM-FIDO</c> or <c>D100</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> is null.
        /// </exception>
        public NameTableEntry(uint magic, string name)
        {
            Magic = magic;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            HasFreeConnections = false;
            FreeConnections = 0;
        }

        /// <summary>
        /// Initialises a row for a connection port, which reports its free connections.
        /// </summary>
        /// <param name="magic">
        /// The 32-bit magic number: system in the high 16 bits, port word in the low 16 bits.
        /// </param>
        /// <param name="name">
        /// The registered name, for example <c>*FA-FSA</c>.
        /// </param>
        /// <param name="freeConnections">
        /// The number of connections the port can still accept (reply parameter 3).
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="name"/> is null.
        /// </exception>
        public NameTableEntry(uint magic, string name, ushort freeConnections)
        {
            Magic = magic;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            HasFreeConnections = true;
            FreeConnections = freeConnections;
        }

        /// <summary>
        /// Gets the 32-bit magic number (reply parameter 1).
        /// </summary>
        public uint Magic { get; }

        /// <summary>
        /// Gets the registered name (reply parameter 2).
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets a value indicating whether the row carries a free-connections count, which only a
        /// connection port does.
        /// </summary>
        public bool HasFreeConnections { get; }

        /// <summary>
        /// Gets the free-connections count (reply parameter 3); <c>0</c> when
        /// <see cref="HasFreeConnections"/> is <c>false</c>.
        /// </summary>
        public ushort FreeConnections { get; }

        /// <summary>
        /// Gets the system number, the high 16 bits of <see cref="Magic"/>.
        /// </summary>
        public ushort System
        {
            get { return (ushort)(Magic >> 16); }
        }

        /// <summary>
        /// Gets the port word, the low 16 bits of <see cref="Magic"/>.
        /// </summary>
        public ushort PortWord
        {
            get { return (ushort)(Magic & 0xFFFF); }
        }

        /// <summary>
        /// Gets the port number as SINTRAN LIST-NAMES prints it: the port word shifted right by 7.
        /// </summary>
        /// <remarks>
        /// The low 7 bits are the random part of the port word; see <see cref="XmsgPortWordAllocator"/>.
        /// </remarks>
        public int PortNumber
        {
            get { return PortWord >> 7; }
        }

        /// <summary>
        /// Gets a value indicating whether this row names a system rather than a port: its port word
        /// is <c>0x0001</c>.
        /// </summary>
        public bool IsSystemName
        {
            get { return PortWord == 0x0001; }
        }
    }
}
