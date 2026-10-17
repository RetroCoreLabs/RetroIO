//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The SINTRAN error numbers the TAD driver hands back to a calling program, and the rule that
    /// turns an XMSG error into one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carved 2026-08-18 from the SINTRAN III version J TAD driver
    /// (<c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/06-COS-TAD-RES-CODE.NPL</c> and
    /// <c>20-COS-TAD-POF-CODE.NPL</c>); the values are also in every symbol table under
    /// <c>SINTRAN/NPL-SOURCE/SYMBOLS</c>.
    /// </para>
    /// <para>
    /// These never travel on the wire. They are what a program on the machine RUNNING the TAD sees
    /// when the exchange goes wrong, which is why a live experiment that reports one of these numbers
    /// is telling you about the far side's driver state rather than about the frame you sent.
    /// </para>
    /// </remarks>
    public static class TadErrorCodes
    {
        /// <summary>
        /// TER00 (0o314 = 204) - an input completed while a delayed escape action was still pending.
        /// </summary>
        /// <remarks>
        /// Raised by <c>IEDCHK</c> in <c>06-COS-TAD-RES-CODE.NPL</c>.
        /// </remarks>
        public const int InputDuringDelayedEscape = 204;

        /// <summary>
        /// TER01 (0o315 = 205) - the message was rejected: the driver sent a
        /// <see cref="TadOp.Reje"/> and failed the caller with this.
        /// </summary>
        /// <remarks>
        /// Set right after <c>CALL SNDREJ</c> in <c>06-COS-TAD-RES-CODE.NPL</c> and reached through the
        /// <c>SRJE</c> path in <c>CTRMES</c>. This is the number to look for when a remote program
        /// complains and we suspect we sent an opcode the far driver does not accept.
        /// </remarks>
        public const int MessageRejected = 205;

        /// <summary>
        /// TER02 (0o316 = 206) - the TAD is not connected (its port number is zero).
        /// </summary>
        public const int TadNotConnected = 206;

        /// <summary>
        /// XKXXX (0o41000 = 16896) - the base a negated XMSG error code is OR-ed onto to make a
        /// SINTRAN error number.
        /// </summary>
        /// <remarks>
        /// The same constant the XMSG layer already carries as "nothing to report"; here it is the
        /// error-conversion base. See <see cref="ToSintranError"/>.
        /// </remarks>
        public const int XmsgErrorBase = 16896;

        /// <summary>
        /// Converts an XMSG error code (negative) into the SINTRAN error number a program sees.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>CNVERR</c> in <c>20-COS-TAD-POF-CODE.NPL</c> is a single instruction pair: negate the
        /// (negative) XMSG code and OR the result with <c>XKXXX</c>. So XENSE, which is -34 decimal
        /// (0o42), surfaces as 0o41042.
        /// </para>
        /// <para>
        /// A non-negative input is returned unchanged - the driver only ever reaches this with an
        /// error in hand, so there is no defined conversion for a success code.
        /// </para>
        /// </remarks>
        /// <param name="xmsgError">
        /// The XMSG error code, normally negative.
        /// </param>
        /// <returns>
        /// The SINTRAN error number.
        /// </returns>
        public static int ToSintranError(int xmsgError)
        {
            if (xmsgError >= 0)
            {
                return xmsgError;
            }

            return XmsgErrorBase | (-xmsgError);
        }
    }
}
