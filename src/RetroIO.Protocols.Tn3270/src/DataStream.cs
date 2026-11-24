//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// Outbound command codes, the SNA and TN3270 set (GA23-0059-4 p. 3-2, Table 3-1).
    /// </summary>
    /// <remarks>
    /// A locally attached 3274 uses other (channel) codes; those are not listed here.
    /// </remarks>
    public static class Command3270
    {
        /// <summary>
        /// Write, X'F1'.
        /// </summary>
        public const byte Write = 0xF1;

        /// <summary>
        /// Erase/Write, X'F5'.
        /// </summary>
        public const byte EraseWrite = 0xF5;

        /// <summary>
        /// Erase/Write Alternate, X'7E'.
        /// </summary>
        public const byte EraseWriteAlternate = 0x7E;

        /// <summary>
        /// Read Buffer, X'F2'.
        /// </summary>
        public const byte ReadBuffer = 0xF2;

        /// <summary>
        /// Read Modified, X'F6'.
        /// </summary>
        public const byte ReadModified = 0xF6;

        /// <summary>
        /// Read Modified All, X'6E'.
        /// </summary>
        public const byte ReadModifiedAll = 0x6E;

        /// <summary>
        /// Erase All Unprotected, X'6F'.
        /// </summary>
        public const byte EraseAllUnprotected = 0x6F;

        /// <summary>
        /// Write Structured Field, X'F3'.
        /// </summary>
        public const byte WriteStructuredField = 0xF3;
    }

    /// <summary>
    /// Write Control Character bits (GA23-0059-4 p. 3-3, Table 3-2).
    /// </summary>
    /// <remarks>
    /// IBM numbers bits from the left, so bit 0 is X'80' and bit 7 is X'01'.
    /// </remarks>
    public static class Wcc
    {
        /// <summary>
        /// Bit 1, X'40': reset partition characteristics to their defaults, which includes the
        /// inbound reply mode (p. 3-3, 3-6).
        /// </summary>
        public const byte Reset = 0x40;

        /// <summary>
        /// Bit 4, X'08': start printer.
        /// </summary>
        public const byte StartPrinter = 0x08;

        /// <summary>
        /// Bit 5, X'04': sound the alarm at the end of the operation.
        /// </summary>
        public const byte SoundAlarm = 0x04;

        /// <summary>
        /// Bit 6, X'02': keyboard restore - unlock the keyboard and reset the AID.
        /// </summary>
        public const byte KeyboardRestore = 0x02;

        /// <summary>
        /// Bit 7, X'01': reset every MDT before any data or order is done.
        /// </summary>
        public const byte ResetMdt = 0x01;
    }

    /// <summary>
    /// Order codes (GA23-0059-4 p. 4-2, the EBCDIC column).
    /// </summary>
    public static class Order3270
    {
        /// <summary>
        /// Start Field, X'1D'.
        /// </summary>
        public const byte StartField = 0x1D;

        /// <summary>
        /// Start Field Extended, X'29'.
        /// </summary>
        public const byte StartFieldExtended = 0x29;

        /// <summary>
        /// Set Buffer Address, X'11'.
        /// </summary>
        public const byte SetBufferAddress = 0x11;

        /// <summary>
        /// Set Attribute, X'28'.
        /// </summary>
        public const byte SetAttribute = 0x28;

        /// <summary>
        /// Modify Field, X'2C'.
        /// </summary>
        public const byte ModifyField = 0x2C;

        /// <summary>
        /// Insert Cursor, X'13'.
        /// </summary>
        public const byte InsertCursor = 0x13;

        /// <summary>
        /// Program Tab, X'05'.
        /// </summary>
        public const byte ProgramTab = 0x05;

        /// <summary>
        /// Repeat to Address, X'3C'.
        /// </summary>
        public const byte RepeatToAddress = 0x3C;

        /// <summary>
        /// Erase Unprotected to Address, X'12'.
        /// </summary>
        public const byte EraseUnprotectedToAddress = 0x12;

        /// <summary>
        /// Graphic Escape, X'08'.
        /// </summary>
        public const byte GraphicEscape = 0x08;
    }

    /// <summary>
    /// Format control codes that are stored in the buffer like characters (GA23-0059-4 p. 4-10).
    /// </summary>
    public static class FormatControl
    {
        /// <summary>
        /// Null, X'00'. Shown as a blank; left out of a Read Modified reply.
        /// </summary>
        public const byte Null = 0x00;

        /// <summary>
        /// Form Feed, X'0C'.
        /// </summary>
        public const byte FormFeed = 0x0C;

        /// <summary>
        /// Carriage Return, X'0D'.
        /// </summary>
        public const byte CarriageReturn = 0x0D;

        /// <summary>
        /// New Line, X'15'.
        /// </summary>
        public const byte NewLine = 0x15;

        /// <summary>
        /// End of Medium, X'19'.
        /// </summary>
        public const byte EndOfMedium = 0x19;

        /// <summary>
        /// DUP, X'1C'. Shown as an overscored asterisk.
        /// </summary>
        public const byte Dup = 0x1C;

        /// <summary>
        /// Field Mark, X'1E'. Shown as an overscored semicolon.
        /// </summary>
        public const byte FieldMark = 0x1E;

        /// <summary>
        /// Substitute, X'3F'.
        /// </summary>
        public const byte Substitute = 0x3F;

        /// <summary>
        /// True for a byte in the order range X'00'-X'3F' that p. 4-10 lists as storable.
        /// </summary>
        /// <remarks>
        /// Every other byte in that range that is not an order is rejected (p. 4-1).
        /// </remarks>
        /// <param name="b">
        /// The byte.
        /// </param>
        /// <returns>
        /// True when the byte may be stored in the buffer.
        /// </returns>
        public static bool IsStorable(byte b) =>
            b == Null || b == FormFeed || b == CarriageReturn || b == NewLine || b == EndOfMedium
            || b == Dup || b == FieldMark || b == Substitute;
    }

    /// <summary>
    /// Field attribute bits (GA23-0059-4 p. 4-13, Table 4-1).
    /// </summary>
    public static class FieldAttribute
    {
        /// <summary>
        /// Bit 2, X'20': protected.
        /// </summary>
        public const byte Protected = 0x20;

        /// <summary>
        /// Bit 3, X'10': numeric. With <see cref="Protected"/> it means autoskip.
        /// </summary>
        public const byte Numeric = 0x10;

        /// <summary>
        /// Bits 4-5, X'0C': display, intensify or nondisplay.
        /// </summary>
        public const byte DisplayMask = 0x0C;

        /// <summary>
        /// Bits 4-5 = B'10': intensified, pen-detectable.
        /// </summary>
        public const byte Intensified = 0x08;

        /// <summary>
        /// Bits 4-5 = B'11': nondisplay.
        /// </summary>
        public const byte NonDisplay = 0x0C;

        /// <summary>
        /// Bit 7, X'01': modified data tag.
        /// </summary>
        public const byte Mdt = 0x01;

        /// <summary>
        /// The bits the display keeps: 2 to 7.
        /// </summary>
        /// <remarks>
        /// Bits 0-1 only make the byte printable, and the display ignores them on input (p. 4-14).
        /// </remarks>
        public const byte Meaningful = 0x3F;

        /// <summary>
        /// True when the field is protected.
        /// </summary>
        /// <param name="attribute">
        /// The field attribute.
        /// </param>
        /// <returns>
        /// True when protected.
        /// </returns>
        public static bool IsProtected(byte attribute) => (attribute & Protected) != 0;

        /// <summary>
        /// True for protected AND numeric: the cursor skips the field (p. 4-13).
        /// </summary>
        /// <param name="attribute">
        /// The field attribute.
        /// </param>
        /// <returns>
        /// True for an autoskip field.
        /// </returns>
        public static bool IsAutoSkip(byte attribute) => (attribute & (Protected | Numeric)) == (Protected | Numeric);
    }

    /// <summary>
    /// Attention identifiers (GA23-0059-4 p. 3-9, Table 3-4, the EBCDIC column).
    /// </summary>
    public static class Aid
    {
        /// <summary>
        /// No AID generated, X'60'.
        /// </summary>
        public const byte None = 0x60;

        /// <summary>
        /// Structured fields follow, X'88'. The first byte of every inbound record that carries
        /// structured fields, such as query replies (p. 3-9 note 1).
        /// </summary>
        public const byte StructuredField = 0x88;

        /// <summary>
        /// Read Partition, X'61'. Only ever found inside an Inbound 3270DS structured field, never as
        /// the first byte of an inbound record (p. 3-9 note 2).
        /// </summary>
        public const byte ReadPartition = 0x61;

        /// <summary>
        /// Enter, X'7D'.
        /// </summary>
        public const byte Enter = 0x7D;

        /// <summary>
        /// Clear, X'6D'.
        /// </summary>
        public const byte Clear = 0x6D;

        /// <summary>
        /// PA1, X'6C'.
        /// </summary>
        public const byte PA1 = 0x6C;

        /// <summary>
        /// PA2 (Cncl), X'6E'.
        /// </summary>
        public const byte PA2 = 0x6E;

        /// <summary>
        /// PA3, X'6B'.
        /// </summary>
        public const byte PA3 = 0x6B;

        /// <summary>
        /// Test Req and Sys Req, X'F0'. Its Read Modified is the test request read (see
        /// <see cref="Display3270.SysReq"/>), not a normal read heading.
        /// </summary>
        public const byte SysReq = 0xF0;

        /// <summary>
        /// Selector-pen attention, X'7E' (p. 3-9): sent by Cursor Select on a field whose designator
        /// is a space or a null. Its Read Modified carries field addresses but no data (p. 3-12).
        /// </summary>
        public const byte SelectorPen = 0x7E;

        /// <summary>
        /// The AID for PF1 to PF24 (p. 3-9).
        /// </summary>
        /// <remarks>
        /// The values:
        ///  - PF1-PF9 are X'F1'-X'F9'.
        ///  - PF10-PF12 are X'7A'-X'7C'.
        ///  - PF13-PF21 are X'C1'-X'C9'.
        ///  - PF22-PF24 are X'4A'-X'4C'.
        /// </remarks>
        /// <param name="number">
        /// The PF key number, 1 to 24.
        /// </param>
        /// <returns>
        /// The AID byte.
        /// </returns>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// Thrown when <paramref name="number"/> is not 1 to 24.
        /// </exception>
        public static byte Pf(int number)
        {
            if (number >= 1 && number <= 9)
            {
                return (byte)(0xF0 + number);
            }

            if (number >= 10 && number <= 12)
            {
                return (byte)(0x7A + (number - 10));
            }

            if (number >= 13 && number <= 21)
            {
                return (byte)(0xC1 + (number - 13));
            }

            if (number >= 22 && number <= 24)
            {
                return (byte)(0x4A + (number - 22));
            }

            throw new System.ArgumentOutOfRangeException(nameof(number), number, "PF keys are 1 to 24.");
        }

        /// <summary>
        /// True for the AIDs whose Read Modified reply is the AID byte alone: Clear, PA1, PA2, PA3
        /// (the "short read", p. 3-13).
        /// </summary>
        /// <param name="aid">
        /// The AID byte.
        /// </param>
        /// <returns>
        /// True for a short-read AID.
        /// </returns>
        public static bool IsShortRead(byte aid) => aid == Clear || aid == PA1 || aid == PA2 || aid == PA3;
    }

    /// <summary>
    /// Structured field IDs the display acts on (GA23-0059-4 p. 5-2 for outbound, chapter 6 for
    /// inbound).
    /// </summary>
    /// <remarks>
    /// An outbound field with any other ID is reported through <see cref="Display3270.ProtocolError"/>
    /// and the rest of the WSF is not applied.
    /// </remarks>
    public static class StructuredFieldId
    {
        /// <summary>
        /// Read Partition, X'01': a read of a partition, or a query (p. 5-47).
        /// </summary>
        public const byte ReadPartition = 0x01;

        /// <summary>
        /// Erase/Reset, X'03': back to implicit partition 0, at the default or alternate size
        /// (p. 5-15).
        /// </summary>
        public const byte EraseReset = 0x03;

        /// <summary>
        /// Set Reply Mode, X'09': the form of later inbound data (p. 5-61).
        /// </summary>
        public const byte SetReplyMode = 0x09;

        /// <summary>
        /// Outbound 3270DS, X'40': a 3270 command with its data, for one partition (p. 5-40).
        /// </summary>
        public const byte Outbound3270DS = 0x40;

        /// <summary>
        /// Query Reply, X'81': the first ID byte of every query reply; the second is the QCODE
        /// (p. 6-17).
        /// </summary>
        public const byte QueryReply = 0x81;

        /// <summary>
        /// Inbound 3270DS, X'80': the AID, cursor and data of a read from a partition with a nonzero
        /// PID (p. 6-7).
        /// </summary>
        public const byte Inbound3270DS = 0x80;

        /// <summary>
        /// Data transfer, X'D0': the DDM structured fields IND$FILE uses in DFT mode (IBM
        /// SS-HCS12-1372-00 p. 23, 26).
        /// </summary>
        public const byte DataTransfer = 0xD0;
    }

    /// <summary>
    /// Query reply codes, QCODEs (GA23-0059-4 p. 6-15, 6-16, the Query Response Table).
    /// </summary>
    public static class QueryCode
    {
        /// <summary>
        /// Summary, X'80': the list of every reply this display supports (p. 6-81).
        /// </summary>
        public const byte Summary = 0x80;

        /// <summary>
        /// Usable Area, X'81': the size of the display surface (p. 6-85).
        /// </summary>
        public const byte UsableArea = 0x81;

        /// <summary>
        /// Alphanumeric Partitions, X'84': how many partitions Create Partition may make, their
        /// storage, and the partition functions supported (p. 6-18).
        /// </summary>
        public const byte AlphanumericPartitions = 0x84;

        /// <summary>
        /// Character Sets, X'85': the base set and the Graphic Escape set (p. 6-21).
        /// </summary>
        public const byte CharacterSets = 0x85;

        /// <summary>
        /// Color, X'86': the foreground colours accepted (p. 6-29).
        /// </summary>
        public const byte Color = 0x86;

        /// <summary>
        /// Highlighting, X'87': the extended highlighting values accepted (p. 6-56).
        /// </summary>
        public const byte Highlighting = 0x87;

        /// <summary>
        /// Reply Modes, X'88': the inbound reply modes accepted (p. 6-74).
        /// </summary>
        public const byte ReplyModes = 0x88;

        /// <summary>
        /// Field Validation, X'8A': mandatory fill, mandatory entry and trigger (p. 6-49).
        /// </summary>
        public const byte FieldValidation = 0x8A;

        /// <summary>
        /// Field Outlining, X'8C': where the outline lines are drawn in a character cell (p. 6-49).
        /// </summary>
        public const byte FieldOutlining = 0x8C;

        /// <summary>
        /// DDM, X'95': the file transfer subset IND$FILE looks for to choose DFT mode (p. 6-37).
        /// </summary>
        public const byte Ddm = 0x95;

        /// <summary>
        /// Implicit Partition, X'A6': the default and alternate screen sizes (p. 6-59).
        /// </summary>
        public const byte ImplicitPartition = 0xA6;

        /// <summary>
        /// Null, X'FF': sent only when a Query List asked for nothing this display supports (p. 6-67).
        /// </summary>
        public const byte Null = 0xFF;
    }

    /// <summary>
    /// The inbound reply modes set by Set Reply Mode (GA23-0059-4 p. 5-61, 5-62).
    /// </summary>
    public enum ReplyMode : byte
    {
        /// <summary>
        /// Field mode, X'00', the default: SF orders only, no extended or character attributes.
        /// </summary>
        Field = 0x00,

        /// <summary>
        /// Extended field mode, X'01': SFE orders with every extended field attribute that is not at
        /// its default.
        /// </summary>
        ExtendedField = 0x01,

        /// <summary>
        /// Character mode, X'02': as extended field, plus SA orders for the listed character
        /// attribute types.
        /// </summary>
        Character = 0x02,
    }

    /// <summary>
    /// The bits of the field validation attribute, type X'C1' (GA23-0059-4 p. 4-21).
    /// </summary>
    /// <remarks>
    /// Bits 0-4 are reserved and must be zero. IBM numbers bit 0 as the high bit, so bit 5 is X'04'.
    /// </remarks>
    public static class FieldValidation
    {
        /// <summary>
        /// Bit 5, mandatory fill: once data is typed into the field, the field must be filled.
        /// </summary>
        public const byte MandatoryFill = 0x04;

        /// <summary>
        /// Bit 6, mandatory entry: no data goes to the host until something is typed in the field.
        /// </summary>
        public const byte MandatoryEntry = 0x02;

        /// <summary>
        /// Bit 7, trigger: the host is told with AID X'7F' when the operator leaves the field.
        /// </summary>
        public const byte Trigger = 0x01;

        /// <summary>
        /// Bits 0-4, which must be zero.
        /// </summary>
        public const byte Reserved = 0xF8;
    }

    /// <summary>
    /// The bits of the field outlining attribute, type X'C2' (GA23-0059-4 p. 4-20).
    /// </summary>
    /// <remarks>
    /// Any mix of the four lines is allowed; all four make a rectangle. Outlining is always drawn
    /// with normal intensity, not reversed, not blinking, in the default colour (p. 4-20).
    /// </remarks>
    public static class FieldOutlining
    {
        /// <summary>
        /// A line under the field.
        /// </summary>
        public const byte Underline = 0x01;

        /// <summary>
        /// A line at the right edge, drawn in the next field attribute position.
        /// </summary>
        public const byte Right = 0x02;

        /// <summary>
        /// A line over the field.
        /// </summary>
        public const byte Overline = 0x04;

        /// <summary>
        /// A line at the left edge, drawn in the field attribute position.
        /// </summary>
        public const byte Left = 0x08;
    }
}
