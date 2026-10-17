//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Qform
{
    /// <summary>
    /// The concrete QFORM tag bytes this code base writes.
    /// </summary>
    /// <remarks>
    /// <para><b>How a tag byte is built</b></para>
    /// A tag is <c>0x80</c>, then the class in bits 6-4, then the length in bits 3-0:
    /// <c>tag = 0x80 | (class shifted left 4) | length</c>. Bit 7 clear means END OF STREAM, which is why
    /// every tag has it set. See <see cref="QformClass"/> for the classes and
    /// <c>QformReader</c> for the rule, which was disassembled out of ND's own reader
    /// <c>qform_read_tag_and_value</c> at <c>ram:0x7d01</c> in <c>COS-FA-SERV-E04.PROG</c>.
    /// <para><b>Why a length of zero means something else</b></para>
    /// For classes 1 to 7 a low nibble of zero ESCAPES: the length comes from the following byte.
    /// That is why <see cref="ByteString"/> is <c>0xB0</c> and always carries a length byte, while
    /// the two-byte tags carry their length in the nibble.
    /// <para><b>Class 0 is different</b></para>
    /// For class 0 the low bits are a SUBTYPE, not a length, and the length is always escaped -
    /// so <see cref="Constructed"/> is <c>0x8C</c> and is always followed by a length byte.
    /// </remarks>
    public enum QformTagByte : byte
    {
        /// <summary>
        /// A constructed value whose contents are themselves tagged, wire byte <c>0x8C</c>.
        /// </summary>
        /// <remarks>
        /// Class 0, subtype 4. Always followed by a length byte, which may itself be
        /// <see cref="LengthEscape"/>. A reader that does not descend into the contents
        /// desynchronises and reads the nested tags as top-level ones.
        /// </remarks>
        Constructed = 0x8C,

        /// <summary>
        /// A plain two-byte integer, wire byte <c>0x92</c>.
        /// </summary>
        /// <remarks>
        /// Class 1, length 2. Carries the operation, the sequence and the listing sub-function.
        /// </remarks>
        Integer = 0x92,

        /// <summary>
        /// A typed two-byte integer, wire byte <c>0xA2</c>.
        /// </summary>
        /// <remarks>
        /// Class 2, length 2. Carries the directory cursor and the entry ordinal.
        /// </remarks>
        TypedInteger = 0xA2,

        /// <summary>
        /// A typed four-byte integer, wire byte <c>0xA4</c>.
        /// </summary>
        /// <remarks>
        /// Class 2, length 4. Carries the 32-bit quantities: a file size in an open reply and a file
        /// position in a read or write request.
        /// </remarks>
        TypedInteger32 = 0xA4,

        /// <summary>
        /// A byte string, wire byte <c>0xB0</c>.
        /// </summary>
        /// <remarks>
        /// Class 3 with an escaped length, so a length byte always follows. It carries every record
        /// on the wire, and the LENGTH is the only thing that says which kind: 64 bytes is a file
        /// object entry, 42 a directory entry, 16 a user name.
        /// </remarks>
        ByteString = 0xB0,

        /// <summary>
        /// A selector, wire byte <c>0xF2</c>.
        /// </summary>
        /// <remarks>
        /// Class 7, length 2. Introduces a section of the body and terminates a list.
        /// </remarks>
        Selector = 0xF2,

        /// <summary>
        /// The long-form length marker, wire byte <c>0x80</c>.
        /// </summary>
        /// <remarks>
        /// Not a tag. When a length byte reads <c>0x80</c> the real length is in the byte after it.
        /// ND's reader accumulates repeated <c>0x80</c> bytes and takes the first non-<c>0x80</c>
        /// byte as the length; no captured frame exercises that continuation, so we only ever write
        /// the single-escape form.
        /// </remarks>
        LengthEscape = 0x80,
    }
}
