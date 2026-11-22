//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// Outbound command codes, the SNA / TN3270 set (GA23-0059-4 p. 3-2, Table 3-1). A locally
    /// attached 3274 uses other (channel) codes; those are not listed here.
    /// </summary>
    public static class Command3270
    {
        /// <summary>Write, X'F1'.</summary>
        public const byte Write = 0xF1;

        /// <summary>Erase/Write, X'F5'.</summary>
        public const byte EraseWrite = 0xF5;

        /// <summary>Erase/Write Alternate, X'7E'.</summary>
        public const byte EraseWriteAlternate = 0x7E;

        /// <summary>Read Buffer, X'F2'.</summary>
        public const byte ReadBuffer = 0xF2;

        /// <summary>Read Modified, X'F6'.</summary>
        public const byte ReadModified = 0xF6;

        /// <summary>Read Modified All, X'6E'.</summary>
        public const byte ReadModifiedAll = 0x6E;

        /// <summary>Erase All Unprotected, X'6F'.</summary>
        public const byte EraseAllUnprotected = 0x6F;

        /// <summary>Write Structured Field, X'F3'.</summary>
        public const byte WriteStructuredField = 0xF3;
    }

    /// <summary>
    /// Write Control Character bits (GA23-0059-4 p. 3-3, Table 3-2). Bit 0 is the leftmost bit,
    /// so bit 7 is X'01'.
    /// </summary>
    public static class Wcc
    {
        /// <summary>Bit 1, X'40': reset partition characteristics to defaults.</summary>
        public const byte Reset = 0x40;

        /// <summary>Bit 4, X'08': start printer.</summary>
        public const byte StartPrinter = 0x08;

        /// <summary>Bit 5, X'04': sound the alarm at the end of the operation.</summary>
        public const byte SoundAlarm = 0x04;

        /// <summary>Bit 6, X'02': keyboard restore - unlock the keyboard and reset the AID.</summary>
        public const byte KeyboardRestore = 0x02;

        /// <summary>Bit 7, X'01': reset every MDT before any data or order is done.</summary>
        public const byte ResetMdt = 0x01;
    }

    /// <summary>
    /// Order codes (GA23-0059-4 p. 4-2, the EBCDIC column).
    /// </summary>
    public static class Order3270
    {
        /// <summary>Start Field, X'1D'.</summary>
        public const byte StartField = 0x1D;

        /// <summary>Start Field Extended, X'29'.</summary>
        public const byte StartFieldExtended = 0x29;

        /// <summary>Set Buffer Address, X'11'.</summary>
        public const byte SetBufferAddress = 0x11;

        /// <summary>Set Attribute, X'28'.</summary>
        public const byte SetAttribute = 0x28;

        /// <summary>Modify Field, X'2C'.</summary>
        public const byte ModifyField = 0x2C;

        /// <summary>Insert Cursor, X'13'.</summary>
        public const byte InsertCursor = 0x13;

        /// <summary>Program Tab, X'05'.</summary>
        public const byte ProgramTab = 0x05;

        /// <summary>Repeat to Address, X'3C'.</summary>
        public const byte RepeatToAddress = 0x3C;

        /// <summary>Erase Unprotected to Address, X'12'.</summary>
        public const byte EraseUnprotectedToAddress = 0x12;

        /// <summary>Graphic Escape, X'08'.</summary>
        public const byte GraphicEscape = 0x08;
    }

    /// <summary>
    /// Format control codes that are stored in the buffer like characters (GA23-0059-4 p. 4-10).
    /// </summary>
    public static class FormatControl
    {
        /// <summary>Null, X'00'. Shown as a blank; left out of a Read Modified reply.</summary>
        public const byte Null = 0x00;

        /// <summary>Form Feed, X'0C'.</summary>
        public const byte FormFeed = 0x0C;

        /// <summary>Carriage Return, X'0D'.</summary>
        public const byte CarriageReturn = 0x0D;

        /// <summary>New Line, X'15'.</summary>
        public const byte NewLine = 0x15;

        /// <summary>End of Medium, X'19'.</summary>
        public const byte EndOfMedium = 0x19;

        /// <summary>DUP, X'1C'. Shown as an overscored asterisk.</summary>
        public const byte Dup = 0x1C;

        /// <summary>Field Mark, X'1E'. Shown as an overscored semicolon.</summary>
        public const byte FieldMark = 0x1E;

        /// <summary>Substitute, X'3F'.</summary>
        public const byte Substitute = 0x3F;

        /// <summary>
        /// True for a byte in the order range X'00'-X'3F' that p. 4-10 lists as storable.
        /// Every other byte in that range that is not an order is rejected (p. 4-1).
        /// </summary>
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
        /// <summary>Bit 2, X'20': protected.</summary>
        public const byte Protected = 0x20;

        /// <summary>Bit 3, X'10': numeric. With Protected it means autoskip.</summary>
        public const byte Numeric = 0x10;

        /// <summary>Bits 4-5, X'0C': display / intensify / nondisplay.</summary>
        public const byte DisplayMask = 0x0C;

        /// <summary>Bits 4-5 = 10: intensified, pen-detectable.</summary>
        public const byte Intensified = 0x08;

        /// <summary>Bits 4-5 = 11: nondisplay.</summary>
        public const byte NonDisplay = 0x0C;

        /// <summary>Bit 7, X'01': modified data tag.</summary>
        public const byte Mdt = 0x01;

        /// <summary>
        /// The bits the display keeps: 2 to 7. Bits 0-1 only make the byte printable and the display
        /// ignores them on input (p. 4-14).
        /// </summary>
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
        /// <summary>No AID generated, X'60'.</summary>
        public const byte None = 0x60;

        /// <summary>Enter, X'7D'.</summary>
        public const byte Enter = 0x7D;

        /// <summary>Clear, X'6D'.</summary>
        public const byte Clear = 0x6D;

        /// <summary>PA1, X'6C'.</summary>
        public const byte PA1 = 0x6C;

        /// <summary>PA2 (Cncl), X'6E'.</summary>
        public const byte PA2 = 0x6E;

        /// <summary>PA3, X'6B'.</summary>
        public const byte PA3 = 0x6B;

        /// <summary>Test Req and Sys Req, X'F0'.</summary>
        public const byte SysReq = 0xF0;

        /// <summary>
        /// The AID for PF1 to PF24 (p. 3-9). PF1-PF9 are X'F1'-X'F9', PF10-PF12 X'7A'-X'7C',
        /// PF13-PF21 X'C1'-X'C9', PF22-PF24 X'4A'-X'4C'.
        /// </summary>
        /// <param name="number">
        /// The PF key number, 1 to 24.
        /// </param>
        /// <returns>
        /// The AID byte.
        /// </returns>
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
}
