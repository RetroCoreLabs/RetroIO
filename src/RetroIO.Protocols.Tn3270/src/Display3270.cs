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
        /// An attention key was sent and the host has not answered with a keyboard restore.
        /// The operator's Reset key does NOT clear this (GA23-0059-4 p. 3-20).
        /// </summary>
        WaitingForHost,

        /// <summary>
        /// The operator did something not allowed, such as typing into a protected field
        /// ("Go Elsewhere", GA27-2890-4 p. 2-21). Reset clears it. IBM locks here; x3270 by default
        /// only ignores the key. RetroTerm follows IBM (decided 22 November 2026).
        /// </summary>
        OperatorError,
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
    /// Not built yet, and loud about it through <see cref="ProtocolError"/>: Write Structured Field
    /// (so no Query Reply), partitions, 16-bit addresses, insert mode, field validation (mandatory
    /// fill / entry, trigger), selector pen, Erase EOF / Erase Input / DUP / Field Mark keys.
    /// </para>
    /// </remarks>
    public sealed class Display3270
    {
        private readonly int _defaultRows;
        private readonly int _defaultColumns;
        private readonly int _alternateRows;
        private readonly int _alternateColumns;

        private readonly ArrayBufferWriter<byte> _inbound = new ArrayBufferWriter<byte>(4096);

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

        /// <summary>
        /// Creates a display. The default size is used after Erase/Write, the alternate size after
        /// Erase/Write Alternate (GA23-0059-4 p. 3-5). A model 2 has 24 x 80 for both.
        /// </summary>
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
        /// A record the terminal sends to the host (an AID reply or a read reply). The span is only
        /// valid during the call.
        /// </summary>
        public event InboundRecordHandler? InboundRecord;

        /// <summary>
        /// The host sent something this model rejects or has not built. The record is not applied
        /// past that point. The text says what and why.
        /// </summary>
        public event Action<string>? ProtocolError;

        /// <summary>
        /// The WCC asked to sound the alarm (GA23-0059-4 p. 3-3).
        /// </summary>
        public event Action? Alarm;

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
        /// Applies one complete outbound record (telnet framing already removed).
        /// </summary>
        /// <param name="record">
        /// The record: command byte first.
        /// </param>
        public void ProcessOutbound(ReadOnlySpan<byte> record)
        {
            if (record.Length == 0)
            {
                return;
            }

            byte command = record[0];
            switch (command)
            {
                case Command3270.Write:
                    Write(record.Slice(1), erase: false, alternate: false);
                    break;

                case Command3270.EraseWrite:
                    Write(record.Slice(1), erase: true, alternate: false);
                    break;

                case Command3270.EraseWriteAlternate:
                    Write(record.Slice(1), erase: true, alternate: true);
                    break;

                case Command3270.EraseAllUnprotected:
                    EraseAllUnprotected();
                    break;

                case Command3270.ReadBuffer:
                    SendReadBuffer(_lastAid);
                    break;

                case Command3270.ReadModified:
                    SendReadModified(_lastAid, all: false);
                    break;

                case Command3270.ReadModifiedAll:
                    SendReadModified(_lastAid, all: true);
                    break;

                case Command3270.WriteStructuredField:
                    Error("Write Structured Field (X'F3') is not built yet, so Query Reply and the other structured fields are not answered.");
                    break;

                default:
                    Error("Unknown command X'" + command.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' (GA23-0059-4 p. 3-2).");
                    break;
            }
        }

        /// <summary>
        /// The operator types a character at the cursor (GA27-2890-4 p. 3-25).
        /// </summary>
        /// <param name="c">
        /// The typed character.
        /// </param>
        /// <returns>
        /// False when the key was refused: keyboard locked, character not in the code page, or the
        /// cursor is in a protected field or on a field attribute (which locks the keyboard with an
        /// operator error).
        /// </returns>
        public bool Type(char c)
        {
            if (Lock != KeyboardLock.None)
            {
                // A real 3278 has no type-ahead; keys pressed while locked are refused
                // (KEYBOARD.md section 5; decided 22 November 2026).
                return false;
            }

            if (!CodePage037.TryFromUnicode(c, out byte ebcdic))
            {
                return false;
            }

            int fa = GetFieldAttributeFor(CursorAddress);
            if (fa >= 0)
            {
                if (_isFieldAttribute[CursorAddress] || FieldAttribute.IsProtected(_buffer[fa]))
                {
                    // "Go Elsewhere" (GA27-2890-4 p. 2-21).
                    Lock = KeyboardLock.OperatorError;
                    return false;
                }

                // Numeric fields: with a plain typewriter keyboard the 3274 accepts any character
                // (GA23-0061-2 Figure 6-2, KEYBOARD.md 4.4), so no check is made here.
                _buffer[fa] = (byte)(_buffer[fa] | FieldAttribute.Mdt);
            }

            StoreCharacter(CursorAddress, ebcdic, graphicEscape: false, useSa: false);
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

            CursorAddress = next;
            return true;
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

            CursorAddress = NextInputPosition(CursorAddress);
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
            CursorAddress = NextInputPosition(last);
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

            CursorAddress = address;
        }

        /// <summary>
        /// Reset key: clears an operator error. It does not clear waiting for the host
        /// (GA23-0059-4 p. 3-20; GA27-2890-4 p. 3-40).
        /// </summary>
        public void Reset()
        {
            if (Lock == KeyboardLock.OperatorError)
            {
                Lock = KeyboardLock.None;
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
                // is cleared and the cursor goes home. Then the short read: the AID alone.
                Array.Clear(_buffer);
                Array.Clear(_isFieldAttribute);
                Array.Clear(_graphicEscape);
                ClearAllAttributes();
                CursorAddress = 0;
            }

            _lastAid = aid;
            Lock = KeyboardLock.WaitingForHost;
            SendReadModified(aid, all: false);
            return true;
        }

        private void Write(ReadOnlySpan<byte> data, bool erase, bool alternate)
        {
            // "A write command with no WCC and no data is a no-op" (p. 3-2); an EW with no WCC is a
            // no-op too, nothing is erased (p. 3-6).
            if (data.Length == 0)
            {
                return;
            }

            if (erase)
            {
                // Erase/Write steps (p. 3-6): screen size, nulls, attributes to X'00', cursor to 0.
                if (alternate)
                {
                    Resize(_alternateRows, _alternateColumns);
                }
                else
                {
                    Resize(_defaultRows, _defaultColumns);
                }

                CursorAddress = 0;
            }

            byte wcc = data[0];
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

            if ((wcc & Wcc.SoundAlarm) != 0)
            {
                Alarm?.Invoke();
            }

        }

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
            _inbound.Clear();
            Span<byte> one = _inbound.GetSpan(1);
            one[0] = aid;
            _inbound.Advance(1);

            if (!all && Aid.IsShortRead(aid))
            {
                // Short read: the AID alone (p. 3-13).
                Send();
                return;
            }

            AppendAddress(CursorAddress);
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

                Span<byte> sba = _inbound.GetSpan(1);
                sba[0] = Order3270.SetBufferAddress;
                _inbound.Advance(1);
                int a = Next(fa);
                AppendAddress(a);
                while (!_isFieldAttribute[a])
                {
                    AppendData(a);
                    a = Next(a);
                }
            }

            Send();
        }

        private void SendReadBuffer(byte aid)
        {
            // Field reply mode: AID, cursor, then every position from 0 with SF before each field
            // attribute; nulls are NOT left out (p. 3-11, 4-10).
            _inbound.Clear();
            Span<byte> one = _inbound.GetSpan(1);
            one[0] = aid;
            _inbound.Advance(1);
            AppendAddress(CursorAddress);
            int size = Size;
            for (int a = 0; a < size; a++)
            {
                Span<byte> s = _inbound.GetSpan(3);
                int n = 0;
                if (_isFieldAttribute[a])
                {
                    s[n++] = Order3270.StartField;

                    // Bits 0-1 made printable per Figure D-1 (p. 4-13).
                    s[n++] = BufferAddress.EncodeSixBits(_buffer[a]);
                }
                else
                {
                    if (_graphicEscape[a])
                    {
                        s[n++] = Order3270.GraphicEscape;
                    }

                    s[n++] = _buffer[a];
                }

                _inbound.Advance(n);
            }

            Send();
        }

        private void AppendData(int address)
        {
            byte b = _buffer[address];
            if (b == FormatControl.Null)
            {
                return;
            }

            Span<byte> s = _inbound.GetSpan(2);
            int n = 0;
            if (_graphicEscape[address])
            {
                s[n++] = Order3270.GraphicEscape;
            }

            s[n++] = b;
            _inbound.Advance(n);
        }

        private void AppendAddress(int address)
        {
            Span<byte> s = _inbound.GetSpan(2);
            BufferAddress.Encode(address, Size, s);
            _inbound.Advance(2);
        }

        private void Send()
        {
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
}
