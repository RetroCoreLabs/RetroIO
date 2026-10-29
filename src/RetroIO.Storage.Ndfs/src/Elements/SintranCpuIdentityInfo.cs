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
    /// Word addresses of the kernel symbols the CPU identity lives at, read from the pack's own
    /// <c>(SYSTEM)SYMBOL-1-LIST:SYMB</c> and <c>SYMBOL-2-LIST:SYMB</c>. A value of -1 means the symbol
    /// was not found. <c>FCPUN</c> moves from build to build (105034B on a K pack, 004414B on L), which
    /// is why nothing here is a constant.
    /// </summary>
    public sealed class SintranSymbolAddresses
    {
        /// <summary>
        /// Gets the address of <c>SYSNO</c>, the CPU number the kernel reports and COSMOS routes by.
        /// </summary>
        public int Sysno { get; init; } = -1;

        /// <summary>
        /// Gets the address of <c>HWINF</c>; the CPU type is the word at <c>HWINF+2</c>.
        /// </summary>
        public int Hwinf { get; init; } = -1;

        /// <summary>
        /// Gets the address of <c>REVLE</c>, the patch level word.
        /// </summary>
        public int Revle { get; init; } = -1;

        /// <summary>
        /// Gets the address of <c>FCPUN</c>, the second copy of the CPU number.
        /// </summary>
        public int Fcpun { get; init; } = -1;

        /// <summary>
        /// Gets the value of <c>33CPN</c>, the CPU number the generation was asked for.
        /// </summary>
        public int Cpn33 { get; init; } = -1;

        /// <summary>
        /// Gets the value of <c>33CPU</c>, the CPU type the generation was asked for.
        /// </summary>
        public int Cpu33 { get; init; } = -1;

        /// <summary>
        /// Gets the address of <c>INIBU</c>, used only to name the SINTRAN version letter.
        /// </summary>
        public int Inibu { get; init; } = -1;
    }

    /// <summary>
    /// One disk copy of the resident data page (DPIT) and the identity words read from it. The image
    /// copy (segment named <c>S3IDPIT</c>) is what a warm start loads; the save copy (<c>S3SDPIT</c>)
    /// is what a cold start copies over it.
    /// </summary>
    public sealed class SintranDpitCopy
    {
        /// <summary>
        /// Gets the segment name the copy was found under (<c>S3IDPIT</c> or <c>S3SDPIT</c>).
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Gets the role of the copy: <c>image</c> or <c>save</c>.
        /// </summary>
        public string Role { get; init; } = string.Empty;

        /// <summary>
        /// Gets the segment number the RT-loader name records give; -1 when unresolved.
        /// </summary>
        public int SegmentNumber { get; set; } = -1;

        /// <summary>
        /// Gets whether the segment table entry is usable: it has pages and covers <c>SYSNO</c>.
        /// </summary>
        public bool Present { get; set; }

        /// <summary>
        /// Gets the LOGAD word of the segment table entry (the low six bits are the logical base page).
        /// </summary>
        public int Logad { get; set; }

        /// <summary>
        /// Gets the length of the segment in pages (SEGLE).
        /// </summary>
        public int Segle { get; set; }

        /// <summary>
        /// Gets the first page of the segment inside its segment file (MADR).
        /// </summary>
        public int Madr { get; set; }

        /// <summary>
        /// Gets the FLAG word of the segment table entry.
        /// </summary>
        public int Flag { get; set; }

        /// <summary>
        /// Gets the segment file number, bits 13 to 15 of FLAG.
        /// </summary>
        public int SegmentFile { get; set; }

        /// <summary>
        /// Gets the NDFS file (<c>USER/NAME:TYPE</c>) that holds the segment file, or an empty string when unknown.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Gets whether <see cref="FilePath"/> exists and reaches the copy's first page.
        /// </summary>
        public bool FileFound { get; set; }

        /// <summary>
        /// Gets the byte offset of <c>SYSNO</c> inside <see cref="FilePath"/>; -1 when the file does not hold it.
        /// </summary>
        public long SysnoOffset { get; set; } = -1;

        /// <summary>
        /// Gets the byte offset of <c>FCPUN</c> inside <see cref="FilePath"/>; -1 when the file does not hold it.
        /// </summary>
        public long FcpunOffset { get; set; } = -1;

        /// <summary>
        /// Gets the byte offset of <c>HWINF+2</c> inside <see cref="FilePath"/>; -1 when the file does not hold it.
        /// </summary>
        public long TypeOffset { get; set; } = -1;

        /// <summary>
        /// Gets the byte offset of <c>REVLE</c> inside <see cref="FilePath"/>; -1 when the file does not hold it.
        /// </summary>
        public long RevleOffset { get; set; } = -1;

        /// <summary>
        /// Gets the CPU number stored in <c>SYSNO</c>; -1 when not read.
        /// </summary>
        public int Sysno { get; set; } = -1;

        /// <summary>
        /// Gets the CPU number stored in <c>FCPUN</c>; -1 when not read.
        /// </summary>
        public int Fcpun { get; set; } = -1;

        /// <summary>
        /// Gets the CPU type stored in <c>HWINF+2</c>; -1 when not read.
        /// </summary>
        public int CpuType { get; set; } = -1;

        /// <summary>
        /// Gets the patch level stored in <c>REVLE</c>; -1 when not read.
        /// </summary>
        public int Revle { get; set; } = -1;
    }

    /// <summary>
    /// Everything <see cref="RetroIO.Storage.Ndfs.FileSystems.NdfsFileSystem.ReadSintranCpuIdentity"/> found
    /// on a pack. Filled as far as the pack allows, so a refusal still reports what was there.
    /// </summary>
    public sealed class SintranCpuIdentityInfo
    {
        /// <summary>
        /// Gets the SINTRAN version letter from the <c>INIBU</c> symbol (K, L or M), or <c>?</c>.
        /// </summary>
        public char Version { get; set; } = '?';

        /// <summary>
        /// Gets the symbol addresses read from the pack's symbol lists.
        /// </summary>
        public SintranSymbolAddresses Symbols { get; set; } = new SintranSymbolAddresses();

        /// <summary>
        /// Gets the path of the first symbol list.
        /// </summary>
        public string Symbol1Path { get; set; } = string.Empty;

        /// <summary>
        /// Gets the path of the second symbol list, which holds <c>33CPN</c> and <c>33CPU</c>.
        /// </summary>
        public string Symbol2Path { get; set; } = string.Empty;

        /// <summary>
        /// Gets where the segment numbers came from: <c>RTFIL:DATA</c>, or the loader table copies inside SEGFIL0.
        /// </summary>
        public string NamesPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets the byte offset of the <c>33CPN</c> digits inside <see cref="Symbol2Path"/>; -1 when absent.
        /// </summary>
        public long Cpn33ValueOffset { get; set; } = -1;

        /// <summary>
        /// Gets the byte offset of the <c>33CPU</c> digits inside <see cref="Symbol2Path"/>; -1 when absent.
        /// </summary>
        public long Cpu33ValueOffset { get; set; } = -1;

        /// <summary>
        /// Gets the number of octal digits <c>33CPN</c> is written with.
        /// </summary>
        public int Cpn33ValueWidth { get; set; }

        /// <summary>
        /// Gets the number of octal digits <c>33CPU</c> is written with.
        /// </summary>
        public int Cpu33ValueWidth { get; set; }

        /// <summary>
        /// Gets whether the second symbol list is stored with ND even parity, which a rewrite must keep.
        /// </summary>
        public bool Symbol2HasParity { get; set; }

        /// <summary>
        /// Gets the page of SEGFIL0 that holds the segment table; -1 when not found.
        /// </summary>
        public int SegmentTablePage { get; set; } = -1;

        /// <summary>
        /// Gets the two disk copies: index 0 is the image copy, index 1 the save copy.
        /// </summary>
        public IReadOnlyList<SintranDpitCopy> Copies { get; init; } = new[]
        {
            new SintranDpitCopy { Name = "S3IDPIT", Role = "image" },
            new SintranDpitCopy { Name = "S3SDPIT", Role = "save" },
        };

        /// <summary>
        /// Gets whether the directory's Extended Info block carries a valid checksum.
        /// </summary>
        public bool DirectoryExtValid { get; set; }

        /// <summary>
        /// Gets whether the directory was left entered (flag word bit 15).
        /// </summary>
        public bool DirectoryEntered { get; set; }

        /// <summary>
        /// Gets the system number stored in word 1755B of the Extended Info block.
        /// </summary>
        public int DirectorySystemNumber { get; set; }
    }
}
