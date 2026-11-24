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
    /// One partition of a <see cref="Display3270"/>: its presentation space (the character buffer
    /// with every attribute), its cursor, its inbound reply mode and where its viewport sits on the
    /// screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// GA23-0059-4 chapter 2 (p. 2-1 to 2-6): every partition has its own presentation space of
    /// H rows by W columns, addressed 0 to W x H - 1 from its own origin; its own current cursor
    /// position; and a viewport, the rectangle of the usable area it is shown in. Without scrolling
    /// "a partition's presentation space and viewport have the same dimensions" (p. 2-3), which is
    /// the only case built here, so the window is always the whole presentation space at offset
    /// (0, 0).
    /// </para>
    /// <para>
    /// The implicit partition 0 (p. 2-6) is one of these too: its viewport is the whole screen at
    /// (0, 0), and Erase/Write and Erase/Write Alternate change its size.
    /// </para>
    /// <para>
    /// Read only from outside RetroIO: the host changes it through the data stream, the operator
    /// through the keys of <see cref="Display3270"/>.
    /// </para>
    /// </remarks>
    public sealed class Partition3270
    {
        // The storage. Display3270 reads and writes these directly for the partition it is working
        // on; they are internal so that nothing outside RetroIO can change a buffer behind the data
        // stream's back.
        internal byte[] Buffer = Array.Empty<byte>();
        internal bool[] IsFieldAttributeAt = Array.Empty<bool>();
        internal bool[] GraphicEscapeAt = Array.Empty<bool>();

        // Extended field attributes, kept at the field attribute's position (GA23-0059-4 p. 4-11).
        internal byte[] FieldHighlight = Array.Empty<byte>();
        internal byte[] FieldColor = Array.Empty<byte>();
        internal byte[] FieldCharset = Array.Empty<byte>();
        internal byte[] FieldBackground = Array.Empty<byte>();
        internal byte[] FieldTransparency = Array.Empty<byte>();
        internal byte[] FieldValidationAt = Array.Empty<byte>();
        internal byte[] FieldOutliningAt = Array.Empty<byte>();

        // Character attributes, kept per position (p. 4-15).
        internal byte[] CharHighlight = Array.Empty<byte>();
        internal byte[] CharColor = Array.Empty<byte>();
        internal byte[] CharCharset = Array.Empty<byte>();
        internal byte[] CharBackground = Array.Empty<byte>();
        internal byte[] CharTransparency = Array.Empty<byte>();

        // Set Reply Mode (p. 5-61) names a partition: the form of later inbound data from it, and
        // for Character mode the character attribute types that are sent with SA orders. Reset
        // Partition and a WCC reset put it back to Field (p. 5-51, Table 3-3 p. 3-4).
        internal ReplyMode InboundReplyMode = ReplyMode.Field;
        internal byte[] ReplyTypes = Array.Empty<byte>();

        // The field attribute address of the primed mandatory-fill field, or -1 (p. 4-21). Only one
        // can be primed: the cursor is in it, and leaving it validates and unprimes it.
        internal int PrimedField = -1;

        // The current cursor position of this partition (p. 2-5: "Associated with each partition
        // is a current cursor position on the presentation space").
        internal int Cursor;

        internal Partition3270(byte pid, int rows, int columns, int viewportRow, int viewportColumn, bool sixteenBitAddressing)
        {
            Pid = pid;
            ViewportRow = viewportRow;
            ViewportColumn = viewportColumn;
            SixteenBitAddressing = sixteenBitAddressing;
            Resize(rows, columns);
        }

        /// <summary>
        /// Gets the partition identifier, X'00' to X'7E' (GA23-0059-4 p. 5-10).
        /// </summary>
        public byte Pid { get; }

        /// <summary>
        /// Gets the rows of the presentation space (H), which are also the viewport's rows: no
        /// scrolling is built.
        /// </summary>
        public int Rows { get; private set; }

        /// <summary>
        /// Gets the columns of the presentation space (W), which are also the viewport's columns.
        /// </summary>
        public int Columns { get; private set; }

        /// <summary>
        /// Gets rows times columns: the number of buffer addresses, 0 to Size - 1 (p. 2-2).
        /// </summary>
        public int Size => Rows * Columns;

        /// <summary>
        /// Gets the screen row of the viewport's top edge, relative to the top of the usable area
        /// (RV, p. 5-11). 0 for the implicit partition.
        /// </summary>
        public int ViewportRow { get; }

        /// <summary>
        /// Gets the screen column of the viewport's left edge, relative to the left of the usable
        /// area (CV, p. 5-11). 0 for the implicit partition.
        /// </summary>
        public int ViewportColumn { get; }

        /// <summary>
        /// Gets a value indicating whether buffer addresses to and from this partition are 16-bit
        /// binary (Create Partition A-MODE B'0001', p. 5-11, 4-4). False means 12/14-bit, which the
        /// implicit partition 0 always is (p. 4-4).
        /// </summary>
        public bool SixteenBitAddressing { get; }

        /// <summary>
        /// Gets the current cursor position on this partition's presentation space (p. 2-5). The
        /// screen cursor shows at this position of the active partition.
        /// </summary>
        public int CursorAddress => Cursor;

        /// <summary>
        /// Gets the inbound reply mode the host last set for this partition with Set Reply Mode.
        /// </summary>
        public ReplyMode ReplyMode => InboundReplyMode;

        /// <summary>
        /// The byte stored at an address of this partition: a character (EBCDIC), a stored control,
        /// or - when <see cref="IsFieldAttribute"/> is true - the field attribute.
        /// </summary>
        /// <param name="address">
        /// The buffer address, relative to the partition's origin.
        /// </param>
        /// <returns>
        /// The byte.
        /// </returns>
        public byte GetByte(int address) => Buffer[address];

        /// <summary>
        /// True when the position holds a field attribute (shown as a blank, p. 4-13).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// True for a field attribute position.
        /// </returns>
        public bool IsFieldAttribute(int address) => IsFieldAttributeAt[address];

        /// <summary>
        /// True when the character came in with Graphic Escape, so it is from code page 310 (p. 4-9).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// True for a GE character.
        /// </returns>
        public bool IsGraphicEscape(int address) => GraphicEscapeAt[address];

        /// <summary>
        /// The address of the field attribute that governs a position, or -1 when the partition has
        /// no field. Searches backwards and wraps inside this partition's buffer.
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The field attribute's address, or -1.
        /// </returns>
        public int GetFieldAttributeFor(int address)
        {
            int size = Size;
            int a = address;
            for (int n = 0; n < size; n++)
            {
                if (IsFieldAttributeAt[a])
                {
                    return a;
                }

                a = a == 0 ? size - 1 : a - 1;
            }

            return -1;
        }

        /// <summary>
        /// The extended foreground colour of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The colour value.
        /// </returns>
        public byte GetFieldColor(int fieldAttributeAddress) => FieldColor[fieldAttributeAddress];

        /// <summary>
        /// The foreground colour character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The colour value.
        /// </returns>
        public byte GetCharacterColor(int address) => CharColor[address];

        /// <summary>
        /// The extended highlighting of the field at a field attribute address.
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The highlight value (p. 4-18).
        /// </returns>
        public byte GetFieldHighlight(int fieldAttributeAddress) => FieldHighlight[fieldAttributeAddress];

        /// <summary>
        /// The highlighting character attribute of one position.
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The highlight value.
        /// </returns>
        public byte GetCharacterHighlight(int address) => CharHighlight[address];

        /// <summary>
        /// The character set of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The character set's local ID (p. 4-19).
        /// </returns>
        public byte GetFieldCharacterSet(int fieldAttributeAddress) => FieldCharset[fieldAttributeAddress];

        /// <summary>
        /// The character set character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The character set's local ID.
        /// </returns>
        public byte GetCharacterCharacterSet(int address) => CharCharset[address];

        /// <summary>
        /// The background colour of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The colour value (p. 4-18).
        /// </returns>
        public byte GetFieldBackground(int fieldAttributeAddress) => FieldBackground[fieldAttributeAddress];

        /// <summary>
        /// The background colour character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The colour value.
        /// </returns>
        public byte GetCharacterBackground(int address) => CharBackground[address];

        /// <summary>
        /// The transparency of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The transparency value (p. 4-20).
        /// </returns>
        public byte GetFieldTransparency(int fieldAttributeAddress) => FieldTransparency[fieldAttributeAddress];

        /// <summary>
        /// The transparency character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The transparency value.
        /// </returns>
        public byte GetCharacterTransparency(int address) => CharTransparency[address];

        /// <summary>
        /// The field validation attribute of the field at a field attribute address (p. 4-21).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The validation bits: see <see cref="FieldValidation"/>.
        /// </returns>
        public byte GetFieldValidation(int fieldAttributeAddress) => FieldValidationAt[fieldAttributeAddress];

        /// <summary>
        /// The field outlining attribute of the field at a field attribute address (p. 4-20).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The outlining bits: see <see cref="FieldOutlining"/>.
        /// </returns>
        public byte GetFieldOutlining(int fieldAttributeAddress) => FieldOutliningAt[fieldAttributeAddress];

        // Gives the presentation space a size and fills it with nulls, every attribute X'00'
        // (Erase/Write steps 2 to 4, p. 3-6). The cursor stays unless it would fall outside.
        internal void Resize(int rows, int columns)
        {
            Rows = rows;
            Columns = columns;
            int size = rows * columns;
            if (Buffer.Length != size)
            {
                Buffer = new byte[size];
                IsFieldAttributeAt = new bool[size];
                GraphicEscapeAt = new bool[size];
                FieldHighlight = new byte[size];
                FieldColor = new byte[size];
                FieldCharset = new byte[size];
                FieldBackground = new byte[size];
                FieldTransparency = new byte[size];
                FieldValidationAt = new byte[size];
                FieldOutliningAt = new byte[size];
                CharHighlight = new byte[size];
                CharColor = new byte[size];
                CharCharset = new byte[size];
                CharBackground = new byte[size];
                CharTransparency = new byte[size];
            }
            else
            {
                Array.Clear(Buffer);
                Array.Clear(IsFieldAttributeAt);
                Array.Clear(GraphicEscapeAt);
                Array.Clear(FieldHighlight);
                Array.Clear(FieldColor);
                Array.Clear(FieldCharset);
                Array.Clear(FieldBackground);
                Array.Clear(FieldTransparency);
                Array.Clear(FieldValidationAt);
                Array.Clear(FieldOutliningAt);
                Array.Clear(CharHighlight);
                Array.Clear(CharColor);
                Array.Clear(CharCharset);
                Array.Clear(CharBackground);
                Array.Clear(CharTransparency);
            }

            if (Cursor >= size)
            {
                Cursor = 0;
            }
        }
    }
}
