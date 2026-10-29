//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Storage.Ndfs.Elements
{
    /// <summary>
    /// One of the five SINTRAN segment files as the kernel's own tables describe it: <c>DBLST</c> gives
    /// the first disk block, <c>ABLPAGE</c> the blocks per page, <c>MASSNO</c> the disk unit.
    /// </summary>
    public sealed class SintranSegmentFile
    {
        /// <summary>
        /// Gets whether the segment file is defined on this pack.
        /// </summary>
        public bool Defined { get; set; }

        /// <summary>
        /// Gets the first disk block from <c>DBLST</c>, a 32-bit value.
        /// </summary>
        public uint Dblst { get; set; }

        /// <summary>
        /// Gets the blocks per page from <c>ABLPAGE</c>.
        /// </summary>
        public int BlocksPerPage { get; set; }

        /// <summary>
        /// Gets the disk unit from <c>MASSNO</c>.
        /// </summary>
        public int Massno { get; set; }

        /// <summary>
        /// Gets whether the segment file is on this pack (same unit as segment file 0, or none).
        /// </summary>
        public bool OnThisDisk { get; set; } = true;

        /// <summary>
        /// Gets the first disk page of the segment file on this pack; -1 when unknown.
        /// </summary>
        public long BasePage { get; set; } = -1;

        /// <summary>
        /// Gets where <see cref="BasePage"/> came from.
        /// </summary>
        public string Source { get; set; } = "unknown";

        /// <summary>
        /// Gets the NDFS file whose first page is <see cref="BasePage"/>, for labelling; empty when none.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// One segment table entry plus its name and its place on the pack.
    /// </summary>
    public sealed class SintranSegment
    {
        /// <summary>
        /// Gets the segment number (the index into the segment table).
        /// </summary>
        public int Number { get; init; }

        /// <summary>
        /// Gets the name from the RT-loader DEFINED SEGMENT NAME records; empty when unnamed.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets word 0 of the entry (SEGLI).
        /// </summary>
        public int Segli { get; init; }

        /// <summary>
        /// Gets word 1 of the entry (PRESE).
        /// </summary>
        public int Prese { get; init; }

        /// <summary>
        /// Gets word 2 of the entry (LOGAD); the low six bits are the logical base page.
        /// </summary>
        public int Logad { get; init; }

        /// <summary>
        /// Gets the length in pages (SEGLE, low ten bits of word 3).
        /// </summary>
        public int Segle { get; init; }

        /// <summary>
        /// Gets the first page inside the segment file (MADR, word 4).
        /// </summary>
        public int Madr { get; init; }

        /// <summary>
        /// Gets the FLAG word (word 5).
        /// </summary>
        public int Flag { get; init; }

        /// <summary>
        /// Gets word 6 of the entry (SGSTA).
        /// </summary>
        public int Sgsta { get; init; }

        /// <summary>
        /// Gets word 7 of the entry (BPAGL).
        /// </summary>
        public int Bpagl { get; init; }

        /// <summary>
        /// Gets the segment file number, bits 13 to 15 of FLAG.
        /// </summary>
        public int SegmentFile { get; init; }

        /// <summary>
        /// Gets whether FLAG bit 0 (5OK) is set: the segment is loaded in memory, set by SEGIN at run time.
        /// It says nothing about pages on disk; a segment the RT-loader just wrote has it clear.
        /// </summary>
        public bool Ok5 { get; init; }

        /// <summary>
        /// Gets whether the segment has no pages on disk (SEGLE 0, for example S3DPIT and S3RPIT).
        /// </summary>
        public bool MemoryOnly { get; init; }

        /// <summary>
        /// Gets the first disk page of the segment on this pack; -1 when it is not on a known local segment file.
        /// </summary>
        public long DiskPage { get; set; } = -1;
    }

    /// <summary>
    /// Result of <see cref="RetroIO.Storage.Ndfs.FileSystems.NdfsFileSystem.ListSintranSegments"/>.
    /// </summary>
    public sealed class SintranSegmentList
    {
        /// <summary>
        /// Gets the SINTRAN version letter from the <c>INIBU</c> symbol, or <c>?</c>.
        /// </summary>
        public char Version { get; set; } = '?';

        /// <summary>
        /// Gets the page of SEGFIL0 that holds the segment table.
        /// </summary>
        public int SegmentTablePage { get; set; } = -1;

        /// <summary>
        /// Gets the path of the SEGFIL0 object file.
        /// </summary>
        public string SegfilPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets where the segment names came from.
        /// </summary>
        public string NamesPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets whether the symbols <c>BLST</c>, <c>ABLPA</c> and <c>MASSN</c> were read from the pack.
        /// </summary>
        public bool HaveSymbols { get; set; }

        /// <summary>
        /// Gets the address of <c>BLST</c>; -1 when absent.
        /// </summary>
        public int BlstSymbol { get; set; } = -1;

        /// <summary>
        /// Gets the address of <c>ABLPA</c>; -1 when absent.
        /// </summary>
        public int AblpaSymbol { get; set; } = -1;

        /// <summary>
        /// Gets the address of <c>MASSN</c>; -1 when absent.
        /// </summary>
        public int MassnSymbol { get; set; } = -1;

        /// <summary>
        /// Gets the five segment files, indexed by segment file number.
        /// </summary>
        public IReadOnlyList<SintranSegmentFile> SegmentFiles { get; init; } = new[]
        {
            new SintranSegmentFile(), new SintranSegmentFile(), new SintranSegmentFile(),
            new SintranSegmentFile(), new SintranSegmentFile(),
        };

        /// <summary>
        /// Gets every segment the table uses or the name records name.
        /// </summary>
        public List<SintranSegment> Segments { get; init; } = new List<SintranSegment>();
    }
}
