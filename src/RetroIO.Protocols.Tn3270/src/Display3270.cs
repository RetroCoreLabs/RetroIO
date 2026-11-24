//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// Why the keyboard is locked.
    /// </summary>
    public enum KeyboardLock
    {
        /// <summary>
        /// Not locked.
        /// </summary>
        None,

        /// <summary>
        /// An attention key was sent and no Write has come back yet: TWAIT, shown as X and a
        /// clock. The operator's Reset key does NOT clear this (GA23-0059-4 p. 3-20).
        /// </summary>
        WaitingForHost,

        /// <summary>
        /// The host has written since the attention key but did not restore the keyboard: System
        /// Lock, shown as "X SYSTEM". Reset clears it, as does a Write with keyboard restore.
        /// </summary>
        /// <remarks>
        /// GA23-0059-4 p. 3-20: System Lock is raised with TWAIT by every enter action, is not
        /// shown while TWAIT exists, and unlike TWAIT can be cleared by Reset; GA27-2890-4 p. 2-21
        /// describes "X SYSTEM" the same way. x3270 makes the change at the end of a Write without
        /// keyboard restore (Common\ctlr.c, ctlr_write: KL_OIA_TWAIT cleared, status_syswait),
        /// which is the rule here.
        /// </remarks>
        SystemLock,

        /// <summary>
        /// The operator did something not allowed, such as typing into a protected field
        /// ("Go Elsewhere", GA27-2890-4 p. 2-21). Reset clears it.
        /// </summary>
        /// <remarks>
        /// IBM locks here; x3270 by default only ignores the key. RetroTerm follows IBM (decided
        /// 22 November 2026).
        /// </remarks>
        OperatorError,

        /// <summary>
        /// Insert mode found no null left in the field to take the character ("Too Much Data",
        /// GA27-2890-4 p. 3-42; GA23-0061-2 p. C-5). Reset clears it.
        /// </summary>
        TooMuchData,

        /// <summary>
        /// The cursor tried to leave, or the operator tried to send, a mandatory-fill field that has
        /// data in it but is not full (GA23-0059-4 p. 4-21). Reset clears it.
        /// </summary>
        MandatoryFill,

        /// <summary>
        /// The operator tried to send while a mandatory-entry field is still empty; the cursor was
        /// moved to it (GA23-0059-4 p. 4-22). Reset clears it.
        /// </summary>
        MandatoryEntry,

        /// <summary>
        /// A key whose function is not available now: "Minus Function", shown as "X -f". Reset
        /// clears it, as does a Write with keyboard restore.
        /// </summary>
        /// <remarks>
        /// GA27-2890-4 p. 3-32: Attn works only when the OIA shows the "my job" symbol, otherwise
        /// Minus Function. x3270 sets it for Attn on a TN3270E connection with no SNA session
        /// bound (Common\kybd.c, Attn_action: kybdlock_set(KL_OIA_MINUS)) and shows "X -f"
        /// (Common\vstatus.c). x3270 keeps its locks as bits and can hold Minus Function and TWAIT
        /// together; here the lock is one value, so Minus Function replaces whatever was there.
        /// The states a person can then reach are the same: Reset clears both in x3270 and clears
        /// this one here, and a Write with keyboard restore clears all of them in both.
        /// </remarks>
        MinusFunction,

        /// <summary>
        /// The terminal has sent its query replies (Read Partition Query or Query List) and the host
        /// has not acknowledged them yet: TWAIT, shown as "X Wait". No keystroking; the operator's
        /// Reset key does NOT clear it. The host's next outbound transmission that is not a read
        /// command clears it.
        /// </summary>
        /// <remarks>
        /// GA23-0059-4, Read Partition Query processing for BSC (p. 9-9) and for locally attached
        /// non-SNA devices (p. 10-8): "The TWAIT indicator is displayed", and after the reply is
        /// sent "the device [is] placed in retry-read state"; the indicator tables give TWAIT for
        /// Retry read (p. 9-6, p. 10-5). TWAIT is an input inhibit condition, "no keystroking is
        /// allowed", and "cannot be cleared by the operator (for example, by the RESET Key)"
        /// (p. 3-20). The SNA chapter says only that a Read Partition puts the device in retry-read
        /// state, where an enter action is rejected (p. 3-17, 3-18); it does not name an
        /// indicator. What ends it (p. 3-23, p. 10-5): "a Query or Query List operation is
        /// acknowledged by any outbound transmission except one with a read command" - a WSF even
        /// when its structured fields hold an error, an EAU, and an EW, EWA or Write with or without
        /// a WCC unless an error is found in its data. Keyboard restore is not needed. x3270 locks
        /// here too (Common\sf.c, query_reply_end: kybd_inhibit(true), shown "X Wait" by
        /// Common\vstatus.c) and unlocks on the next Write, Erase/Write or EAU (Common\ctlr.c); its
        /// Reset key also unlocks it, which the manual's TWAIT rule does not allow, so here it does
        /// not. Unlike <see cref="WaitingForHost"/>, a Write without keyboard restore does not
        /// leave System Lock behind: System Lock is raised only by an enter action (p. 3-21).
        /// </remarks>
        QueryReplySent,
    }

    /// <summary>
    /// A 3270 display station screen: buffer, fields, attributes, cursor and keyboard lock, with
    /// the host side (outbound records) and the operator side (keys) both handled here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No network and no window. A host record goes in through <see cref="ProcessOutbound"/>; what the
    /// terminal sends back comes out through <see cref="InboundRecord"/>; a renderer reads the
    /// buffer through <see cref="GetByte"/>, <see cref="IsFieldAttribute"/> and
    /// <see cref="GetFieldAttributeFor"/>.
    /// </para>
    /// <para>
    /// Write Structured Field is handled for Read Partition (Query, Query List, and the three reads
    /// of partition 0), Erase/Reset, Set Reply Mode, Outbound 3270DS and the file transfer field
    /// X'D0', which goes to <see cref="FileTransferField"/>.
    /// </para>
    /// <para>
    /// Not built yet, and loud about it through <see cref="ProtocolError"/>: partitions other than
    /// the implicit partition 0, 16-bit addresses, every other structured field, and the BSC Copy
    /// command inside Outbound 3270DS.
    /// </para>
    /// </remarks>
    public sealed class Display3270
    {
        // EBCDIC code page 037 characters the selector-pen designator and Field End test for
        // (GA23-0059-4 p. 4-22: "?" X'6F', ">" X'6E', "&" X'50', space X'40').
        private const byte EbcdicSpace = 0x40;
        private const byte EbcdicQuestion = 0x6F;
        private const byte EbcdicGreater = 0x6E;
        private const byte EbcdicAmpersand = 0x50;

        // The character attribute types an SA order may carry (p. 4-16): highlighting, foreground
        // colour, character set, background colour, transparency.
        private static readonly byte[] CharacterAttributeTypes = { 0x41, 0x42, 0x43, 0x45, 0x46 };

        /// <summary>
        /// The local ID the Character Sets query reply gives the base character set (GA23-0059-4
        /// p. 6-26: X'F0' to X'F7' for non-loadable 1-byte sets; X'F1' is the Graphic Escape set).
        /// </summary>
        public const byte BaseCharacterSetId = 0xF0;

        /// <summary>
        /// The local ID the Character Sets query reply gives the Graphic Escape set, code page 310.
        /// </summary>
        public const byte GraphicEscapeCharacterSetId = 0xF1;

        // The query replies this terminal sends, in ascending code order (p. 6-81: Null is never listed).
        private static readonly byte[] QueryCodesWithoutFileTransfer =
        {
            QueryCode.Summary, QueryCode.UsableArea, QueryCode.CharacterSets, QueryCode.Color,
            QueryCode.Highlighting, QueryCode.ReplyModes, QueryCode.FieldValidation, QueryCode.ImplicitPartition,
        };

        private static readonly byte[] QueryCodesWithFileTransfer =
        {
            QueryCode.Summary, QueryCode.UsableArea, QueryCode.CharacterSets, QueryCode.Color,
            QueryCode.Highlighting, QueryCode.ReplyModes, QueryCode.FieldValidation, QueryCode.Ddm, QueryCode.ImplicitPartition,
        };

        private readonly int _defaultRows;
        private readonly int _defaultColumns;
        private readonly int _alternateRows;
        private readonly int _alternateColumns;

        private readonly ArrayBufferWriter<byte> _inbound = new ArrayBufferWriter<byte>(4096);

        // The last inbound record sent with SendStructuredFieldReply(field, retry: true), kept until
        // the host acknowledges it, so a Read Modified command sends it again (see
        // SendStructuredFieldReply). _retryLength 0: nothing to retry.
        private byte[] _retryRecord = Array.Empty<byte>();
        private int _retryLength;

        private byte[] _buffer = Array.Empty<byte>();
        private bool[] _isFieldAttribute = Array.Empty<bool>();
        private bool[] _graphicEscape = Array.Empty<bool>();

        // Extended field attributes, kept at the field attribute's position (GA23-0059-4 p. 4-11).
        private byte[] _fieldHighlight = Array.Empty<byte>();
        private byte[] _fieldColor = Array.Empty<byte>();
        private byte[] _fieldCharset = Array.Empty<byte>();
        private byte[] _fieldBackground = Array.Empty<byte>();
        private byte[] _fieldTransparency = Array.Empty<byte>();
        private byte[] _fieldValidation = Array.Empty<byte>();
        private byte[] _fieldOutlining = Array.Empty<byte>();

        // Character attributes, kept per position (p. 4-15).
        private byte[] _charHighlight = Array.Empty<byte>();
        private byte[] _charColor = Array.Empty<byte>();
        private byte[] _charCharset = Array.Empty<byte>();
        private byte[] _charBackground = Array.Empty<byte>();
        private byte[] _charTransparency = Array.Empty<byte>();

        // The current SA set, applied to each character written (p. 4-5).
        private byte _saHighlight;
        private byte _saColor;
        private byte _saCharset;
        private byte _saBackground;
        private byte _saTransparency;

        private byte _lastAid = Aid.None;

        // Set Reply Mode (p. 5-61): the form of later inbound data, and for Character mode the
        // character attribute types that are sent with SA orders.
        private ReplyMode _replyMode = ReplyMode.Field;
        private byte[] _replyTypes = Array.Empty<byte>();

        // The character attribute values last sent inbound with SA, in the order of
        // CharacterAttributeTypes. Every inbound record starts with all at default (p. 3-12, 3-13).
        private readonly byte[] _inboundSa = new byte[5];

        private int _fileTransferLimit;

        // The field attribute address of the primed mandatory-fill field, or -1 (p. 4-21). Only one
        // can be primed: the cursor is in it, and leaving it validates and unprimes it.
        private int _primedField = -1;

        // Set when the record being applied raised ProtocolError.
        private bool _failed;
        private CodePage _codePage = CodePage.Cp037;

        /// <summary>
        /// Creates a display with a default and an alternate screen size.
        /// </summary>
        /// <remarks>
        /// The default size is used after Erase/Write, the alternate size after Erase/Write
        /// Alternate (GA23-0059-4 p. 3-5). A model 2 has 24 x 80 for both.
        /// </remarks>
        /// <param name="rows">
        /// Default rows.
        /// </param>
        /// <param name="columns">
        /// Default columns.
        /// </param>
        /// <param name="alternateRows">
        /// Alternate rows.
        /// </param>
        /// <param name="alternateColumns">
        /// Alternate columns.
        /// </param>
        public Display3270(int rows = 24, int columns = 80, int alternateRows = 24, int alternateColumns = 80)
        {
            if (rows < 1 || columns < 1 || alternateRows < 1 || alternateColumns < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rows), "Every dimension must be at least 1.");
            }

            if (rows * columns > 0x3FFF || alternateRows * alternateColumns > 0x3FFF)
            {
                // Above 16383 cells only 16-bit addressing reaches, which needs partitions (p. D-1).
                throw new ArgumentOutOfRangeException(nameof(rows), "More than 16383 cells needs 16-bit addressing, which is not built.");
            }

            _defaultRows = rows;
            _defaultColumns = columns;
            _alternateRows = alternateRows;
            _alternateColumns = alternateColumns;
            Resize(rows, columns);
        }

        /// <summary>
        /// A record the terminal sends to the host: an AID reply, a read reply or query replies.
        /// </summary>
        /// <remarks>
        /// The span is only valid during the call.
        /// </remarks>
        public event InboundRecordHandler? InboundRecord;

        /// <summary>
        /// The host sent something this model rejects or has not built.
        /// </summary>
        /// <remarks>
        /// The record is not applied past that point. The text says what and why, with the page of
        /// the manual that decides it.
        /// </remarks>
        public event ProtocolErrorHandler? ProtocolError;

        /// <summary>
        /// The WCC asked to sound the alarm (GA23-0059-4 p. 3-3).
        /// </summary>
        public event AlarmHandler? Alarm;

        /// <summary>
        /// A file transfer structured field (ID X'D0') arrived in a Write Structured Field.
        /// </summary>
        /// <remarks>
        /// Raised only while <see cref="FileTransferLimit"/> is above 0; otherwise such a field is a
        /// <see cref="ProtocolError"/>. The span is the whole structured field, length bytes first, and
        /// is only valid during the call. <see cref="IndFileTransfer"/> handles it.
        /// </remarks>
        public event StructuredFieldHandler? FileTransferField;

        /// <summary>
        /// Gets the number of rows on the screen now.
        /// </summary>
        public int Rows { get; private set; }

        /// <summary>
        /// Gets the number of columns on the screen now.
        /// </summary>
        public int Columns { get; private set; }

        /// <summary>
        /// Gets rows times columns.
        /// </summary>
        public int Size => Rows * Columns;

        /// <summary>
        /// Gets the cursor's buffer address.
        /// </summary>
        public int CursorAddress { get; private set; }

        /// <summary>
        /// Gets why the keyboard is locked, or <see cref="KeyboardLock.None"/>.
        /// </summary>
        public KeyboardLock Lock { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the keyboard is in insert mode.
        /// </summary>
        /// <remarks>
        /// Switched by the Insert key through <see cref="ToggleInsert"/>; ended by Reset and by every
        /// key that talks to the host (GA23-0061-2 p. C-5, C-6).
        /// </remarks>
        public bool InsertMode { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether the screen shows colour, as a 3279 does.
        /// </summary>
        /// <remarks>
        /// Only changes the Color query reply (GA23-0059-4 p. 6-29, 6-30): with colour every value
        /// X'F1'-X'F7' is shown as itself; without, every value is shown as the default colour.
        /// The colour attributes are stored either way. Default true.
        /// </remarks>
        public bool Color { get; set; } = true;

        /// <summary>
        /// Gets or sets the width of one character cell, in screen pixels, as the Usable Area and
        /// Character Sets query replies report it (AW and SDW, p. 6-87, 6-22).
        /// </summary>
        /// <remarks>
        /// The renderer knows its font and should set this. The default of 9 is an ASSUMPTION (the
        /// usual 3278 cell width), not a measured value.
        /// </remarks>
        public int CellWidth { get; set; } = 9;

        /// <summary>
        /// Gets or sets the height of one character cell, in screen pixels (AH and SDH, p. 6-87, 6-22).
        /// </summary>
        /// <remarks>
        /// The renderer should set this. The default of 16 is an ASSUMPTION, not a measured value.
        /// </remarks>
        public int CellHeight { get; set; } = 16;

        /// <summary>
        /// Gets or sets the largest file transfer structured field transmission, in bytes, both
        /// ways; 0 switches file transfer off.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Above 0, the Query Reply includes DDM (X'95') with this value as LIMIN and LIMOUT
        /// (GA23-0059-4 p. 6-37), which is what makes IND$FILE choose DFT mode, and X'D0' fields go
        /// to <see cref="FileTransferField"/>. A WSF whose X'D0' fields add up to more than this is
        /// rejected (p. 6-37).
        /// </para>
        /// <para>
        /// 0 is never sent as a limit, because to the host it means "no limit" (p. 6-37).
        /// </para>
        /// </remarks>
        public int FileTransferLimit
        {
            get => _fileTransferLimit;
            set
            {
                if (value < 0 || value > 0x7FFF)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "The file transfer limit is 0 (off) or 1 to 32767 bytes.");
                }

                _fileTransferLimit = value;
            }
        }

        /// <summary>
        /// Gets the inbound reply mode the host last set with Set Reply Mode.
        /// </summary>
        public ReplyMode ReplyMode => _replyMode;

        /// <summary>
        /// Gets or sets the base code page that turns typed characters into bytes, and that the
        /// Character Sets query reply names. Default 037.
        /// </summary>
        /// <remarks>
        /// Set it before connecting: the host learns the page from the query reply, and bytes
        /// already in the buffer stay as they are. Only base pages are allowed, never
        /// <see cref="CodePage.Cp310"/>, which is the Graphic Escape set beside every base page.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when the value is <see cref="CodePage.Cp310"/>.
        /// </exception>
        public CodePage CodePage
        {
            get => _codePage;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (value == CodePage.Cp310)
                {
                    throw new ArgumentException("Code page 310 is the Graphic Escape set, not a base page.", nameof(value));
                }

                _codePage = value;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the screen has at least one field (a "formatted" screen).
        /// </summary>
        public bool IsFormatted
        {
            get
            {
                for (int i = 0; i < _isFieldAttribute.Length; i++)
                {
                    if (_isFieldAttribute[i])
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// The byte stored at an address: a character (EBCDIC), a stored control, or - when
        /// <see cref="IsFieldAttribute"/> is true - the field attribute.
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The byte.
        /// </returns>
        public byte GetByte(int address) => _buffer[address];

        /// <summary>
        /// True when the position holds a field attribute (shown as a blank, p. 4-13).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// True for a field attribute position.
        /// </returns>
        public bool IsFieldAttribute(int address) => _isFieldAttribute[address];

        /// <summary>
        /// True when the character came in with Graphic Escape, so it is from the alternate set
        /// (code page 310) and not the base code page (p. 4-9).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// True for a GE character.
        /// </returns>
        public bool IsGraphicEscape(int address) => _graphicEscape[address];

        /// <summary>
        /// The address of the field attribute that governs a position, or -1 on an unformatted
        /// screen. Searches backwards and wraps, because a field may run past the end of the buffer.
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
                if (_isFieldAttribute[a])
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
        /// The colour value (GA23-0061-2 p. 2-10 lists X'F1'-X'F7').
        /// </returns>
        public byte GetFieldColor(int fieldAttributeAddress) => _fieldColor[fieldAttributeAddress];

        /// <summary>
        /// The foreground colour character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The colour value.
        /// </returns>
        public byte GetCharacterColor(int address) => _charColor[address];

        /// <summary>
        /// The extended highlighting of the field at a field attribute address.
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The highlight value (p. 4-18).
        /// </returns>
        public byte GetFieldHighlight(int fieldAttributeAddress) => _fieldHighlight[fieldAttributeAddress];

        /// <summary>
        /// The highlighting character attribute of one position.
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The highlight value.
        /// </returns>
        public byte GetCharacterHighlight(int address) => _charHighlight[address];

        /// <summary>
        /// The character set of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <remarks>
        /// X'F1' is the Graphic Escape set, code page 310: the Character Sets query reply gives it
        /// local ID X'F1' (p. 6-25).
        /// </remarks>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The character set's local ID (p. 4-19).
        /// </returns>
        public byte GetFieldCharacterSet(int fieldAttributeAddress) => _fieldCharset[fieldAttributeAddress];

        /// <summary>
        /// The character set character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The character set's local ID.
        /// </returns>
        public byte GetCharacterCharacterSet(int address) => _charCharset[address];

        /// <summary>
        /// The background colour of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The colour value (p. 4-18).
        /// </returns>
        public byte GetFieldBackground(int fieldAttributeAddress) => _fieldBackground[fieldAttributeAddress];

        /// <summary>
        /// The background colour character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The colour value.
        /// </returns>
        public byte GetCharacterBackground(int address) => _charBackground[address];

        /// <summary>
        /// The transparency of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The transparency value (p. 4-20).
        /// </returns>
        public byte GetFieldTransparency(int fieldAttributeAddress) => _fieldTransparency[fieldAttributeAddress];

        /// <summary>
        /// The transparency character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The transparency value.
        /// </returns>
        public byte GetCharacterTransparency(int address) => _charTransparency[address];

        /// <summary>
        /// The field validation attribute of the field at a field attribute address (p. 4-21).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The validation bits: see <see cref="FieldValidation"/>.
        /// </returns>
        public byte GetFieldValidation(int fieldAttributeAddress) => _fieldValidation[fieldAttributeAddress];

        /// <summary>
        /// The field outlining attribute of the field at a field attribute address (p. 4-20).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The outlining bits: see <see cref="FieldOutlining"/>.
        /// </returns>
        public byte GetFieldOutlining(int fieldAttributeAddress) => _fieldOutlining[fieldAttributeAddress];

        /// <summary>
        /// Applies one complete outbound record (telnet framing already removed).
        /// </summary>
        /// <param name="record">
        /// The record: command byte first.
        /// </param>
        /// <returns>
        /// False when <see cref="ProtocolError"/> was raised for this record. A TN3270E session
        /// that was asked for a response answers negatively then (RFC 2355 section 10.4.1).
        /// </returns>
        public bool ProcessOutbound(ReadOnlySpan<byte> record)
        {
            _failed = false;
            if (record.Length == 0)
            {
                return true;
            }

            byte command = record[0];

            // Any outbound transmission other than a read command acknowledges the inbound record
            // kept for a retry (p. 3-23: the acknowledgment is "any outbound transmission except
            // one with a read command"), so a later Read Modified is an ordinary one again.
            if (command != Command3270.ReadModified && command != Command3270.ReadModifiedAll && command != Command3270.ReadBuffer)
            {
                _retryLength = 0;
            }

            switch (command)
            {
                case Command3270.Write:
                    Write(record.Slice(1), erase: false, alternate: false);
                    AcknowledgeQueryUnlessFailed();
                    break;

                case Command3270.EraseWrite:
                    Write(record.Slice(1), erase: true, alternate: false);
                    AcknowledgeQueryUnlessFailed();
                    break;

                case Command3270.EraseWriteAlternate:
                    Write(record.Slice(1), erase: true, alternate: true);
                    AcknowledgeQueryUnlessFailed();
                    break;

                case Command3270.EraseAllUnprotected:
                    // EAU acknowledges a query (p. 10-5), and it restores the keyboard anyway.
                    EraseAllUnprotected();
                    break;

                case Command3270.ReadBuffer:
                    SendReadBuffer(_lastAid);
                    break;

                case Command3270.ReadModified:
                    // In a retry state "the read modified command performs a retry of the inbound
                    // operation" (p. 3-14, 3-17); only the Read Modified command does, not Read
                    // Modified All, Read Buffer or a Read Partition (p. 3-14).
                    if (_retryLength > 0)
                    {
                        InboundRecord?.Invoke(_retryRecord.AsSpan(0, _retryLength));
                        break;
                    }

                    SendReadModified(_lastAid, all: false);
                    break;

                case Command3270.ReadModifiedAll:
                    SendReadModified(_lastAid, all: true);
                    break;

                case Command3270.WriteStructuredField:
                    // A WSF acknowledges a query "regardless of an error being detected in the
                    // accompanying structured fields" (p. 3-23, 10-5), so the lock ends before the
                    // fields run; a Read Partition Query among them locks it again when it replies.
                    if (Lock == KeyboardLock.QueryReplySent)
                    {
                        Lock = KeyboardLock.None;
                    }

                    WriteStructuredField(record.Slice(1));
                    break;

                default:
                    Error("Unknown command X'" + command.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' (GA23-0059-4 p. 3-2).");
                    break;
            }

            return !_failed;
        }

        private void AcknowledgeQueryUnlessFailed()
        {
            // An EW, EWA or Write acknowledges a query "with or without a WCC or data", but not when
            // "an error is detected in the data" (p. 3-23, 10-5). Keyboard restore is not needed;
            // with it, Write has already unlocked.
            if (!_failed && Lock == KeyboardLock.QueryReplySent)
            {
                Lock = KeyboardLock.None;
            }
        }

        /// <summary>
        /// The operator types a character at the cursor (GA27-2890-4 p. 3-25; GA23-0061-2 p. C-3).
        /// </summary>
        /// <param name="c">
        /// The typed character.
        /// </param>
        /// <returns>
        /// False when the key was refused: keyboard locked, character not in the code page, the
        /// cursor in a protected field or on a field attribute, or no room in insert mode (the last
        /// two lock the keyboard).
        /// </returns>
        public bool Type(char c)
        {
            if (Lock != KeyboardLock.None)
            {
                // A real 3278 has no type-ahead; keys pressed while locked are refused
                // (KEYBOARD.md section 5; decided 22 November 2026).
                return false;
            }

            if (!_codePage.TryFromUnicode(c, out byte ebcdic))
            {
                return false;
            }

            return TypeByte(ebcdic, tabAfter: false);
        }

        /// <summary>
        /// The DUP key: stores the DUP code X'1C', sets the MDT and tabs to the next input field
        /// (GA23-0061-2 p. C-6, C-7).
        /// </summary>
        /// <returns>
        /// False when refused: keyboard locked, or the cursor in a protected field or on a field
        /// attribute (which locks the keyboard).
        /// </returns>
        public bool Dup()
        {
            return Lock == KeyboardLock.None && TypeByte(FormatControl.Dup, tabAfter: true);
        }

        /// <summary>
        /// The FIELD MARK key: stores the FM code X'1E' and sets the MDT; the cursor moves on as
        /// after any character (GA23-0061-2 p. C-7).
        /// </summary>
        /// <returns>
        /// False when refused, as for <see cref="Dup"/>.
        /// </returns>
        public bool FieldMark()
        {
            return Lock == KeyboardLock.None && TypeByte(FormatControl.FieldMark, tabAfter: false);
        }

        /// <summary>
        /// The Insert key: switches insert mode on, or off again. Refused while the keyboard is
        /// locked.
        /// </summary>
        public void ToggleInsert()
        {
            if (Lock == KeyboardLock.None)
            {
                InsertMode = !InsertMode;
            }
        }

        /// <summary>
        /// The Delete key: removes the character at the cursor; the rest of the field on the same
        /// row moves one place left and a null fills the end. The cursor stays (GA23-0061-2 p. C-6).
        /// </summary>
        /// <returns>
        /// False when refused: keyboard locked, or the cursor in a protected field or on a field
        /// attribute (which locks the keyboard).
        /// </returns>
        public bool Delete()
        {
            if (Lock != KeyboardLock.None || !CheckInputPosition(out int fa))
            {
                return false;
            }

            // The end is the last position before the next field attribute, never past the row.
            int rowEnd = ((CursorAddress / Columns) * Columns) + Columns - 1;
            int end = CursorAddress;
            while (end < rowEnd && !_isFieldAttribute[end + 1])
            {
                end++;
            }

            for (int a = CursorAddress; a < end; a++)
            {
                MoveCell(a + 1, a);
            }

            StoreCharacter(end, FormatControl.Null, graphicEscape: false, useSa: false);
            SetMdt(fa);
            Prime(fa);
            return true;
        }

        /// <summary>
        /// Erase left, the PC Backspace of wc3270: the cursor moves one place left and the
        /// character there is deleted as <see cref="Delete"/> deletes it, so the rest of the field
        /// moves one place left. At the first position of a field nothing happens.
        /// </summary>
        /// <remarks>
        /// IBM's 3278 had no such key: its Backspace only moved the cursor (GA27-2890-4 p. 3-31).
        /// This follows x3270's Erase action, step for step (Common\kybd.c, do_erase, read 23
        /// November 2026): refused in a protected field or on a field attribute; nothing when the
        /// position to the left is the field's attribute; otherwise left one, then delete. wc3270
        /// gives the PC Backspace key this action in its built-in key map (Common\fb-c3270, the
        /// _WIN32 part: "Key BACK: Erase()"). The DBCS steps of do_erase are left out: this
        /// display has no DBCS.
        /// </remarks>
        /// <returns>
        /// False when refused: keyboard locked, or the cursor in a protected field or on a field
        /// attribute (which locks the keyboard). True otherwise, also when nothing was erased.
        /// </returns>
        public bool Erase()
        {
            if (Lock != KeyboardLock.None || !CheckInputPosition(out int fa))
            {
                return false;
            }

            int left = CursorAddress == 0 ? Size - 1 : CursorAddress - 1;
            if (fa >= 0 && left == fa)
            {
                return true;
            }

            MoveCursor(left);
            return Delete();
        }

        /// <summary>
        /// The ERASE EOF key: nulls from the cursor to the end of the field, or on an unformatted
        /// screen to the end of the screen. The cursor stays and the MDT is set (GA23-0061-2 p. C-4).
        /// </summary>
        /// <returns>
        /// False when refused: keyboard locked, or the cursor in a protected field or on a field
        /// attribute (which locks the keyboard).
        /// </returns>
        public bool EraseEndOfField()
        {
            if (Lock != KeyboardLock.None || !CheckInputPosition(out int fa))
            {
                return false;
            }

            if (fa < 0)
            {
                // Unformatted: to the last position on the bottom line (GA27-2890-4 p. 3-35).
                for (int a = CursorAddress; a < Size; a++)
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                }

                return true;
            }

            // It may wrap from the end of the screen to the end of the field.
            int p = CursorAddress;
            while (!_isFieldAttribute[p])
            {
                StoreCharacter(p, FormatControl.Null, graphicEscape: false, useSa: false);
                p = Next(p);
            }

            SetMdt(fa);
            Prime(fa);
            return true;
        }

        /// <summary>
        /// The ERASE INPUT key: nulls in every unprotected field, their MDTs reset, the cursor to
        /// the first input position (GA23-0061-2 p. C-5).
        /// </summary>
        /// <remarks>
        /// With only protected fields nothing is erased and the cursor goes to 0; with no fields at
        /// all the whole screen is erased and the cursor goes to 0.
        /// </remarks>
        /// <returns>
        /// False when refused because the keyboard is locked.
        /// </returns>
        public bool EraseInput()
        {
            if (Lock != KeyboardLock.None)
            {
                return false;
            }

            // An erase-input keystroke unprimes a mandatory-fill field (p. 4-21).
            _primedField = -1;
            int size = Size;
            int first = GetFieldAttributeFor(size - 1);
            if (first < 0)
            {
                for (int a = 0; a < size; a++)
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                }

                CursorAddress = 0;
                return true;
            }

            bool isProtected = FieldAttribute.IsProtected(_buffer[first]);
            bool any = false;
            for (int a = 0; a < size; a++)
            {
                if (_isFieldAttribute[a])
                {
                    isProtected = FieldAttribute.IsProtected(_buffer[a]);
                    if (!isProtected)
                    {
                        any = true;
                        _buffer[a] = (byte)(_buffer[a] & ~FieldAttribute.Mdt);
                    }
                }
                else if (!isProtected)
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                }
            }

            CursorAddress = any ? NextInputPosition(size - 1) : 0;
            return true;
        }

        /// <summary>
        /// Back Tab: the first position of the current input field, or of the previous one when the
        /// cursor is already there, on an attribute or in a protected field; no input field: 0
        /// (GA23-0061-2 p. C-4).
        /// </summary>
        public void BackTab()
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            int size = Size;
            int fa = GetFieldAttributeFor(CursorAddress);
            if (fa >= 0 && !_isFieldAttribute[CursorAddress] && !FieldAttribute.IsProtected(_buffer[fa]) && Next(fa) != CursorAddress)
            {
                OperatorMove(Next(fa));
                return;
            }

            // Search backwards from the position before the current field start.
            int a = CursorAddress;
            for (int n = 0; n < size; n++)
            {
                a = a == 0 ? size - 1 : a - 1;
                if (_isFieldAttribute[a] && !FieldAttribute.IsProtected(_buffer[a]) && Next(a) != CursorAddress && !_isFieldAttribute[Next(a)])
                {
                    OperatorMove(Next(a));
                    return;
                }
            }

            OperatorMove(0);
        }

        /// <summary>
        /// New Line: the first input position on the next line or after; no input field: 0; no
        /// fields at all: the first position of the next line (GA23-0061-2 p. C-4).
        /// </summary>
        public void NewLine()
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            int size = Size;
            int lineStart = ((CursorAddress / Columns) + 1) * Columns;
            if (lineStart >= size)
            {
                lineStart = 0;
            }

            if (!IsFormatted)
            {
                OperatorMove(lineStart);
                return;
            }

            int a = lineStart;
            for (int n = 0; n < size; n++)
            {
                int fa = GetFieldAttributeFor(a);
                if (!_isFieldAttribute[a] && fa >= 0 && !FieldAttribute.IsProtected(_buffer[fa]))
                {
                    OperatorMove(a);
                    return;
                }

                a = Next(a);
            }

            OperatorMove(0);
        }

        /// <summary>
        /// The arrow keys and Backspace: one position, wrapping to the row above or below at the
        /// edges and from top to bottom in the same column (GA23-0061-2 p. C-3). The MDT is not
        /// touched.
        /// </summary>
        /// <param name="columns">
        /// Positions to the right, negative to the left (ALT gives two).
        /// </param>
        /// <param name="rows">
        /// Rows down, negative up.
        /// </param>
        public void MoveCursorBy(int columns, int rows)
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            int size = Size;
            int a = CursorAddress + columns;
            a = ((a % size) + size) % size;
            int row = (a / Columns) + rows;
            row = ((row % Rows) + Rows) % Rows;
            OperatorMove((row * Columns) + (a % Columns));
        }

        /// <summary>
        /// Tab: first character of the next input field; none, or an unformatted screen: address 0
        /// (GA27-2890-4 p. 3-28).
        /// </summary>
        public void Tab()
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            OperatorMove(NextInputPosition(CursorAddress));
        }

        /// <summary>
        /// Home: the first input position on the screen; none: address 0 (GA27-2890-4 p. 3-30).
        /// </summary>
        public void Home()
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            int last = Size - 1;
            OperatorMove(NextInputPosition(last));
        }

        /// <summary>
        /// End (Field End): the cursor goes just after the last character in its field that is not
        /// a blank or a null, or to the field's first position when it holds none.
        /// </summary>
        /// <remarks>
        /// The 3278 keyboard has no such key; the 3270 PC and x3270 do. Taken from x3270
        /// (Common\kybd.c, FieldEnd_action): nothing happens on an unformatted screen, on a field
        /// attribute or in a protected field, and when the last character is in the field's last
        /// position the cursor stays on it rather than landing on the next attribute.
        /// </remarks>
        public void FieldEnd()
        {
            if (Lock != KeyboardLock.None || !IsFormatted)
            {
                return;
            }

            int fa = GetFieldAttributeFor(CursorAddress);
            if (fa == CursorAddress || FieldAttribute.IsProtected(_buffer[fa]))
            {
                return;
            }

            int lastNonBlank = -1;
            for (int a = Next(fa); !_isFieldAttribute[a]; a = Next(a))
            {
                byte c = _buffer[a];
                if (c != FormatControl.Null && c != EbcdicSpace)
                {
                    lastNonBlank = a;
                }
            }

            int target = lastNonBlank < 0 ? Next(fa) : Next(lastNonBlank);
            if (lastNonBlank >= 0 && _isFieldAttribute[target])
            {
                target = lastNonBlank;
            }

            OperatorMove(target);
        }

        /// <summary>
        /// Cursor Select: selects the selector-pen detectable field the cursor is in, by its
        /// designator character, the first character of the field.
        /// </summary>
        /// <remarks>
        /// GA23-0059-4 p. 4-22 to 4-24 and x3270 (Common\kybd.c, lightpen_select):
        ///  - "?" becomes ">" and the field's MDT is set (selected).
        ///  - ">" becomes "?" and the MDT is cleared (deselected).
        ///  - a space or a null: MDT set, then a selector-pen attention (AID X'7E'), which sends the
        ///    modified fields' addresses without their data (p. 3-12).
        ///  - an ampersand: MDT set, then an Enter.
        /// A field that is not detectable (display bits other than 01 or 10), or any other
        /// designator, locks the keyboard with Go Elsewhere, as GA27-2890-4 p. 2-22 says for "a
        /// cursor select operation when the cursor is not in a valid cursor select field"; x3270
        /// only rings the bell there. RetroIO follows IBM, as it does for typing on a protected
        /// field.
        /// </remarks>
        /// <returns>
        /// True when the field was selected, deselected or sent; false when refused.
        /// </returns>
        public bool CursorSelect()
        {
            if (Lock != KeyboardLock.None)
            {
                return false;
            }

            int fa = GetFieldAttributeFor(CursorAddress);
            byte display = fa >= 0 ? (byte)(_buffer[fa] & FieldAttribute.DisplayMask) : (byte)0;
            if (fa < 0 || (display != 0x04 && display != 0x08))
            {
                Lock = KeyboardLock.OperatorError;
                return false;
            }

            int designator = Next(fa);
            switch (_buffer[designator])
            {
                case EbcdicQuestion:
                    _buffer[designator] = EbcdicGreater;
                    _buffer[fa] = (byte)(_buffer[fa] | FieldAttribute.Mdt);
                    return true;
                case EbcdicGreater:
                    _buffer[designator] = EbcdicQuestion;
                    _buffer[fa] = (byte)(_buffer[fa] & ~FieldAttribute.Mdt);
                    return true;
                case EbcdicSpace:
                case FormatControl.Null:
                    _buffer[fa] = (byte)(_buffer[fa] | FieldAttribute.Mdt);
                    return Attention(Aid.SelectorPen);
                case EbcdicAmpersand:
                    _buffer[fa] = (byte)(_buffer[fa] | FieldAttribute.Mdt);
                    return Attention(Aid.Enter);
                default:
                    Lock = KeyboardLock.OperatorError;
                    return false;
            }
        }

        /// <summary>
        /// Sys Req (Test Req on the 3278): sends a test request read and waits for the host.
        /// </summary>
        /// <remarks>
        /// GA23-0059-4 p. 9-3: the heading is SOH % / STX (X'01' X'6C' X'61' X'02') instead of the
        /// AID and cursor address; the rest is a Read Modified's data (every modified field, nulls
        /// left out; all data on an unformatted screen). The manual's non-SNA layout ends with ETX;
        /// x3270 (Common\ctlr.c, ctlr_read_modified, case AID_SYSREQ) sends no ETX over TN3270,
        /// and neither does RetroIO. Under TN3270E Sys Req is a telnet-level request instead
        /// (RFC 2355), not built.
        /// </remarks>
        /// <returns>
        /// True when sent; false when the keyboard is locked.
        /// </returns>
        public bool SysReq() => Attention(Aid.SysReq);

        /// <summary>
        /// The data-stream half of Attn in plain TN3270: the PA1 short read, sent whatever the
        /// keyboard lock is, and the lock left as it was.
        /// </summary>
        /// <remarks>
        /// Taken from x3270 (Common\kybd.c, Attn_action, the IN_3270 case, commented "This is
        /// what PCOMM does in plain TN3270 mode: PA1, then TELNET BREAK"): ctlr_read_modified with
        /// AID_PA1, then net_break. The telnet break is the connection's job, not the display's;
        /// the caller sends it after this. Attn works while the keyboard is locked, because
        /// interrupting a host that does not answer is what it is for. No IBM manual held here
        /// describes Attn on a TN3270 connection, so x3270 is the only source.
        /// </remarks>
        public void Attn() => SendReadModified(Aid.PA1, all: false);

        /// <summary>
        /// Refuses a key whose function is not available now: the keyboard locks with Minus
        /// Function ("X -f") until Reset. Nothing is sent.
        /// </summary>
        /// <remarks>
        /// See <see cref="KeyboardLock.MinusFunction"/> for where this comes from.
        /// </remarks>
        public void RefuseFunction() => Lock = KeyboardLock.MinusFunction;

        /// <summary>
        /// Pastes text the way x3270's normal paste does: typed character by character, with a
        /// line break pressing New Line (the first input field on a following row).
        /// </summary>
        /// <remarks>
        /// From x3270 (Common\kybd.c, emulate_uinput with pasting set, overlay and margined paste
        /// off, which are its defaults):
        ///  - a line feed presses New Line, unless the text just wrapped onto a new row by itself.
        ///  - a carriage return does nothing, so CR LF counts once.
        ///  - tab presses Tab, backspace moves left, form feed types a space.
        ///  - everything else is typed, with the auto-skip Type already does.
        /// The paste stops when the keyboard locks (a character on a protected field is an
        /// operator error, as when typing it) and when the cursor wraps past the end of the screen
        /// back above where it started. The rest of the text is dropped, as x3270 drops it.
        /// </remarks>
        /// <param name="text">
        /// The pasted text.
        /// </param>
        public void Paste(ReadOnlySpan<char> text)
        {
            int columns = Columns;
            int start = CursorAddress;
            int lastAddress = start;
            int lastRow = start / columns;
            bool justWrapped = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (Lock != KeyboardLock.None || CursorAddress < start)
                {
                    return;
                }

                if (lastAddress != CursorAddress)
                {
                    lastAddress = CursorAddress;
                    int row = CursorAddress / columns;
                    justWrapped = row != lastRow;
                    lastRow = row;
                }

                char c = text[i];
                switch (c)
                {
                    case '\n':
                        if (!justWrapped)
                        {
                            NewLine();
                        }

                        lastRow = CursorAddress / columns;
                        justWrapped = false;
                        break;
                    case '\r':
                        break;
                    case '\t':
                        Tab();
                        break;
                    case '\b':
                        MoveCursorBy(-1, 0);
                        break;
                    case '\f':
                        Type(' ');
                        break;
                    default:
                        Type(c);
                        break;
                }
            }
        }

        /// <summary>
        /// Moves the cursor to an address (mouse click, arrow keys). Refused while locked.
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        public void MoveCursor(int address)
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            if (address < 0 || address >= Size)
            {
                throw new ArgumentOutOfRangeException(nameof(address));
            }

            OperatorMove(address);
        }

        /// <summary>
        /// Reset key: clears an operator error or a System Lock and ends insert mode. It does not
        /// clear waiting for the host (GA23-0059-4 p. 3-20; GA27-2890-4 p. 2-21 and 3-40;
        /// GA23-0061-2 p. C-6).
        /// </summary>
        public void Reset()
        {
            if (Lock == KeyboardLock.SystemLock || Lock == KeyboardLock.OperatorError || Lock == KeyboardLock.TooMuchData || Lock == KeyboardLock.MandatoryFill || Lock == KeyboardLock.MandatoryEntry || Lock == KeyboardLock.MinusFunction)
            {
                Lock = KeyboardLock.None;
            }

            if (Lock == KeyboardLock.None)
            {
                InsertMode = false;
            }
        }

        /// <summary>
        /// An attention key: Enter, PF1-PF24, PA1-PA3 or Clear. Sends the reply to the host and
        /// locks the keyboard until the host restores it (GA23-0059-4 p. 3-8, 3-12, 3-13).
        /// </summary>
        /// <param name="aid">
        /// The AID byte, from <see cref="Aid"/>.
        /// </param>
        /// <returns>
        /// False when refused because the keyboard is locked.
        /// </returns>
        public bool Attention(byte aid)
        {
            if (Lock != KeyboardLock.None)
            {
                return false;
            }

            if (aid == Aid.Clear)
            {
                // Clear: every field attribute becomes a null - no fields left (p. 4-13), the buffer
                // and all extended attributes are cleared, the screen goes back to the default size
                // and the cursor to 0 (GA23-0061-2 p. C-8). Then the short read: the AID alone.
                Resize(_defaultRows, _defaultColumns);
                CursorAddress = 0;

                // And the inbound reply mode goes back to field mode: GA23-0059-4 Table C-1
                // (p. C-1), row "CLEAR Key LULU", Inbound Reply Mode = R. x3270 does not do this
                // (it resets the mode only on connect and on an Erase/Write with WCC reset), but the
                // manual is the authority. A TN3270 host session is the LU-LU case; the "Unowned"
                // row says implementation defined.
                _replyMode = ReplyMode.Field;
                _replyTypes = Array.Empty<byte>();
            }

            if (aid != Aid.Clear && !Aid.IsShortRead(aid))
            {
                // An enter action with the cursor in a primed mandatory-fill field validates it
                // first (p. 4-22 note 4); then no data goes while an unprotected mandatory-entry
                // field has its MDT off, and the cursor goes there (p. 4-22).
                if (!ValidateFill())
                {
                    return false;
                }

                int empty = FirstEmptyMandatoryEntryField();
                if (empty >= 0)
                {
                    CursorAddress = Next(empty);
                    Lock = KeyboardLock.MandatoryEntry;
                    return false;
                }
            }

            // Every key that talks to the host ends insert mode (GA23-0061-2 p. C-6).
            InsertMode = false;
            _lastAid = aid;
            Lock = KeyboardLock.WaitingForHost;
            SendReadModified(aid, all: false);
            return true;
        }

        /// <summary>
        /// Sends one structured field to the host as its own inbound record: AID X'88', then the
        /// field (GA23-0059-4 p. 3-10).
        /// </summary>
        /// <remarks>
        /// For the replies of <see cref="IndFileTransfer"/>; any other inbound structured field a
        /// caller builds goes the same way.
        /// </remarks>
        /// <param name="field">
        /// The whole structured field, its two length bytes first.
        /// </param>
        public void SendStructuredFieldReply(ReadOnlySpan<byte> field) => SendStructuredFieldReply(field, retry: false);

        /// <summary>
        /// Sends one structured field to the host as its own inbound record, as
        /// <see cref="SendStructuredFieldReply(ReadOnlySpan{byte})"/>, and when asked keeps it so
        /// that a Read Modified command before the host's next other transmission sends the very
        /// same record again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For the file data <see cref="IndFileTransfer"/> sends on the host's Get. TK5's IND$FILE
        /// answers every full upload block with a Read Modified command (X'F6') and then expects
        /// the block a second time; a terminal that answers with an ordinary Read Modified loses
        /// that block without any error (measured 24 November 2026 on TK5: RetroTerm sent a
        /// 16384-byte block, answered the X'F6' with AID X'88' alone, and the data set held only
        /// the second block; s3270 sent each block again and the file came back whole).
        /// </para>
        /// <para>
        /// This is the retry of GA23-0059-4 p. 3-14 to 3-18: a Read Modified command in a retry
        /// state repeats the last inbound operation, and any outbound transmission other than a
        /// read command acknowledges it (p. 3-23). The manual names only an enter action and a
        /// Read Partition as starting a retry state; IBM's IND$FILE description does not mention
        /// this exchange. x3270 does the same for upload data only (Common/ft_dft.c, the
        /// saved upload buffer sent again on a Read Modified).
        /// </para>
        /// </remarks>
        /// <param name="field">
        /// The whole structured field, its two length bytes first.
        /// </param>
        /// <param name="retry">
        /// True to keep the record for a retry; false sends it without, and ends any retry kept
        /// from before.
        /// </param>
        public void SendStructuredFieldReply(ReadOnlySpan<byte> field, bool retry)
        {
            _inbound.Clear();
            AppendByte(Aid.StructuredField);
            AppendBytes(field);
            if (retry)
            {
                ReadOnlySpan<byte> record = _inbound.WrittenSpan;
                if (_retryRecord.Length < record.Length)
                {
                    _retryRecord = new byte[record.Length];
                }

                record.CopyTo(_retryRecord);
            }

            // Send ends any retry kept from before; this record's is set after it.
            int length = _inbound.WrittenCount;
            Send();
            _retryLength = retry ? length : 0;
        }

        private void OperatorMove(int address)
        {
            // Moving the cursor out of a primed mandatory-fill field validates it first; on failure
            // the cursor stays in the field (p. 4-21).
            if (_primedField >= 0 && GetFieldAttributeFor(address) != _primedField && !ValidateFill())
            {
                return;
            }

            CursorAddress = address;
        }

        private void Prime(int fieldAttribute)
        {
            // An unprotected mandatory-fill field is primed by a data key, Delete or Erase EOF in
            // it (p. 4-21). Protected fields cannot get here.
            if (fieldAttribute >= 0 && (_fieldValidation[fieldAttribute] & FieldValidation.MandatoryFill) != 0)
            {
                _primedField = fieldAttribute;
            }
        }

        private bool ValidateFill()
        {
            // A DUP anywhere in the field passes; otherwise any null fails (p. 4-21).
            if (_primedField < 0)
            {
                return true;
            }

            bool hasNull = false;
            int a = Next(_primedField);
            int guard = Size;
            while (!_isFieldAttribute[a] && guard-- > 0)
            {
                if (_buffer[a] == FormatControl.Dup)
                {
                    _primedField = -1;
                    return true;
                }

                hasNull |= _buffer[a] == FormatControl.Null;
                a = Next(a);
            }

            if (hasNull)
            {
                Lock = KeyboardLock.MandatoryFill;
                return false;
            }

            _primedField = -1;
            return true;
        }

        private int FirstEmptyMandatoryEntryField()
        {
            // The first unprotected mandatory-entry field with its MDT off, in buffer order (p. 4-22).
            int size = Size;
            for (int a = 0; a < size; a++)
            {
                if (_isFieldAttribute[a]
                    && (_fieldValidation[a] & FieldValidation.MandatoryEntry) != 0
                    && !FieldAttribute.IsProtected(_buffer[a])
                    && (_buffer[a] & FieldAttribute.Mdt) == 0)
                {
                    return a;
                }
            }

            return -1;
        }

        private bool TypeByte(byte ebcdic, bool tabAfter)
        {
            if (!CheckInputPosition(out int fa))
            {
                return false;
            }

            if (InsertMode && _buffer[CursorAddress] != FormatControl.Null)
            {
                // Insert (GA23-0061-2 p. C-5): the first null at or after the cursor in the field
                // takes the shift; characters after that null stay. No null: Too Much Data. The
                // operator guide's "one less than the blanks" (GA27-2890-4 p. 3-42) is a rule of
                // thumb for the operator; the control unit's rule is the null count, used here.
                int size = Size;
                int hole = -1;
                int p = CursorAddress;
                for (int n = 0; n < size && !_isFieldAttribute[p]; n++)
                {
                    if (_buffer[p] == FormatControl.Null)
                    {
                        hole = p;
                        break;
                    }

                    p = Next(p);
                }

                if (hole < 0)
                {
                    Lock = KeyboardLock.TooMuchData;
                    return false;
                }

                // Shift right, the last row's character moving to the next row; character
                // attributes move with the characters (p. C-5).
                while (hole != CursorAddress)
                {
                    int from = hole == 0 ? size - 1 : hole - 1;
                    MoveCell(from, hole);
                    hole = from;
                }
            }

            Prime(fa);

            // Numeric fields: with a plain typewriter keyboard the 3274 accepts any character
            // (GA23-0061-2 Figure 6-2, KEYBOARD.md 4.4), so no check is made here.
            SetMdt(fa);

            // The character attributes of a typed position are X'00' (p. C-3).
            StoreCharacter(CursorAddress, ebcdic, graphicEscape: false, useSa: false);
            if (tabAfter)
            {
                // DUP: "a Tab key operation" (p. C-6). On an unformatted screen Tab gives 0.
                OperatorMove(NextInputPosition(CursorAddress));
                return true;
            }

            int next = Next(CursorAddress);

            // Never leave the cursor on a field attribute: skip an autoskip field to the next input
            // field, otherwise step past the attribute. The IBM rule in KEYBOARD.md 4.2 covers only
            // the autoskip case; the step past a plain attribute follows x3270 kybd.c
            // ("Implement auto-skip"), read, not copied.
            int guard = Size;
            while (_isFieldAttribute[next] && guard-- > 0)
            {
                next = FieldAttribute.IsAutoSkip(_buffer[next]) ? NextInputPosition(next) : Next(next);
            }

            OperatorMove(next);
            return true;
        }

        private bool CheckInputPosition(out int fieldAttribute)
        {
            // Keys that change data need an alphanumeric position in an unprotected field; on a
            // field attribute or in a protected field they lock the keyboard ("Go Elsewhere",
            // GA27-2890-4 p. 2-21; GA23-0061-2 p. C-3 to C-7). An unformatted screen is all input.
            fieldAttribute = GetFieldAttributeFor(CursorAddress);
            if (fieldAttribute >= 0 && (_isFieldAttribute[CursorAddress] || FieldAttribute.IsProtected(_buffer[fieldAttribute])))
            {
                Lock = KeyboardLock.OperatorError;
                return false;
            }

            return true;
        }

        private void SetMdt(int fieldAttribute)
        {
            if (fieldAttribute >= 0)
            {
                _buffer[fieldAttribute] = (byte)(_buffer[fieldAttribute] | FieldAttribute.Mdt);
            }
        }

        private void MoveCell(int from, int to)
        {
            // A character and its character attributes, never a field attribute.
            _buffer[to] = _buffer[from];
            _graphicEscape[to] = _graphicEscape[from];
            _charHighlight[to] = _charHighlight[from];
            _charColor[to] = _charColor[from];
            _charCharset[to] = _charCharset[from];
            _charBackground[to] = _charBackground[from];
            _charTransparency[to] = _charTransparency[from];
        }

        private void Write(ReadOnlySpan<byte> data, bool erase, bool alternate, bool fromStructuredField = false)
        {
            // "A write operation to that partition" unprimes a mandatory-fill field (p. 4-21).
            _primedField = -1;

            // "A write command with no WCC and no data is a no-op" (p. 3-2); an EW with no WCC is a
            // no-op too, nothing is erased (p. 3-6).
            if (data.Length == 0)
            {
                return;
            }

            byte wcc = data[0];
            if (fromStructuredField && !erase)
            {
                // A Write inside Outbound 3270DS ignores WCC bits 0-3 (p. 5-47).
                wcc = (byte)(wcc & 0x0F);
            }

            if (erase)
            {
                // Erase/Write steps (p. 3-6): screen size, nulls, attributes to X'00', cursor to 0.
                // Inside Outbound 3270DS the size does not change (p. 5-47).
                if (fromStructuredField)
                {
                    Resize(Rows, Columns);
                }
                else if (alternate)
                {
                    Resize(_alternateRows, _alternateColumns);
                }
                else
                {
                    Resize(_defaultRows, _defaultColumns);
                }

                CursorAddress = 0;

                if ((wcc & Wcc.Reset) != 0)
                {
                    // EW or EWA with the WCC reset bit puts the reply mode back to Field (p. 5-62).
                    _replyMode = ReplyMode.Field;
                    _replyTypes = Array.Empty<byte>();
                }
            }
            if ((wcc & Wcc.ResetMdt) != 0)
            {
                // Reset MDT happens before any data or order (p. 3-3).
                for (int i = 0; i < _buffer.Length; i++)
                {
                    if (_isFieldAttribute[i])
                    {
                        _buffer[i] = (byte)(_buffer[i] & ~FieldAttribute.Mdt);
                    }
                }
            }

            // The current SA set starts at default on every write command (p. 4-5).
            _saHighlight = _saColor = _saCharset = _saBackground = _saTransparency = 0;

            // Write starts at the cursor when no SBA follows the WCC (p. 3-5); EW has put it at 0.
            int address = CursorAddress;
            bool afterCommandOrOrder = true;
            bool ptNullingWrapped = false;

            int i2 = 1;
            while (i2 < data.Length)
            {
                byte b = data[i2++];
                bool wasPt = false;
                switch (b)
                {
                    case Order3270.StartField:
                        if (i2 >= data.Length)
                        {
                            Error("SF without its attribute byte at the end of the record (p. 4-1).");
                            goto Done;
                        }

                        SetFieldAttribute(address, (byte)(data[i2++] & FieldAttribute.Meaningful));
                        address = Next(address);
                        break;

                    case Order3270.StartFieldExtended:
                    {
                        if (!ReadPairs(data, ref i2, out int start, out int count))
                        {
                            goto Done;
                        }

                        // "Unspecified types take their default" (p. 4-2); the field attribute is the
                        // type X'C0' pair, so a missing one gives the default attribute X'00'
                        // (Table 4-2, p. 4-28).
                        SetFieldAttribute(address, 0);
                        if (!ApplyFieldPairs(address, data.Slice(start, count * 2), fromSfe: true))
                        {
                            goto Done;
                        }

                        address = Next(address);
                        break;
                    }

                    case Order3270.ModifyField:
                    {
                        if (!ReadPairs(data, ref i2, out int start, out int count))
                        {
                            goto Done;
                        }

                        if (!_isFieldAttribute[address])
                        {
                            Error("MF where the buffer address holds no field attribute is rejected (p. 4-6).");
                            goto Done;
                        }

                        if (!ApplyFieldPairs(address, data.Slice(start, count * 2), fromSfe: false))
                        {
                            goto Done;
                        }

                        address = Next(address);
                        break;
                    }

                    case Order3270.SetBufferAddress:
                        if (!ReadAddress(data, ref i2, out int sba))
                        {
                            goto Done;
                        }

                        address = sba;
                        break;

                    case Order3270.SetAttribute:
                        if (i2 + 1 >= data.Length)
                        {
                            Error("SA cut short by the end of the record (p. 4-1).");
                            goto Done;
                        }

                        if (!ApplySetAttribute(data[i2], data[i2 + 1]))
                        {
                            goto Done;
                        }

                        i2 += 2;
                        break;

                    case Order3270.InsertCursor:
                        CursorAddress = address;
                        break;

                    case Order3270.ProgramTab:
                        address = ProgramTab(address, !afterCommandOrOrder || ptNullingWrapped, out ptNullingWrapped);
                        wasPt = true;
                        break;

                    case Order3270.RepeatToAddress:
                    {
                        if (!ReadAddress(data, ref i2, out int stop))
                        {
                            goto Done;
                        }

                        bool ge = false;
                        if (i2 < data.Length && data[i2] == Order3270.GraphicEscape)
                        {
                            ge = true;
                            i2++;
                        }

                        if (i2 >= data.Length)
                        {
                            Error("RA without its character at the end of the record (p. 4-8).");
                            goto Done;
                        }

                        byte c = data[i2++];
                        int a = address;
                        do
                        {
                            // Field attributes in the range are overwritten (p. 4-8).
                            StoreCharacter(a, c, ge, useSa: true);
                            a = Next(a);
                        }
                        while (a != stop);

                        address = stop;
                        break;
                    }

                    case Order3270.EraseUnprotectedToAddress:
                    {
                        if (!ReadAddress(data, ref i2, out int stop))
                        {
                            goto Done;
                        }

                        int fa = GetFieldAttributeFor(address);
                        bool isProtected = fa >= 0 && FieldAttribute.IsProtected(_buffer[fa]);
                        int a = address;
                        do
                        {
                            if (_isFieldAttribute[a])
                            {
                                // Field attributes are left alone; they decide what follows (p. 4-9).
                                isProtected = FieldAttribute.IsProtected(_buffer[a]);
                            }
                            else if (!isProtected)
                            {
                                StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                            }

                            a = Next(a);
                        }
                        while (a != stop);

                        address = stop;
                        break;
                    }

                    case Order3270.GraphicEscape:
                        if (i2 >= data.Length)
                        {
                            Error("GE without its character at the end of the record (p. 4-9).");
                            goto Done;
                        }

                        byte g = data[i2++];
                        if (g < 0x40 || g == 0xFF)
                        {
                            Error("GE with a character outside X'40'-X'FE' is rejected (p. 4-9).");
                            goto Done;
                        }

                        StoreCharacter(address, g, graphicEscape: true, useSa: true);
                        address = Next(address);
                        afterCommandOrOrder = false;
                        ptNullingWrapped = false;
                        continue;

                    default:
                        if (b < 0x40 && !FormatControl.IsStorable(b))
                        {
                            Error("Byte X'" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' is in the order range but is no order and no stored control (p. 4-1).");
                            goto Done;
                        }

                        StoreCharacter(address, b, graphicEscape: false, useSa: true);
                        address = Next(address);
                        afterCommandOrOrder = false;
                        ptNullingWrapped = false;
                        continue;
                }

                afterCommandOrOrder = true;
                if (!wasPt)
                {
                    ptNullingWrapped = false;
                }
            }

        Done:
            // Every WCC function except Reset MDT waits until the data and orders are done (p. 3-2).
            if ((wcc & Wcc.KeyboardRestore) != 0)
            {
                Lock = KeyboardLock.None;
                _lastAid = Aid.None;
            }
            else if (Lock == KeyboardLock.WaitingForHost)
            {
                // The host answered but left the keyboard locked: TWAIT ends, System Lock shows
                // (see KeyboardLock.SystemLock).
                Lock = KeyboardLock.SystemLock;
            }

            if ((wcc & Wcc.SoundAlarm) != 0)
            {
                Alarm?.Invoke();
            }
        }

        private void WriteStructuredField(ReadOnlySpan<byte> data)
        {
            // First pass: check every field's length, that Read Partition is the last one, and the
            // file transfer total, before anything is applied. Each field is a 2-byte length that
            // counts itself, then the ID; length 0 means the field runs to the end (p. 5-1).
            int fileTransferBytes = 0;
            int pos = 0;
            while (pos < data.Length)
            {
                if (!NextStructuredField(data, pos, out int length))
                {
                    return;
                }

                byte id = data[pos + 2];
                if (id == StructuredFieldId.ReadPartition && pos + length < data.Length)
                {
                    Error("Read Partition must be the last structured field in the transmission (p. 5-48).");
                    return;
                }

                if (id == StructuredFieldId.DataTransfer)
                {
                    fileTransferBytes += length;
                }

                pos += length;
            }

            if (fileTransferBytes > 0)
            {
                if (_fileTransferLimit == 0)
                {
                    Error("A file transfer structured field (X'D0') arrived, but file transfer is off, so the DDM query reply was never sent.");
                    return;
                }

                if (fileTransferBytes > _fileTransferLimit)
                {
                    Error("The file transfer fields add up to " + fileTransferBytes + " bytes, more than the " + _fileTransferLimit + " this terminal reported as LIMOUT (p. 6-37).");
                    return;
                }
            }

            pos = 0;
            while (pos < data.Length)
            {
                NextStructuredField(data, pos, out int length);
                ReadOnlySpan<byte> field = data.Slice(pos, length);
                pos += length;

                bool ok;
                switch (field[2])
                {
                    case StructuredFieldId.ReadPartition:
                        ok = ReadPartition(field);
                        break;
                    case StructuredFieldId.EraseReset:
                        ok = EraseReset(field);
                        break;
                    case StructuredFieldId.SetReplyMode:
                        ok = SetReplyMode(field);
                        break;
                    case StructuredFieldId.Outbound3270DS:
                        ok = Outbound3270DS(field);
                        break;
                    case StructuredFieldId.DataTransfer:
                        FileTransferField?.Invoke(field);
                        ok = true;
                        break;
                    default:
                        Error("Structured field X'" + Hex(field[2]) + "' is not built (only Read Partition, Erase/Reset, Set Reply Mode, Outbound 3270DS and X'D0' file transfer are).");
                        ok = false;
                        break;
                }

                if (!ok)
                {
                    // A rejected field stops the rest of the transmission (p. 5-1).
                    return;
                }
            }
        }

        private bool NextStructuredField(ReadOnlySpan<byte> data, int pos, out int length)
        {
            length = 0;
            if (pos + 3 > data.Length)
            {
                Error("Structured field cut short: fewer than 3 bytes left for its length and ID (p. 5-1).");
                return false;
            }

            length = (data[pos] << 8) | data[pos + 1];
            if (length == 0)
            {
                length = data.Length - pos;
            }

            if (length < 3 || pos + length > data.Length)
            {
                Error("Structured field length " + length + " does not fit the " + (data.Length - pos) + " bytes left (p. 5-1).");
                return false;
            }

            byte id = data[pos + 2];
            if (id == 0x0F || id == 0x10)
            {
                // Two-byte IDs begin with X'0F' or X'10' (p. 5-2); none of them is built.
                Error("Structured field with the two-byte ID X'" + Hex(id) + (length > 3 ? Hex(data[pos + 3]) : string.Empty) + "' is not built.");
                return false;
            }

            return true;
        }

        private bool ReadPartition(ReadOnlySpan<byte> field)
        {
            // L, X'01', PID, TYPE, then for Query List the flags byte and the codes (p. 5-47).
            if (field.Length < 5)
            {
                Error("Read Partition shorter than 5 bytes (p. 5-47).");
                return false;
            }

            byte pid = field[3];
            byte type = field[4];
            switch (type)
            {
                case 0x02:
                    if (pid != 0xFF)
                    {
                        Error("Read Partition Query with partition X'" + Hex(pid) + "', which must be X'FF' (p. 5-48).");
                        return false;
                    }

                    SendQueryReplies(Array.Empty<byte>(), all: true);
                    return true;

                case 0x03:
                {
                    if (pid != 0xFF)
                    {
                        Error("Read Partition Query List with partition X'" + Hex(pid) + "', which must be X'FF' (p. 5-48).");
                        return false;
                    }

                    if (field.Length < 6)
                    {
                        Error("Read Partition Query List without its request type byte (p. 5-47).");
                        return false;
                    }

                    // Bits 0-1 of byte 5, IBM bit 0 being the high bit (p. 5-47, 5-48).
                    int requestType = field[5] >> 6;
                    ReadOnlySpan<byte> codes = field.Slice(6);
                    switch (requestType)
                    {
                        case 0:
                            SendQueryReplies(codes, all: false);
                            return true;
                        case 1:
                            // "Equivalent": everything a Query sends, plus the listed codes, no
                            // duplicates. A Query sends every reply this terminal has, so the list
                            // adds nothing.
                            SendQueryReplies(codes, all: true);
                            return true;
                        case 2:
                            // "All" overrides the list.
                            SendQueryReplies(Array.Empty<byte>(), all: true);
                            return true;
                        default:
                            Error("Read Partition Query List with the reserved request type B'11' (p. 5-47).");
                            return false;
                    }
                }

                case Command3270.ReadBuffer:
                case Command3270.ReadModified:
                case Command3270.ReadModifiedAll:
                    if (pid != 0x00)
                    {
                        Error("Read Partition of partition X'" + Hex(pid) + "': only the implicit partition 0 exists, partitions are not built (p. 5-48).");
                        return false;
                    }

                    // Partition 0 answers in plain 3270 format, not in Inbound 3270DS (p. 6-7). The
                    // manual contradicts itself on the AID: p. 5-49 step 8 says X'61', but p. 3-9
                    // note 2 says X'61' can never be the first byte of an inbound stream, and in
                    // plain 3270 format the AID is the first byte. The current AID is sent, which
                    // obeys p. 3-9.
                    if (type == Command3270.ReadBuffer)
                    {
                        SendReadBuffer(_lastAid);
                    }
                    else
                    {
                        SendReadModified(_lastAid, all: type == Command3270.ReadModifiedAll);
                    }

                    return true;

                default:
                    Error("Read Partition type X'" + Hex(type) + "' is reserved and rejected (p. 5-48).");
                    return false;
            }
        }

        private bool EraseReset(ReadOnlySpan<byte> field)
        {
            // L, X'03', flags; bit 0 (X'80') IPZ: the implicit partition gets the alternate size,
            // otherwise the default size (p. 5-15). All partitions are destroyed and implicit
            // partition 0 is made; with no partitions built that is the screen itself.
            if (field.Length < 4)
            {
                Error("Erase/Reset without its flags byte (p. 5-15).");
                return false;
            }

            if ((field[3] & 0x80) != 0)
            {
                Resize(_alternateRows, _alternateColumns);
            }
            else
            {
                Resize(_defaultRows, _defaultColumns);
            }

            CursorAddress = 0;
            _replyMode = ReplyMode.Field;
            _replyTypes = Array.Empty<byte>();
            return true;
        }

        private bool SetReplyMode(ReadOnlySpan<byte> field)
        {
            // L, X'09', PID, MODE, then the attribute type list for Character mode (p. 5-61).
            if (field.Length < 5)
            {
                Error("Set Reply Mode shorter than 5 bytes (p. 5-61).");
                return false;
            }

            if (field[3] != 0x00)
            {
                Error("Set Reply Mode for partition X'" + Hex(field[3]) + "': only the implicit partition 0 exists, partitions are not built.");
                return false;
            }

            byte mode = field[4];
            if (mode > (byte)ReplyMode.Character)
            {
                Error("Set Reply Mode X'" + Hex(mode) + "' is reserved and rejected (p. 5-62).");
                return false;
            }

            ReadOnlySpan<byte> types = mode == (byte)ReplyMode.Character ? field.Slice(5) : ReadOnlySpan<byte>.Empty;
            for (int i = 0; i < types.Length; i++)
            {
                if (Array.IndexOf(CharacterAttributeTypes, types[i]) < 0)
                {
                    Error("Set Reply Mode lists attribute type X'" + Hex(types[i]) + "', which is not a character attribute type (p. 5-62).");
                    return false;
                }
            }

            _replyMode = (ReplyMode)mode;
            _replyTypes = types.ToArray();
            return true;
        }

        private bool Outbound3270DS(ReadOnlySpan<byte> field)
        {
            // L, X'40', PID, CMND, then the WCC (none for EAU) and the orders and data (p. 5-40).
            if (field.Length < 5)
            {
                Error("Outbound 3270DS shorter than 5 bytes (p. 5-40).");
                return false;
            }

            if (field[3] != 0x00)
            {
                Error("Outbound 3270DS for partition X'" + Hex(field[3]) + "': only partition 0 exists, partitions are not built (p. 5-40).");
                return false;
            }

            ReadOnlySpan<byte> rest = field.Slice(5);
            switch (field[4])
            {
                case Command3270.Write:
                    Write(rest, erase: false, alternate: false, fromStructuredField: true);
                    return true;
                case Command3270.EraseWrite:
                    Write(rest, erase: true, alternate: false, fromStructuredField: true);
                    return true;
                case Command3270.EraseWriteAlternate:
                    Write(rest, erase: true, alternate: true, fromStructuredField: true);
                    return true;
                case Command3270.EraseAllUnprotected:
                    EraseAllUnprotected();
                    return true;
                case 0xF7:
                    Error("Outbound 3270DS with BSC Copy (X'F7') is not built: it needs remote BSC, which TN3270 is not (p. 5-41).");
                    return false;
                default:
                    Error("Outbound 3270DS command X'" + Hex(field[4]) + "' is reserved and rejected (p. 5-40).");
                    return false;
            }
        }

        private void SendQueryReplies(ReadOnlySpan<byte> listed, bool all)
        {
            // AID X'88' once, then the replies in ascending code order, each code once however often
            // it is listed (p. 3-10, 5-48). Summary is among them for Query, Equivalent and All, and
            // for a list only when the list names it (p. 6-81).
            _inbound.Clear();
            AppendByte(Aid.StructuredField);

            bool any = false;
            ReadOnlySpan<byte> supported = SupportedQueryCodes();
            for (int i = 0; i < supported.Length; i++)
            {
                byte code = supported[i];
                if (all || listed.IndexOf(code) >= 0)
                {
                    AppendQueryReply(code);
                    any = true;
                }
            }

            if (!any)
            {
                // None of the listed codes is supported, or the list is empty: Null (p. 5-48, 6-67).
                AppendBytes(stackalloc byte[] { 0x00, 0x04, StructuredFieldId.QueryReply, QueryCode.Null });
            }

            Send();

            // The reply is out: retry-read state, TWAIT, until the host acknowledges (see
            // KeyboardLock.QueryReplySent for the pages). Only from an unlocked keyboard: the lock
            // is one value here, and a keyboard already locked for another reason stays locked
            // for that reason. With an enter outstanding (WaitingForHost) the manual keeps TWAIT
            // in effect for the enter (p. 9-9, 10-8, "The TWAIT condition remains in effect").
            if (Lock == KeyboardLock.None)
            {
                Lock = KeyboardLock.QueryReplySent;
            }
        }

        private ReadOnlySpan<byte> SupportedQueryCodes()
        {
            // Ascending order, the order they are sent in. DDM only while file transfer is on.
            return _fileTransferLimit > 0 ? QueryCodesWithFileTransfer : QueryCodesWithoutFileTransfer;
        }

        private void AppendQueryReply(byte code)
        {
            int start = _inbound.WrittenCount;
            AppendBytes(stackalloc byte[] { 0x00, 0x00, StructuredFieldId.QueryReply, code });
            switch (code)
            {
                case QueryCode.Summary:
                    // The codes of every reply this terminal has (p. 6-81).
                    AppendBytes(SupportedQueryCodes());
                    break;

                case QueryCode.UsableArea:
                {
                    // p. 6-85 to 6-90. Flags: 12/14-bit addressing (X'01'), no special features.
                    // W and H are the largest screen; units inches (X'00') with Xr and Yr X'FFFFFFFF',
                    // "cannot be determined", which is allowed without Load PS (p. 6-89).
                    //
                    // When one size holds the other (every 3278 model: 24 x 80 inside the
                    // alternate), the larger is "the maximum number of characters that can be
                    // presented" (p. 6-89) and is the only size reported.
                    //
                    // When neither holds the other (43 x 80 with 27 x 132), the larger column count
                    // with the larger row count is a screen that cannot be shown. p. 6-93 gives that
                    // very device, a 3180: base 43 rows by 80 columns, and 27 by 132 in an Alternate
                    // Usable Area self-defining parameter, which "is always required when the
                    // device supports more than one usable area size on one display surface". So
                    // the base is the default size, BUFFSZ the larger of the two cell counts, then
                    // XMIN, YMIN, XMAX and YMAX as zeros - p. 6-87 lets them be left out only when
                    // no self-defining parameter follows - then the parameter (p. 6-94).
                    bool defaultHoldsAlternate = _defaultColumns >= _alternateColumns && _defaultRows >= _alternateRows;
                    bool alternateHoldsDefault = _alternateColumns >= _defaultColumns && _alternateRows >= _defaultRows;
                    bool nested = defaultHoldsAlternate || alternateHoldsDefault;
                    int columns = nested ? Math.Max(_defaultColumns, _alternateColumns) : _defaultColumns;
                    int rows = nested ? Math.Max(_defaultRows, _alternateRows) : _defaultRows;
                    int bufferSize = Math.Max(_defaultColumns * _defaultRows, _alternateColumns * _alternateRows);
                    AppendBytes(stackalloc byte[] { 0x01, 0x00 });
                    AppendUInt16(columns);
                    AppendUInt16(rows);
                    AppendBytes(stackalloc byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
                    AppendByte((byte)CellWidth);
                    AppendByte((byte)CellHeight);
                    AppendUInt16(bufferSize);
                    if (!nested)
                    {
                        // XMIN, YMIN, XMAX, YMAX (bytes 23 to 26, p. 6-87).
                        AppendBytes(stackalloc byte[] { 0x00, 0x00, 0x00, 0x00 });

                        // Alternate Usable Area (p. 6-94): length X'13', X'02', reserved X'00',
                        // AUAID X'01', WAUA, HAUA, units inches, Xr and Yr X'FFFFFFFF' ("unable to
                        // determine", allowed without Load PS), AWAUA, AHAUA.
                        AppendBytes(stackalloc byte[] { 0x13, 0x02, 0x00, 0x01 });
                        AppendUInt16(_alternateColumns);
                        AppendUInt16(_alternateRows);
                        AppendBytes(stackalloc byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
                        AppendByte((byte)CellWidth);
                        AppendByte((byte)CellHeight);
                    }

                    break;
                }

                case QueryCode.CharacterSets:
                    // p. 6-21 to 6-28. Flags X'82': ALT (Graphic Escape is supported) and GF (the
                    // CGCSGID fields are present). SDW, SDH, FORM 0, descriptor length 7.
                    AppendBytes(stackalloc byte[] { 0x82, 0x00 });
                    AppendByte((byte)CellWidth);
                    AppendByte((byte)CellHeight);
                    AppendBytes(stackalloc byte[] { 0x00, 0x00, 0x00, 0x00, 0x07 });

                    // Base set: SET 0, flags 0, local ID X'F0', then character set and code page.
                    // p. 6-26: "Nonloadable character sets will have default LCID assigned to them
                    // (X'F0' to X'FE'), where: X'F0' to X'F7' = 1-byte character sets". The base
                    // set is non-loadable and 1-byte. Which ID in that range is not given by the
                    // manual; X'F1' is the Graphic Escape set's, X'F0' is the lowest free one
                    // (decided 24 November 2026). X'00' is not an ID: in SA and SFE it means
                    // "Default character set" (p. 4-19), a value a host sends. SA and SFE values
                    // are stored as sent and not interpreted here (GetCharacterCharacterSet,
                    // GetFieldCharacterSet), so X'00' and X'F0' both reach the caller, which
                    // must treat both as the base set and X'F1' as code page 310.
                    AppendBytes(stackalloc byte[] { 0x00, 0x00, BaseCharacterSetId });
                    AppendUInt16(_codePage.CharacterSet);
                    AppendUInt16(_codePage.Number);

                    // Graphic Escape set: SET 1, flags 0, local ID X'F1', code page 310.
                    AppendBytes(stackalloc byte[] { 0x01, 0x00, GraphicEscapeCharacterSetId });
                    AppendUInt16(CodePage.Cp310.CharacterSet);
                    AppendUInt16(CodePage.Cp310.Number);
                    break;

                case QueryCode.Color:
                {
                    // p. 6-29, 6-30. Flags 0, 8 pairs: the default X'00' is green (X'F4'), then
                    // X'F1'-X'F7' shown as themselves on a colour screen, as the default on a
                    // monochrome one.
                    AppendBytes(stackalloc byte[] { 0x00, 0x08, 0x00, 0xF4 });
                    for (byte c = 0xF1; c <= 0xF7; c++)
                    {
                        AppendByte(c);
                        AppendByte(Color ? c : (byte)0x00);
                    }

                    break;
                }

                case QueryCode.Highlighting:
                    // p. 6-56. 5 pairs: default X'00' is normal, normal, blink, reverse, underscore.
                    AppendBytes(stackalloc byte[] { 0x05, 0x00, 0xF0, 0xF0, 0xF0, 0xF1, 0xF1, 0xF2, 0xF2, 0xF4, 0xF4 });
                    break;

                case QueryCode.FieldValidation:
                    // p. 6-52: mandatory fill and mandatory entry; trigger is not built.
                    AppendByte((byte)(FieldValidation.MandatoryFill | FieldValidation.MandatoryEntry));
                    break;

                case QueryCode.ReplyModes:
                    // p. 6-74: Field, Extended Field, Character.
                    AppendBytes(stackalloc byte[] { 0x00, 0x01, 0x02 });
                    break;

                case QueryCode.Ddm:
                    // p. 6-37. Flags 0, LIMIN, LIMOUT, one subset, subset ID 1.
                    AppendBytes(stackalloc byte[] { 0x00, 0x00 });
                    AppendUInt16(_fileTransferLimit);
                    AppendUInt16(_fileTransferLimit);
                    AppendBytes(stackalloc byte[] { 0x01, 0x01 });
                    break;

                case QueryCode.ImplicitPartition:
                    // p. 6-59 to 6-61. Flags 0, then the size self-defining parameter X'01', length
                    // 11: flags 0, default width and height, alternate width and height.
                    AppendBytes(stackalloc byte[] { 0x00, 0x00, 0x0B, 0x01, 0x00 });
                    AppendUInt16(_defaultColumns);
                    AppendUInt16(_defaultRows);
                    AppendUInt16(_alternateColumns);
                    AppendUInt16(_alternateRows);
                    break;
            }

            // Fill in the length, which counts itself.
            int length = _inbound.WrittenCount - start;
            Span<byte> written = MemoryMarshalWritten(start);
            written[0] = (byte)(length >> 8);
            written[1] = (byte)length;
        }

        private Span<byte> MemoryMarshalWritten(int start)
        {
            // ArrayBufferWriter only hands out the written bytes read-only; the length bytes are ours.
            return System.Runtime.InteropServices.MemoryMarshal.AsMemory(_inbound.WrittenMemory).Span.Slice(start);
        }

        private void AppendByte(byte value)
        {
            Span<byte> s = _inbound.GetSpan(1);
            s[0] = value;
            _inbound.Advance(1);
        }

        private void AppendBytes(ReadOnlySpan<byte> values)
        {
            values.CopyTo(_inbound.GetSpan(values.Length));
            _inbound.Advance(values.Length);
        }

        private void AppendUInt16(int value)
        {
            Span<byte> s = _inbound.GetSpan(2);
            s[0] = (byte)(value >> 8);
            s[1] = (byte)value;
            _inbound.Advance(2);
        }

        private static string Hex(byte value) => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);

        private int ProgramTab(int address, bool nullFill, out bool nullingWrapped)
        {
            nullingWrapped = false;
            int size = Size;

            // On the attribute of an unprotected field: move on by one only (p. 4-7).
            if (_isFieldAttribute[address] && !FieldAttribute.IsProtected(_buffer[address]))
            {
                return Next(address);
            }

            int a = address;
            if (nullFill)
            {
                // Nulls from here to the end of the field, whatever the protection (p. 4-7). The
                // search ends at the last location of the buffer.
                while (!_isFieldAttribute[a])
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                    if (a == size - 1)
                    {
                        nullingWrapped = true;
                        return 0;
                    }

                    a++;
                }
            }

            // Next unprotected field attribute, stopping at the last location (p. 4-7).
            for (int i = a; i < size; i++)
            {
                if (_isFieldAttribute[i] && !FieldAttribute.IsProtected(_buffer[i]))
                {
                    // One found in the last location also gives address 0.
                    return i == size - 1 ? 0 : i + 1;
                }
            }

            return 0;
        }

        private bool ReadAddress(ReadOnlySpan<byte> data, ref int i, out int address)
        {
            address = 0;
            if (i + 1 >= data.Length)
            {
                Error("Buffer address cut short by the end of the record (p. 4-1).");
                return false;
            }

            if (!BufferAddress.TryDecode(data[i], data[i + 1], out address))
            {
                Error("Buffer address with the reserved flag bits B'10' is rejected (p. 4-4).");
                return false;
            }

            i += 2;
            if (address >= Size)
            {
                // "the write stops at once, with a negative response" (p. 4-3).
                Error("Buffer address " + address + " is outside the " + Size + "-cell screen (p. 4-3).");
                return false;
            }

            return true;
        }

        private bool ReadPairs(ReadOnlySpan<byte> data, ref int i, out int start, out int count)
        {
            start = 0;
            count = 0;
            if (i >= data.Length)
            {
                Error("SFE or MF without its pair count (p. 4-2, 4-6).");
                return false;
            }

            count = data[i++];
            start = i;
            if (i + (count * 2) > data.Length)
            {
                Error("SFE or MF pairs cut short by the end of the record (p. 4-1).");
                return false;
            }

            i += count * 2;
            return true;
        }

        private bool ApplyFieldPairs(int address, ReadOnlySpan<byte> pairs, bool fromSfe)
        {
            if (fromSfe)
            {
                // SFE sets every type not given to its default (p. 4-2).
                ClearFieldAttributes(address);
            }

            for (int p = 0; p + 1 < pairs.Length; p += 2)
            {
                byte type = pairs[p];
                byte value = pairs[p + 1];
                switch (type)
                {
                    case 0xC0:
                        // Keep the MDT the field already has? No: the pair carries the whole attribute.
                        _buffer[address] = (byte)(value & FieldAttribute.Meaningful);
                        break;
                    case 0xC1:
                        if ((value & FieldValidation.Reserved) != 0)
                        {
                            Error("Field validation X'" + Hex(value) + "' sets the reserved bits 0-4 (p. 4-21).");
                            return false;
                        }

                        if ((value & FieldValidation.Trigger) != 0)
                        {
                            Error("Field validation with the trigger bit: trigger fields are not built, and the Field Validation query reply says so (p. 4-23, 6-52).");
                            return false;
                        }

                        _fieldValidation[address] = value;
                        break;
                    case 0xC2:
                        _fieldOutlining[address] = value;
                        break;
                    case 0x41:
                        _fieldHighlight[address] = value;
                        break;
                    case 0x42:
                        _fieldColor[address] = value;
                        break;
                    case 0x43:
                        _fieldCharset[address] = value;
                        break;
                    case 0x45:
                        _fieldBackground[address] = value;
                        break;
                    case 0x46:
                        _fieldTransparency[address] = value;
                        break;
                    default:
                        Error("Attribute type X'" + type.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' is not valid in SFE or MF (p. 4-16).");
                        return false;
                }
            }

            return true;
        }

        private bool ApplySetAttribute(byte type, byte value)
        {
            switch (type)
            {
                case 0x00:
                    // All character attributes back to default (p. 4-17).
                    _saHighlight = _saColor = _saCharset = _saBackground = _saTransparency = 0;
                    return true;
                case 0x41:
                    _saHighlight = value;
                    return true;
                case 0x42:
                    _saColor = value;
                    return true;
                case 0x43:
                    _saCharset = value;
                    return true;
                case 0x45:
                    _saBackground = value;
                    return true;
                case 0x46:
                    _saTransparency = value;
                    return true;
                default:
                    Error("Attribute type X'" + type.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' is not valid in SA (p. 4-16).");
                    return false;
            }
        }

        private void EraseAllUnprotected()
        {
            _primedField = -1;

            // GA23-0059-4 p. 3-7.
            bool any = false;
            bool isProtected = true;
            int size = Size;
            int first = GetFieldAttributeFor(size - 1);
            if (first >= 0)
            {
                isProtected = FieldAttribute.IsProtected(_buffer[first]);
            }

            for (int a = 0; a < size; a++)
            {
                if (_isFieldAttribute[a])
                {
                    isProtected = FieldAttribute.IsProtected(_buffer[a]);
                    if (!isProtected)
                    {
                        any = true;
                        _buffer[a] = (byte)(_buffer[a] & ~FieldAttribute.Mdt);
                    }
                }
                else if (!isProtected || first < 0)
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                    any = true;
                }
            }

            Lock = KeyboardLock.None;
            _lastAid = Aid.None;
            CursorAddress = any ? NextInputPosition(size - 1) : 0;
        }

        private void SendReadModified(byte aid, bool all)
        {
            // Read Modified is the same in Field and Extended field mode: SBA and the data, no
            // attributes. Character mode adds SA orders for the listed types (p. 5-61, 5-62).
            _inbound.Clear();
            Array.Clear(_inboundSa);
            if (aid == Aid.SysReq)
            {
                // Test request read: SOH % / STX instead of the AID and cursor (p. 9-3; see SysReq).
                AppendByte(0x01);
                AppendByte(0x6C);
                AppendByte(0x61);
                AppendByte(0x02);
            }
            else
            {
                AppendByte(aid);
            }

            if (!all && Aid.IsShortRead(aid))
            {
                // Short read: the AID alone (p. 3-13).
                Send();
                return;
            }

            if (aid != Aid.SysReq)
            {
                AppendAddress(CursorAddress);
            }

            // Selector-pen attention: each modified field's address, but none of its data (p. 3-12).
            bool sendData = all || aid != Aid.SelectorPen;
            int size = Size;

            if (!IsFormatted)
            {
                // Unformatted: all data from 0 to the end, nulls left out, no SBA (p. 3-13).
                for (int a = 0; a < size; a++)
                {
                    AppendData(a);
                }

                Send();
                return;
            }

            // Every modified field in buffer order: SBA (attribute + 1), then its data without nulls
            // (p. 3-13). A field that runs past the end of the buffer continues at address 0.
            for (int fa = 0; fa < size; fa++)
            {
                if (!_isFieldAttribute[fa] || (_buffer[fa] & FieldAttribute.Mdt) == 0)
                {
                    continue;
                }

                AppendByte(Order3270.SetBufferAddress);
                int a = Next(fa);
                AppendAddress(a);
                while (sendData && !_isFieldAttribute[a])
                {
                    AppendData(a);
                    a = Next(a);
                }
            }

            Send();
        }

        private void SendReadBuffer(byte aid)
        {
            // AID, cursor, then every position from 0; nulls are NOT left out (p. 3-11, 4-10). How a
            // field attribute goes depends on the reply mode (p. 5-61, 5-62):
            //  - Field: SF and the attribute.
            //  - Extended field and Character: SFE with the attribute and every extended field
            //    attribute that is not at its default.
            //  - Character also sends SA before a character whose listed character attributes
            //    differ from the ones last sent in this record, which start at default.
            _inbound.Clear();
            Array.Clear(_inboundSa);
            AppendByte(aid);
            AppendAddress(CursorAddress);
            int size = Size;
            for (int a = 0; a < size; a++)
            {
                if (_isFieldAttribute[a])
                {
                    AppendFieldAttribute(a);
                    continue;
                }

                AppendCharacterAttributes(a);
                if (_graphicEscape[a])
                {
                    AppendByte(Order3270.GraphicEscape);
                }

                AppendByte(_buffer[a]);
            }

            Send();
        }

        private void AppendFieldAttribute(int address)
        {
            // Bits 0-1 made printable per Figure D-1 (p. 4-13).
            byte attribute = BufferAddress.EncodeSixBits(_buffer[address]);
            if (_replyMode == ReplyMode.Field)
            {
                AppendByte(Order3270.StartField);
                AppendByte(attribute);
                return;
            }

            Span<byte> pairs = stackalloc byte[16];
            int n = 0;
            pairs[n++] = 0xC0;
            pairs[n++] = attribute;
            AddPair(pairs, ref n, 0x41, _fieldHighlight[address]);
            AddPair(pairs, ref n, 0x42, _fieldColor[address]);
            AddPair(pairs, ref n, 0x43, _fieldCharset[address]);
            AddPair(pairs, ref n, 0x45, _fieldBackground[address]);
            AddPair(pairs, ref n, 0x46, _fieldTransparency[address]);
            AddPair(pairs, ref n, 0xC1, _fieldValidation[address]);
            AddPair(pairs, ref n, 0xC2, _fieldOutlining[address]);
            AppendByte(Order3270.StartFieldExtended);
            AppendByte((byte)(n / 2));
            AppendBytes(pairs.Slice(0, n));
        }

        private static void AddPair(Span<byte> pairs, ref int n, byte type, byte value)
        {
            // Default values are not sent (p. 5-61).
            if (value != 0)
            {
                pairs[n++] = type;
                pairs[n++] = value;
            }
        }

        private void AppendCharacterAttributes(int address)
        {
            // Character mode only, and only the types Set Reply Mode listed (p. 5-62).
            if (_replyMode != ReplyMode.Character)
            {
                return;
            }

            for (int i = 0; i < _replyTypes.Length; i++)
            {
                byte type = _replyTypes[i];
                int slot = Array.IndexOf(CharacterAttributeTypes, type);
                byte value = CharacterAttribute(slot, address);
                if (value != _inboundSa[slot])
                {
                    _inboundSa[slot] = value;
                    AppendByte(Order3270.SetAttribute);
                    AppendByte(type);
                    AppendByte(value);
                }
            }
        }

        private byte CharacterAttribute(int slot, int address)
        {
            // Slots in the order of CharacterAttributeTypes.
            switch (slot)
            {
                case 0:
                    return _charHighlight[address];
                case 1:
                    return _charColor[address];
                case 2:
                    return _charCharset[address];
                case 3:
                    return _charBackground[address];
                default:
                    return _charTransparency[address];
            }
        }

        private void AppendData(int address)
        {
            // Nulls are left out; in Character mode SA orders go before a non-null character only
            // (p. 3-13, 5-62).
            byte b = _buffer[address];
            if (b == FormatControl.Null)
            {
                return;
            }

            AppendCharacterAttributes(address);
            if (_graphicEscape[address])
            {
                AppendByte(Order3270.GraphicEscape);
            }

            AppendByte(b);
        }

        private void AppendAddress(int address)
        {
            Span<byte> s = _inbound.GetSpan(2);
            BufferAddress.Encode(address, Size, s);
            _inbound.Advance(2);
        }

        private void Send()
        {
            // A new inbound record replaces the one kept for a retry: the last inbound operation
            // is the one a Read Modified repeats (p. 3-14). SendStructuredFieldReply keeps this
            // one again when asked to, after this.
            _retryLength = 0;
            InboundRecord?.Invoke(_inbound.WrittenSpan);
        }

        private int NextInputPosition(int from)
        {
            // First position of the next unprotected field after "from", wrapping once; none: 0.
            int size = Size;
            int a = from;
            for (int n = 0; n < size; n++)
            {
                a = Next(a);
                if (_isFieldAttribute[a] && !FieldAttribute.IsProtected(_buffer[a]))
                {
                    int first = Next(a);
                    if (!_isFieldAttribute[first])
                    {
                        return first;
                    }
                }
            }

            return 0;
        }

        private void SetFieldAttribute(int address, byte attribute)
        {
            _isFieldAttribute[address] = true;
            _graphicEscape[address] = false;
            _buffer[address] = attribute;

            // SF sets the extended field attribute of the position to its default (p. 4-2).
            ClearFieldAttributes(address);
            ClearCharacterAttributes(address);
        }

        private void StoreCharacter(int address, byte value, bool graphicEscape, bool useSa)
        {
            // The host may overwrite a field attribute with data (p. 4-13).
            _isFieldAttribute[address] = false;
            ClearFieldAttributes(address);
            _buffer[address] = value;
            _graphicEscape[address] = graphicEscape;
            if (useSa)
            {
                _charHighlight[address] = _saHighlight;
                _charColor[address] = _saColor;
                _charCharset[address] = _saCharset;
                _charBackground[address] = _saBackground;
                _charTransparency[address] = _saTransparency;
            }
            else
            {
                // Overwriting or erasing a character replaces its character attribute (p. 4-15).
                ClearCharacterAttributes(address);
            }
        }

        private void ClearFieldAttributes(int address)
        {
            _fieldHighlight[address] = 0;
            _fieldColor[address] = 0;
            _fieldCharset[address] = 0;
            _fieldBackground[address] = 0;
            _fieldTransparency[address] = 0;
            _fieldValidation[address] = 0;
            _fieldOutlining[address] = 0;
        }

        private void ClearCharacterAttributes(int address)
        {
            _charHighlight[address] = 0;
            _charColor[address] = 0;
            _charCharset[address] = 0;
            _charBackground[address] = 0;
            _charTransparency[address] = 0;
        }

        private void ClearAllAttributes()
        {
            Array.Clear(_fieldHighlight);
            Array.Clear(_fieldColor);
            Array.Clear(_fieldCharset);
            Array.Clear(_fieldBackground);
            Array.Clear(_fieldTransparency);
            Array.Clear(_fieldValidation);
            Array.Clear(_fieldOutlining);
            Array.Clear(_charHighlight);
            Array.Clear(_charColor);
            Array.Clear(_charCharset);
            Array.Clear(_charBackground);
            Array.Clear(_charTransparency);
        }

        private void Resize(int rows, int columns)
        {
            Rows = rows;
            Columns = columns;
            int size = rows * columns;
            if (_buffer.Length != size)
            {
                _buffer = new byte[size];
                _isFieldAttribute = new bool[size];
                _graphicEscape = new bool[size];
                _fieldHighlight = new byte[size];
                _fieldColor = new byte[size];
                _fieldCharset = new byte[size];
                _fieldBackground = new byte[size];
                _fieldTransparency = new byte[size];
                _fieldValidation = new byte[size];
                _fieldOutlining = new byte[size];
                _charHighlight = new byte[size];
                _charColor = new byte[size];
                _charCharset = new byte[size];
                _charBackground = new byte[size];
                _charTransparency = new byte[size];
            }
            else
            {
                Array.Clear(_buffer);
                Array.Clear(_isFieldAttribute);
                Array.Clear(_graphicEscape);
                ClearAllAttributes();
            }

            if (CursorAddress >= size)
            {
                CursorAddress = 0;
            }
        }

        private int Next(int address)
        {
            int n = address + 1;
            return n == Size ? 0 : n;
        }

        private void Error(string message)
        {
            _failed = true;
            ProtocolError?.Invoke(message);
        }
    }

    /// <summary>
    /// Receives one record the terminal sends to the host.
    /// </summary>
    /// <param name="record">
    /// The record, valid only during the call.
    /// </param>
    public delegate void InboundRecordHandler(ReadOnlySpan<byte> record);

    /// <summary>
    /// Receives the text of a protocol error.
    /// </summary>
    /// <param name="message">
    /// What was rejected or not built, and the page of the manual that decides it.
    /// </param>
    public delegate void ProtocolErrorHandler(string message);

    /// <summary>
    /// Called when the host asks the terminal to sound its alarm.
    /// </summary>
    public delegate void AlarmHandler();

    /// <summary>
    /// Receives one structured field from a Write Structured Field.
    /// </summary>
    /// <param name="field">
    /// The whole structured field, its two length bytes first, valid only during the call.
    /// </param>
    public delegate void StructuredFieldHandler(ReadOnlySpan<byte> field);
}
