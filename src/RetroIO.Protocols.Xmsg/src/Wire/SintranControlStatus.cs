//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Reads the CONTROL-datagram bits of <c>XDTYP</c> (header word 1) and the error code a
    /// BAD-STATUS control datagram carries in <c>XDSCR</c> (header word 5).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// <para>
    /// A peer that refuses a letter answers with a CONTROL datagram whose BAD STATUS bit is set,
    /// and the reason travels in word 5 as a NEGATIVE <see cref="XroutError"/>. We were not
    /// reading it, so a driver waiting for a reply reported "no answer to the connect letter"
    /// while the machine was busy answering with a name for the fault. Measured 2026-08-18: three
    /// "no answer" lines in one push log, against 39 BAD STATUS datagrams across the session.
    /// </para>
    /// <para><b>The bits, from the kernel's own table</b></para>
    /// <para>
    /// <c>XMSG-POFTABS-L03.SYMB</c> defines word 1 as <c>XDTYP % DATAGRAM TYPE INFO</c> with
    /// <c>XD5CO=0</c> marking a control datagram. On a control datagram the remaining bits are
    /// REUSED: <c>XD5UT=1</c> refers to an outgoing message, <c>XD5BA=2</c> BAD STATUS (REJ or
    /// NAK), <c>XD5IN=4</c> network-connection initialise.
    /// </para>
    /// <para>
    /// Word 5 is <c>XDSCR % SCRATCH (SIZE, DISPL, STATUS)</c> - scratch the layers reuse, which is
    /// why it is a size on an initialise and a status here. Only read it as an error when the BAD
    /// STATUS bit says it is one.
    /// </para>
    /// </remarks>
    public static class SintranControlStatus
    {
        /// <summary>
        /// Bit <c>XD5CO</c>: the datagram is a CONTROL datagram.
        /// </summary>
        public const ushort ControlBit = 1 << 0;

        /// <summary>
        /// Bit <c>XD5UT</c>: the control datagram refers to an OUTGOING message.
        /// </summary>
        public const ushort RefersToOutgoingBit = 1 << 1;

        /// <summary>
        /// Bit <c>XD5BA</c>: BAD STATUS - the control datagram is a REJ or NAK.
        /// </summary>
        public const ushort BadStatusBit = 1 << 2;

        /// <summary>
        /// Bit <c>XD5IN</c>: network-connection initialise.
        /// </summary>
        public const ushort InitialiseBit = 1 << 4;

        /// <summary>
        /// Returns whether header word 1 marks a CONTROL datagram.
        /// </summary>
        /// <param name="datagramType">
        /// Header word 1, <c>XDTYP</c>.
        /// </param>
        /// <returns>
        /// True when the control bit is set.
        /// </returns>
        public static bool IsControl(ushort datagramType)
        {
            return (datagramType & ControlBit) != 0;
        }

        /// <summary>
        /// Returns whether header word 1 marks a CONTROL datagram carrying BAD STATUS.
        /// </summary>
        /// <param name="datagramType">
        /// Header word 1, <c>XDTYP</c>.
        /// </param>
        /// <returns>
        /// True when both the control bit and the bad-status bit are set.
        /// </returns>
        public static bool IsBadStatus(ushort datagramType)
        {
            return (datagramType & (ControlBit | BadStatusBit)) == (ControlBit | BadStatusBit);
        }

        /// <summary>
        /// Reads the refusal reason from a BAD STATUS control datagram.
        /// </summary>
        /// <param name="datagramType">
        /// Header word 1, <c>XDTYP</c>.
        /// </param>
        /// <param name="scratch">
        /// Header word 5, <c>XDSCR</c>, which carries the status on this datagram kind.
        /// </param>
        /// <param name="error">
        /// The reason when this is a BAD STATUS control datagram.
        /// </param>
        /// <returns>
        /// True when a reason was read.
        /// </returns>
        /// <remarks>
        /// The code is carried NEGATED - <c>0xFFDE</c> is -34 - and the positive value is an
        /// <see cref="XroutError"/>. Two real answers from D100, both decoded by this rule:
        ///  - <c>0xFFDE</c> = -34 = <c>XRMFL</c>, "Remote system message table space full".
        ///  - <c>0xFFFD</c> = -3 = <c>XRDDF</c>, "Another port already has this name" - which the
        ///    machine ALSO printed as plain text at a terminal, so the decoding is corroborated
        ///    against something read independently.
        /// </remarks>
        public static bool TryGetRefusal(ushort datagramType, ushort scratch, out XroutError error)
        {
            error = default(XroutError);
            if (!IsBadStatus(datagramType))
            {
                return false;
            }

            int signed = scratch >= 0x8000 ? scratch - 0x10000 : scratch;
            if (signed >= 0)
            {
                return false;
            }

            error = (XroutError)(-signed);
            return true;
        }
    }
}
