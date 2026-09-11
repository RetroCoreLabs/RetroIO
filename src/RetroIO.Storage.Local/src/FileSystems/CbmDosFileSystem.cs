//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RetroIO.Storage.Core;
using RetroIO.Storage.Local.BlockDevices;

namespace RetroIO.Storage.Local.FileSystems
{
    /// <summary>
    /// CBM DOS filesystem format (versions 2.x, 3.0, and 10.0).
    /// Works with D64 (CBM DOS 2.6), D71 (CBM DOS 3.0), and D81 (CBM DOS 10.0).
    /// Operates on block devices using Track/Sector addressing.
    /// </summary>
    public class CbmDosFileSystem : IBlockFileSystem
    {
        private IBlockDevice? _disk;
        private string _volumeLabel = string.Empty;
        private readonly int _directoryTrack;
        private readonly int _bamTrack;
        private bool _isD71;   // double-sided 1571 (set from the BAM's double-sided flag)

        private const int DirectoryEntrySizeValue = 32;
        // A directory sector holds EIGHT 32-byte entries (256 / 32 = 8). The sector's first two
        // bytes are entry 0's track/sector-link field, which also serves as the pointer to the next
        // directory sector. Earlier code used 7 here and silently dropped the 8th entry of every
        // directory sector. Confirmed against VICE (vdrive-dir.c) and MAME (fs_cbmdos.cpp).
        private const int EntriesPerDirectorySectorValue = 8;

        public string FileSystemType => "CBM-DOS";
        public string VolumeLabel => _volumeLabel;
        public string ExtensionSeparator => "";  // CBM DOS uses FileType as separate metadata, not part of filename

        /// <summary>
        /// Creates a CBM DOS filesystem with specified directory and BAM locations.
        /// </summary>
        /// <param name="directoryTrack">Track containing directory (18 for D64/D71, 40 for D81)</param>
        /// <param name="bamTrack">Track containing BAM (18 for D64/D71, 40 for D81)</param>
        public CbmDosFileSystem(int directoryTrack = 18, int bamTrack = 18)
        {
            _directoryTrack = directoryTrack;
            _bamTrack = bamTrack;
        }

        public void Initialize(IBlockDevice device)
        {
            if (device == null)
            {
                throw new ArgumentNullException(nameof(device));
            }

            _disk = device;
            ParseHeader();
        }

        public bool CanHandle(IBlockDevice device)
        {
            try
            {
                // Check for CBM DOS signature in BAM sector
                Span<byte> bam = stackalloc byte[device.BlockSize];
                device.ReadBlock(BlockAddress.FromTrackSector((byte)_bamTrack, 0), bam);

                // CBM DOS BAM sector has:
                // - Byte 0: Track of first directory sector (should be 18 for D64/D71, 40 for D81)
                // - Byte 1: Sector of first directory sector (should be 1-3)
                // - Byte 2: DOS version ('A' = 0x41 for CBM DOS)

                byte dirTrack = bam[0];
                byte dirSector = bam[1];
                byte dosVersion = bam[2];

                // Simple heuristic: directory track should match expected value
                // and sector should be reasonable (1-3 typically)
                return dirTrack == _directoryTrack && dirSector >= 1 && dirSector <= 3;
            }
            catch
            {
                return false;
            }
        }

        public DirectoryEntry[] ListDirectory(string path)
        {
            EnsureInitialized();

            List<DirectoryEntry> entries = new List<DirectoryEntry>();

            // Directory starts at:
            // - D64/D71: Track 18, Sector 1
            // - D81: Track 40, Sector 3 (sectors 0-2 are BAM)
            byte track = (byte)_directoryTrack;
            byte sector = (_directoryTrack == 40) ? (byte)3 : (byte)1;

            byte[] sectorData = new byte[_disk!.BlockSize];

            while (track != 0)
            {
                _disk!.ReadBlock(BlockAddress.FromTrackSector(track, sector), sectorData);

                for (int i = 0; i < EntriesPerDirectorySectorValue; i++)
                {
                    int offset = 2 + (i * DirectoryEntrySizeValue);
                    byte typeFlags = sectorData[offset];

                    // Check if entry is valid (bit 7 SET = closed/valid, bit 7 CLEAR = scratched/unclosed/empty)
                    if ((typeFlags & 0x80) == 0)
                    {
                        continue; // Skip unclosed, scratched, or empty entries
                    }

                    DirectoryEntry entry = ParseDirectoryEntry(sectorData, offset, entries.Count);
                    entries.Add(entry);
                }

                // Get next directory sector
                track = sectorData[0];
                sector = sectorData[1];
            }

            return entries.ToArray();
        }

        public byte[] ReadFile(string path)
        {
            EnsureInitialized();

            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            CbmDirectoryEntry? entry = FindEntry(path);
            if (entry == null)
            {
                throw new FileNotFoundException($"File not found: {path}");
            }

            return ReadFileData(entry.StartTrack, entry.StartSector);
        }

        // ==============================================================
        // Write support (Phase 2). Currently implements the D64 (1541)
        // layout: BAM at track 18/0, 35 tracks, single side. D71/D81 write
        // builds on this and is added in later phases.
        // ==============================================================

        public void WriteFile(string path, byte[] data)
        {
            EnsureInitialized();
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (_disk!.IsReadOnly)
            {
                throw new InvalidOperationException("Filesystem is mounted read-only");
            }
            EnsureWritableFormat();

            string name = NormalizeFileName(path);

            // Overwrite semantics: if a file with this name already exists, scratch it first (free
            // its data blocks and directory slot) and then write the new content. This matches the
            // CBM "save-and-replace" behaviour and lets the UI replace files.
            if (FindEntry(name) != null)
            {
                DeleteFile("/" + name);
            }

            // Every file uses at least one block; otherwise ceil(len / 254).
            int blockCount = data.Length <= 0 ? 1 : (data.Length + 253) / 254;

            // Allocate the whole chain first so we can roll back cleanly if the disk fills mid-write.
            byte[] chainTracks = new byte[blockCount];
            byte[] chainSectors = new byte[blockCount];
            int allocated = 0;
            try
            {
                for (int i = 0; i < blockCount; i++)
                {
                    if (!AllocateSector(out byte t, out byte s))
                    {
                        throw new IOException("Disk full: not enough free blocks");
                    }
                    chainTracks[i] = t;
                    chainSectors[i] = s;
                    allocated++;
                }
            }
            catch
            {
                for (int i = 0; i < allocated; i++)
                {
                    FreeSector(chainTracks[i], chainSectors[i]);
                }
                throw;
            }

            // Write each data sector, linking to the next; the last sector terminates the chain.
            byte[] buf = new byte[_disk!.BlockSize];
            for (int i = 0; i < blockCount; i++)
            {
                Array.Clear(buf, 0, buf.Length);
                int dataOffset = i * 254;
                int chunk = data.Length - dataOffset;
                if (chunk < 0) chunk = 0;
                if (chunk > 254) chunk = 254;

                if (i < blockCount - 1)
                {
                    buf[0] = chainTracks[i + 1];
                    buf[1] = chainSectors[i + 1];
                }
                else
                {
                    buf[0] = 0;                    // end-of-chain
                    buf[1] = (byte)(chunk + 1);    // index of last used data byte
                }
                for (int b = 0; b < chunk; b++)
                {
                    buf[2 + b] = data[dataOffset + b];
                }
                _disk!.WriteBlock(BlockAddress.FromTrackSector(chainTracks[i], chainSectors[i]), buf);
            }

            // PRG directory entry (file type 2).
            WriteDirectoryEntry(name, 2, chainTracks[0], chainSectors[0], blockCount);
            _disk!.Flush();
        }

        public void DeleteFile(string path)
        {
            EnsureInitialized();
            if (_disk!.IsReadOnly)
            {
                throw new InvalidOperationException("Filesystem is mounted read-only");
            }
            EnsureWritableFormat();

            string name = NormalizeFileName(path);
            if (!LocateDirSlot(name, out byte dirTrack, out byte dirSector, out int slot, out byte startT, out byte startS))
            {
                throw new FileNotFoundException($"File not found: {name}");
            }

            // Free every data block in the chain, then mark the directory slot as scratched (type 0).
            FreeChain(startT, startS);

            byte[] sec = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector(dirTrack, dirSector), sec);
            sec[slot * 32 + 2] = 0x00;
            _disk!.WriteBlock(BlockAddress.FromTrackSector(dirTrack, dirSector), sec);
            _disk!.Flush();
        }

        private bool IsD81 => _bamTrack == 40;

        // First directory sector: D81 = track 40 sector 3; D64/D71 = track 18 sector 1.
        private byte FirstDirSector => (byte)(_directoryTrack == 40 ? 3 : 1);

        /// <summary>
        /// Formats the mounted (read-write) device as a blank CBM DOS disk: zeroes all sectors and
        /// writes a fresh BAM, disk header (name + id + "2A" DOS type) and an empty first directory
        /// sector. Currently implements the D64 (35-track 1541) layout.
        /// </summary>
        /// <param name="diskName">Disk name (max 16 chars, PETSCII-encoded).</param>
        /// <param name="diskId">Two-character disk id.</param>
        public void Format(string diskName, string diskId)
        {
            EnsureInitialized();
            if (_disk!.IsReadOnly)
            {
                throw new InvalidOperationException("Filesystem is mounted read-only");
            }
            string name = diskName ?? string.Empty;
            string id = diskId ?? string.Empty;
            if (_bamTrack == 40)
            {
                FormatD81(name, id);
            }
            else
            {
                int maxTrack = MaxTrack();
                if (maxTrack >= 70)
                {
                    FormatD71(name, id);
                }
                else if (maxTrack == 35)
                {
                    FormatD64(name, id);
                }
                else
                {
                    throw new NotSupportedException("CBM format: unsupported disk geometry");
                }
            }
            _disk!.Flush();
            ParseHeader(); // refresh volume label + format flags from the freshly written BAM
        }

        // All-free bitmap for a track with the given sector count (bit set = free).
        private static uint AllFreeBits(int sectorsPerTrack)
        {
            return sectorsPerTrack >= 32 ? 0xFFFFFFFFu : ((1u << sectorsPerTrack) - 1);
        }

        // Writes the 16-byte disk name (0xA0-padded) at the given offset.
        private static void WriteDiskName(byte[] buffer, int offset, string name)
        {
            for (int i = 0; i < 16; i++)
            {
                buffer[offset + i] = 0xA0;
            }
            for (int i = 0; i < name.Length && i < 16; i++)
            {
                buffer[offset + i] = AsciiToPetscii(name[i]);
            }
        }

        private void FormatD64(string diskName, string diskId)
        {
            // 1) Zero every sector (so a re-format wipes any prior content).
            byte[] zero = new byte[_disk!.BlockSize];
            for (int t = 1; t <= 35; t++)
            {
                int spt = _disk!.GetBlocksPerTrack(t);
                for (int s = 0; s < spt; s++)
                {
                    _disk!.WriteBlock(BlockAddress.FromTrackSector((byte)t, (byte)s), zero);
                }
            }

            // 2) Build the BAM at track 18 sector 0.
            byte[] bam = new byte[_disk!.BlockSize];
            bam[0] = 18;   // first directory track
            bam[1] = 1;    // first directory sector
            bam[2] = 0x41; // 'A' = CBM DOS format
            bam[3] = 0;    // single-sided (D64)

            for (int t = 1; t <= 35; t++)
            {
                int spt = _disk!.GetBlocksPerTrack(t);
                int e = 4 * t;
                int free = spt;
                uint bits = spt >= 32 ? 0xFFFFFFFFu : ((1u << spt) - 1); // all sectors free
                if (t == 18)
                {
                    bits &= ~3u; // sectors 0 (BAM) and 1 (directory) allocated
                    free -= 2;
                }
                bam[e] = (byte)free;
                bam[e + 1] = (byte)(bits & 0xFF);
                bam[e + 2] = (byte)((bits >> 8) & 0xFF);
                bam[e + 3] = (byte)((bits >> 16) & 0xFF);
            }

            // Disk name (16 bytes, 0xA0-padded), id, and DOS type "2A".
            for (int i = 0; i < 16; i++)
            {
                bam[0x90 + i] = 0xA0;
            }
            for (int i = 0; i < diskName.Length && i < 16; i++)
            {
                bam[0x90 + i] = AsciiToPetscii(diskName[i]);
            }
            bam[0xA0] = 0xA0;
            bam[0xA1] = 0xA0;
            bam[0xA2] = diskId.Length > 0 ? AsciiToPetscii(diskId[0]) : (byte)0xA0;
            bam[0xA3] = diskId.Length > 1 ? AsciiToPetscii(diskId[1]) : (byte)0xA0;
            bam[0xA4] = 0xA0;
            bam[0xA5] = 0x32; // '2'
            bam[0xA6] = 0x41; // 'A'
            bam[0xA7] = 0xA0;
            bam[0xA8] = 0xA0;
            bam[0xA9] = 0xA0;
            bam[0xAA] = 0xA0;
            _disk!.WriteBlock(BlockAddress.FromTrackSector(18, 0), bam);

            // 3) Empty first directory sector: no next sector (track 0), sector marker 0xFF.
            byte[] dir = new byte[_disk!.BlockSize];
            dir[0] = 0;
            dir[1] = 0xFF;
            _disk!.WriteBlock(BlockAddress.FromTrackSector(18, 1), dir);
        }

        // Blank D71 (1571): side-1 BAM on track 18 (with the double-sided flag and the side-2 free
        // counts at 0xDD), side-2 bitmaps on track 53, empty directory at 18/1. Tracks 18 and 53 are
        // fully reserved (giving the canonical 1328 free). Layout verified by hexdumping a c1541 image.
        private void FormatD71(string diskName, string diskId)
        {
            byte[] zero = new byte[_disk!.BlockSize];
            for (int t = 1; t <= 70; t++)
            {
                int spt = _disk!.GetBlocksPerTrack(t);
                for (int s = 0; s < spt; s++)
                {
                    _disk!.WriteBlock(BlockAddress.FromTrackSector((byte)t, (byte)s), zero);
                }
            }

            byte[] bam18 = new byte[_disk!.BlockSize];
            bam18[0] = 18;
            bam18[1] = 1;
            bam18[2] = 0x41;
            bam18[3] = 0x80; // double-sided flag

            // Side-1 per-track entries (tracks 1-35).
            for (int t = 1; t <= 35; t++)
            {
                int spt = _disk!.GetBlocksPerTrack(t);
                int e = 4 * t;
                int free = spt;
                uint bits = AllFreeBits(spt);
                if (t == 18)
                {
                    bits &= ~3u;
                    free -= 2;
                }
                bam18[e] = (byte)free;
                bam18[e + 1] = (byte)(bits & 0xFF);
                bam18[e + 2] = (byte)((bits >> 8) & 0xFF);
                bam18[e + 3] = (byte)((bits >> 16) & 0xFF);
            }

            // Side-2 free counts at 0xDD (tracks 36-70); track 53 is fully reserved (count 0).
            for (int t = 36; t <= 70; t++)
            {
                int spt = _disk!.GetBlocksPerTrack(t);
                bam18[0xDD + (t - 36)] = t == 53 ? (byte)0 : (byte)spt;
            }

            WriteDiskName(bam18, 0x90, diskName);
            bam18[0xA0] = 0xA0;
            bam18[0xA1] = 0xA0;
            bam18[0xA2] = diskId.Length > 0 ? AsciiToPetscii(diskId[0]) : (byte)0xA0;
            bam18[0xA3] = diskId.Length > 1 ? AsciiToPetscii(diskId[1]) : (byte)0xA0;
            bam18[0xA4] = 0xA0;
            bam18[0xA5] = 0x32;
            bam18[0xA6] = 0x41;
            bam18[0xA7] = 0xA0;
            bam18[0xA8] = 0xA0;
            bam18[0xA9] = 0xA0;
            bam18[0xAA] = 0xA0;
            _disk!.WriteBlock(BlockAddress.FromTrackSector(18, 0), bam18);

            // Side-2 bitmaps on track 53 (3 bytes/track from offset 0); track 53 fully allocated.
            byte[] bam53 = new byte[_disk!.BlockSize];
            for (int t = 36; t <= 70; t++)
            {
                int spt = _disk!.GetBlocksPerTrack(t);
                int b = (t - 36) * 3;
                uint bits = t == 53 ? 0u : AllFreeBits(spt);
                bam53[b] = (byte)(bits & 0xFF);
                bam53[b + 1] = (byte)((bits >> 8) & 0xFF);
                bam53[b + 2] = (byte)((bits >> 16) & 0xFF);
            }
            _disk!.WriteBlock(BlockAddress.FromTrackSector(53, 0), bam53);

            byte[] dir = new byte[_disk!.BlockSize];
            dir[0] = 0;
            dir[1] = 0xFF;
            _disk!.WriteBlock(BlockAddress.FromTrackSector(18, 1), dir);
        }

        // Blank D81 (1581): header at 40/0, split BAM at 40/1 (tracks 1-40) and 40/2 (tracks 41-80,
        // 6-byte entries from 0x10), empty directory at 40/3. Track 40 is fully reserved (3160 free).
        // Layout (incl. the 0x44/0xBB/id/0xC0 BAM headers) verified by hexdumping a c1541 image.
        private void FormatD81(string diskName, string diskId)
        {
            byte[] zero = new byte[_disk!.BlockSize];
            for (int t = 1; t <= 80; t++)
            {
                for (int s = 0; s < 40; s++)
                {
                    _disk!.WriteBlock(BlockAddress.FromTrackSector((byte)t, (byte)s), zero);
                }
            }

            byte id0 = diskId.Length > 0 ? AsciiToPetscii(diskId[0]) : (byte)0xA0;
            byte id1 = diskId.Length > 1 ? AsciiToPetscii(diskId[1]) : (byte)0xA0;

            // Header (40,0).
            byte[] hdr = new byte[_disk!.BlockSize];
            hdr[0] = 0x28; // 40 (first dir track)
            hdr[1] = 0x03; // first dir sector
            hdr[2] = 0x44; // 'D'
            hdr[3] = 0x00;
            WriteDiskName(hdr, 0x04, diskName);
            hdr[0x14] = 0xA0;
            hdr[0x15] = 0xA0;
            hdr[0x16] = id0;
            hdr[0x17] = id1;
            hdr[0x18] = 0xA0;
            hdr[0x19] = 0x33; // '3'
            hdr[0x1A] = 0x44; // 'D'
            hdr[0x1B] = 0xA0;
            hdr[0x1C] = 0xA0;
            _disk!.WriteBlock(BlockAddress.FromTrackSector(40, 0), hdr);

            // BAM sector 1 (tracks 1-40); links to BAM sector 2.
            byte[] bam1 = new byte[_disk!.BlockSize];
            bam1[0] = 0x28;
            bam1[1] = 0x02;
            bam1[2] = 0x44;
            bam1[3] = 0xBB;
            bam1[4] = id0;
            bam1[5] = id1;
            bam1[6] = 0xC0;
            FillD81BamEntries(bam1, 1, 40);
            _disk!.WriteBlock(BlockAddress.FromTrackSector(40, 1), bam1);

            // BAM sector 2 (tracks 41-80); end of chain.
            byte[] bam2 = new byte[_disk!.BlockSize];
            bam2[0] = 0x00;
            bam2[1] = 0xFF;
            bam2[2] = 0x44;
            bam2[3] = 0xBB;
            bam2[4] = id0;
            bam2[5] = id1;
            bam2[6] = 0xC0;
            FillD81BamEntries(bam2, 41, 80);
            _disk!.WriteBlock(BlockAddress.FromTrackSector(40, 2), bam2);

            byte[] dir = new byte[_disk!.BlockSize];
            dir[0] = 0;
            dir[1] = 0xFF;
            _disk!.WriteBlock(BlockAddress.FromTrackSector(40, 3), dir);
        }

        // Fills the 6-byte-per-track D81 BAM entries (from offset 0x10) for the given track range.
        // All sectors free (40 each) except the reserved directory track 40 (free 0, bitmap 0).
        private static void FillD81BamEntries(byte[] bam, int firstTrack, int lastTrack)
        {
            for (int t = firstTrack; t <= lastTrack; t++)
            {
                int entry = 0x10 + (t - firstTrack) * 6;
                if (t == 40)
                {
                    bam[entry] = 0; // directory track fully reserved
                    for (int k = 1; k <= 5; k++)
                    {
                        bam[entry + k] = 0;
                    }
                }
                else
                {
                    bam[entry] = 40; // all 40 sectors free
                    for (int k = 1; k <= 5; k++)
                    {
                        bam[entry + k] = 0xFF;
                    }
                }
            }
        }

        // Write is supported for D64 (1541), D71 (1571) and D81 (1581).
        private void EnsureWritableFormat()
        {
            if (_bamTrack == 40 || _bamTrack == 18)
            {
                return; // D81 (40) or D64/D71 (18)
            }
            throw new NotSupportedException("CBM write: unsupported disk layout");
        }

        // Dispatches sector allocation/free to the format-specific BAM implementation.
        private bool AllocateSector(out byte outTrack, out byte outSector)
        {
            if (IsD81)
            {
                return AllocateSectorD81(out outTrack, out outSector);
            }
            if (_isD71)
            {
                return AllocateSectorD71(out outTrack, out outSector);
            }
            return AllocateSectorD64(out outTrack, out outSector);
        }

        private void FreeSector(byte track, byte sector)
        {
            if (IsD81)
            {
                FreeSectorD81(track, sector);
                return;
            }
            if (_isD71)
            {
                FreeSectorD71(track, sector);
                return;
            }
            FreeSectorD64(track, sector);
        }

        // D71 BAM: side 1 (tracks 1-35) is the standard 1541 BAM on track 18 (4 bytes/track at
        // 4*track). Side 2 (tracks 36-70) keeps its free COUNTS on track 18 at 0xDD+(t-36) and its
        // BITMAPS on track 53/0 at (t-36)*3 (3 bytes/track). Verified by hexdumping a c1541 image.
        // Tracks 18 and 53 (the two BAM/directory tracks) are skipped for data allocation.
        private bool AllocateSectorD71(out byte outTrack, out byte outSector)
        {
            outTrack = 0;
            outSector = 0;

            // ---- Side 1: tracks 1-35 ----
            byte[] bam18 = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector(18, 0), bam18);
            for (int track = 1; track <= 35; track++)
            {
                if (track == _directoryTrack)
                {
                    continue;
                }
                if (bam18[4 * track] == 0)
                {
                    continue;
                }
                int spt1 = _disk!.GetBlocksPerTrack(track);
                for (int sec = 0; sec < spt1; sec++)
                {
                    int byteIdx = 4 * track + 1 + (sec >> 3);
                    int mask = 1 << (sec & 7);
                    if ((bam18[byteIdx] & mask) != 0)
                    {
                        bam18[byteIdx] = (byte)(bam18[byteIdx] & ~mask);
                        bam18[4 * track] = (byte)(bam18[4 * track] - 1);
                        _disk!.WriteBlock(BlockAddress.FromTrackSector(18, 0), bam18);
                        outTrack = (byte)track;
                        outSector = (byte)sec;
                        return true;
                    }
                }
            }

            // ---- Side 2: tracks 36-70 (counts on track 18 @0xDD, bitmaps on track 53) ----
            byte[] bam53 = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector(53, 0), bam53);
            for (int track = 36; track <= 70; track++)
            {
                if (track == 53)
                {
                    continue; // side-2 BAM/directory track
                }
                int countIdx = 0xDD + (track - 36);
                if (bam18[countIdx] == 0)
                {
                    continue;
                }
                int spt2 = _disk!.GetBlocksPerTrack(track);
                int bmBase = (track - 36) * 3;
                for (int sec = 0; sec < spt2; sec++)
                {
                    int byteIdx = bmBase + (sec >> 3);
                    int mask = 1 << (sec & 7);
                    if ((bam53[byteIdx] & mask) != 0)
                    {
                        bam53[byteIdx] = (byte)(bam53[byteIdx] & ~mask);
                        bam18[countIdx] = (byte)(bam18[countIdx] - 1);
                        _disk!.WriteBlock(BlockAddress.FromTrackSector(53, 0), bam53);
                        _disk!.WriteBlock(BlockAddress.FromTrackSector(18, 0), bam18);
                        outTrack = (byte)track;
                        outSector = (byte)sec;
                        return true;
                    }
                }
            }
            return false;
        }

        private void FreeSectorD71(byte track, byte sector)
        {
            if (track <= 35)
            {
                FreeSectorD64(track, sector); // side 1 is identical to D64
                return;
            }

            byte[] bam18 = new byte[_disk!.BlockSize];
            byte[] bam53 = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector(18, 0), bam18);
            _disk!.ReadBlock(BlockAddress.FromTrackSector(53, 0), bam53);

            int countIdx = 0xDD + (track - 36);
            int bmBase = (track - 36) * 3;
            int byteIdx = bmBase + (sector >> 3);
            int mask = 1 << (sector & 7);
            if ((bam53[byteIdx] & mask) == 0) // currently allocated
            {
                bam53[byteIdx] = (byte)(bam53[byteIdx] | mask);
                bam18[countIdx] = (byte)(bam18[countIdx] + 1);
                _disk!.WriteBlock(BlockAddress.FromTrackSector(53, 0), bam53);
                _disk!.WriteBlock(BlockAddress.FromTrackSector(18, 0), bam18);
            }
        }

        // D81 BAM: track 40 sector 1 covers tracks 1-40, sector 2 covers tracks 41-80. Per-track entry
        // = 6 bytes from offset 0x10 (byte0 = free count, bytes 1-5 = 40-bit bitmap, LSB = sector 0).
        private bool AllocateSectorD81(out byte outTrack, out byte outSector)
        {
            outTrack = 0;
            outSector = 0;
            byte[] bam = new byte[_disk!.BlockSize];

            for (int track = 1; track <= 80; track++)
            {
                if (track == _directoryTrack)
                {
                    continue;
                }
                int bamSector = track <= 40 ? 1 : 2;
                int firstTrack = track <= 40 ? 1 : 41;
                _disk!.ReadBlock(BlockAddress.FromTrackSector(40, (byte)bamSector), bam);
                int entry = 0x10 + (track - firstTrack) * 6;
                if (bam[entry] == 0)
                {
                    continue;
                }
                for (int sec = 0; sec < 40; sec++)
                {
                    int byteIdx = entry + 1 + (sec >> 3);
                    int mask = 1 << (sec & 7);
                    if ((bam[byteIdx] & mask) != 0)
                    {
                        bam[byteIdx] = (byte)(bam[byteIdx] & ~mask);
                        bam[entry] = (byte)(bam[entry] - 1);
                        _disk!.WriteBlock(BlockAddress.FromTrackSector(40, (byte)bamSector), bam);
                        outTrack = (byte)track;
                        outSector = (byte)sec;
                        return true;
                    }
                }
            }
            return false;
        }

        private void FreeSectorD81(byte track, byte sector)
        {
            int bamSector = track <= 40 ? 1 : 2;
            int firstTrack = track <= 40 ? 1 : 41;
            byte[] bam = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector(40, (byte)bamSector), bam);
            int entry = 0x10 + (track - firstTrack) * 6;
            int byteIdx = entry + 1 + (sector >> 3);
            int mask = 1 << (sector & 7);
            if ((bam[byteIdx] & mask) == 0)
            {
                bam[byteIdx] = (byte)(bam[byteIdx] | mask);
                bam[entry] = (byte)(bam[entry] + 1);
                _disk!.WriteBlock(BlockAddress.FromTrackSector(40, (byte)bamSector), bam);
            }
        }

        // Validates and canonicalises a CBM file name (max 16 chars, no DOS-illegal characters).
        private static string NormalizeFileName(string path)
        {
            string name = path ?? string.Empty;
            if (name.StartsWith("/"))
            {
                name = name.Substring(1);
            }
            name = name.Trim();
            if (name.Length == 0)
            {
                throw new ArgumentException("Empty file name");
            }
            if (name.Length > 16)
            {
                throw new ArgumentException("CBM file names are at most 16 characters");
            }
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == ',' || c == '=' || c == '*' || c == '?' || c == ':' || c == '$' || c < ' ')
                {
                    throw new ArgumentException($"Illegal character in CBM file name: '{c}'");
                }
            }
            return name.ToUpperInvariant();
        }

        // Highest valid track on the mounted device (probes the block-device geometry).
        private int MaxTrack()
        {
            int max = 0;
            // Probe past D81's 80 tracks so 70-track D71 and 80-track D81 are detected correctly.
            for (int t = 1; t <= 84; t++)
            {
                try
                {
                    _disk!.GetBlocksPerTrack(t);
                    max = t;
                }
                catch
                {
                    break;
                }
            }
            return max;
        }

        // Finds and claims the first free sector in the D64 BAM (tracks 1-35, skipping track 18).
        private bool AllocateSectorD64(out byte outTrack, out byte outSector)
        {
            outTrack = 0;
            outSector = 0;

            byte[] bam = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector((byte)_bamTrack, 0), bam);

            int maxTrack = MaxTrack();
            for (int track = 1; track <= maxTrack && track <= 35; track++)
            {
                if (track == _directoryTrack)
                {
                    continue; // keep the directory track for directory sectors
                }
                if (bam[4 * track] == 0)
                {
                    continue; // no free sectors on this track
                }

                int spt = _disk!.GetBlocksPerTrack(track);
                for (int sec = 0; sec < spt; sec++)
                {
                    int byteIdx = 4 * track + 1 + (sec >> 3);
                    int mask = 1 << (sec & 7);
                    if ((bam[byteIdx] & mask) != 0) // bit set = free
                    {
                        bam[byteIdx] = (byte)(bam[byteIdx] & ~mask);  // mark allocated
                        bam[4 * track] = (byte)(bam[4 * track] - 1);  // decrement free count
                        _disk!.WriteBlock(BlockAddress.FromTrackSector((byte)_bamTrack, 0), bam);
                        outTrack = (byte)track;
                        outSector = (byte)sec;
                        return true;
                    }
                }
            }
            return false;
        }

        // Releases a single sector back to the D64 BAM.
        private void FreeSectorD64(byte track, byte sector)
        {
            byte[] bam = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector((byte)_bamTrack, 0), bam);

            int byteIdx = 4 * track + 1 + (sector >> 3);
            int mask = 1 << (sector & 7);
            if ((bam[byteIdx] & mask) == 0) // currently allocated
            {
                bam[byteIdx] = (byte)(bam[byteIdx] | mask);
                bam[4 * track] = (byte)(bam[4 * track] + 1);
                _disk!.WriteBlock(BlockAddress.FromTrackSector((byte)_bamTrack, 0), bam);
            }
        }

        // Frees a whole track/sector chain (the data blocks of a file).
        private void FreeChain(byte track, byte sector)
        {
            byte[] block = new byte[_disk!.BlockSize];
            int guard = 0;
            while (track != 0 && guard < 100000)
            {
                guard++;
                _disk!.ReadBlock(BlockAddress.FromTrackSector(track, sector), block);
                byte nextTrack = block[0];
                byte nextSector = block[1];
                FreeSector(track, sector);
                if (nextTrack == 0)
                {
                    break;
                }
                track = nextTrack;
                sector = nextSector;
            }
        }

        // Writes a directory entry into the first free slot, extending the directory chain if needed.
        private void WriteDirectoryEntry(string name, byte fileType, byte startTrack, byte startSector, int blocks)
        {
            byte[] sec = new byte[_disk!.BlockSize];
            byte track = (byte)_directoryTrack;
            byte sector = FirstDirSector;

            while (true)
            {
                _disk!.ReadBlock(BlockAddress.FromTrackSector(track, sector), sec);

                for (int slot = 0; slot < 8; slot++)
                {
                    int off = slot * 32;
                    if ((sec[off + 2] & 0x80) == 0) // free or scratched slot
                    {
                        FillDirEntry(sec, off, name, fileType, startTrack, startSector, blocks);
                        _disk!.WriteBlock(BlockAddress.FromTrackSector(track, sector), sec);
                        return;
                    }
                }

                byte nextTrack = sec[0];
                byte nextSector = sec[1];
                if (nextTrack == 0)
                {
                    // Directory full — allocate the next directory sector ON the directory track
                    // (CBM convention; the directory track is excluded from the data free count, so
                    // directory growth does not consume usable blocks, matching c1541).
                    if (!AllocateDirSector(out byte newT, out byte newS))
                    {
                        throw new IOException("Directory full");
                    }
                    sec[0] = newT;
                    sec[1] = newS;
                    _disk!.WriteBlock(BlockAddress.FromTrackSector(track, sector), sec);

                    byte[] fresh = new byte[_disk!.BlockSize];
                    fresh[0] = 0;
                    fresh[1] = 0xFF;
                    FillDirEntry(fresh, 0, name, fileType, startTrack, startSector, blocks);
                    _disk!.WriteBlock(BlockAddress.FromTrackSector(newT, newS), fresh);
                    return;
                }
                track = nextTrack;
                sector = nextSector;
            }
        }

        // Allocates a free sector ON the directory track itself (track 18 for D64/D71, track 40 for
        // D81) for directory expansion. The directory track is excluded from the data free count, so
        // this does not reduce usable blocks — matching how c1541 grows the directory.
        private bool AllocateDirSector(out byte outTrack, out byte outSector)
        {
            outTrack = (byte)_directoryTrack;
            outSector = 0;

            if (IsD81)
            {
                // Dir track 40, BAM entry in track-40 sector 1 (track 40 -> offset 0x10 + 39*6).
                byte[] bam = new byte[_disk!.BlockSize];
                _disk!.ReadBlock(BlockAddress.FromTrackSector(40, 1), bam);
                int entry = 0x10 + (40 - 1) * 6;
                if (bam[entry] == 0)
                {
                    return false;
                }
                for (int sec = 0; sec < 40; sec++)
                {
                    int byteIdx = entry + 1 + (sec >> 3);
                    int mask = 1 << (sec & 7);
                    if ((bam[byteIdx] & mask) != 0)
                    {
                        bam[byteIdx] = (byte)(bam[byteIdx] & ~mask);
                        bam[entry] = (byte)(bam[entry] - 1);
                        _disk!.WriteBlock(BlockAddress.FromTrackSector(40, 1), bam);
                        outSector = (byte)sec;
                        return true;
                    }
                }
                return false;
            }

            // D64/D71 dir track 18, standard 1541 BAM entry at 4*18.
            byte[] bam18 = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector(18, 0), bam18);
            int t = _directoryTrack;
            if (bam18[4 * t] == 0)
            {
                return false;
            }
            int spt = _disk!.GetBlocksPerTrack(t);
            for (int sec = 0; sec < spt; sec++)
            {
                int byteIdx = 4 * t + 1 + (sec >> 3);
                int mask = 1 << (sec & 7);
                if ((bam18[byteIdx] & mask) != 0)
                {
                    bam18[byteIdx] = (byte)(bam18[byteIdx] & ~mask);
                    bam18[4 * t] = (byte)(bam18[4 * t] - 1);
                    _disk!.WriteBlock(BlockAddress.FromTrackSector(18, 0), bam18);
                    outSector = (byte)sec;
                    return true;
                }
            }
            return false;
        }

        // Fills a 32-byte directory slot (does NOT touch bytes 0-1, which hold the dir chain pointer).
        private static void FillDirEntry(byte[] sec, int off, string name, byte fileType, byte startTrack, byte startSector, int blocks)
        {
            sec[off + 2] = (byte)(0x80 | (fileType & 0x0F)); // closed + type (e.g. PRG -> 0x82)
            sec[off + 3] = startTrack;
            sec[off + 4] = startSector;
            for (int i = 0; i < 16; i++)
            {
                sec[off + 5 + i] = i < name.Length ? AsciiToPetscii(name[i]) : (byte)0xA0;
            }
            for (int i = 21; i <= 29; i++)
            {
                sec[off + i] = 0; // REL info + GEOS + unused
            }
            sec[off + 30] = (byte)(blocks & 0xFF);
            sec[off + 31] = (byte)((blocks >> 8) & 0xFF);
        }

        // Locates a live directory entry by name, returning its sector/slot and the file's start t/s.
        private bool LocateDirSlot(string name, out byte dirTrack, out byte dirSector, out int slot, out byte startTrack, out byte startSector)
        {
            dirTrack = 0;
            dirSector = 0;
            slot = 0;
            startTrack = 0;
            startSector = 0;

            byte track = (byte)_directoryTrack;
            byte sector = (_directoryTrack == 40) ? (byte)3 : (byte)1;
            byte[] sec = new byte[_disk!.BlockSize];
            int guard = 0;

            while (track != 0 && guard < 4096)
            {
                guard++;
                _disk!.ReadBlock(BlockAddress.FromTrackSector(track, sector), sec);
                for (int i = 0; i < 8; i++)
                {
                    int off = i * 32;
                    if ((sec[off + 2] & 0x80) == 0)
                    {
                        continue;
                    }
                    string entryName = PetsciiToAscii(sec, off + 5, 16);
                    if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        dirTrack = track;
                        dirSector = sector;
                        slot = i;
                        startTrack = sec[off + 3];
                        startSector = sec[off + 4];
                        return true;
                    }
                }
                track = sec[0];
                sector = sec[1];
            }
            return false;
        }

        // Converts an ASCII filename character to its PETSCII byte for on-disk storage, matching
        // the convention used by VICE c1541 (verified by hexdump): uppercase ASCII 'A'-'Z' maps to
        // SHIFTED PETSCII 0xC1-0xDA, and lowercase 'a'-'z' maps to 0x41-0x5A. Using the unshifted
        // 0x41-0x5A range for uppercase would make c1541's name lookup (e.g. `-read HELLO`) miss the
        // file, even though it still appears in the directory listing. PetsciiToAscii already decodes
        // 0xC1-0xDA back to 'A'-'Z', so the round-trip is symmetric.
        private static byte AsciiToPetscii(char c)
        {
            if (c >= 'A' && c <= 'Z')
            {
                return (byte)(c + 0x80); // 'A'(0x41) -> 0xC1 .. 'Z'(0x5A) -> 0xDA
            }
            if (c >= 'a' && c <= 'z')
            {
                return (byte)(c - 'a' + 0x41); // 'a' -> 0x41 .. 'z' -> 0x5A
            }
            if (c >= ' ' && c <= '?')
            {
                return (byte)c; // space, digits and common symbols share ASCII/PETSCII codes
            }
            return 0x3F; // '?'
        }

        public FileMetadata? GetMetadata(string path)
        {
            EnsureInitialized();

            if (path.StartsWith("/"))
            {
                path = path.Substring(1);
            }

            CbmDirectoryEntry? entry = FindEntry(path);
            if (entry == null)
            {
                return null;
            }

            FileMetadata metadata = new FileMetadata
            {
                Name = entry.FileName,
                Path = "/" + entry.FileName,
                IsDirectory = entry.FileType == "CBM", // CBM type indicates subdirectory (D81 only)
                Size = entry.Blocks * 254, // Approximate size (each block has 2 overhead bytes)
                FileType = entry.FileType,
                Attributes = entry.Locked ? "L" : "",
                UrnIdentifier = UrnIdentifier.Encode(entry.FileName)
            };

            metadata.SetProperty("StartTrack", entry.StartTrack);
            metadata.SetProperty("StartSector", entry.StartSector);
            metadata.SetProperty("Blocks", entry.Blocks);
            metadata.SetProperty("Locked", entry.Locked);

            return metadata;
        }

        public int GetBlocksFree()
        {
            EnsureInitialized();

            // The "blocks free" the drive reports = sum of each track's BAM free-sector count,
            // EXCLUDING the directory/BAM track. Layouts (per VICE vdrive-bam.c / diskconstants.h):
            //   D64 : track 18 sector 0; per-track entry = 4 bytes at offset 4*track; byte0 = free.
            //   D71 : as D64 for tracks 1-35; side-2 (tracks 36-70) free counts packed at 0xDD.. in
            //         the same track-18 BAM sector (bitmaps live on track 53).
            //   D81 : track 40 sectors 1 & 2; per-track entry = 6 bytes from offset 0x10; byte0 = free.
            // NOTE: the D64 path is unit-validated against real images; the D71/D81 paths follow the
            // documented VICE layout but await real D71/D81 images for validation.
            if (_bamTrack == 40)
            {
                return GetBlocksFreeD81();
            }
            return GetBlocksFreeD64OrD71();
        }

        // D64 (35 tracks) plus, when the disk is flagged double-sided, the D71 second side.
        private int GetBlocksFreeD64OrD71()
        {
            byte[] bam = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector((byte)_bamTrack, 0), bam);

            int free = 0;
            // Tracks 1..35: 4-byte entry per track, byte0 = free-sector count.
            for (int track = 1; track <= 35; track++)
            {
                if (track == _directoryTrack)
                {
                    continue; // directory/BAM track is reserved, not counted as free space
                }
                free += bam[4 * track];
            }

            // D71 second side: byte 3 bit 7 set marks a double-sided (1571) disk. Side-2 free counts
            // for tracks 36..70 are stored at offsets 0xDD..0xFF (one byte per track).
            bool doubleSided = (bam[3] & 0x80) != 0;
            if (doubleSided)
            {
                for (int track = 36; track <= 70; track++)
                {
                    if (track == _directoryTrack)
                    {
                        continue;
                    }
                    free += bam[0xDD + (track - 36)];
                }
            }

            return free;
        }

        // D81: BAM is split across track 40 sector 1 (tracks 1-40) and sector 2 (tracks 41-80).
        private int GetBlocksFreeD81()
        {
            int free = 0;
            free += SumD81BamSector(sector: 1, firstTrack: 1, lastTrack: 40);
            free += SumD81BamSector(sector: 2, firstTrack: 41, lastTrack: 80);
            return free;
        }

        private int SumD81BamSector(int sector, int firstTrack, int lastTrack)
        {
            byte[] bam = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector((byte)_bamTrack, (byte)sector), bam);

            // Per-track entry = 6 bytes (byte0 = free count, bytes1-5 = 40-bit bitmap), starting at
            // offset 0x10 within the BAM sector (VICE D81 BAM layout).
            const int bamEntryBase = 0x10;
            const int bamEntryStride = 6;

            int free = 0;
            for (int track = firstTrack; track <= lastTrack; track++)
            {
                if (track == _directoryTrack)
                {
                    continue; // track 40 (directory) is reserved
                }
                int entry = bamEntryBase + (track - firstTrack) * bamEntryStride;
                free += bam[entry];
            }
            return free;
        }

        public void CreateDirectory(string path)
        {
            throw new NotSupportedException("CBM DOS does not support subdirectories");
        }

        public void DeleteDirectory(string path)
        {
            throw new NotSupportedException("CBM DOS does not support subdirectories");
        }

        public void Rename(string oldPath, string newPath)
        {
            EnsureInitialized();
            if (_disk!.IsReadOnly)
            {
                throw new InvalidOperationException("Filesystem is mounted read-only");
            }
            EnsureWritableFormat();

            string oldName = NormalizeFileName(oldPath);
            string newName = NormalizeFileName(newPath);
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                return; // no-op
            }
            if (FindEntry(newName) != null)
            {
                throw new IOException($"Target name already exists: {newName}");
            }
            if (!LocateDirSlot(oldName, out byte dirTrack, out byte dirSector, out int slot, out _, out _))
            {
                throw new FileNotFoundException($"File not found: {oldName}");
            }

            // Rewrite the 16-byte name field in place (data chain and file type are unchanged).
            byte[] sec = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector(dirTrack, dirSector), sec);
            int off = slot * 32;
            for (int i = 0; i < 16; i++)
            {
                sec[off + 5 + i] = i < newName.Length ? AsciiToPetscii(newName[i]) : (byte)0xA0;
            }
            _disk!.WriteBlock(BlockAddress.FromTrackSector(dirTrack, dirSector), sec);
            _disk!.Flush();
        }

        /// <summary>
        /// Parses a directory entry from sector data.
        ///
        /// Directory Entry Structure (32 bytes per spec, but we skip 2-byte t/s link):
        /// Spec byte 00-01: T/S link (at sector start, we skip this)
        /// Spec byte 02:    File type        ← offset+0 in our code
        /// Spec byte 03-04: Start T/S        ← offset+1,2
        /// Spec byte 05-14: Filename         ← offset+3 to 18
        /// Spec byte 15-17: REL info         ← offset+13 to 15
        /// Spec byte 18-1D: Unused           ← offset+16 to 27
        /// Spec byte 1E-1F: File size blocks ← offset+28,29 (0x1E-2=28, 0x1F-2=29)
        ///
        /// Note: Offset points to file type byte (sector[2] for first entry).
        /// Spec file size is at entry byte 1E-1F (30-31 dec), but since offset skips
        /// the 2-byte t/s link, we read at offset+28-29 instead of offset+30-31.
        /// </summary>
        private DirectoryEntry ParseDirectoryEntry(byte[] sectorData, int offset, int directoryIndex)
        {
            byte typeFlags = sectorData[offset];
            byte startTrack = sectorData[offset + 1];
            byte startSector = sectorData[offset + 2];
            string fileName = PetsciiToAscii(sectorData, offset + 3, 16);
            ushort blocks = (ushort)(sectorData[offset + 28] | (sectorData[offset + 29] << 8));

            byte fileType = (byte)(typeFlags & 0x07);
            string fileTypeStr = GetFileTypeString(fileType);

            FileMetadata metadata = new FileMetadata
            {
                Name = fileName,
                Path = "/" + fileName,
                IsDirectory = fileType == 5, // CBM type (subdirectory, D81 only)
                Size = blocks * 254,
                FileType = fileTypeStr,
                Attributes = (typeFlags & 0x40) != 0 ? "L" : "",
                UrnIdentifier = UrnIdentifier.Encode(fileName)
            };

            metadata.SetProperty("StartTrack", startTrack);
            metadata.SetProperty("StartSector", startSector);
            metadata.SetProperty("Blocks", blocks);
            metadata.SetProperty("DirectoryIndex", directoryIndex);

            return new DirectoryEntry { Name = fileName, Metadata = metadata };
        }

        /// <summary>
        /// Finds a directory entry by filename.
        /// </summary>
        private CbmDirectoryEntry? FindEntry(string fileName)
        {
            byte track = (byte)_directoryTrack;
            byte sector = (_directoryTrack == 40) ? (byte)3 : (byte)1;

            byte[] sectorData = new byte[_disk!.BlockSize];

            while (track != 0)
            {
                _disk!.ReadBlock(BlockAddress.FromTrackSector(track, sector), sectorData);

                for (int i = 0; i < EntriesPerDirectorySectorValue; i++)
                {
                    int offset = 2 + (i * DirectoryEntrySizeValue);
                    byte typeFlags = sectorData[offset];

                    if ((typeFlags & 0x80) == 0)
                    {
                        continue;
                    }

                    string entryName = PetsciiToAscii(sectorData, offset + 3, 16);
                    if (string.Equals(entryName, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return new CbmDirectoryEntry
                        {
                            StartTrack = sectorData[offset + 1],
                            StartSector = sectorData[offset + 2],
                            FileName = entryName,
                            Blocks = (ushort)(sectorData[offset + 28] | (sectorData[offset + 29] << 8)),
                            FileType = GetFileTypeString((byte)(typeFlags & 0x07)),
                            Locked = (typeFlags & 0x40) != 0
                        };
                    }
                }

                track = sectorData[0];
                sector = sectorData[1];
            }

            return null;
        }

        /// <summary>
        /// Reads file data by following track/sector chain.
        /// </summary>
        private byte[] ReadFileData(byte track, byte sector)
        {
            List<byte> fileData = new List<byte>();
            byte[] block = new byte[_disk!.BlockSize];

            while (track != 0)
            {
                _disk!.ReadBlock(BlockAddress.FromTrackSector(track, sector), block);

                byte nextTrack = block[0];
                byte nextSector = block[1];

                if (nextTrack == 0)
                {
                    // Last block: byte 1 (nextSector) is the INDEX of the last used data byte, so the
                    // data occupies indices 2..lastUsed inclusive (count = lastUsed - 1). The previous
                    // code read "2 + byteCount" bytes, which over-read and could index past the
                    // 256-byte sector (IndexOutOfRange). Bound-check defends against malformed images.
                    int lastUsed = nextSector;
                    for (int i = 2; i <= lastUsed && i < block.Length; i++)
                    {
                        fileData.Add(block[i]);
                    }
                    break;
                }
                else
                {
                    // Full block - all 254 data bytes
                    for (int i = 2; i < _disk!.BlockSize; i++)
                    {
                        fileData.Add(block[i]);
                    }
                }

                track = nextTrack;
                sector = nextSector;
            }

            return fileData.ToArray();
        }

        /// <summary>
        /// Parses disk header to extract volume label.
        /// </summary>
        private void ParseHeader()
        {
            byte[] headerSector = new byte[_disk!.BlockSize];
            _disk!.ReadBlock(BlockAddress.FromTrackSector((byte)_bamTrack, 0), headerSector);

            // Disk name location depends on format:
            // - D64/D71: 0x90-0x9F
            // - D81: 0x04-0x13
            int nameOffset = (_bamTrack == 40) ? 0x04 : 0x90;
            _volumeLabel = PetsciiToAscii(headerSector, nameOffset, 16);

            // On a 1541-layout BAM (track 18), byte 3 bit 7 marks a double-sided 1571 disk.
            _isD71 = (_bamTrack == 18) && (headerSector[3] & 0x80) != 0;
        }

        /// <summary>
        /// Converts PETSCII string to ASCII.
        /// </summary>
        protected static string PetsciiToAscii(byte[] data, int offset, int length)
        {
            StringBuilder sb = new StringBuilder(length);

            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];

                if (b == 0xA0)
                {
                    break; // Shifted space padding
                }

                if (b >= 0x41 && b <= 0x5A)
                {
                    sb.Append((char)b); // A-Z
                }
                else if (b >= 0xC1 && b <= 0xDA)
                {
                    sb.Append((char)(b - 0x80)); // Shifted lowercase -> uppercase
                }
                else if (b >= 0x20 && b <= 0x3F)
                {
                    sb.Append((char)b); // Numbers and symbols
                }
                else
                {
                    sb.Append('?'); // Unknown character
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Gets file type string from type flags.
        /// </summary>
        protected static string GetFileTypeString(byte fileType)
        {
            return fileType switch
            {
                1 => "SEQ",
                2 => "PRG",
                3 => "USR",
                4 => "REL",
                5 => "CBM", // D81-specific subdirectory type
                _ => "DEL"
            };
        }

        private void EnsureInitialized()
        {
            if (_disk == null)
            {
                throw new InvalidOperationException("DOS filesystem not initialized");
            }
        }

        /// <summary>
        /// Internal directory entry structure.
        /// </summary>
        private sealed class CbmDirectoryEntry
        {
            public byte StartTrack { get; set; }
            public byte StartSector { get; set; }
            public string FileName { get; set; } = string.Empty;
            public ushort Blocks { get; set; }
            public string FileType { get; set; } = string.Empty;
            public bool Locked { get; set; }
        }
    }
}
