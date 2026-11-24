//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// The screen-size code in byte 24 of an LU type 2 BIND image, bits 1 to 7 (bit 0 is
    /// reserved).
    /// </summary>
    /// <remarks>
    /// GA23-0061-2 (3274 Control Unit Description and Programmer's Guide) Figure 1-11, "LU Type 2
    /// Screen Size Bind Format", p. 1-20 and 1-21. X'03' is not in that 1985 manual; it is in the
    /// z/OS Communications Server SNA Programming page "Presentation space size" ("The default
    /// presentation space is 24 by 80 bytes", the alternate from the Query Reply implicit
    /// partition size), read 24 November 2026.
    /// </remarks>
    public static class BindScreenSize
    {
        /// <summary>
        /// b'000 0000', base default: the 3277 default size of that model display, 12 x 40 for a
        /// model 1 and 24 x 80 for every other model; bytes 20 to 23 are ignored and Erase/Write
        /// Alternate runs as Erase/Write (GA23-0061-2 p. 1-20, 1-21).
        /// </summary>
        public const byte BaseDefault = 0x00;

        /// <summary>
        /// b'000 0001', base model 1, 12 x 40; a model 2, 3, 4 or 5 rejects it with sense X'0821'
        /// (GA23-0061-2 p. 1-21).
        /// </summary>
        public const byte BaseModel1 = 0x01;

        /// <summary>
        /// b'000 0010', base model 2, 24 x 80; a model 1 rejects it with sense X'0821'
        /// (GA23-0061-2 p. 1-21).
        /// </summary>
        public const byte BaseModel2 = 0x02;

        /// <summary>
        /// X'03': the default is 24 x 80 and the alternate is the terminal's own, the size its
        /// Query Reply reports (z/OS "Presentation space size"; not in GA23-0061-2).
        /// </summary>
        public const byte DefaultAndQueryAlternate = 0x03;

        /// <summary>
        /// b'111 1110', extended default: bytes 20 and 21 give the rows and columns for the whole
        /// session, bytes 22 and 23 are ignored, Erase/Write Alternate runs as Erase/Write
        /// (GA23-0061-2 p. 1-21).
        /// </summary>
        public const byte ExtendedDefault = 0x7E;

        /// <summary>
        /// b'111 1111', extended alternate: bytes 20 and 21 give the default size and bytes 22
        /// and 23 the alternate size (GA23-0061-2 p. 1-21).
        /// </summary>
        public const byte ExtendedAlternate = 0x7F;
    }

    /// <summary>
    /// An SNA BIND image as a TN3270E server sends it in a BIND-IMAGE message (RFC 2355 section
    /// 10.3: "the bind image exactly as it was received in the Bind RU").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The byte numbers are the ones in GA23-0061-2 Figure 5-4, "Bind Command Session
    /// Parameters" (p. 5-11 to 5-13), counted from 0, where byte 0 is X'31', the request code
    /// that "identifies this RU as a Bind command":
    /// </para>
    /// <para>
    ///  - byte 0: X'31'.
    ///  - byte 10: the largest RU the secondary LU (this terminal) may send; byte 11 the largest
    ///    the primary LU sends; each a mantissa (high nibble) times 2 to the power of the low
    ///    nibble ("hex 85 ... 256 bytes", p. 5-12).
    ///  - byte 14: the LU type for types 2 and 3 (X'02' a 3270 display, X'03' a 3270 printer).
    ///  - bytes 20 to 24: for LU type 2 the screen sizes of Figure 1-11 (p. 1-20): default rows,
    ///    default columns, alternate rows, alternate columns, then the size code.
    /// </para>
    /// <para>
    /// x3270 (Common\3270ds.h, BIND_OFF_RD 20, BIND_OFF_CD 21, BIND_OFF_RA 22, BIND_OFF_CA 23,
    /// BIND_OFF_SSIZE 24, BIND_OFF_MAXRU_SEC 10, BIND_OFF_MAXRU_PRI 11) uses the same offsets.
    /// The IBM 3708 Network Conversion Unit manual ("Processing BINDs") also names bytes 20 to 24.
    /// The z/OS "Presentation space size" page numbers the same fields 19 to 23 because it counts
    /// inside the presentation services usage field, not from the request code.
    /// </para>
    /// </remarks>
    public readonly struct BindImage
    {
        /// <summary>
        /// X'31', the BIND request code (GA23-0061-2 Figure 5-4, byte 0).
        /// </summary>
        public const byte RequestCode = 0x31;

        /// <summary>
        /// The fewest bytes that reach the screen-size code in byte 24.
        /// </summary>
        public const int LengthWithScreenSize = 25;

        private BindImage(byte luType, byte screenSizeCode, int defaultRows, int defaultColumns, int alternateRows, int alternateColumns, int maxSecondaryRu, int maxPrimaryRu)
        {
            LuType = luType;
            ScreenSizeCode = screenSizeCode;
            DefaultRows = defaultRows;
            DefaultColumns = defaultColumns;
            AlternateRows = alternateRows;
            AlternateColumns = alternateColumns;
            MaxSecondaryRu = maxSecondaryRu;
            MaxPrimaryRu = maxPrimaryRu;
        }

        /// <summary>
        /// Gets the LU type in byte 14: X'02' for a 3270 display (GA23-0061-2 p. 5-13).
        /// </summary>
        public byte LuType { get; }

        /// <summary>
        /// Gets byte 24 with the reserved bit 0 taken off, one of <see cref="BindScreenSize"/>
        /// or a value that table does not have.
        /// </summary>
        public byte ScreenSizeCode { get; }

        /// <summary>
        /// Gets byte 20, the default number of rows, as sent.
        /// </summary>
        public int DefaultRows { get; }

        /// <summary>
        /// Gets byte 21, the default number of columns, as sent.
        /// </summary>
        public int DefaultColumns { get; }

        /// <summary>
        /// Gets byte 22, the alternate number of rows, as sent.
        /// </summary>
        public int AlternateRows { get; }

        /// <summary>
        /// Gets byte 23, the alternate number of columns, as sent.
        /// </summary>
        public int AlternateColumns { get; }

        /// <summary>
        /// Gets the largest RU this terminal may send (byte 10), in bytes; 0 when the byte is 0.
        /// </summary>
        public int MaxSecondaryRu { get; }

        /// <summary>
        /// Gets the largest RU the host sends (byte 11), in bytes; 0 when the byte is 0.
        /// </summary>
        public int MaxPrimaryRu { get; }

        /// <summary>
        /// Reads a BIND image.
        /// </summary>
        /// <param name="image">
        /// The data part of a TN3270E BIND-IMAGE message.
        /// </param>
        /// <param name="bind">
        /// The image read, when the method returns true.
        /// </param>
        /// <param name="error">
        /// Why it could not be read, when the method returns false.
        /// </param>
        /// <returns>
        /// True when the image starts with X'31' and reaches byte 24.
        /// </returns>
        public static bool TryRead(ReadOnlySpan<byte> image, out BindImage bind, out string? error)
        {
            bind = default;
            if (image.Length == 0 || image[0] != RequestCode)
            {
                error = "The BIND image does not start with X'31', the Bind request code (GA23-0061-2 Figure 5-4, byte 0).";
                return false;
            }

            if (image.Length < LengthWithScreenSize)
            {
                error = "The BIND image is " + image.Length + " bytes, too short to hold the screen-size code in byte 24 (GA23-0061-2 Figure 1-11).";
                return false;
            }

            // Bit 0 of byte 24 is reserved (Figure 1-11); bits 1 to 7 are the code.
            byte code = (byte)(image[24] & 0x7F);
            bind = new BindImage(image[14], code, image[20], image[21], image[22], image[23], RuSize(image[10]), RuSize(image[11]));
            error = null;
            return true;
        }

        // A mantissa in the high nibble times 2 to the power of the low nibble (GA23-0061-2
        // p. 5-12: "hex 85, the RU size represented is 256 bytes").
        private static int RuSize(byte value) => value == 0 ? 0 : ((value >> 4) & 0x0F) << (value & 0x0F);
    }
}
