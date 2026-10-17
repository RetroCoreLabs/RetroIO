//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// The function code that opens a file-transfer message body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A transfer does not use the QFORM request form. Once a transfer is under way, each message
    /// body starts with this word instead of an <see cref="FaMessageType"/>, which is why a reader
    /// has to know it is inside a transfer before it can make sense of the bytes.
    /// </para>
    /// <para>
    /// Both values were read off the wire between two live ND-100s. They are consecutive, which
    /// suggests the pair belongs to a wider function range we have not yet seen the rest of - so
    /// treat an unrecognised value here as "not decoded", not as "impossible".
    /// </para>
    /// </remarks>
    public enum FaTransferFunction : ushort
    {
        /// <summary>
        /// Carries one block of file content.
        /// </summary>
        /// <remarks>
        /// The block is <see cref="FaTransferCodec.BlockLength"/> bytes.
        /// </remarks>
        Data = 0x0042,

        /// <summary>
        /// Marks the end of the transfer.
        /// </summary>
        /// <remarks>
        /// Carries no file content of its own.
        /// </remarks>
        EndOfTransfer = 0x0043,
    }
}
