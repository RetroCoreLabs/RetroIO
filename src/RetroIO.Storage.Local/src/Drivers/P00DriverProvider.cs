//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Local.Drivers
{
    /// <summary>
    /// Provider for PC64 ".P00" program containers (a PRG wrapped with the "C64File" signature and
    /// the original 16-byte CBM filename). Detected by the signature, strengthened by the extension.
    /// </summary>
    public sealed class P00DriverProvider : FileSystemDriverProviderBase
    {
        // The machines this format's disks come from, shown to users.
        private static readonly string[] MachineList = new[] { "Commodore 64" };

        // The one extension list for this format: detection below uses it, and programs read it
        // through FileExtensions (for example to link these files to themselves).
        private static readonly string[] KnownExtensions = { ".p00" };

        /// <summary>
        /// Gets the file system type identifier of this provider: <c>P00</c>.
        /// </summary>
        public override string FileSystemType => "P00";

        /// <summary>
        /// Gets the extensions this format is stored under: .p00.
        /// </summary>
        /// <remarks>
        /// Detection uses the same list as its extension signal.
        /// </remarks>
        public override IReadOnlyList<string> FileExtensions => KnownExtensions;
        /// <summary>
        /// Gets the human-readable description of the PC64 P00 program container format.
        /// </summary>
        public override string Description => "Commodore PC64 program container (.P00)";

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyList<string> Machines => MachineList;
        /// <summary>
        /// Gets 26, the number of header bytes the detection needs.
        /// </summary>
        public override int RequiredHeaderBytes => 26;

        /// <summary>
        /// Returns 0 unless the header holds the C64File signature. A match scores 0.7, and the extension .p00 adds 0.25.
        /// </summary>
        /// <param name="hints">
        /// The file name and header bytes.
        /// </param>
        /// <returns>
        /// The confidence from 0 to 1.
        /// </returns>
        public override float DetectConfidence(DetectionHints hints)
        {
            float confidence = 0.0f;

            if (hints.HeaderBytes != null && ProgramFileDriver.IsP00(hints.HeaderBytes))
            {
                confidence += 0.7f; // the "C64File" signature is definitive
            }
            else
            {
                return 0.0f; // without the signature it is not a P00
            }

            if (HasKnownFileExtension(hints))
            {
                confidence += 0.25f;
            }

            return Math.Min(confidence, 1.0f);
        }

        /// <summary>
        /// Creates a new, unmounted program file driver, the same driver the PRG provider creates.
        /// </summary>
        /// <returns>
        /// A new <see cref="ProgramFileDriver"/>.
        /// </returns>
        public override IFileSystemDriver CreateDriver() => new ProgramFileDriver();
    }
}
