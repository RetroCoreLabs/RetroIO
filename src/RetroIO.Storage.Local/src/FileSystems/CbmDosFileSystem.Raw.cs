//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.FileSystems
{
    /// <summary>
    /// One directory entry as it is stored, with its name as the 16 raw PETSCII bytes.
    /// </summary>
    public sealed class CbmRawDirectoryEntry
    {
        /// <summary>
        /// Gets the place in the listing, counting closed entries only; the same number as the
        /// <c>DirectoryIndex</c> property of <see cref="CbmDosFileSystem.ListDirectory"/>.
        /// </summary>
        public int Index { get; init; }

        /// <summary>
        /// Gets the file type byte: bit 7 closed, bit 6 locked, the low three bits the type
        /// (0 DEL, 1 SEQ, 2 PRG, 3 USR, 4 REL).
        /// </summary>
        public byte TypeByte { get; init; }

        /// <summary>
        /// Gets the 16 name bytes as stored, including any A0h padding.
        /// </summary>
        public byte[] Name { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Gets the block count the entry states.
        /// </summary>
        public int Blocks { get; init; }

        /// <summary>
        /// Gets the track of the file's first sector, 0 for an entry with no data.
        /// </summary>
        public byte StartTrack { get; init; }

        /// <summary>
        /// Gets the sector of the file's first sector.
        /// </summary>
        public byte StartSector { get; init; }
    }

    /// <summary>
    /// Raw directory access: names, the disk name and the disk id as the bytes that are stored, for
    /// directory art made of PETSCII graphics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Directory art is a run of entries whose names hold graphic characters and shifted spaces,
    /// usually of file type DEL with no data, so the listing draws a picture. These methods store
    /// any byte exactly as given and make no check of their own on the name; the normal name
    /// methods (<see cref="WriteFile"/>, <see cref="Rename"/>) still take ASCII names.
    /// </para>
    /// <para>
    /// A DEL entry here is closed (type byte 80h), so the listing shows it. Its start track and
    /// sector are 0 and it owns no blocks.
    /// </para>
    /// </remarks>
    public partial class CbmDosFileSystem
    {
        /// <summary>
        /// The length of a name, a disk name, in bytes.
        /// </summary>
        public const int RawNameLength = 16;

        /// <summary>
        /// The type byte of a closed DEL entry, the usual type of a directory art line.
        /// </summary>
        public const byte ClosedDelType = 0x80;

        /// <summary>
        /// Reads every closed directory entry with its raw name bytes, in listing order.
        /// </summary>
        /// <returns>
        /// The entries.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        public CbmRawDirectoryEntry[] ReadRawDirectory()
        {
            EnsureInitialized();
            List<SlotPlace> slots = ReadSlots();
            List<CbmRawDirectoryEntry> entries = new List<CbmRawDirectoryEntry>();
            for (int i = 0; i < slots.Count; i++)
            {
                byte[] body = slots[i].Body;
                if ((body[0] & 0x80) == 0)
                {
                    continue;
                }

                byte[] name = new byte[RawNameLength];
                Array.Copy(body, 3, name, 0, RawNameLength);
                entries.Add(new CbmRawDirectoryEntry
                {
                    Index = entries.Count,
                    TypeByte = body[0],
                    Name = name,
                    StartTrack = body[1],
                    StartSector = body[2],
                    Blocks = body[28] | (body[29] << 8),
                });
            }

            return entries.ToArray();
        }

        /// <summary>
        /// Replaces the 16 name bytes of one directory entry with raw bytes.
        /// </summary>
        /// <param name="index">
        /// The entry's <see cref="CbmRawDirectoryEntry.Index"/>.
        /// </param>
        /// <param name="name">
        /// Exactly 16 bytes, stored as given; pad a short name with A0h.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> is not 16 bytes.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when no entry has <paramref name="index"/>.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the disk is mounted read-only.
        /// </exception>
        public void WriteRawName(int index, ReadOnlySpan<byte> name)
        {
            CheckRawWrite(name, RawNameLength, nameof(name));
            List<SlotPlace> slots = ReadSlots();
            int closed = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if ((slots[i].Body[0] & 0x80) == 0)
                {
                    continue;
                }

                if (closed == index)
                {
                    byte[] sec = new byte[_disk!.BlockSize];
                    _disk.ReadBlock(BlockAddress.FromTrackSector(slots[i].Track, slots[i].Sector), sec);
                    name.CopyTo(sec.AsSpan((slots[i].Slot * 32) + 5, RawNameLength));
                    _disk.WriteBlock(BlockAddress.FromTrackSector(slots[i].Track, slots[i].Sector), sec);
                    _disk.Flush();
                    return;
                }

                closed++;
            }

            throw new ArgumentOutOfRangeException(nameof(index), "No directory entry " + index);
        }

        /// <summary>
        /// Puts a closed DEL entry with a raw name at a chosen place in the listing; the entries
        /// from that place on move down one.
        /// </summary>
        /// <remarks>
        /// The directory is written again in order. Slots of scratched entries (type byte 0) are
        /// free space and are not kept; an unclosed file keeps its slot and its order. The directory
        /// grows by one sector on its own track when it is full.
        /// </remarks>
        /// <param name="position">
        /// The <see cref="CbmRawDirectoryEntry.Index"/> the new entry gets, from 0 to the number of
        /// closed entries (the end).
        /// </param>
        /// <param name="name">
        /// Exactly 16 bytes, stored as given.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="name"/> is not 16 bytes.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="position"/> is past the end.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the disk is mounted read-only or the directory track is full.
        /// </exception>
        public void InsertDelEntry(int position, ReadOnlySpan<byte> name)
        {
            CheckRawWrite(name, RawNameLength, nameof(name));
            List<SlotPlace> slots = ReadSlots();
            List<byte[]> kept = new List<byte[]>();
            List<byte> sectorTracks = new List<byte>();
            List<byte> sectorNumbers = new List<byte>();
            int closed = 0;
            bool placed = false;
            byte[] entry = new byte[30];
            entry[0] = ClosedDelType;
            name.CopyTo(entry.AsSpan(3, RawNameLength));
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Slot == 0)
                {
                    sectorTracks.Add(slots[i].Track);
                    sectorNumbers.Add(slots[i].Sector);
                }

                byte[] body = slots[i].Body;
                if (body[0] == 0)
                {
                    continue;
                }

                if ((body[0] & 0x80) != 0)
                {
                    if (closed == position)
                    {
                        kept.Add(entry);
                        placed = true;
                    }

                    closed++;
                }

                kept.Add(body);
            }

            if (!placed)
            {
                if (position != closed)
                {
                    throw new ArgumentOutOfRangeException(nameof(position), "The directory has " + closed + " entries");
                }

                kept.Add(entry);
            }

            byte[] sec = new byte[_disk!.BlockSize];
            if (kept.Count > sectorTracks.Count * EntriesPerDirectorySectorValue)
            {
                if (!AllocateDirSector(out byte newTrack, out byte newSector))
                {
                    throw new IOException("Directory full");
                }

                int last = sectorTracks.Count - 1;
                _disk.ReadBlock(BlockAddress.FromTrackSector(sectorTracks[last], sectorNumbers[last]), sec);
                sec[0] = newTrack;
                sec[1] = newSector;
                _disk.WriteBlock(BlockAddress.FromTrackSector(sectorTracks[last], sectorNumbers[last]), sec);
                byte[] fresh = new byte[_disk.BlockSize];
                fresh[1] = 0xFF;
                _disk.WriteBlock(BlockAddress.FromTrackSector(newTrack, newSector), fresh);
                sectorTracks.Add(newTrack);
                sectorNumbers.Add(newSector);
            }

            int next = 0;
            for (int s = 0; s < sectorTracks.Count; s++)
            {
                BlockAddress address = BlockAddress.FromTrackSector(sectorTracks[s], sectorNumbers[s]);
                _disk.ReadBlock(address, sec);
                for (int slot = 0; slot < EntriesPerDirectorySectorValue; slot++)
                {
                    Span<byte> target = sec.AsSpan((slot * 32) + 2, 30);
                    if (next < kept.Count)
                    {
                        kept[next].CopyTo(target);
                        next++;
                    }
                    else
                    {
                        target.Clear();
                    }
                }

                _disk.WriteBlock(address, sec);
            }

            _disk.Flush();
        }

        /// <summary>
        /// Reads the 16 raw bytes of the disk name.
        /// </summary>
        /// <returns>
        /// The disk name as stored, including any A0h padding.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        public byte[] ReadRawDiskName()
        {
            EnsureInitialized();
            byte[] header = ReadHeaderSector();
            return header.AsSpan(DiskNameOffset, RawNameLength).ToArray();
        }

        /// <summary>
        /// Reads the 2 raw bytes of the disk id.
        /// </summary>
        /// <returns>
        /// The disk id as stored.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the file system is not initialized.
        /// </exception>
        public byte[] ReadRawDiskId()
        {
            EnsureInitialized();
            byte[] header = ReadHeaderSector();
            return header.AsSpan(DiskIdOffset, 2).ToArray();
        }

        /// <summary>
        /// Replaces the disk name and the disk id with raw bytes.
        /// </summary>
        /// <remarks>
        /// On a 1581 disk the id is also kept in its two BAM sectors (40/1 and 40/2, bytes 4 and 5),
        /// and those copies are written too.
        /// </remarks>
        /// <param name="name">
        /// Exactly 16 bytes, stored as given.
        /// </param>
        /// <param name="id">
        /// Exactly 2 bytes, stored as given.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when a value has the wrong length.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the disk is mounted read-only.
        /// </exception>
        public void WriteRawDiskHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> id)
        {
            CheckRawWrite(name, RawNameLength, nameof(name));
            if (id.Length != 2)
            {
                throw new ArgumentException("A disk id is 2 bytes", nameof(id));
            }

            byte[] header = ReadHeaderSector();
            name.CopyTo(header.AsSpan(DiskNameOffset, RawNameLength));
            id.CopyTo(header.AsSpan(DiskIdOffset, 2));
            _disk!.WriteBlock(BlockAddress.FromTrackSector((byte)_bamTrack, 0), header);
            if (IsD81)
            {
                byte[] bam = new byte[_disk.BlockSize];
                for (byte s = 1; s <= 2; s++)
                {
                    _disk.ReadBlock(BlockAddress.FromTrackSector(40, s), bam);
                    id.CopyTo(bam.AsSpan(4, 2));
                    _disk.WriteBlock(BlockAddress.FromTrackSector(40, s), bam);
                }
            }

            _disk.Flush();
            ParseHeader();
        }

        /// <summary>
        /// Sets the file type and the locked flag of a closed file.
        /// </summary>
        /// <remarks>
        /// Only the types a file written here can be: DEL, SEQ, PRG and USR. A REL file needs side
        /// sectors and a record length, and a 1581 partition (CBM) a block range, which a plain
        /// write does not make, so those are refused.
        /// </remarks>
        /// <param name="path">
        /// File name, with or without a leading <c>/</c>.
        /// </param>
        /// <param name="fileType">
        /// <c>DEL</c>, <c>SEQ</c>, <c>PRG</c> or <c>USR</c>.
        /// </param>
        /// <param name="locked">
        /// Whether the file is locked (bit 6 of the type byte; the listing shows it with a less-than sign).
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="fileType"/> is not one of the four.
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// Thrown when there is no such file.
        /// </exception>
        /// <exception cref="IOException">
        /// Thrown when the disk is mounted read-only.
        /// </exception>
        public void SetFileType(string path, string fileType, bool locked)
        {
            int type = fileType switch
            {
                "DEL" => 0,
                "SEQ" => 1,
                "PRG" => 2,
                "USR" => 3,
                _ => throw new ArgumentException("A written file can be DEL, SEQ, PRG or USR, not " + fileType, nameof(fileType)),
            };
            CheckRawWrite(new byte[RawNameLength], RawNameLength, nameof(path));
            string name = NormalizeFileName(path);
            if (!LocateDirSlot(name, out byte dirTrack, out byte dirSector, out int slot, out _, out _))
            {
                throw new FileNotFoundException("File not found: " + name);
            }

            byte[] sec = new byte[_disk!.BlockSize];
            BlockAddress address = BlockAddress.FromTrackSector(dirTrack, dirSector);
            _disk.ReadBlock(address, sec);
            sec[(slot * 32) + 2] = (byte)(0x80 | (locked ? 0x40 : 0) | type);
            _disk.WriteBlock(address, sec);
            _disk.Flush();
        }

        // Where the disk name and id are in the header sector: 1541/1571 at 90h and A2h of 18/0,
        // 1581 at 04h and 16h of 40/0.
        private int DiskNameOffset => IsD81 ? 0x04 : 0x90;

        private int DiskIdOffset => IsD81 ? 0x16 : 0xA2;

        private byte[] ReadHeaderSector()
        {
            byte[] header = new byte[_disk!.BlockSize];
            _disk.ReadBlock(BlockAddress.FromTrackSector((byte)_bamTrack, 0), header);
            return header;
        }

        private void CheckRawWrite(ReadOnlySpan<byte> value, int length, string parameter)
        {
            EnsureInitialized();
            if (value.Length != length)
            {
                throw new ArgumentException("Must be exactly " + length + " bytes", parameter);
            }

            if (_disk!.IsReadOnly)
            {
                throw new IOException("Filesystem is mounted read-only");
            }

            EnsureWritableFormat();
        }

        // Every slot of the directory chain, in order, with a copy of its 30 entry bytes.
        private List<SlotPlace> ReadSlots()
        {
            List<SlotPlace> slots = new List<SlotPlace>();
            byte track = (byte)_directoryTrack;
            byte sector = FirstDirSector;
            byte[] sec = new byte[_disk!.BlockSize];
            int guard = 0;
            while (track != 0)
            {
                guard++;
                if (guard > 256)
                {
                    throw new InvalidDataException("The directory chain loops back on itself.");
                }

                ReadLinkedBlock(track, sector, sec);
                for (int slot = 0; slot < EntriesPerDirectorySectorValue; slot++)
                {
                    slots.Add(new SlotPlace(track, sector, slot, sec.AsSpan((slot * 32) + 2, 30).ToArray()));
                }

                track = sec[0];
                sector = sec[1];
            }

            return slots;
        }

        private readonly struct SlotPlace
        {
            public readonly byte Track;
            public readonly byte Sector;
            public readonly int Slot;
            public readonly byte[] Body;

            public SlotPlace(byte track, byte sector, int slot, byte[] body)
            {
                Track = track;
                Sector = sector;
                Slot = slot;
                Body = body;
            }
        }
    }
}
