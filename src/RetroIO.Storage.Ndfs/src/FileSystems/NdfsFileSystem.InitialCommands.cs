//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;
using RetroIO.Storage.Ndfs.BlockDevices;
using RetroIO.Storage.Ndfs.Elements;

namespace RetroIO.Storage.Ndfs.FileSystems
{
    /// <summary>
    /// Device-bound entry points for the SINTRAN III initial-command buffer. The buffer has no file
    /// form: it lives at kernel symbol INIBU inside the SINTRAN command segment, whose disk image is
    /// held in the (SYSTEM)SEGFIL0:DATA object file. Reading pulls SEGFIL0's bytes and hands them to
    /// the pure locator (<see cref="SintranInitialCommands"/>); writing patches the buffer back
    /// IN PLACE (page-by-page over SEGFIL0's own blocks) so the large contiguous system file is never
    /// re-allocated. See <see cref="SintranInitialCommands"/> for the full derivation and provenance.
    /// </summary>
    public partial class NdfsFileSystem
    {
        /// <summary>
        /// Locates and decodes the SINTRAN III initial-command buffer on this pack. Returns <c>null</c>
        /// when the pack is not a SINTRAN system disk (no SEGFIL0) or the buffer can't be located
        /// (unrecognised version). Read-only safe.
        /// </summary>
        public SintranInitialCommandBuffer? ReadSintranInitialCommands()
        {
            EnsureInitialized();

            ObjectEntry? segfil = FindSegfilEntry(out string segfilPath);
            if (segfil == null)
                return null;

            byte[] seg = ReadObjectData(segfil);
            var loc = SintranInitialCommands.Locate(seg);
            if (loc == null)
                return null;

            return new SintranInitialCommandBuffer
            {
                Commands = loc.Commands,
                VersionLetter = loc.VersionLetter,
                SegmentNumber = loc.SegmentNumber,
                Madr = loc.Madr,
                ByteOffset = loc.ByteOffset,
                SegmentTablePage = loc.SegmentTablePage,
                TextLength = loc.TextLength,
                LengthCellOffset = loc.LengthCellOffset,
                SegfilPath = segfilPath,
            };
        }

        /// <summary>
        /// Replaces the entire SINTRAN III initial-command buffer with <paramref name="commands"/> and
        /// writes it back to SEGFIL0 in place. Commands are upper-cased and validated (no embedded quote,
        /// printable ASCII only); the total encoded text must fit SINTRAN's 254-byte limit. The length
        /// cell at INIBU+130 is updated so SINTRAN reads exactly the new text.
        /// </summary>
        /// <param name="commands">The new initial commands, in execution order (may be empty to clear).</param>
        /// <returns>True on success; false if the pack has no locatable buffer.</returns>
        /// <exception cref="InvalidOperationException">Device is read-only, or a command overflows the buffer.</exception>
        /// <exception cref="ArgumentException">A command is empty, contains a quote, or a non-printable character.</exception>
        public bool WriteSintranInitialCommands(IReadOnlyList<string> commands)
        {
            EnsureInitialized();

            if (commands == null)
                throw new ArgumentNullException(nameof(commands));

            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Cannot write to read-only device");

            ObjectEntry? segfil = FindSegfilEntry(out _);
            if (segfil == null)
                return false;

            byte[] seg = ReadObjectData(segfil);
            var loc = SintranInitialCommands.Locate(seg);
            if (loc == null)
                return false;

            // Encode the new command list and build the exact 262-byte region image (text area + length
            // cell, stopping before the INCOM code word). This throws on overflow / invalid commands.
            var enc = SintranInitialCommands.EncodeBuffer(commands);
            byte[] region = SintranInitialCommands.BuildRegionImage(enc);

            // Patch SEGFIL0 in place at the file byte offset of INIBU.
            PatchObjectFileInPlace(segfil, loc.ByteOffset, region);

            // GenericBlockDevice buffers writes; flush so the change actually reaches the image.
            _device.Flush();
            return true;
        }

        /// <summary>
        /// REPAIRS a SINTRAN initial-command buffer whose header was clobbered (length cell / first bytes
        /// corrupted) so <see cref="ReadSintranInitialCommands"/> can no longer locate it. It finds the
        /// buffer by matching the surviving command text (<see cref="SintranInitialCommands.LocateRepairTarget"/>,
        /// which demands a single unambiguous winner) and writes <paramref name="commands"/> there, fixing
        /// the length cell and leading bytes. Refuses (returns false via <paramref name="targetByteOffset"/>
        /// = -1) when no clear target is found — never guesses, since a wrong write would patch SINTRAN code.
        /// After this succeeds the buffer is self-consistent again, so <see cref="ReadSintranInitialCommands"/>
        /// will find it normally — the caller should re-read to VERIFY.
        /// </summary>
        /// <param name="commands">The commands to write (the good set to restore).</param>
        /// <param name="targetByteOffset">Set to the SEGFIL0 byte offset written, or -1 when no target was found.</param>
        /// <param name="keywordScore">Set to the winning keyword score (higher = more confident) for reporting.</param>
        /// <returns>True when the buffer was repaired; false when no unambiguous target was found.</returns>
        public bool RepairSintranInitialCommands(IReadOnlyList<string> commands, out long targetByteOffset, out int keywordScore)
        {
            EnsureInitialized();
            targetByteOffset = -1;
            keywordScore = 0;

            if (commands == null)
                throw new ArgumentNullException(nameof(commands));
            if (_device == null || _device.IsReadOnly)
                throw new InvalidOperationException("Cannot write to read-only device");

            ObjectEntry? segfil = FindSegfilEntry(out _);
            if (segfil == null)
                return false;

            byte[] seg = ReadObjectData(segfil);

            var target = SintranInitialCommands.LocateRepairTarget(seg, out keywordScore);
            if (target == null)
                return false;   // no unambiguous target — refuse rather than corrupt SINTRAN code

            // Encode the good command set and patch it in place at the identified buffer offset.
            var enc = SintranInitialCommands.EncodeBuffer(commands);
            byte[] region = SintranInitialCommands.BuildRegionImage(enc);
            PatchObjectFileInPlace(segfil, target.ByteOffset, region);
            _device.Flush();

            targetByteOffset = target.ByteOffset;
            return true;
        }

        /// <summary>
        /// Diagnostic: enumerates EVERY INIBU-containing segment candidate in SEGFIL0 (all SINTRAN
        /// versions, all matching segments), in the order <see cref="ReadSintranInitialCommands"/> would
        /// scan them. Use this to see which segment the reader picks and whether other copies hold a
        /// stale/garbage buffer. Returns an empty list when there is no SEGFIL0 / no segment table.
        /// </summary>
        public IReadOnlyList<SintranInitialCommands.Candidate> DiagnoseSintranInitialCommands()
        {
            EnsureInitialized();

            ObjectEntry? segfil = FindSegfilEntry(out _);
            if (segfil == null)
                return System.Array.Empty<SintranInitialCommands.Candidate>();

            byte[] seg = ReadObjectData(segfil);
            return SintranInitialCommands.EnumerateCandidates(seg);
        }

        /// <summary>
        /// Finds the SEGFIL0 (or legacy SEGFIL) object file — the SINTRAN memory/segment swap image.
        /// Returns the entry and its USER/NAME:TYPE path, or null when absent (not a system pack).
        /// </summary>
        private ObjectEntry? FindSegfilEntry(out string path)
        {
            path = string.Empty;
            var entries = GetObjectEntries();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                string name = e.ObjectName ?? string.Empty;
                // Match SEGFIL0 or the older SEGFIL (case-insensitive), exactly.
                if (string.Equals(name, "SEGFIL0", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "SEGFIL", StringComparison.OrdinalIgnoreCase))
                {
                    string type = (e.Type ?? string.Empty).TrimEnd('\x27', ' ');
                    string display = string.IsNullOrEmpty(type) ? name : $"{name}{ExtensionSeparator}{type}";
                    path = $"{e.UserName}/{display}";
                    return e;
                }
            }
            return null;
        }

        /// <summary>
        /// Overwrites <paramref name="patch"/> bytes at file byte offset <paramref name="fileByteOffset"/>
        /// within an object file, page-by-page over the file's real block chain. Only the affected pages
        /// are touched, so allocation/contiguity is preserved (safe for a large contiguous system file
        /// like SEGFIL0). The patch region may straddle a page boundary.
        /// </summary>
        private void PatchObjectFileInPlace(ObjectEntry obj, long fileByteOffset, byte[] patch)
        {
            if (_device == null)
                throw new InvalidOperationException("Device not initialized");
            if (obj.FilePointer == null || obj.FilePointer.BlockID == 0)
                throw new InvalidDataException("Invalid file pointer");

            int pageSize = NdfsBlockDeviceDescriptor.NDFS_PAGE_SIZE;   // 2048
            var pointers = GetAllBlockPointersForFile(obj.FilePointer, obj.BytesInFile);

            long endExclusive = fileByteOffset + patch.Length;
            long startPage = fileByteOffset / pageSize;
            long endPage = (endExclusive - 1) / pageSize;

            var pageBuffer = new byte[pageSize];

            for (long pg = startPage; pg <= endPage; pg++)
            {
                if (pg < 0 || pg >= pointers.Count)
                    throw new InvalidDataException($"Patch offset {fileByteOffset} extends past file '{obj.ObjectName}' (page {pg} of {pointers.Count})");

                uint blockId = pointers[(int)pg].BlockID;
                if (blockId == 0)
                    throw new InvalidDataException($"Cannot patch a sparse/unallocated page (index {pg}) of '{obj.ObjectName}'");

                // Read-modify-write this page: only the overlapping slice of the patch is applied.
                _device.ReadBlock(BlockAddress.FromLBA(blockId), pageBuffer);

                long pageStart = pg * pageSize;
                long ovStart = Math.Max(fileByteOffset, pageStart);
                long ovEnd = Math.Min(endExclusive, pageStart + pageSize);
                int ovLen = (int)(ovEnd - ovStart);
                int srcIndex = (int)(ovStart - fileByteOffset);
                int dstIndex = (int)(ovStart - pageStart);

                Array.Copy(patch, srcIndex, pageBuffer, dstIndex, ovLen);
                _device.WriteBlock(BlockAddress.FromLBA(blockId), pageBuffer);
            }
        }
    }
}
