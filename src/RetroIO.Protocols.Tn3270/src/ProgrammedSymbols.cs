//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Globalization;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// One loadable character set: a read/write store (RWS) of 191 symbol slots, one per code
    /// X'40' to X'FE', loaded by Load Programmed Symbols (GA23-0059-4 p. 5-18 to 5-33).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every dot of every slot holds a plane mask: bit 0 (value 1) the blue plane, bit 1 (value
    /// 2) the red plane, bit 2 (value 4) the green plane, the same values as the COLOR byte of
    /// Load PS (p. 5-20). A dot with mask 0 is off. When shown, the planes of a dot are ORed
    /// (p. 5-24), so mask 3 is blue and red, which is the colour X'F3' (pink), and so on: for the
    /// colours X'F1' to X'F7' the mask is the low three bits of the colour value.
    /// </para>
    /// <para>
    /// A set loaded while the display is single-plane (no colour) stores mask 7 for every dot
    /// that is on, so "not 0" means "on" for every set.
    /// </para>
    /// <para>
    /// A slot that was never loaded, or was cleared, holds all zeros and shows a space (p. 5-23).
    /// The X'40' slot is never loaded and always holds a blank (p. 5-18).
    /// </para>
    /// </remarks>
    public sealed class ProgrammedSymbolSet
    {
        /// <summary>
        /// The number of slots: codes X'40' to X'FE' (p. 5-18).
        /// </summary>
        public const int SlotCount = 191;

        /// <summary>
        /// The code of the first slot, X'40', which always holds a blank (p. 5-18).
        /// </summary>
        public const byte FirstCode = 0x40;

        /// <summary>
        /// The code of the last slot, X'FE' (p. 5-18).
        /// </summary>
        public const byte LastCode = 0xFE;

        /// <summary>
        /// The local character set ID of a free store, X'FF' (p. 5-19, 6-25).
        /// </summary>
        public const byte FreeLcid = 0xFF;

        private byte[] _dots = Array.Empty<byte>();
        private byte[] _envelopes = Array.Empty<byte>();

        internal ProgrammedSymbolSet(byte rws)
        {
            Rws = rws;
        }

        /// <summary>
        /// Gets the store's number: the RWS byte of Load PS (p. 5-19) and the SET byte of its
        /// Character Sets query reply descriptor (p. 6-25).
        /// </summary>
        public byte Rws { get; }

        /// <summary>
        /// Gets the local character set ID now tied to this store, X'40' to X'EF', or
        /// <see cref="FreeLcid"/> when the store is free (p. 5-19, 5-22).
        /// </summary>
        public byte Lcid { get; private set; } = FreeLcid;

        /// <summary>
        /// Gets the slot width in dots. 0 until the first Load PS names this store.
        /// </summary>
        public int SlotWidth { get; private set; }

        /// <summary>
        /// Gets the slot height in dots. 0 until the first Load PS names this store.
        /// </summary>
        public int SlotHeight { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the last Load PS asked for skip suppression: a row holding
        /// characters of this set has the skip after it suppressed (p. 5-19, 5-21).
        /// </summary>
        public bool SkipSuppress { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the last extended Load PS set APA = B'1', "not all
        /// points addressable", which lets a printer leave points out (p. 5-20, 5-23).
        /// </summary>
        public bool NotAllPointsAddressable { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the last extended Load PS set CB = B'1', no LCID compare
        /// for a local copy (p. 5-20, 5-23). It is also the CB bit of the query reply descriptor.
        /// </summary>
        public bool NoLcidCompare { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the last extended Load PS set OB = B'1': output only, the
        /// operator may not pick this set with its PS key (p. 5-20, 5-23).
        /// </summary>
        public bool NotOperatorSelectable { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the last extended Load PS set MULTID = B'1' (p. 5-20, 5-22).
        /// </summary>
        public bool MultipleLcid { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the last extended Load PS set USET = B'1': the Symbol
        /// Envelope Table is to be used (p. 5-20).
        /// </summary>
        public bool UseEnvelopeTable { get; private set; }

        /// <summary>
        /// Gets the number of width indentation pairs of this set's Symbol Envelope Table (NW,
        /// p. 5-24).
        /// </summary>
        public int WidthPairs { get; private set; }

        /// <summary>
        /// Gets the number of height indentation pairs of this set's Symbol Envelope Table (NH,
        /// p. 5-25).
        /// </summary>
        public int HeightPairs { get; private set; }

        /// <summary>
        /// Gets the plane mask of one dot of one symbol.
        /// </summary>
        /// <param name="code">
        /// The code point, X'40' to X'FE'.
        /// </param>
        /// <param name="x">
        /// The column, 0 at the left, below <see cref="SlotWidth"/>.
        /// </param>
        /// <param name="y">
        /// The row, 0 at the top, below <see cref="SlotHeight"/>.
        /// </param>
        /// <returns>
        /// 0 when the dot is off; else bit 0 blue, bit 1 red, bit 2 green (see the remarks on the
        /// class). 0 for a code outside X'40' to X'FE' or a dot outside the slot.
        /// </returns>
        public byte GetDot(byte code, int x, int y)
        {
            if (code < FirstCode || code > LastCode || (uint)x >= (uint)SlotWidth || (uint)y >= (uint)SlotHeight)
            {
                return 0;
            }

            return _dots[(((code - FirstCode) * SlotHeight) + y) * SlotWidth + x];
        }

        /// <summary>
        /// Gets one symbol's dots, row by row from the top, <see cref="SlotWidth"/> bytes per row,
        /// one plane mask per dot.
        /// </summary>
        /// <param name="code">
        /// The code point, X'40' to X'FE'.
        /// </param>
        /// <returns>
        /// The dots, valid until the next Load PS; empty for a code outside X'40' to X'FE' or before
        /// the first Load PS.
        /// </returns>
        public ReadOnlySpan<byte> GetSymbol(byte code)
        {
            if (code < FirstCode || code > LastCode || _dots.Length == 0)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            int size = SlotWidth * SlotHeight;
            return _dots.AsSpan((code - FirstCode) * size, size);
        }

        /// <summary>
        /// Gets one symbol's Symbol Envelope Table entry: <see cref="WidthPairs"/> pairs of (left,
        /// right) indents, then <see cref="HeightPairs"/> pairs of (bottom, top) indents (p. 5-25).
        /// All zeros for a code never given one (p. 5-23).
        /// </summary>
        /// <param name="code">
        /// The code point, X'40' to X'FE'.
        /// </param>
        /// <returns>
        /// The entry, valid until the next Load PS; empty when the set has no pairs.
        /// </returns>
        public ReadOnlySpan<byte> GetEnvelope(byte code)
        {
            int size = 2 * (WidthPairs + HeightPairs);
            if (code < FirstCode || code > LastCode || size == 0 || _envelopes.Length == 0)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            return _envelopes.AsSpan((code - FirstCode) * size, size);
        }

        internal void Release() => Lcid = FreeLcid;

        internal void Take(byte lcid, bool skip, byte flags8)
        {
            Lcid = lcid;
            SkipSuppress = skip;
            NotAllPointsAddressable = (flags8 & 0x80) != 0;
            NoLcidCompare = (flags8 & 0x40) != 0;
            NotOperatorSelectable = (flags8 & 0x20) != 0;
            MultipleLcid = (flags8 & 0x10) != 0;
            UseEnvelopeTable = (flags8 & 0x08) != 0;
        }

        // Makes the store slotWidth x slotHeight. A different size than before starts the store
        // again with every slot blank (this display's choice: the manual's devices have one fixed
        // slot size, so it never says what a size change does to loaded symbols).
        internal void Size(int slotWidth, int slotHeight)
        {
            if (slotWidth == SlotWidth && slotHeight == SlotHeight && _dots.Length != 0)
            {
                return;
            }

            SlotWidth = slotWidth;
            SlotHeight = slotHeight;
            _dots = new byte[SlotCount * slotWidth * slotHeight];
        }

        // CLEAR = B'1': every slot of the store is cleared, the envelope values too (p. 5-21, 5-25).
        internal void Clear()
        {
            Array.Clear(_dots);
            Array.Clear(_envelopes);
        }

        // NW and NH are set for the whole set (p. 5-24, 5-25). A change of either makes a new,
        // all-zero table (this display's choice: the entries of the old shape cannot be read in
        // the new one, and the manual does not say).
        internal void EnvelopePairs(int widthPairs, int heightPairs)
        {
            if (widthPairs == WidthPairs && heightPairs == HeightPairs && _envelopes.Length != 0)
            {
                return;
            }

            WidthPairs = widthPairs;
            HeightPairs = heightPairs;
            _envelopes = new byte[SlotCount * 2 * (widthPairs + heightPairs)];
        }

        internal void SetEnvelope(byte code, ReadOnlySpan<byte> pairs)
        {
            pairs.CopyTo(_envelopes.AsSpan((code - FirstCode) * pairs.Length, pairs.Length));
        }

        // Loads one shape of width x height dots into the slot for code, top left (p. 5-24, 6-23).
        // The planes in mask are cleared over the whole slot first, "as if the character slot was
        // cleared to zero immediately before" (p. 6-24); the other planes are left as they were.
        internal void Load(byte code, ReadOnlySpan<bool> shape, int width, int height, byte mask)
        {
            int size = SlotWidth * SlotHeight;
            Span<byte> slot = _dots.AsSpan((code - FirstCode) * size, size);
            byte keep = (byte)~mask;
            for (int i = 0; i < slot.Length; i++)
            {
                slot[i] &= keep;
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (shape[(y * width) + x])
                    {
                        slot[(y * SlotWidth) + x] |= mask;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The loadable character sets of a display and the Load Programmed Symbols structured field,
    /// X'06' (GA23-0059-4 p. 5-18 to 5-33).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A renderer asks "what does code X in the set with LCID Y look like" through
    /// <see cref="Find"/> and then <see cref="ProgrammedSymbolSet.GetDot"/> or
    /// <see cref="ProgrammedSymbolSet.GetSymbol"/>. The LCID is the character set attribute value
    /// that SA, SFE and MF carry (p. 4-19).
    /// </para>
    /// <para>
    /// Built: the basic and the extended form; data types 1 to 6, so the three uncompressed
    /// layouts and the compression of p. 5-26 to 5-33; CLEAR, SKIP, the byte 8 flags, LW/LH, COLOR
    /// on a triple-plane set (one plane, all planes, or a colour value), ECHAR, and the Symbol
    /// Envelope Table and Terminator self-defining parameters. Refused out loud: type 8 (vector),
    /// whose data format the manual does not give; 2-byte sets (SUBSN, ST.SUBS); anything Note 1
    /// on p. 5-21 rejects.
    /// </para>
    /// <para>
    /// Where the manual contradicts itself the reading its own worked example supports is built
    /// (see 3270-DATA-STREAM.md section 15 in the RetroTerm repository): rule 2's header is B'10'
    /// (p. 5-32; p. 5-29 prints B'1'), and rule 3 compares slices 1 and 2 with zero (Figure 5-6
    /// p. 5-33; the text on p. 5-29 says "a 1 element").
    /// </para>
    /// </remarks>
    public sealed class ProgrammedSymbolStore
    {
        /// <summary>
        /// The RWS number of the first loadable set. The stores are numbered from here up. SET
        /// X'00' is the base set and X'01' the alternate (Graphic Escape) set (p. 6-25), so the
        /// loadable ones start at X'02' - this display's choice; the manual leaves the numbers to
        /// the device.
        /// </summary>
        public const byte FirstRws = 0x02;

        /// <summary>
        /// The Load PS format types this store takes, as the FORM field of the Character Sets query
        /// reply: bit i set means type i, bit 0 the high bit (p. 6-22, 6-24). Types 1 to 6.
        /// </summary>
        public const uint FormTypes = 0x7E000000;

        private readonly ProgrammedSymbolSet[] _sets;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgrammedSymbolStore"/> class: every
        /// store free and blank, as at power on (Table C-1 p. C-1, PS Content and PS LCID).
        /// </summary>
        /// <param name="count">
        /// How many loadable sets, numbered from <see cref="FirstRws"/>.
        /// </param>
        public ProgrammedSymbolStore(int count)
        {
            if (count < 0 || count > 0xFF - FirstRws)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _sets = new ProgrammedSymbolSet[count];
            for (int i = 0; i < count; i++)
            {
                _sets[i] = new ProgrammedSymbolSet((byte)(FirstRws + i));
            }
        }

        /// <summary>
        /// Gets the number of loadable sets.
        /// </summary>
        public int Count => _sets.Length;

        /// <summary>
        /// Gets one loadable set by its index, 0 to <see cref="Count"/> - 1.
        /// </summary>
        /// <param name="index">
        /// The index; the set's RWS number is <see cref="FirstRws"/> + index.
        /// </param>
        /// <returns>
        /// The set.
        /// </returns>
        public ProgrammedSymbolSet this[int index] => _sets[index];

        /// <summary>
        /// Finds the loadable set that has a local character set ID.
        /// </summary>
        /// <param name="lcid">
        /// The LCID, as SA, SFE or MF name it.
        /// </param>
        /// <returns>
        /// The set, or null when no store has that LCID, or for X'FF' (free, which cannot be
        /// picked, p. 6-25). When MULTID left two stores with one LCID (p. 5-22) the one with the
        /// lowest RWS number is returned - this display's choice: with one slot size for every set
        /// the manual gives nothing to pick between them by.
        /// </returns>
        public ProgrammedSymbolSet? Find(byte lcid)
        {
            if (lcid == ProgrammedSymbolSet.FreeLcid)
            {
                return null;
            }

            for (int i = 0; i < _sets.Length; i++)
            {
                if (_sets[i].Lcid == lcid)
                {
                    return _sets[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Runs one Load Programmed Symbols structured field.
        /// </summary>
        /// <param name="field">
        /// The whole structured field, its two length bytes first.
        /// </param>
        /// <param name="slotWidth">
        /// The device's slot width, SDW of the Character Sets reply.
        /// </param>
        /// <param name="slotHeight">
        /// The device's slot height, SDH.
        /// </param>
        /// <param name="triplePlane">
        /// Whether the sets are triple-plane (TRIPLE in the reply's descriptors).
        /// </param>
        /// <returns>
        /// Null when it ran; else why it was rejected, with the page. A rejection found before the
        /// store is touched leaves it and its LCID as they were; one found while loading leaves what
        /// was loaded so far and the new LCID (p. 5-22).
        /// </returns>
        public string? Load(ReadOnlySpan<byte> field, int slotWidth, int slotHeight, bool triplePlane)
        {
            if (field.Length < 7)
            {
                return "Load PS shorter than its 7-byte basic form (p. 5-18, 5-19).";
            }

            byte flags = field[3];
            bool extended = (flags & 0x80) != 0;
            bool clear = (flags & 0x40) != 0;
            bool skip = (flags & 0x20) != 0;
            int type = flags & 0x1F;
            byte lcid = field[4];
            byte first = field[5];
            byte rws = field[6];

            if (type == 8)
            {
                return "Load PS data type 8 (vector) is not built: this manual names it (p. 5-19) but gives no format for its data, and FORM in the Character Sets reply does not offer it.";
            }

            if (type < 1 || type > 6)
            {
                return "Load PS data type " + type + " is reserved (p. 5-19; Note 1 p. 5-21).";
            }

            if (!((lcid >= 0x40 && lcid <= 0xEF) || lcid == ProgrammedSymbolSet.FreeLcid))
            {
                return "Load PS LCID X'" + Hex(lcid) + "' is reserved: X'40' to X'EF' or X'FF' (p. 5-19).";
            }

            if (first < 0x41 || first > 0xFE)
            {
                return "Load PS CHAR X'" + Hex(first) + "' is outside X'41' to X'FE' (p. 5-19, 5-23; sense X'1005' or X'1001').";
            }

            ProgrammedSymbolSet? set = null;
            for (int i = 0; i < _sets.Length; i++)
            {
                if (_sets[i].Rws == rws)
                {
                    set = _sets[i];
                }
            }

            if (set == null)
            {
                return "Load PS RWS X'" + Hex(rws) + "' is not a loadable set of this display (the Character Sets reply lists X'" + Hex(FirstRws) + "' to X'" + Hex((byte)(FirstRws + _sets.Length - 1)) + "', p. 5-22, 6-25).";
            }

            // The extended form: byte 7 is P LENGTH, counting itself; "omitted parameters are
            // assumed to have the defined default indicated by the zero value" (p. 5-20).
            Span<byte> p = stackalloc byte[18];
            int dataStart = 7;
            if (extended)
            {
                if (field.Length < 8)
                {
                    return "Extended Load PS with no P LENGTH byte (p. 5-19, 5-20).";
                }

                int pLength = field[7];
                if (pLength < 1 || 7 + pLength > field.Length)
                {
                    return "Load PS P LENGTH " + pLength + " does not fit the field (p. 5-20).";
                }

                if (pLength > 11)
                {
                    // Bytes 7 to 17 are every parameter the table defines (p. 5-20, 5-21); more
                    // would be values "other than those shown" (Note 1 p. 5-21).
                    return "Load PS P LENGTH " + pLength + " is longer than the 11 bytes of defined parameters (bytes 7 to 17, p. 5-20, 5-21).";
                }

                field.Slice(7, pLength).CopyTo(p.Slice(7));
                dataStart = 7 + pLength;
            }

            byte flags8 = p[8];
            if ((flags8 & 0x06) != 0)
            {
                return "Load PS byte 8 reserved bits 5-6 are not zero (p. 5-20; Note 1 p. 5-21).";
            }

            bool selfDefining = (flags8 & 0x01) != 0;
            int lw = p[9];
            int lh = p[10];
            byte color = p[12];
            byte last = p[14];
            int nw = p[15];
            int nh = p[16];

            if (p[11] != 0)
            {
                return "Load PS SUBSN X'" + Hex(p[11]) + "': 2-byte sets are not built (CH2 is 0 in the Character Sets reply, p. 5-20, 6-23).";
            }

            if (p[13] != 0)
            {
                return "Load PS ST. SUBS X'" + Hex(p[13]) + "': 2-byte sets are not built (p. 5-21, 6-23).";
            }

            if (p[17] != 0)
            {
                return "Load PS byte 17 is reserved and must be zero (p. 5-21).";
            }

            // The shape size. Types 1 and 2: a 9 x 16 cell, "only the values LW = 9 and LH = 16 are
            // valid" (p. 5-22, 5-24). Types 3 to 6: LW and LH, or the slot size when they are not
            // given or zero (p. 5-22, 6-26 rules 1 to 3; MS is 0, so SDW and SDH). One given and
            // the other zero takes the slot size for the zero one - this display's choice: the
            // text speaks of both together only.
            int width;
            int height;
            if (type <= 2)
            {
                if ((lw != 0 || lh != 0) && (lw != 9 || lh != 16))
                {
                    return "Load PS type " + type + " with LW " + lw + " and LH " + lh + ": only LW = 9 and LH = 16 are valid for types 1 and 2 (p. 5-24).";
                }

                width = 9;
                height = 16;
            }
            else
            {
                width = lw != 0 ? lw : slotWidth;
                height = lh != 0 ? lh : slotHeight;
            }

            if (width > slotWidth || height > slotHeight)
            {
                return "Load PS symbols " + width + " x " + height + " do not fit the " + slotWidth + " x " + slotHeight + " slot (p. 5-24, 6-23).";
            }

            // COLOR (p. 5-20, 5-24). Triple-plane: X'00' all planes, B'001', B'010', B'100' one
            // plane, or a colour attribute value of the Color reply (X'F1' to X'F7' here) split into
            // its primaries; "other color values are reserved and rejected". Single-plane: only
            // X'00'. p. 5-24 rejects B'001', B'010' and B'000' there, but B'000' is also the value
            // an omitted COLOR takes (p. 5-20), and p. 5-20 calls X'00' "All planes", so X'00' is
            // taken and every other value refused - this display's reading of a contradiction.
            byte mask;
            if (triplePlane)
            {
                if (color == 0x00)
                {
                    mask = 0x07;
                }
                else if (color == 0x01 || color == 0x02 || color == 0x04)
                {
                    mask = color;
                }
                else if (color >= 0xF1 && color <= 0xF7)
                {
                    mask = (byte)(color & 0x07);
                }
                else
                {
                    return "Load PS COLOR X'" + Hex(color) + "' is reserved: X'00', X'01', X'02', X'04' or a colour X'F1' to X'F7' (p. 5-20, 5-24).";
                }
            }
            else
            {
                if (color != 0x00)
                {
                    return "Load PS COLOR X'" + Hex(color) + "' on a single-plane set: only X'00' is taken (p. 5-20, 5-24).";
                }

                mask = 0x07;
            }

            if (last != 0 && last < first)
            {
                return "Load PS ECHAR X'" + Hex(last) + "' is below CHAR X'" + Hex(first) + "' (p. 5-24).";
            }

            // The self-defining parameters: Symbol Envelope Table entries (L, X'01', then the NW
            // width pairs and the NH height pairs), ended by the Terminator X'02FF' (p. 5-25, 5-26).
            // All checked here, before the store changes.
            int envelopeSize = 2 * (nw + nh);
            int envelopeStart = 0;
            int envelopeCount = 0;
            if (selfDefining)
            {
                int pos = dataStart;
                envelopeStart = pos;
                while (true)
                {
                    if (pos + 2 > field.Length)
                    {
                        return "Load PS self-defining parameters with no Terminator X'02FF' (p. 5-26).";
                    }

                    int length = field[pos];
                    byte id = field[pos + 1];
                    if (id == 0xFF && length == 2)
                    {
                        dataStart = pos + 2;
                        break;
                    }

                    if (id != 0x01)
                    {
                        return "Load PS self-defining parameter X'" + Hex(id) + "' (length " + length + ") is not one this manual defines (p. 5-25, 5-26).";
                    }

                    if (length != 2 + envelopeSize || pos + length > field.Length)
                    {
                        return "Load PS Symbol Envelope Table length " + length + ": with NW " + nw + " and NH " + nh + " it is " + (2 + envelopeSize) + " (p. 5-25).";
                    }

                    for (int i = 0; i < nw; i++)
                    {
                        if (field[pos + 2 + (2 * i)] + field[pos + 3 + (2 * i)] > width)
                        {
                            return "Load PS Symbol Envelope Table width pair " + (i + 1) + " adds up to more than the cell width " + width + " (p. 5-25).";
                        }
                    }

                    for (int i = 0; i < nh; i++)
                    {
                        int at = pos + 2 + (2 * nw) + (2 * i);
                        if (field[at] + field[at + 1] > height)
                        {
                            return "Load PS Symbol Envelope Table height pair " + (i + 1) + " adds up to more than the cell height " + height + " (p. 5-25).";
                        }
                    }

                    envelopeCount++;
                    pos += length;
                }
            }

            // NW and NH set the numbers for the whole set (p. 5-24, 5-25), with or without an
            // envelope table in this field, so they are taken below either way.

            // From here the store changes. The LCID first: it is released from every other store
            // that has it - always with MULTID = B'0'; with B'1' only from a store loaded with
            // MULTID = B'0' (p. 5-22) - and this store's old LCID goes with it.
            bool multid = (flags8 & 0x10) != 0;
            if (lcid != ProgrammedSymbolSet.FreeLcid)
            {
                for (int i = 0; i < _sets.Length; i++)
                {
                    ProgrammedSymbolSet other = _sets[i];
                    if (other != set && other.Lcid == lcid && (!multid || !other.MultipleLcid))
                    {
                        other.Release();
                    }
                }
            }

            set.Take(lcid, skip, flags8);
            set.Size(slotWidth, slotHeight);
            set.EnvelopePairs(nw, nh);
            if (clear)
            {
                set.Clear();
            }

            // ECHAR: more symbols (or envelope entries) than ECHAR - CHAR + 1 is a reject, with the
            // slots CHAR to ECHAR loaded (p. 5-24). X'FE' is the last slot in any case (p. 5-22).
            int limit = last != 0 ? last : ProgrammedSymbolSet.LastCode;

            if (envelopeCount > 0)
            {
                int pos = envelopeStart + 2;
                for (int i = 0; i < envelopeCount; i++)
                {
                    int code = first + i;
                    if (code > limit)
                    {
                        return TooMany(last, "Symbol Envelope Table entries");
                    }

                    set.SetEnvelope((byte)code, field.Slice(pos, envelopeSize));
                    pos += 2 + envelopeSize;
                }
            }

            ReadOnlySpan<byte> data = field.Slice(dataStart);
            return (type & 1) == 1
                ? LoadUncompressed(set, data, type, width, height, first, limit, last, mask)
                : LoadCompressed(set, data, type, width, height, first, limit, last, mask);
        }

        // Decompresses one shape's slices from the bit string (p. 5-27 to 5-30): the header names
        // the comparison rule, then per bunch of four elements a 0 bit (all match the reference) or
        // a 1 bit and, per element, 0 (match) or 1 and its 4 bits. Returns null, or the reject.
        internal static string? Decompress(ReadOnlySpan<byte> data, ref int bit, Span<byte> elements, int elementsPerSlice)
        {
            int total = data.Length * 8;

            // Header: B'0' rule 1, B'10' rule 2, B'110' rule 3, B'1110' rule 4 (p. 5-28, 5-29).
            // p. 5-29 prints rule 2 as B'1'; the worked example of rule 2 says "the header is B'10'"
            // and its bit string starts 10 (p. 5-32), which is what is built.
            int rule = 0;
            for (int ones = 0; ones < 4; ones++)
            {
                if (bit >= total)
                {
                    return "Load PS compressed data ends inside a symbol header (p. 5-28).";
                }

                if (!Bit(data, bit++))
                {
                    rule = ones + 1;
                    break;
                }
            }

            if (rule == 0)
            {
                return "Load PS compressed header B'1111' is not one of the four rules (p. 5-29).";
            }

            if (rule == 4)
            {
                // A blank symbol: no comparison bits follow (p. 5-29).
                elements.Clear();
                return null;
            }

            for (int start = 0; start < elements.Length; start += 4)
            {
                int end = Math.Min(start + 4, elements.Length);
                if (bit >= total)
                {
                    return "Load PS compressed data ends inside a symbol (p. 5-29, 5-30).";
                }

                bool differs = Bit(data, bit++);
                for (int k = start; k < end; k++)
                {
                    byte reference = Reference(elements, k, elementsPerSlice, rule);
                    if (!differs)
                    {
                        elements[k] = reference;
                        continue;
                    }

                    if (bit >= total)
                    {
                        return "Load PS compressed data ends inside a symbol (p. 5-29, 5-30).";
                    }

                    if (!Bit(data, bit++))
                    {
                        elements[k] = reference;
                        continue;
                    }

                    if (bit + 4 > total)
                    {
                        return "Load PS compressed data ends inside an element (p. 5-29).";
                    }

                    int value = 0;
                    for (int b = 0; b < 4; b++)
                    {
                        value = (value << 1) | (Bit(data, bit++) ? 1 : 0);
                    }

                    elements[k] = (byte)value;
                }
            }

            return null;
        }

        private static string? LoadUncompressed(ProgrammedSymbolSet set, ReadOnlySpan<byte> data, int type, int width, int height, byte first, int limit, byte last, byte mask)
        {
            // Type 1: 18 bytes. Types 3 and 5: LW x LH bits, padded to a whole byte (p. 5-22).
            int bytesPerSymbol = type == 1 ? 18 : ((width * height) + 7) / 8;
            Span<bool> shape = stackalloc bool[width * height];
            int pos = 0;
            int code = first;
            while (data.Length - pos >= bytesPerSymbol)
            {
                if (code > limit)
                {
                    return TooMany(last, "symbols");
                }

                ReadOnlySpan<byte> symbol = data.Slice(pos, bytesPerSymbol);
                if (type == 1)
                {
                    Type1(symbol, shape);
                }
                else
                {
                    for (int i = 0; i < width * height; i++)
                    {
                        // Type 3 rows top to bottom, type 5 columns left to right (p. 5-22).
                        int x = type == 3 ? i % width : i / height;
                        int y = type == 3 ? i / width : i % height;
                        shape[(y * width) + x] = Bit(symbol, i);
                    }
                }

                set.Load((byte)code, shape, width, height, mask);
                pos += bytesPerSymbol;
                code++;
            }

            if (pos < data.Length)
            {
                // "If there are any excess bits, they are ignored and a -RSP (with a sense code of
                // X'1001' or X'1005') is returned" (p. 5-22).
                return code > limit
                    ? TooMany(last, "symbols")
                    : "Load PS data ends " + (data.Length - pos) + " bytes into a symbol of " + bytesPerSymbol + "; the whole symbols before it are loaded (p. 5-22, sense X'1001' or X'1005').";
            }

            return null;
        }

        private static string? LoadCompressed(ProgrammedSymbolSet set, ReadOnlySpan<byte> data, int type, int width, int height, byte first, int limit, byte last, byte mask)
        {
            // Type 2: 18 slices of 8 bits (Figure 5-2). Type 4: LH rows of LW bits. Type 6: LW
            // columns of LH bits. "Each slice is extended as necessary with zero bits so that the
            // slice length is a multiple of 4 bits" (p. 5-27).
            int slices = type == 2 ? 18 : type == 4 ? height : width;
            int sliceBits = type == 2 ? 8 : type == 4 ? width : height;
            int elementsPerSlice = (sliceBits + 3) / 4;
            Span<byte> elements = stackalloc byte[slices * elementsPerSlice];
            Span<byte> type1 = stackalloc byte[18];
            Span<bool> shape = stackalloc bool[width * height];
            int total = data.Length * 8;
            int bit = 0;
            int code = first;
            if (total == 0)
            {
                // No data: "the operation executes anyway ... and no data is loaded" (p. 5-18).
                return null;
            }

            while (true)
            {
                // The terminator: 1 bits to the end of the byte, at least 4, so 4 to 11 (p. 5-30).
                // B'1111' is not a header (p. 5-29), so four 1 bits where a symbol would start can
                // only be the terminator.
                if (AllOnes(data, bit, total))
                {
                    int ones = total - bit;
                    if (ones < 4)
                    {
                        return "Load PS compressed data ends with " + ones + " terminator bits; at least 4 are needed (p. 5-30).";
                    }

                    if (ones > 11)
                    {
                        return "Load PS compressed data ends with " + ones + " 1 bits; the terminator is 4 to 11 (p. 5-30).";
                    }

                    return null;
                }

                if (code > limit)
                {
                    return TooMany(last, "symbols");
                }

                string? error = Decompress(data, ref bit, elements, elementsPerSlice);
                if (error != null)
                {
                    return error;
                }

                for (int s = 0; s < slices; s++)
                {
                    for (int b = 0; b < sliceBits; b++)
                    {
                        int element = elements[(s * elementsPerSlice) + (b / 4)];
                        bool on = ((element >> (3 - (b % 4))) & 1) != 0;
                        if (type == 2)
                        {
                            type1[s] = (byte)(type1[s] & ~(0x80 >> b));
                            if (on)
                            {
                                type1[s] |= (byte)(0x80 >> b);
                            }
                        }
                        else if (type == 4)
                        {
                            shape[(s * width) + b] = on;
                        }
                        else
                        {
                            shape[(b * width) + s] = on;
                        }
                    }
                }

                if (type == 2)
                {
                    Type1(type1, shape);
                }

                set.Load((byte)code, shape, width, height, mask);
                code++;
            }
        }

        // The reference element for element k (p. 5-28, 5-29). Rule 1: zero. Rule 2: the same
        // element of the previous slice, zero for slice 1. Rule 3: the same element two slices
        // back; for slices 1 and 2 the text says "a 1 element", but the worked example compares
        // them with X'0000' (Figure 5-6 p. 5-33) and its bit string only decodes to Figure 5-3
        // that way, so zero is built.
        private static byte Reference(ReadOnlySpan<byte> elements, int k, int elementsPerSlice, int rule)
        {
            int back = rule == 2 ? 1 : rule == 3 ? 2 : 0;
            int slice = k / elementsPerSlice;
            if (back == 0 || slice < back)
            {
                return 0;
            }

            return elements[k - (back * elementsPerSlice)];
        }

        // Type 1, a 9 x 16 cell (Figure 5-2 p. 5-27, Figure 5-3 p. 5-28): slices 1 and 2 are the
        // leftmost column, rows 0-7 then 8-15, high bit at the top; slices 3 to 18 are the rows,
        // top to bottom, columns 1 to 8, high bit at the left.
        private static void Type1(ReadOnlySpan<byte> slices, Span<bool> shape)
        {
            for (int y = 0; y < 16; y++)
            {
                byte column = slices[y < 8 ? 0 : 1];
                shape[y * 9] = ((column >> (7 - (y % 8))) & 1) != 0;
                for (int x = 1; x < 9; x++)
                {
                    shape[(y * 9) + x] = ((slices[2 + y] >> (8 - x)) & 1) != 0;
                }
            }
        }

        private static string TooMany(byte last, string what)
        {
            return last != 0
                ? "Load PS has more " + what + " than ECHAR - CHAR + 1; those up to ECHAR X'" + Hex(last) + "' are loaded (p. 5-24)."
                : "Load PS has more " + what + " after the X'FE' slot was loaded; the rest is ignored (p. 5-22, sense X'1001' or X'1005').";
        }

        private static bool Bit(ReadOnlySpan<byte> data, int bit) => ((data[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;

        private static bool AllOnes(ReadOnlySpan<byte> data, int bit, int total)
        {
            for (int b = bit; b < total; b++)
            {
                if (!Bit(data, b))
                {
                    return false;
                }
            }

            return true;
        }

        private static string Hex(byte value) => value.ToString("X2", CultureInfo.InvariantCulture);
    }
}
