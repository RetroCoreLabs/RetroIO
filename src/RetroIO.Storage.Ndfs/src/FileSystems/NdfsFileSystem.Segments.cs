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
    /// SINTRAN III segments on a system pack: list, export, import.
    ///
    /// A segment is a run of pages on one of up to five segment files. The segment table (inside
    /// SEGFIL0) gives each segment its LOGAD, SEGLE, MADR and FLAG, and bits 13 to 15 of FLAG name the
    /// segment file. Where a segment file begins on the pack comes from the kernel's own tables in the
    /// resident data page (<c>DBLST</c>, <c>ABLPAGE</c>, <c>MASSNO</c>, read from the S3IDPIT copy),
    /// with one rule the kernel applies at every start-up: segment file 4 starts at disk page 1
    /// (OPPSTART:751). Names come from the RT-loader DEFINED SEGMENT NAME records. Ported
    /// behaviour-for-behaviour from norskdata-ndfs <c>ndfs-c/src/sintran_segments.c</c>; the model is in
    /// NDInsight chapters SINTRAN/OS/11 and 22, the measurements in chapter 28.
    /// </summary>
    public partial class NdfsFileSystem
    {
        /// <summary>
        /// SINTRAN knows segment files 0 to 4.
        /// </summary>
        public const int SintranSegmentFileCount = 5;

        private int? ReadSintranSymbol(string path, string name)
        {
            byte[] text = SintranCpuIdentity.StripParity(ReadFile(path));
            return SintranCpuIdentity.FindSymbol(text, name, out int v, out _, out _) ? v : null;
        }

        private static SintranSegment ReadSegmentEntry(byte[] seg, int page, int n)
        {
            int o = page * SintranCpuIdentity.PageBytes + n * SintranCpuIdentity.SegEntryBytes;
            int flag = SintranCpuIdentity.Word(seg, o + 10);
            int segle = SintranCpuIdentity.Word(seg, o + 6) & 0x3FF;
            return new SintranSegment
            {
                Number = n,
                Segli = SintranCpuIdentity.Word(seg, o + 0),
                Prese = SintranCpuIdentity.Word(seg, o + 2),
                Logad = SintranCpuIdentity.Word(seg, o + 4),
                Segle = segle,
                Madr = SintranCpuIdentity.Word(seg, o + 8),
                Flag = flag,
                Sgsta = SintranCpuIdentity.Word(seg, o + 12),
                Bpagl = SintranCpuIdentity.Word(seg, o + 14),
                SegmentFile = (flag >> 13) & 7,
                Ok5 = (flag & 1) != 0,
                // No pages: S3DPIT, S3RPIT ... MADR 0 is a real offset (S3IMAGE, S3SAVE).
                MemoryOnly = segle == 0,
                DiskPage = -1,
            };
        }

        /// <summary>
        /// The NDFS file whose first disk page is <paramref name="page"/>, for labelling only.
        /// </summary>
        private string LabelFileAtPage(long page)
        {
            if (page < 0) return string.Empty;
            var entries = GetObjectEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.PagesInFile == 0 || e.FilePointer == null || e.FilePointer.BlockID == 0) continue;
                List<Structures.BlockPointer> blocks;
                try
                {
                    blocks = GetAllBlockPointersForFile(e.FilePointer, e.BytesInFile);
                }
                catch (IOException)
                {
                    continue;
                }
                if (blocks.Count > 0 && blocks[0].BlockID == page)
                {
                    string type = (e.Type ?? string.Empty).TrimEnd('\x27', ' ');
                    return $"{e.UserName}/{e.ObjectName}{ExtensionSeparator}{type}";
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Segment-file bases from the kernel tables inside the S3IDPIT copy.
        /// </summary>
        private void ReadSegmentFileTables(byte[] seg, SintranSegmentList outList, SintranSegment? idpit)
        {
            var files = outList.SegmentFiles;
            for (int n = 0; n < files.Count; n++)
            {
                files[n].Defined = false;
                files[n].BasePage = -1;
                files[n].Source = "unknown";
                files[n].FilePath = string.Empty;
                files[n].OnThisDisk = true;
            }

            // Where the DPIT image copy is inside SEGFIL0 (segment file 0 by construction: the table and
            // the DPIT image are both in SEGFIL0:DATA).
            bool dpitOk = idpit != null && !idpit.MemoryOnly && outList.HaveSymbols &&
                          (long)(idpit.Madr + idpit.Segle) * SintranCpuIdentity.PageBytes <= seg.Length;

            if (dpitOk && idpit != null)
            {
                int ablpage0 = 0;
                int massno0 = 0;
                for (int n = 0; n < files.Count; n++)
                {
                    var sf = files[n];
                    long bl = SintranCpuIdentity.SegmentWordOffset(idpit.Logad, idpit.Segle, idpit.Madr, outList.BlstSymbol + 2 * n);
                    long ab = SintranCpuIdentity.SegmentWordOffset(idpit.Logad, idpit.Segle, idpit.Madr, outList.AblpaSymbol + n);
                    long mn = SintranCpuIdentity.SegmentWordOffset(idpit.Logad, idpit.Segle, idpit.Madr, outList.MassnSymbol + n);
                    if (bl < 0 || ab < 0 || mn < 0) continue;
                    sf.Dblst = ((uint)SintranCpuIdentity.Word(seg, bl) << 16) | (uint)SintranCpuIdentity.Word(seg, bl + 2);
                    sf.BlocksPerPage = SintranCpuIdentity.Word(seg, ab);
                    sf.Massno = SintranCpuIdentity.Word(seg, mn);
                    if (n == 0)
                    {
                        ablpage0 = sf.BlocksPerPage;
                        massno0 = sf.Massno;
                    }
                    if (sf.BlocksPerPage == 0) sf.BlocksPerPage = ablpage0;
                    sf.OnThisDisk = sf.Massno == 0 || sf.Massno == massno0;
                    if (sf.Dblst != 0 && sf.BlocksPerPage > 0)
                    {
                        sf.Defined = true;
                        sf.BasePage = sf.Dblst / (uint)sf.BlocksPerPage;
                        sf.Source = "DBLST / ABLPAGE in S3IDPIT";
                    }
                    else if (n == 4 && ablpage0 > 0)
                    {
                        // Set at every start-up, not stored: BLST(11B) := ABLPAGE(0) + XXSWTYPE
                        // (PH-P2-OPPSTART.NPL:751), i.e. one page when XXSWTYPE is 0. Measured on the K
                        // packs: the save copy at MADR 27 is disk page 28.
                        sf.Defined = true;
                        sf.BlocksPerPage = ablpage0;
                        sf.BasePage = 1;
                        sf.Source = "start-up rule, ABLPAGE(0) blocks (OPPSTART:751)";
                    }
                }
            }

            // Without the tables, segment file 0 is where SEGFIL0:DATA begins.
            if (!files[0].Defined)
            {
                ObjectEntry? segfil = FindObjectForPath(outList.SegfilPath);
                if (segfil != null && segfil.FilePointer != null && segfil.FilePointer.BlockID != 0)
                {
                    var blocks = GetAllBlockPointersForFile(segfil.FilePointer, segfil.BytesInFile);
                    if (blocks.Count > 0)
                    {
                        files[0].Defined = true;
                        files[0].BasePage = blocks[0].BlockID;
                        files[0].BlocksPerPage = 0;
                        files[0].Source = "first page of SEGFIL0:DATA (no symbol list)";
                    }
                }
            }

            for (int n = 0; n < files.Count; n++)
            {
                if (files[n].Defined && files[n].OnThisDisk) files[n].FilePath = LabelFileAtPage(files[n].BasePage);
            }
        }

        /// <summary>
        /// Lists every segment the table uses or the name records name, with the five segment files and
        /// each segment's first disk page. Read-only safe.
        /// </summary>
        /// <returns>
        /// The segment list.
        /// </returns>
        /// <exception cref="SintranCpuIdentityException">
        /// The pack has no SEGFIL0 or no segment table.
        /// </exception>
        public SintranSegmentList ListSintranSegments()
        {
            EnsureInitialized();
            var outList = new SintranSegmentList();

            // Symbols: INIBU for the version letter, BLST / ABLPA / MASSN for the segment files.
            string? symPath = FindSintranSystemFile("SYMBOL-1-LIST", "SYMB");
            if (symPath != null)
            {
                int? inibu = ReadSintranSymbol(symPath, "INIBU");
                if (inibu.HasValue) outList.Version = SintranCpuIdentity.VersionFromInibu(inibu.Value);
                int? blst = ReadSintranSymbol(symPath, "BLST");
                int? ablpa = ReadSintranSymbol(symPath, "ABLPA");
                int? massn = ReadSintranSymbol(symPath, "MASSN");
                if (blst.HasValue && ablpa.HasValue && massn.HasValue)
                {
                    outList.HaveSymbols = true;
                    outList.BlstSymbol = blst.Value;
                    outList.AblpaSymbol = ablpa.Value;
                    outList.MassnSymbol = massn.Value;
                }
            }

            // SEGFIL0 and its segment table.
            string? segfil = FindSintranSegfil();
            if (segfil == null) throw new SintranCpuIdentityException("no SEGFIL0:DATA");
            outList.SegfilPath = segfil;
            byte[] seg = ReadFile(segfil);
            outList.SegmentTablePage = SintranInitialCommands.FindSegmentTablePage(seg);
            if (outList.SegmentTablePage < 0) throw new SintranCpuIdentityException("no segment table found in SEGFIL0");

            // Names: RTFIL:DATA first, then the loader table copies inside SEGFIL0.
            var names = new Dictionary<int, string>();
            string? rtPath = FindSintranSystemFile("RTFIL", "DATA");
            if (rtPath != null)
            {
                names = SintranCpuIdentity.CollectSegmentNames(ReadFile(rtPath));
                if (names.Count > 0) outList.NamesPath = rtPath;
            }
            if (outList.NamesPath.Length == 0)
            {
                names = SintranCpuIdentity.CollectSegmentNames(seg);
                if (names.Count > 0) outList.NamesPath = "SEGFIL0 (loader table copy)";
            }

            // Every entry that is used or named.
            SintranSegment? idpit = null;
            for (int n = 1; n < 256; n++)
            {
                SintranSegment s = ReadSegmentEntry(seg, outList.SegmentTablePage, n);
                bool named = names.TryGetValue(n, out string? name);
                if (s.Segle == 0 && s.Madr == 0 && s.Logad == 0 && !named) continue;
                s.Name = named ? name! : string.Empty;
                if (s.Name == SintranCpuIdentity.SegmentNameImageDpit) idpit = s;
                outList.Segments.Add(s);
            }

            ReadSegmentFileTables(seg, outList, idpit);

            // Disk page of every segment with a disk copy on a known, local segment file.
            for (int i = 0; i < outList.Segments.Count; i++)
            {
                var s = outList.Segments[i];
                var sf = outList.SegmentFiles[s.SegmentFile];
                if (s.MemoryOnly || s.Segle == 0) continue;
                if (!s.Ok5 || !sf.Defined || !sf.OnThisDisk) continue;
                s.DiskPage = sf.BasePage + s.Madr;
            }
            return outList;
        }

        /// <summary>
        /// Index into <see cref="SintranSegmentList.Segments"/> of the segment named, or numbered
        /// (octal with a trailing B, decimal without), case-insensitive; -1 when absent.
        /// </summary>
        /// <param name="list">
        /// The list from <see cref="ListSintranSegments"/>.
        /// </param>
        /// <param name="nameOrNumber">
        /// A segment name such as <c>S3SDPIT</c>, or a number such as <c>53B</c> or <c>43</c>.
        /// </param>
        /// <returns>
        /// The index, or -1.
        /// </returns>
        public static int FindSintranSegment(SintranSegmentList list, string nameOrNumber)
        {
            if (list == null || string.IsNullOrEmpty(nameOrNumber)) return -1;
            string text = nameOrNumber;
            int radix = 10;
            char last = text[text.Length - 1];
            if (last == 'B' || last == 'b')
            {
                radix = 8;
                text = text.Substring(0, text.Length - 1);
            }
            bool digits = text.Length > 0;
            for (int k = 0; k < text.Length && digits; k++)
            {
                char ch = text[k];
                if (ch < '0' || ch > (radix == 8 ? '7' : '9')) digits = false;
            }
            if (digits)
            {
                int v = Convert.ToInt32(text, radix);
                for (int i = 0; i < list.Segments.Count; i++)
                {
                    if (list.Segments[i].Number == v) return i;
                }
                return -1;
            }
            for (int i = 0; i < list.Segments.Count; i++)
            {
                var s = list.Segments[i];
                if (s.Name.Length > 0 && string.Equals(s.Name, nameOrNumber, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private SintranSegment CheckSegmentPlace(SintranSegmentList list, int index)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            if (index < 0 || index >= list.Segments.Count) throw new ArgumentOutOfRangeException(nameof(index));
            var s = list.Segments[index];
            if (s.MemoryOnly || s.Segle == 0 || s.DiskPage < 0)
                throw new SintranCpuIdentityException($"segment {Convert.ToString(s.Number, 8)}B has no disk copy on this pack");
            if (s.DiskPage + s.Segle > TotalPages)
                throw new ArgumentOutOfRangeException(nameof(index), $"segment {Convert.ToString(s.Number, 8)}B runs past the end of the image");
            return s;
        }

        /// <summary>
        /// The segment's pages as they are on the pack (SEGLE * 2048 bytes).
        /// </summary>
        /// <param name="list">
        /// The list from <see cref="ListSintranSegments"/>.
        /// </param>
        /// <param name="index">
        /// The index into <see cref="SintranSegmentList.Segments"/>.
        /// </param>
        /// <returns>
        /// The segment bytes.
        /// </returns>
        /// <exception cref="SintranCpuIdentityException">
        /// The segment has no disk copy on this pack.
        /// </exception>
        public byte[] ExportSintranSegment(SintranSegmentList list, int index)
        {
            EnsureInitialized();
            SintranSegment s = CheckSegmentPlace(list, index);
            var outBytes = new byte[s.Segle * SintranCpuIdentity.PageBytes];
            for (int p = 0; p < s.Segle; p++)
            {
                byte[] page = ReadDiskPage((uint)(s.DiskPage + p));
                Array.Copy(page, 0, outBytes, p * SintranCpuIdentity.PageBytes, SintranCpuIdentity.PageBytes);
            }
            return outBytes;
        }

        /// <summary>
        /// Writes <paramref name="data"/> over the segment's pages. Up to SEGLE pages; a last partial page
        /// keeps its remaining bytes.
        /// </summary>
        /// <param name="list">
        /// The list from <see cref="ListSintranSegments"/>.
        /// </param>
        /// <param name="index">
        /// The index into <see cref="SintranSegmentList.Segments"/>.
        /// </param>
        /// <param name="data">
        /// The bytes to write.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="data"/> is longer than the segment.
        /// </exception>
        /// <exception cref="SintranCpuIdentityException">
        /// The segment has no disk copy on this pack.
        /// </exception>
        public void ImportSintranSegment(SintranSegmentList list, int index, ReadOnlySpan<byte> data)
        {
            EnsureInitialized();
            SintranSegment s = CheckSegmentPlace(list, index);
            int size = data.Length;
            if (size > s.Segle * SintranCpuIdentity.PageBytes) throw new ArgumentOutOfRangeException(nameof(data), "data is longer than the segment");
            int done = 0;
            while (done < size)
            {
                uint pg = (uint)(s.DiskPage + done / SintranCpuIdentity.PageBytes);
                int chunk = Math.Min(size - done, SintranCpuIdentity.PageBytes);
                byte[] page = chunk < SintranCpuIdentity.PageBytes ? ReadDiskPage(pg) : new byte[SintranCpuIdentity.PageBytes];
                data.Slice(done, chunk).CopyTo(page);
                WriteDiskPage(pg, page);
                done += chunk;
            }
        }
    }
}
