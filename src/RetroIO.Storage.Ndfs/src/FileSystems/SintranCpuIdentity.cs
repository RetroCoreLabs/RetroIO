//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Storage.Ndfs.FileSystems
{
    /// <summary>
    /// A pack does not hold what the CPU identity reader or writer needs: no symbol lists, no
    /// <c>SYSNO</c> or <c>FCPUN</c> symbol, no segment table, no name records, or a copy whose file
    /// does not hold the word. <see cref="Identity"/> carries what was found before the refusal.
    /// </summary>
    public sealed class SintranCpuIdentityException : IOException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SintranCpuIdentityException"/> class.
        /// </summary>
        /// <param name="message">
        /// Which piece is missing.
        /// </param>
        /// <param name="identity">
        /// What the reader found before it stopped, or <c>null</c>.
        /// </param>
        public SintranCpuIdentityException(string message, SintranCpuIdentityInfo? identity = null)
            : base(message)
        {
            Identity = identity;
        }

        /// <summary>
        /// Gets what the reader found before it stopped, or <c>null</c>.
        /// </summary>
        public SintranCpuIdentityInfo? Identity { get; }
    }

    /// <summary>
    /// Pure (device-independent) logic for the SINTRAN III CPU number and CPU type on a system pack:
    /// symbol list parsing, the RT-loader packed segment names, the segment table arithmetic and ND
    /// even parity. The device-bound entry points live on <see cref="NdfsFileSystem"/>
    /// (<c>ReadSintranCpuIdentity</c> / <c>WriteSintranCpuIdentity</c>).
    ///
    /// A SINTRAN pack carries its CPU number twice (<c>SYSNO</c> and <c>FCPUN</c>) and its CPU type once
    /// (<c>HWINF+2</c>) in the resident data page, stored on disk in the segments <c>S3IDPIT</c> (warm
    /// start) and <c>S3SDPIT</c> (cold start). Nothing is hard-coded: the word addresses come from the
    /// pack's own symbol lists, the segment numbers from the DEFINED SEGMENT NAME records (type 25B) in
    /// <c>RTFIL:DATA</c>, the pages from the segment table. Ported behaviour-for-behaviour from
    /// norskdata-ndfs <c>ndfs-c/src/sintran_identity.c</c>; the measurements are in NDInsight chapter
    /// <c>SINTRAN/OS/28-CPU-NUMBER-AND-TYPE-ON-DISK.md</c>.
    /// </summary>
    public static class SintranCpuIdentity
    {
        /// <summary>
        /// Segment name of the image copy of the resident data page.
        /// </summary>
        public const string SegmentNameImageDpit = "S3IDPIT";

        /// <summary>
        /// Segment name of the save copy of the resident data page.
        /// </summary>
        public const string SegmentNameSaveDpit = "S3SDPIT";

        /// <summary>
        /// RT-loader record type of a DEFINED SEGMENT NAME record (octal 25).
        /// </summary>
        public const int RtfilTypeSegmentName = 0x15;

        /// <summary>
        /// A segment name is at most 8 SIXBIT characters (three 16-bit words).
        /// </summary>
        public const int SegmentNameMax = 8;

        internal const int PageBytes = 2048;
        internal const int SegEntryBytes = 16;
        internal const int NameRecordBytes = 16;

        /// <summary>
        /// Big-endian 16-bit word read.
        /// </summary>
        internal static int Word(ReadOnlySpan<byte> b, long o) => (b[(int)o] << 8) | b[(int)o + 1];

        /// <summary>
        /// Names the SINTRAN version from the <c>INIBU</c> symbol value (067172 K, 074123 L, 102327 M).
        /// Only used to print the letter; nothing is addressed through it.
        /// </summary>
        /// <param name="inibu">
        /// The value of the <c>INIBU</c> symbol.
        /// </param>
        /// <returns>
        /// K, L or M, or <c>?</c> when the value is none of the known ones.
        /// </returns>
        public static char VersionFromInibu(int inibu)
        {
            switch (inibu)
            {
                case 0x6E7A: return 'K';
                case 0x7853: return 'L';
                case 0x84D7: return 'M';
                default: return '?';
            }
        }

        /// <summary>
        /// Byte offset of word address <paramref name="addr"/> inside the segment file, or -1 when the
        /// segment does not cover it. The logical base is the low six bits of LOGAD in pages:
        /// byte = MADR*2048 + 2*(addr - base).
        /// </summary>
        /// <param name="logad">
        /// The LOGAD word of the segment table entry.
        /// </param>
        /// <param name="segle">
        /// The length of the segment in pages.
        /// </param>
        /// <param name="madr">
        /// The first page of the segment inside its segment file.
        /// </param>
        /// <param name="addr">
        /// The word address to locate.
        /// </param>
        /// <returns>
        /// The byte offset, or -1.
        /// </returns>
        public static long SegmentWordOffset(int logad, int segle, int madr, int addr)
        {
            int base_ = (logad & 0x3F) * 1024;
            if (addr < base_ || addr >= base_ + segle * 1024) return -1;
            return (long)madr * PageBytes + (long)(addr - base_) * 2;
        }

        /// <summary>
        /// Finds <c>NAME=OOOOOO</c> at the start of a line (leading blanks allowed) in a parity-stripped
        /// symbol list. The name must be followed by <c>=</c>, so <c>CPN</c> never matches <c>33CPN</c>.
        /// </summary>
        /// <param name="text">
        /// The symbol list with parity stripped.
        /// </param>
        /// <param name="name">
        /// The symbol name.
        /// </param>
        /// <param name="value">
        /// Set to the octal value.
        /// </param>
        /// <param name="valueOffset">
        /// Set to the offset of the first digit in <paramref name="text"/>.
        /// </param>
        /// <param name="valueWidth">
        /// Set to the number of octal digits.
        /// </param>
        /// <returns>
        /// True when the symbol was found.
        /// </returns>
        public static bool FindSymbol(ReadOnlySpan<byte> text, string name, out int value, out int valueOffset, out int valueWidth)
        {
            value = 0;
            valueOffset = -1;
            valueWidth = 0;
            if (string.IsNullOrEmpty(name)) return false;

            int nlen = name.Length;
            int n = text.Length;
            int i = 0;
            while (i < n)
            {
                int p = i;
                while (p < n && (text[p] == 0x20 || text[p] == 0x09)) p++;
                if (p + nlen < n && text[p + nlen] == 0x3D)
                {
                    bool same = true;
                    for (int k = 0; k < nlen; k++)
                    {
                        if (text[p + k] != (byte)name[k]) { same = false; break; }
                    }
                    if (same)
                    {
                        int d = p + nlen + 1;
                        int v = 0;
                        int width = 0;
                        while (d + width < n && text[d + width] >= 0x30 && text[d + width] <= 0x37)
                        {
                            v = v * 8 + (text[d + width] - 0x30);
                            width++;
                        }
                        if (width > 0)
                        {
                            value = v;
                            valueOffset = d;
                            valueWidth = width;
                            return true;
                        }
                    }
                }
                while (i < n && text[i] != 0x0A && text[i] != 0x0D) i++;
                while (i < n && (text[i] == 0x0A || text[i] == 0x0D)) i++;
            }
            return false;
        }

        /// <summary>
        /// Decodes an RT-loader packed segment name: SIXBIT characters, right-justified base 64 across
        /// three words (1 to 26 are A to Z, 0x30 to 0x39 are the digits).
        /// </summary>
        /// <param name="w0">
        /// The first word.
        /// </param>
        /// <param name="w1">
        /// The second word.
        /// </param>
        /// <param name="w2">
        /// The third word.
        /// </param>
        /// <returns>
        /// The name, or <c>null</c> when the words are not a name.
        /// </returns>
        public static string? DecodePackedName(int w0, int w1, int w2)
        {
            ulong n = ((ulong)(uint)w0 << 32) | ((ulong)(uint)w1 << 16) | (uint)w2;
            if (n == 0) return null;
            Span<char> rev = stackalloc char[SegmentNameMax];
            int count = 0;
            while (n != 0)
            {
                int c6 = (int)(n & 0x3F);
                n >>= 6;
                if (count >= SegmentNameMax) return null;
                char ch;
                if (c6 >= 1 && c6 <= 26) ch = (char)('A' + (c6 - 1));
                else if (c6 >= 0x30 && c6 <= 0x39) ch = (char)c6;
                else return null;
                rev[count++] = ch;
            }
            Span<char> outChars = stackalloc char[count];
            for (int i = 0; i < count; i++) outChars[i] = rev[count - 1 - i];
            return new string(outChars);
        }

        /// <summary>
        /// Walks every well-formed DEFINED SEGMENT NAME record in <paramref name="buf"/> on word
        /// boundaries and calls <paramref name="onRecord"/> with the segment number and the name.
        /// The record is eight words: the packed name in words 0 to 2, the segment number in words 3
        /// and 6, the type 25B in word 5, zero in words 4 and 7.
        /// </summary>
        /// <param name="buf">
        /// <c>RTFIL:DATA</c>, or SEGFIL0 (the S3RTFIL segment holds copies of the table).
        /// </param>
        /// <param name="onRecord">
        /// Called for each record; return false to stop the walk.
        /// </param>
        public static void WalkNameRecords(ReadOnlySpan<byte> buf, Func<int, string, bool> onRecord)
        {
            int size = buf.Length;
            for (int o = 0; o + NameRecordBytes <= size; o += 2)
            {
                if (Word(buf, o + 10) != RtfilTypeSegmentName) continue;
                if (Word(buf, o + 8) != 0 || Word(buf, o + 14) != 0) continue;
                int segno = Word(buf, o + 6);
                if (segno == 0 || segno > 0xFF || segno != Word(buf, o + 12)) continue;
                string? name = DecodePackedName(Word(buf, o), Word(buf, o + 2), Word(buf, o + 4));
                if (name == null) continue;
                if (!onRecord(segno, name)) return;
            }
        }

        /// <summary>
        /// Segment number the first DEFINED SEGMENT NAME record for <paramref name="name"/> gives, or -1.
        /// </summary>
        /// <param name="buf">
        /// <c>RTFIL:DATA</c>, or SEGFIL0.
        /// </param>
        /// <param name="name">
        /// The segment name, for example <c>S3IDPIT</c>.
        /// </param>
        /// <returns>
        /// The segment number, or -1.
        /// </returns>
        public static int FindSegmentByName(ReadOnlySpan<byte> buf, string name)
        {
            if (buf.Length < NameRecordBytes) return -1;
            int found = -1;
            WalkNameRecords(buf, (segno, decoded) =>
            {
                if (decoded != name) return true;
                found = segno;
                return false;
            });
            return found;
        }

        /// <summary>
        /// Every DEFINED SEGMENT NAME record in <paramref name="buf"/>: segment number to name. The first
        /// record for a number wins.
        /// </summary>
        /// <param name="buf">
        /// <c>RTFIL:DATA</c>, or SEGFIL0.
        /// </param>
        /// <returns>
        /// The names found.
        /// </returns>
        public static Dictionary<int, string> CollectSegmentNames(ReadOnlySpan<byte> buf)
        {
            var names = new Dictionary<int, string>();
            if (buf.Length < NameRecordBytes) return names;
            WalkNameRecords(buf, (segno, name) =>
            {
                if (!names.ContainsKey(segno)) names[segno] = name;
                return true;
            });
            return names;
        }

        /// <summary>
        /// Whether any byte in <paramref name="data"/> has bit 7 set, which on a SINTRAN text file means
        /// it is stored with ND even parity.
        /// </summary>
        /// <param name="data">
        /// The file bytes.
        /// </param>
        /// <returns>
        /// True when at least one byte has bit 7 set.
        /// </returns>
        public static bool HasParity(ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if ((data[i] & 0x80) != 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Clears bit 7 of every byte (the ND parity bit) into a new array.
        /// </summary>
        /// <param name="data">
        /// The file bytes.
        /// </param>
        /// <returns>
        /// A copy with bit 7 cleared.
        /// </returns>
        public static byte[] StripParity(ReadOnlySpan<byte> data)
        {
            var outBytes = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) outBytes[i] = (byte)(data[i] & 0x7F);
            return outBytes;
        }

        /// <summary>
        /// Sets ND even parity on every byte in place: bit 7 is set when the low seven bits hold an odd
        /// number of ones, so every byte has an even number of ones.
        /// </summary>
        /// <param name="data">
        /// The bytes to change.
        /// </param>
        public static void SetParity(Span<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                int v = data[i] & 0x7F;
                int ones = 0;
                for (int b = 0; b < 7; b++) ones += (v >> b) & 1;
                data[i] = (byte)((ones & 1) != 0 ? v | 0x80 : v);
            }
        }

        /// <summary>
        /// Builds the digits of a <c>NAME=OOOOOO</c> symbol value: <paramref name="width"/> octal digits,
        /// with ND even parity when <paramref name="parity"/> is set.
        /// </summary>
        /// <param name="value">
        /// The new value.
        /// </param>
        /// <param name="width">
        /// The number of digits the symbol is written with (6 holds any 16-bit word).
        /// </param>
        /// <param name="parity">
        /// Whether the list is stored with parity.
        /// </param>
        /// <returns>
        /// The digit bytes.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value does not fit the digits that are there.
        /// </exception>
        public static byte[] EncodeSymbolDigits(int value, int width, bool parity)
        {
            if (width <= 0 || width > 15) throw new ArgumentOutOfRangeException(nameof(width));
            if (width < 6 && value >= (1 << (3 * width)))
                throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit {width} octal digits");
            var digits = new byte[width];
            int v = value;
            for (int i = width - 1; i >= 0; i--)
            {
                digits[i] = (byte)(0x30 + (v & 7));
                v >>= 3;
            }
            if (parity) SetParity(digits);
            return digits;
        }
    }
}
