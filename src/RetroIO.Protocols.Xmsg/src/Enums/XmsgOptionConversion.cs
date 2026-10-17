//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Converts between the MON 200B option word (<see cref="XmsgOption"/>) and the XMSG
    /// sub-header role byte (<see cref="XmsgSendOptions"/>).
    /// </summary>
    /// <remarks>
    /// The two enums describe ONE bit set in two physical encodings, so they must never drift:
    ///  - <see cref="XmsgOption"/> is the 16-bit T-register word passed to MON 200B; the send
    ///    options occupy the high byte (bits 8-15). It is generated from the official
    ///    XMSG-PL-VALUES-M.INCL constants and is the source of truth for names and bit numbers.
    ///  - <see cref="XmsgSendOptions"/> is that same high byte as it appears on the wire at
    ///    sub-header offset 17 (the role byte).
    /// The relationship is a plain 8-bit shift, asserted by the enum-parity unit test. Bits below
    /// bit 8 of the option word (notably XFSYS, bit 7) are NOT part of the role byte and are
    /// dropped by <see cref="ToRoleByte"/>.
    /// </remarks>
    public static class XmsgOptionConversion
    {
        /// <summary>
        /// Extracts the wire role byte from a MON 200B option word.
        /// </summary>
        /// <param name="options">
        /// The full 16-bit option word; only bits 8-15 contribute.
        /// </param>
        /// <returns>
        /// The role byte written at XMSG sub-header offset 17.
        /// </returns>
        public static XmsgSendOptions ToRoleByte(XmsgOption options)
        {
            return (XmsgSendOptions)(byte)(((ushort)options) >> 8);
        }

        /// <summary>
        /// Widens a wire role byte back into a MON 200B option word.
        /// </summary>
        /// <param name="role">
        /// The role byte read from XMSG sub-header offset 17.
        /// </param>
        /// <returns>
        /// The option word with the role bits in their high-byte positions and no low-byte bits set.
        /// </returns>
        /// <remarks>
        /// The round trip is lossy in one direction only: an option word carrying XFSYS (bit 7)
        /// loses that bit on the way out, because XFSYS is a call-mode flag and never reaches the wire.
        /// </remarks>
        public static XmsgOption FromRoleByte(XmsgSendOptions role)
        {
            return (XmsgOption)(ushort)(((ushort)(byte)role) << 8);
        }
    }
}
