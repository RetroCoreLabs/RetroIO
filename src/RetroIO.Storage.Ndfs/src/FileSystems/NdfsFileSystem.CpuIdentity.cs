//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Elements;
using RetroIO.Storage.Ndfs.Structures;

namespace RetroIO.Storage.Ndfs.FileSystems
{
    /// <summary>
    /// Device-bound entry points for the SINTRAN III CPU number and CPU type on a system pack, the raw
    /// disk page access the segment tools need, and the directory stamp release. See
    /// <see cref="SintranCpuIdentity"/> for the model and the provenance.
    /// </summary>
    public partial class NdfsFileSystem
    {
        // ==================== Raw disk pages ====================
        //
        // The SINTRAN segment tools address the pack by DISK page, not through the file system: a
        // segment file's pages come from the kernel's own DBLST table, and segment file 4 is absolute
        // on the pack. These mirror ndfs_get_total_pages / ndfs_read_disk_page / ndfs_write_disk_page.

        /// <summary>
        /// Gets the number of 2048-byte pages the device holds.
        /// </summary>
        public uint TotalPages
        {
            get
            {
                EnsureInitialized();
                return _device!.TotalBlocks;
            }
        }

        /// <summary>
        /// Reads one raw disk page.
        /// </summary>
        /// <param name="page">
        /// The disk page number.
        /// </param>
        /// <returns>
        /// The 2048 bytes of the page.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The page is past the end of the device.
        /// </exception>
        public byte[] ReadDiskPage(uint page)
        {
            EnsureInitialized();
            if (page >= _device!.TotalBlocks) throw new ArgumentOutOfRangeException(nameof(page), $"Disk page {page} out of range");
            var buffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(page), buffer);
            return buffer;
        }

        /// <summary>
        /// Overwrites one raw disk page and flushes the device.
        /// </summary>
        /// <param name="page">
        /// The disk page number.
        /// </param>
        /// <param name="pageData">
        /// Exactly 2048 bytes.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The page is past the end of the device.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="pageData"/> is not exactly one page.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The device is read-only.
        /// </exception>
        public void WriteDiskPage(uint page, ReadOnlySpan<byte> pageData)
        {
            EnsureInitialized();
            if (_device == null || _device.IsReadOnly) throw new InvalidOperationException("Cannot write to read-only device");
            if (page >= _device.TotalBlocks) throw new ArgumentOutOfRangeException(nameof(page), $"Disk page {page} out of range");
            if (pageData.Length != NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE) throw new ArgumentException("A disk page is exactly 2048 bytes", nameof(pageData));
            _device.WriteBlock(BlockAddress.FromLBA(page), pageData);
            _device.Flush();
        }

        // ==================== Directory stamp ====================

        /// <summary>
        /// Clears the directory's "entered by system N" stamp: flag word bit 15 AND the system number in
        /// word 1755B of the Extended Info block, then rewrites the block with a fresh additive
        /// checksum. The kernel sets both at ENTER-DIRECTORY (CHDSI) and refuses a pack that another
        /// system left entered with error 3203 "DIRECTORY ENTERED BY ANOTHER SYSTEM". Unlike
        /// <see cref="ClearEnteredDirectory"/>, which only clears the bit and only when it is set, this
        /// also zeroes the system number, the way <c>ndtool --release</c> and the other ports do.
        /// </summary>
        /// <param name="wasEntered">
        /// Set to whether the bit was set before the clear.
        /// </param>
        /// <param name="wasSystemNumber">
        /// Set to the system number stored before the clear.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// The device is read-only.
        /// </exception>
        public void ReleaseDirectory(out bool wasEntered, out int wasSystemNumber)
        {
            EnsureInitialized();
            if (_device == null || _masterBlock == null || _device.IsReadOnly)
                throw new InvalidOperationException("Cannot write to read-only device");

            wasEntered = _masterBlock.IsDirectoryEntered;
            wasSystemNumber = (int)_masterBlock.ExtLastSystemNumber;

            _masterBlock.ExtFlagWord &= unchecked((uint)~MasterBlock.EXT_FLAG_ENTERED);
            _masterBlock.ExtLastSystemNumber = 0;

            // Read page 0 fresh and patch only the 8-word Extended Info block, so the master block and
            // the boot code stay byte-for-byte intact. WriteExtendedInfoToBytes stores a new checksum and
            // marks the cached block valid.
            var pageBuffer = new byte[NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE];
            _device.ReadBlock(BlockAddress.FromLBA(0), pageBuffer);
            _masterBlock.WriteExtendedInfoToBytes(pageBuffer);
            _device.WriteBlock(BlockAddress.FromLBA(0), pageBuffer);
            _masterBlock.ExtValid = true;
            _device.Flush();
        }

        // ==================== File lookup ====================

        /// <summary>
        /// Finds the first object owned by SYSTEM with this name and type (case-insensitive) and returns
        /// its <c>USER/NAME:TYPE</c> path, or <c>null</c>.
        /// </summary>
        /// <param name="name">
        /// The object name, for example <c>SYMBOL-1-LIST</c>.
        /// </param>
        /// <param name="wantType">
        /// The type, for example <c>SYMB</c>.
        /// </param>
        /// <returns>
        /// The path, or <c>null</c>.
        /// </returns>
        public string? FindSintranSystemFile(string name, string wantType)
        {
            EnsureInitialized();
            var entries = GetObjectEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!string.Equals(e.UserName, "SYSTEM", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(e.ObjectName, name, StringComparison.OrdinalIgnoreCase)) continue;
                string type = (e.Type ?? string.Empty).TrimEnd('\x27', ' ');
                if (!string.Equals(type, wantType, StringComparison.OrdinalIgnoreCase)) continue;
                return $"{e.UserName}/{e.ObjectName}{ExtensionSeparator}{type}";
            }
            return null;
        }

        /// <summary>
        /// SEGFIL0:DATA, or the older name SEGFIL:DATA.
        /// </summary>
        private string? FindSintranSegfil()
        {
            return FindSintranSystemFile("SEGFIL0", "DATA") ?? FindSintranSystemFile("SEGFIL", "DATA");
        }

        /// <summary>
        /// Reads a symbol list and strips its parity; reports whether it had any.
        /// </summary>
        private byte[] ReadSymbolList(string path, out bool hasParity)
        {
            byte[] raw = ReadFile(path);
            hasParity = SintranCpuIdentity.HasParity(raw);
            return SintranCpuIdentity.StripParity(raw);
        }

        private static int SymbolOrMinusOne(byte[] a, byte[] b, string name)
        {
            if (SintranCpuIdentity.FindSymbol(a, name, out int v, out _, out _)) return v;
            if (SintranCpuIdentity.FindSymbol(b, name, out v, out _, out _)) return v;
            return -1;
        }

        /// <summary>
        /// Both DPIT names to segment numbers: <c>RTFIL:DATA</c> first (small), then the copies of the
        /// loader table inside SEGFIL0.
        /// </summary>
        private void ResolveDpitSegmentNames(byte[] seg, SintranCpuIdentityInfo outInfo)
        {
            var copies = outInfo.Copies;
            for (int i = 0; i < copies.Count; i++) copies[i].SegmentNumber = -1;
            outInfo.NamesPath = string.Empty;

            string? path = FindSintranSystemFile("RTFIL", "DATA");
            if (path != null)
            {
                byte[] rt = ReadFile(path);
                for (int i = 0; i < copies.Count; i++) copies[i].SegmentNumber = SintranCpuIdentity.FindSegmentByName(rt, copies[i].Name);
                if (AllResolved(copies))
                {
                    outInfo.NamesPath = path;
                    return;
                }
            }

            // Fall back to the S3RTFIL segment pages inside SEGFIL0.
            for (int i = 0; i < copies.Count; i++)
            {
                if (copies[i].SegmentNumber <= 0) copies[i].SegmentNumber = SintranCpuIdentity.FindSegmentByName(seg, copies[i].Name);
            }
            if (AllResolved(copies)) outInfo.NamesPath = "SEGFIL0 (loader table copy)";
        }

        private static bool AllResolved(IReadOnlyList<SintranDpitCopy> copies)
        {
            for (int i = 0; i < copies.Count; i++)
            {
                if (copies[i].SegmentNumber <= 0) return false;
            }
            return true;
        }

        /// <summary>
        /// The file a segment file number names. 0 is SEGFIL0; on K the save copy sits on segment file 4,
        /// which the K release information places in SINTRAN:DATA. Other numbers are reported and never
        /// written.
        /// </summary>
        private string? SintranSegmentFilePath(int segmentFile, string segfilPath)
        {
            if (segmentFile == 0) return segfilPath;
            if (segmentFile == 4) return FindSintranSystemFile("SINTRAN", "DATA");
            return null;
        }

        /// <summary>
        /// A word offset is usable only when the file really holds it; otherwise -1. The K pack's
        /// SINTRAN:DATA is 60 pages while S3SDPIT is listed as 35 pages from page 27, so the segment's
        /// tail is past the file end but every identity word is inside it. The check is therefore per
        /// word, not per segment.
        /// </summary>
        private static long OffsetInFile(long offset, long fileSize)
        {
            if (offset < 0 || offset + 2 > fileSize) return -1;
            return offset;
        }

        /// <summary>
        /// Reads the four identity words of one copy out of its file.
        /// </summary>
        private void ReadDpitCopyWords(SintranSymbolAddresses sym, SintranDpitCopy c)
        {
            c.Sysno = c.Fcpun = c.CpuType = c.Revle = -1;
            c.SysnoOffset = SintranCpuIdentity.SegmentWordOffset(c.Logad, c.Segle, c.Madr, sym.Sysno);
            c.FcpunOffset = SintranCpuIdentity.SegmentWordOffset(c.Logad, c.Segle, c.Madr, sym.Fcpun);
            c.TypeOffset = sym.Hwinf >= 0 ? SintranCpuIdentity.SegmentWordOffset(c.Logad, c.Segle, c.Madr, sym.Hwinf + 2) : -1;
            c.RevleOffset = sym.Revle >= 0 ? SintranCpuIdentity.SegmentWordOffset(c.Logad, c.Segle, c.Madr, sym.Revle) : -1;
            c.FileFound = false;
            if (c.FilePath.Length == 0) return;

            byte[] data;
            try
            {
                data = ReadFile(c.FilePath);
            }
            catch (IOException)
            {
                return;
            }
            long size = data.Length;
            // The file must at least reach the copy's first page; each word is then checked against the
            // file length on its own.
            if (size > (long)c.Madr * SintranCpuIdentity.PageBytes)
            {
                c.FileFound = true;
                c.SysnoOffset = OffsetInFile(c.SysnoOffset, size);
                c.FcpunOffset = OffsetInFile(c.FcpunOffset, size);
                c.TypeOffset = OffsetInFile(c.TypeOffset, size);
                c.RevleOffset = OffsetInFile(c.RevleOffset, size);
                if (c.SysnoOffset >= 0) c.Sysno = SintranCpuIdentity.Word(data, c.SysnoOffset);
                if (c.FcpunOffset >= 0) c.Fcpun = SintranCpuIdentity.Word(data, c.FcpunOffset);
                if (c.TypeOffset >= 0) c.CpuType = SintranCpuIdentity.Word(data, c.TypeOffset);
                if (c.RevleOffset >= 0) c.Revle = SintranCpuIdentity.Word(data, c.RevleOffset);
            }
        }

        // ==================== Read ====================

        /// <summary>
        /// Reads the CPU number and type from both disk copies of the resident data page, the
        /// generation symbols <c>33CPN</c> / <c>33CPU</c>, and the directory stamp. Read-only safe.
        /// </summary>
        /// <returns>
        /// Everything found.
        /// </returns>
        /// <exception cref="SintranCpuIdentityException">
        /// The pack has no symbol lists, no <c>SYSNO</c> / <c>FCPUN</c> symbol, no segment table or no
        /// name records; the partial result travels on the exception.
        /// </exception>
        public SintranCpuIdentityInfo ReadSintranCpuIdentity()
        {
            EnsureInitialized();
            var outInfo = new SintranCpuIdentityInfo();

            // Directory stamp first: it needs no SINTRAN at all.
            var mb = GetMasterBlock();
            outInfo.DirectoryExtValid = mb.ExtValid;
            outInfo.DirectoryEntered = mb.IsDirectoryEntered;
            outInfo.DirectorySystemNumber = (int)mb.ExtLastSystemNumber;

            // The pack's own symbol lists.
            string? p1 = FindSintranSystemFile("SYMBOL-1-LIST", "SYMB");
            string? p2 = FindSintranSystemFile("SYMBOL-2-LIST", "SYMB");
            if (p1 == null || p2 == null)
                throw new SintranCpuIdentityException("not a SINTRAN system pack: no SYMBOL-1-LIST / SYMBOL-2-LIST under SYSTEM", outInfo);
            outInfo.Symbol1Path = p1;
            outInfo.Symbol2Path = p2;
            byte[] s1 = ReadSymbolList(p1, out _);
            byte[] s2 = ReadSymbolList(p2, out bool s2Parity);
            outInfo.Symbol2HasParity = s2Parity;

            int cpn33 = -1, cpu33 = -1;
            if (SintranCpuIdentity.FindSymbol(s2, "33CPN", out int v, out int off, out int width))
            {
                cpn33 = v;
                outInfo.Cpn33ValueOffset = off;
                outInfo.Cpn33ValueWidth = width;
            }
            if (SintranCpuIdentity.FindSymbol(s2, "33CPU", out v, out off, out width))
            {
                cpu33 = v;
                outInfo.Cpu33ValueOffset = off;
                outInfo.Cpu33ValueWidth = width;
            }
            outInfo.Symbols = new SintranSymbolAddresses
            {
                Sysno = SymbolOrMinusOne(s1, s2, "SYSNO"),
                Hwinf = SymbolOrMinusOne(s1, s2, "HWINF"),
                Revle = SymbolOrMinusOne(s1, s2, "REVLE"),
                Fcpun = SymbolOrMinusOne(s2, s1, "FCPUN"),   // K keeps it in list 2
                Inibu = SymbolOrMinusOne(s1, s2, "INIBU"),
                Cpn33 = cpn33,
                Cpu33 = cpu33,
            };
            outInfo.Version = SintranCpuIdentity.VersionFromInibu(outInfo.Symbols.Inibu);

            // Without SYSNO and FCPUN nothing can be addressed safely.
            if (outInfo.Symbols.Sysno < 0 || outInfo.Symbols.Fcpun < 0)
                throw new SintranCpuIdentityException("SYSNO or FCPUN missing from the symbol lists", outInfo);

            // The segment table inside SEGFIL0.
            string? segfilPath = FindSintranSegfil();
            if (segfilPath == null) throw new SintranCpuIdentityException("no SEGFIL0:DATA", outInfo);
            byte[] seg = ReadFile(segfilPath);
            outInfo.SegmentTablePage = SintranInitialCommands.FindSegmentTablePage(seg);
            if (outInfo.SegmentTablePage < 0) throw new SintranCpuIdentityException("no segment table found in SEGFIL0", outInfo);

            // The segment numbers, by name, from the pack's RT-loader records.
            ResolveDpitSegmentNames(seg, outInfo);
            if (!AllResolved(outInfo.Copies))
                throw new SintranCpuIdentityException("DEFINED SEGMENT NAME records for S3IDPIT / S3SDPIT not found", outInfo);

            for (int i = 0; i < outInfo.Copies.Count; i++)
            {
                var c = outInfo.Copies[i];
                int o = outInfo.SegmentTablePage * SintranCpuIdentity.PageBytes + c.SegmentNumber * SintranCpuIdentity.SegEntryBytes;
                c.Logad = SintranCpuIdentity.Word(seg, o + 4);
                c.Segle = SintranCpuIdentity.Word(seg, o + 6) & 0x3FF;
                c.Madr = SintranCpuIdentity.Word(seg, o + 8);
                c.Flag = SintranCpuIdentity.Word(seg, o + 10);
                c.SegmentFile = (c.Flag >> 13) & 7;
                c.FilePath = string.Empty;
                // Usable: a length, a disk address (the 5OK bit) and a range that covers SYSNO. The
                // logical base comes from the entry itself, not from a constant.
                c.Present = c.Segle > 0 && (c.Flag & 1) != 0 &&
                            SintranCpuIdentity.SegmentWordOffset(c.Logad, c.Segle, c.Madr, outInfo.Symbols.Sysno) >= 0;
                if (!c.Present) continue;
                string? path = SintranSegmentFilePath(c.SegmentFile, segfilPath);
                if (path == null) continue;
                c.FilePath = path;
                ReadDpitCopyWords(outInfo.Symbols, c);
            }
            return outInfo;
        }

        // ==================== Write ====================

        private void PatchWordInFile(string path, long offset, int value)
        {
            if (offset < 0) throw new SintranCpuIdentityException($"{path}: word not reachable");
            ObjectEntry? obj = FindObjectForPath(path);
            if (obj == null) throw new SintranCpuIdentityException($"{path}: file not found");
            PatchObjectFileInPlace(obj, offset, new[] { (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF) });
        }

        /// <summary>
        /// Rewrites the octal digits of a <c>NAME=OOOOOO</c> symbol in place, same width, with the list's
        /// own parity convention.
        /// </summary>
        private void PatchSymbolValue(string path, long offset, int width, bool parity, int value)
        {
            if (offset < 0) throw new SintranCpuIdentityException($"{path}: symbol value not reachable");
            ObjectEntry? obj = FindObjectForPath(path);
            if (obj == null) throw new SintranCpuIdentityException($"{path}: file not found");
            PatchObjectFileInPlace(obj, offset, SintranCpuIdentity.EncodeSymbolDigits(value, width, parity));
        }

        private ObjectEntry? FindObjectForPath(string path)
        {
            var (userName, fileName) = ParseNdfsPath(path);
            return FindObjectEntry(userName, fileName);
        }

        /// <summary>
        /// Sets the CPU number (<c>SYSNO</c> and <c>FCPUN</c>) in both disk copies of the resident data
        /// page, the CPU type (<c>HWINF+2</c>) when given, rewrites <c>33CPN</c> / <c>33CPU</c> in
        /// SYMBOL-2-LIST so a later ND patch run keeps the values, clears the directory's "entered by
        /// system N" stamp unless <paramref name="keepStamp"/>, then reads everything back and compares.
        /// Nothing is written unless every target word is reachable. This is the offline form of the
        /// DMAC session <c>)CLOAD S3IDPIT ... SYSNO/old new</c>.
        /// </summary>
        /// <param name="cpuNumber">
        /// The new CPU number, 0 to 65535.
        /// </param>
        /// <param name="cpuType">
        /// The new CPU type, 0 to 65535, or <c>null</c> to keep the one on the pack.
        /// </param>
        /// <param name="keepStamp">
        /// True to leave the directory stamp as it is.
        /// </param>
        /// <returns>
        /// The identity read back after the write.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A value is outside 0 to 65535.
        /// </exception>
        /// <exception cref="SintranCpuIdentityException">
        /// A target word is not reachable on this pack.
        /// </exception>
        /// <exception cref="InvalidDataException">
        /// The read-back does not show what was written.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The device is read-only.
        /// </exception>
        public SintranCpuIdentityInfo WriteSintranCpuIdentity(int cpuNumber, int? cpuType, bool keepStamp)
        {
            EnsureInitialized();
            if (cpuNumber < 0 || cpuNumber > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(cpuNumber), "cpuNumber must be 0..65535");
            if (cpuType.HasValue && (cpuType.Value < 0 || cpuType.Value > 0xFFFF)) throw new ArgumentOutOfRangeException(nameof(cpuType), "cpuType must be 0..65535");
            if (_device == null || _device.IsReadOnly) throw new InvalidOperationException("Cannot write to read-only device");

            SintranCpuIdentityInfo id = ReadSintranCpuIdentity();

            // Everything must be addressable before the first byte is written.
            for (int i = 0; i < id.Copies.Count; i++)
            {
                var c = id.Copies[i];
                if (!c.Present || !c.FileFound) throw new SintranCpuIdentityException($"{c.Name} ({c.Role} copy) has no reachable disk copy", id);
                if (c.SysnoOffset < 0 || c.FcpunOffset < 0) throw new SintranCpuIdentityException($"{c.Name}: SYSNO or FCPUN is outside {c.FilePath}", id);
                if (cpuType.HasValue && c.TypeOffset < 0) throw new SintranCpuIdentityException($"{c.Name}: HWINF+2 is outside {c.FilePath}", id);
            }

            for (int i = 0; i < id.Copies.Count; i++)
            {
                var c = id.Copies[i];
                PatchWordInFile(c.FilePath, c.SysnoOffset, cpuNumber);
                PatchWordInFile(c.FilePath, c.FcpunOffset, cpuNumber);
                if (cpuType.HasValue) PatchWordInFile(c.FilePath, c.TypeOffset, cpuType.Value);
            }

            // The generation symbols, so a later ND patch run keeps the new values.
            if (id.Cpn33ValueOffset >= 0)
                PatchSymbolValue(id.Symbol2Path, id.Cpn33ValueOffset, id.Cpn33ValueWidth, id.Symbol2HasParity, cpuNumber);
            if (cpuType.HasValue && id.Cpu33ValueOffset >= 0)
                PatchSymbolValue(id.Symbol2Path, id.Cpu33ValueOffset, id.Cpu33ValueWidth, id.Symbol2HasParity, cpuType.Value);

            if (!keepStamp && (id.DirectoryEntered || id.DirectorySystemNumber != 0))
                ReleaseDirectory(out _, out _);

            _device.Flush();

            // Read back and compare.
            SintranCpuIdentityInfo after = ReadSintranCpuIdentity();
            for (int i = 0; i < after.Copies.Count; i++)
            {
                var c = after.Copies[i];
                if (c.Sysno != cpuNumber || c.Fcpun != cpuNumber) throw new InvalidDataException($"read-back of {c.Name} does not show CPU number {cpuNumber}");
                if (cpuType.HasValue && c.CpuType != cpuType.Value) throw new InvalidDataException($"read-back of {c.Name} does not show CPU type {cpuType.Value}");
            }
            if (after.Cpn33ValueOffset >= 0 && after.Symbols.Cpn33 != cpuNumber) throw new InvalidDataException("read-back of 33CPN does not show the new CPU number");
            if (cpuType.HasValue && after.Cpu33ValueOffset >= 0 && after.Symbols.Cpu33 != cpuType.Value) throw new InvalidDataException("read-back of 33CPU does not show the new CPU type");
            if (!keepStamp && (after.DirectoryEntered || after.DirectorySystemNumber != 0)) throw new InvalidDataException("the directory stamp is still set after the release");
            return after;
        }
    }
}
