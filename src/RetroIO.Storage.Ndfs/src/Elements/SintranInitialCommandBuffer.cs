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
    /// Result of locating and decoding the SINTRAN III *initial-command buffer* on an NDFS
    /// system pack. These are the commands SINTRAN runs first at every restart, exactly as
    /// <c>@LIST-INITIAL-COMMANDS</c> prints them (e.g. ENTER-DIR,,DI-75-1,0 / SET-AVAIL / ...).
    ///
    /// The buffer has no file form: it lives at kernel symbol INIBU inside the SINTRAN command
    /// segment, whose disk image is stored in the (SYSTEM)SEGFIL0:DATA object file. It is located
    /// WITHOUT byte-searching — purely from the INIBU symbol value plus the segment table read off
    /// SEGFIL0 itself. See <see cref="RetroIO.Storage.Ndfs.FileSystems.SintranInitialCommands"/> for the
    /// full derivation. Ported (behaviour-for-behaviour) from the nd100x "glass" UI feature
    /// template-glass/js/sintran-initial-commands.js.
    /// </summary>
    public sealed class SintranInitialCommandBuffer
    {
        /// <summary>The decoded initial commands, in execution order.</summary>
        public IReadOnlyList<string> Commands { get; init; } = new List<string>();

        /// <summary>SINTRAN version letter whose INIBU symbol matched ('K', 'L' or 'M').</summary>
        public char VersionLetter { get; init; }

        /// <summary>Segment-table entry number (octal in the UI) that holds the live command segment.</summary>
        public int SegmentNumber { get; init; }

        /// <summary>MADR (memory/disk address, in NDFS pages) of that segment inside SEGFIL0.</summary>
        public uint Madr { get; init; }

        /// <summary>Byte offset of the buffer start (INIBU) WITHIN the SEGFIL0 file data.</summary>
        public long ByteOffset { get; init; }

        /// <summary>Page (in SEGFIL0) where the self-describing segment table was found.</summary>
        public int SegmentTablePage { get; init; }

        /// <summary>
        /// Value of the length cell (byte count of the command text, EXCLUDING the terminator).
        /// SINTRAN reads exactly this many bytes; any writer MUST keep it in sync.
        /// </summary>
        public int TextLength { get; init; }

        /// <summary>Byte offset of the length cell WITHIN the SEGFIL0 file data (= <see cref="ByteOffset"/> + 260).</summary>
        public long LengthCellOffset { get; init; }

        /// <summary>Path (USER/NAME:TYPE) of the SEGFIL0 object file the buffer was read from.</summary>
        public string SegfilPath { get; init; } = string.Empty;
    }
}
