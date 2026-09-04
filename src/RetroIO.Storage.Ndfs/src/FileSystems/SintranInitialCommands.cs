//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.Text;

namespace RetroIO.Storage.Ndfs.FileSystems
{
    /// <summary>
    /// Pure (device-independent) logic for the SINTRAN III *initial-command buffer*: the segment-table
    /// location pipeline plus the buffer parser/encoder. The device-bound read/write entry points live
    /// on <see cref="NdfsFileSystem"/> (ReadSintranInitialCommands / WriteSintranInitialCommands), which
    /// supply the raw SEGFIL0 bytes to these routines.
    ///
    /// Ported behaviour-for-behaviour from the nd100x "glass" UI feature
    /// (template-glass/js/sintran-initial-commands.js), which was itself verified on live K03/L07/M06
    /// systems including an offline write + reboot. Buffer format (7-bit ASCII text + 0x27 terminator,
    /// zero-padded to word boundaries; a lone 0x27 ends the buffer; a length word at INIBU+130 gives the
    /// text byte count SINTRAN actually reads).
    /// </summary>
    public static class SintranInitialCommands
    {
        // Kernel symbol INIBU (start of the buffer) per SINTRAN version.
        // Source: SYMBOL-1-LIST.SYMB.TXT of each version (octal values).
        //   K03 = 067172, L07 = 074123, M06 = 102327
        public static IReadOnlyDictionary<char, int> Inibu { get; } = new Dictionary<char, int>
        {
            { 'K', 0x6E7A /* 067172 octal */ },
            { 'L', 0x7853 /* 074123 octal */ },
            { 'M', 0x84D7 /* 102327 octal */ },
        };

        // Region layout (identical in K03/L07/M06):
        //   INIBU+0..+129  command text        (260 bytes)
        //   INIBU+130      length cell         (byte count of the text)
        //   INIBU+131      INCOM               (executable code - never write here)
        public const int CapWords = 131;
        public const int CapBytes = CapWords * 2;               // 262
        public const int LengthCellWords = 130;                 // word offset of the length cell
        public const int LengthCellBytes = LengthCellWords * 2; // 260

        // Maximum text length SINTRAN itself accepts. Read from the NEXIN handler: a character is stored
        // only while its byte offset X <= 252 (literal 0o374 = 252), then the length becomes (X+2)
        // rounded down to even => at most 254. Verified on both the L07 and M06 handlers (identical
        // instruction sequence); K03 is laid out the same, so 254 is expected there too.
        public const int MaxTextBytes = 254;

        private const int SegEntryBytes = 16;   // 8 words
        private const int PageBytes = 2048;     // 1024 words
        private const byte CmdEnd = 0x27;       // "'" terminates a command; alone = end of buffer

        /// <summary>Big-endian 16-bit word read (SINTRAN stores these words big-endian in SEGFIL0).</summary>
        private static int Word(byte[] b, int o) => (b[o] << 8) | b[o + 1];

        private readonly struct SegEntry
        {
            public readonly int Logad;
            public readonly int Segle;
            public readonly int Madr;
            public readonly int Flag;
            public readonly int Sgsta;

            public SegEntry(byte[] seg, int page, int n)
            {
                int o = page * PageBytes + n * SegEntryBytes;
                Logad = Word(seg, o + 4);
                Segle = Word(seg, o + 6) & 0x3FF;
                Madr = Word(seg, o + 8);
                Flag = Word(seg, o + 10);
                Sgsta = Word(seg, o + 12);
            }
        }

        /// <summary>
        /// Locate the segment-table page inside SEGFIL0. A real segment-table page: entry 0 is all-zero,
        /// it has many plausible entries, and it is self-describing (some entry's MADR == this page).
        /// Returns the page index, or -1 if not found.
        /// </summary>
        public static int FindSegmentTablePage(byte[] seg)
        {
            if (seg == null) throw new ArgumentNullException(nameof(seg));

            int pages = seg.Length / PageBytes;
            int best = -1;
            int bestGood = -1;

            for (int p = 0; p < pages; p++)
            {
                // Entry 0 must be all-zero.
                bool zero = true;
                for (int i = 0; i < SegEntryBytes; i++)
                {
                    if (seg[p * PageBytes + i] != 0) { zero = false; break; }
                }
                if (!zero) continue;

                int good = 0;
                bool selfRef = false;
                for (int n = 1; n < 128; n++)
                {
                    var e = new SegEntry(seg, p, n);
                    if (e.Segle == 0 && e.Madr == 0 && e.Logad == 0) continue;
                    if (e.Segle > 0 && e.Segle < 1024 && e.Madr > 0 && (e.Flag & 1) != 0 && (e.Sgsta & 0xE000) != 0)
                    {
                        good++;
                        if (e.Madr == p && e.Segle <= 32) selfRef = true;
                    }
                }

                if (good >= 40 && selfRef && good > bestGood)
                {
                    best = p;
                    bestGood = good;
                }
            }

            return best;
        }

        /// <summary>
        /// Parse the buffer: text + 0x27, word-aligned; a lone 0x27 ends it. Returns the command list, or
        /// <c>null</c> when the region does not decode to a plausible buffer (non-printable payload).
        /// </summary>
        public static List<string>? ParseBuffer(byte[] seg, int off, int capBytes)
        {
            if (seg == null) throw new ArgumentNullException(nameof(seg));

            var cmds = new List<string>();
            int i = off;
            int end = Math.Min(off + capBytes, seg.Length);

            while (i < end)
            {
                if (seg[i] == 0x00) { i++; continue; }       // word-alignment padding
                if (seg[i] == CmdEnd) break;                 // empty command = terminator

                var sb = new StringBuilder();
                while (i < end && seg[i] != CmdEnd)
                {
                    sb.Append((char)(seg[i] & 0x7F));        // strip the parity bit
                    i++;
                }
                i++;                                          // consume the "'"
                if (i < end && seg[i] == 0x00 && (((i - off) & 1) != 0)) i++;

                string s = sb.ToString();
                if (!IsPrintable(s)) return null;             // not a real buffer
                cmds.Add(s);
            }

            return cmds;
        }

        /// <summary>Result of <see cref="EncodeBuffer"/>: the on-disk bytes plus the length-cell value.</summary>
        public readonly struct EncodedBuffer
        {
            /// <summary>Encoded bytes (command text + terminators, word-aligned, ending in a lone 0x27).</summary>
            public readonly byte[] Bytes;

            /// <summary>
            /// The length-cell value: the text byte count EXCLUDING the buffer terminator. This is exactly
            /// what must be stored at INIBU+130 — SINTRAN ignores anything past it.
            /// </summary>
            public readonly int TextLen;

            public EncodedBuffer(byte[] bytes, int textLen) { Bytes = bytes; TextLen = textLen; }
        }

        /// <summary>
        /// Encode a command list back into buffer bytes. Enforces the same limit the NEXIN handler does
        /// (<paramref name="maxTextBytes"/>, default <see cref="MaxTextBytes"/> = 254). Commands are
        /// upper-cased; an embedded quote (0x27) or a non-printable character is rejected.
        /// </summary>
        public static EncodedBuffer EncodeBuffer(IReadOnlyList<string> cmds, int maxTextBytes = MaxTextBytes)
        {
            if (cmds == null) throw new ArgumentNullException(nameof(cmds));

            int limit = maxTextBytes > 0 ? maxTextBytes : MaxTextBytes;
            var outBytes = new List<byte>();

            for (int idx = 0; idx < cmds.Count; idx++)
            {
                string c = (cmds[idx] ?? string.Empty).ToUpperInvariant();
                if (!IsPrintable(c) || c.IndexOf('\'') != -1)
                    throw new ArgumentException("invalid command: " + cmds[idx]);

                for (int j = 0; j < c.Length; j++) outBytes.Add((byte)(c[j] & 0x7F));
                outBytes.Add(CmdEnd);
                if ((outBytes.Count & 1) != 0) outBytes.Add(0x00);  // word-align
            }

            int textLen = outBytes.Count;                            // <- the length-cell value
            if (textLen > limit)
                throw new InvalidOperationException($"buffer overflow: text is {textLen} bytes, max {limit}");

            outBytes.Add(CmdEnd);                                    // buffer terminator
            if ((outBytes.Count & 1) != 0) outBytes.Add(0x00);

            return new EncodedBuffer(outBytes.ToArray(), textLen);
        }

        /// <summary>
        /// True when re-encoding <paramref name="cmds"/> reproduces exactly <paramref name="storedTextLen"/>
        /// bytes — i.e. the parsed commands are a self-consistent round trip of the stored buffer, not a
        /// coincidental parse of unrelated bytes. Encoding can throw on invalid commands; that counts as
        /// "not consistent".
        /// </summary>
        public static bool ReEncodeMatches(IReadOnlyList<string> cmds, int storedTextLen)
        {
            try { return EncodeBuffer(cmds).TextLen == storedTextLen; }
            catch { return false; }
        }

        /// <summary>True when every character is a printable 7-bit ASCII glyph (0x20..0x7E).</summary>
        private static bool IsPrintable(string s)
        {
            if (s.Length == 0) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch < 0x20 || ch > 0x7E) return false;
            }
            return true;
        }

        /// <summary>
        /// Locate and decode the initial-command buffer inside a raw SEGFIL0 file image. Returns the
        /// decoded buffer, or <c>null</c> when no plausible buffer is found (not a SINTRAN system pack,
        /// or an unrecognised version). Version order tried: L, M, K (or a caller-preferred version first).
        /// </summary>
        public static SintranInitialCommandBufferLocation? Locate(byte[] seg, char? preferVersion = null)
        {
            if (seg == null) throw new ArgumentNullException(nameof(seg));

            int stPage = FindSegmentTablePage(seg);
            if (stPage < 0) return null;

            // Build the version try-order. Prefer the caller-supplied version, then L, M, K.
            var order = new List<char>();
            if (preferVersion.HasValue)
            {
                char pv = char.ToUpperInvariant(preferVersion.Value);
                if (Inibu.ContainsKey(pv)) order.Add(pv);
            }
            char[] fallback = { 'L', 'M', 'K' };
            for (int f = 0; f < fallback.Length; f++)
                if (!order.Contains(fallback[f])) order.Add(fallback[f]);

            for (int k = 0; k < order.Count; k++)
            {
                char letter = order[k];
                if (!Inibu.TryGetValue(letter, out int inibu)) continue;

                // Several segments' virtual ranges contain INIBU (save/image copies share the same LOGAD
                // low bits). Only the running command segment holds a live buffer, so try each in segment
                // order and take the first that yields a sane length cell AND parses to >= 1 command.
                for (int n = 1; n < 128; n++)
                {
                    var e = new SegEntry(seg, stPage, n);
                    if (e.Madr == 0 || e.Segle == 0) continue;

                    int segBase = (e.Logad & 0x3F) * 1024;               // words
                    if (inibu < segBase || inibu >= segBase + e.Segle * 1024) continue;

                    int off = inibu - segBase;
                    long byteOffset = (long)e.Madr * PageBytes + (long)off * 2;
                    if (byteOffset + CapBytes > seg.Length) continue;

                    int textLen = Word(seg, (int)(byteOffset + LengthCellBytes));
                    if (textLen == 0 || textLen > LengthCellBytes) continue;   // implausible

                    var cmds = ParseBuffer(seg, (int)byteOffset, textLen);
                    // Self-consistency gate: a REAL buffer's stored length cell equals the length of
                    // re-encoding the parsed commands (parse and encode are exact inverses). This rejects
                    // coincidental parses — e.g. a code word whose length cell happens to read as a small
                    // number and whose first bytes happen to be printable (observed as a bogus "B," match
                    // on a pack whose real buffer header was clobbered). Without this, Locate could pick a
                    // garbage segment and a subsequent write would corrupt SINTRAN code.
                    if (cmds != null && cmds.Count > 0 && ReEncodeMatches(cmds, textLen))
                    {
                        return new SintranInitialCommandBufferLocation
                        {
                            Commands = cmds,
                            VersionLetter = letter,
                            SegmentNumber = n,
                            Madr = (uint)e.Madr,
                            ByteOffset = byteOffset,
                            SegmentTablePage = stPage,
                            TextLength = textLen,
                            LengthCellOffset = byteOffset + LengthCellBytes,
                        };
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// One INIBU-containing segment candidate found while locating the buffer. Diagnostic aid: the
        /// live buffer lives in exactly one segment, but save/image copies share INIBU's low LOGAD bits
        /// so several segments' virtual ranges can contain it. Dumping every candidate shows which one
        /// <see cref="Locate"/> would pick and whether the others hold a stale/garbage buffer.
        /// </summary>
        public sealed class Candidate
        {
            public char VersionLetter { get; init; }
            public int SegmentNumber { get; init; }
            public uint Madr { get; init; }
            public long ByteOffset { get; init; }
            public int LengthCell { get; init; }
            public bool LengthPlausible { get; init; }
            /// <summary>Parsed commands, or null when the region does not decode to a plausible buffer.</summary>
            public IReadOnlyList<string>? Commands { get; init; }
            /// <summary>True when the parse is self-consistent (re-encode == stored length): a REAL buffer.</summary>
            public bool Consistent { get; init; }
            /// <summary>First bytes of the buffer region, for a hex/ASCII dump.</summary>
            public byte[] RegionHead { get; init; } = System.Array.Empty<byte>();
        }

        /// <summary>
        /// Enumerates EVERY INIBU-containing segment candidate (all versions, all matching segments),
        /// in the same order <see cref="Locate"/> scans them. Purely diagnostic — the first candidate
        /// with a plausible length cell that parses is what <see cref="Locate"/> returns.
        /// </summary>
        public static List<Candidate> EnumerateCandidates(byte[] seg, int headBytes = 96)
        {
            if (seg == null) throw new ArgumentNullException(nameof(seg));

            var result = new List<Candidate>();
            int stPage = FindSegmentTablePage(seg);
            if (stPage < 0) return result;

            char[] order = { 'L', 'M', 'K' };
            for (int k = 0; k < order.Length; k++)
            {
                char letter = order[k];
                if (!Inibu.TryGetValue(letter, out int inibu)) continue;

                for (int n = 1; n < 128; n++)
                {
                    var e = new SegEntry(seg, stPage, n);
                    if (e.Madr == 0 || e.Segle == 0) continue;

                    int segBase = (e.Logad & 0x3F) * 1024;
                    if (inibu < segBase || inibu >= segBase + e.Segle * 1024) continue;

                    int off = inibu - segBase;
                    long byteOffset = (long)e.Madr * PageBytes + (long)off * 2;
                    if (byteOffset + CapBytes > seg.Length) continue;

                    int textLen = Word(seg, (int)(byteOffset + LengthCellBytes));
                    bool plausible = textLen > 0 && textLen <= LengthCellBytes;
                    var cmds = plausible ? ParseBuffer(seg, (int)byteOffset, textLen) : null;
                    bool consistent = cmds != null && cmds.Count > 0 && ReEncodeMatches(cmds, textLen);

                    int hn = Math.Min(headBytes, seg.Length - (int)byteOffset);
                    var head = new byte[hn];
                    Array.Copy(seg, (int)byteOffset, head, 0, hn);

                    result.Add(new Candidate
                    {
                        VersionLetter = letter,
                        SegmentNumber = n,
                        Madr = (uint)e.Madr,
                        ByteOffset = byteOffset,
                        LengthCell = textLen,
                        LengthPlausible = plausible,
                        Commands = cmds,
                        Consistent = consistent,
                        RegionHead = head,
                    });
                }
            }

            return result;
        }

        // Recognisable SINTRAN initial-command keywords, used to identify a REAL buffer whose header word
        // was clobbered (so Locate can't see it) for repair. Chosen to survive a few corrupted bytes:
        // e.g. "ENTER-DIRECTORY" clobbered to "'N'ER-DIRECTORY" still contains "DIRECTORY".
        private static readonly string[] RepairKeywords =
        {
            "DIRECTORY", "DISC-", "SET-AVAIL", "APPEND-BATCH", "LOAD-MODE", "HENT-MODE",
            "CONNECT", "ENTER-DIR", "SYSTEM-OUTPUT",
        };

        /// <summary>
        /// Finds the write target for REPAIRING a buffer whose header (length cell / first bytes) was
        /// clobbered so <see cref="Locate"/> can no longer see it. It scores every INIBU-containing
        /// segment by how many recognisable command keywords survive in its region and requires a single
        /// clear winner (score ≥ 2 and strictly above the runner-up). Returns null when there is no clear
        /// winner — the caller MUST then refuse to write rather than risk patching SINTRAN code. The
        /// returned <see cref="SintranInitialCommandBufferLocation.ByteOffset"/> is where to write; the
        /// Commands/TextLength fields reflect whatever currently (mis)parses and are not meaningful here.
        /// </summary>
        public static SintranInitialCommandBufferLocation? LocateRepairTarget(byte[] seg, out int winningScore)
        {
            winningScore = 0;
            if (seg == null) throw new ArgumentNullException(nameof(seg));

            var cands = EnumerateCandidates(seg, headBytes: CapBytes);
            SintranInitialCommandBufferLocation? best = null;
            int bestScore = 0, secondScore = 0;

            for (int i = 0; i < cands.Count; i++)
            {
                var c = cands[i];

                // Decode the region to 7-bit ASCII and count keyword hits.
                var sb = new StringBuilder(c.RegionHead.Length);
                for (int b = 0; b < c.RegionHead.Length; b++)
                {
                    int ch = c.RegionHead[b] & 0x7F;
                    sb.Append(ch >= 0x20 && ch < 0x7F ? (char)ch : ' ');
                }
                string text = sb.ToString();

                int score = 0;
                for (int k = 0; k < RepairKeywords.Length; k++)
                    if (text.Contains(RepairKeywords[k])) score++;

                if (score > bestScore)
                {
                    secondScore = bestScore;
                    bestScore = score;
                    best = new SintranInitialCommandBufferLocation
                    {
                        VersionLetter = c.VersionLetter,
                        SegmentNumber = c.SegmentNumber,
                        Madr = c.Madr,
                        ByteOffset = c.ByteOffset,
                        SegmentTablePage = FindSegmentTablePage(seg),
                        TextLength = c.LengthCell,
                        LengthCellOffset = c.ByteOffset + LengthCellBytes,
                        Commands = c.Commands ?? new List<string>(),
                    };
                }
                else if (score > secondScore)
                {
                    secondScore = score;
                }
            }

            // Require a clear, unambiguous winner.
            if (best == null || bestScore < 2 || bestScore == secondScore)
                return null;

            winningScore = bestScore;
            return best;
        }

        /// <summary>
        /// Build the exact 262-byte region image (text area + length cell) to patch back into SEGFIL0 at
        /// INIBU. Zero-fills the text area, copies the encoded command bytes, and stores the length cell
        /// (big-endian) at word 130. The region deliberately stops before word 131 (INCOM code).
        /// </summary>
        public static byte[] BuildRegionImage(EncodedBuffer enc)
        {
            var region = new byte[CapBytes];                 // 262 bytes, zero-filled
            if (enc.Bytes.Length > LengthCellBytes)
                throw new InvalidOperationException($"encoded buffer is {enc.Bytes.Length} bytes, exceeds text area {LengthCellBytes}");

            Array.Copy(enc.Bytes, 0, region, 0, enc.Bytes.Length);

            // Length cell (big-endian word) at INIBU+130.
            region[LengthCellBytes] = (byte)(enc.TextLen >> 8);
            region[LengthCellBytes + 1] = (byte)(enc.TextLen & 0xFF);
            return region;
        }
    }

    /// <summary>
    /// Internal locate result carrying the same fields as
    /// <see cref="RetroIO.Storage.Ndfs.Elements.SintranInitialCommandBuffer"/> but
    /// used by the device-bound writer (which still needs the SEGFIL0 object entry to map the byte offset
    /// to a physical page). Kept as a mutable init-only carrier to avoid duplicating the field list.
    /// </summary>
    public sealed class SintranInitialCommandBufferLocation
    {
        public System.Collections.Generic.IReadOnlyList<string> Commands { get; init; } = new System.Collections.Generic.List<string>();
        public char VersionLetter { get; init; }
        public int SegmentNumber { get; init; }
        public uint Madr { get; init; }
        public long ByteOffset { get; init; }
        public int SegmentTablePage { get; init; }
        public int TextLength { get; init; }
        public long LengthCellOffset { get; init; }
    }
}
