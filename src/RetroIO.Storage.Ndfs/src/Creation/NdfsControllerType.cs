//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Storage.Ndfs.Creation
{
    /// <summary>
    /// Controller type determines overhead calculation for spare sectors.
    /// </summary>
    public enum NdfsControllerType
    {
        /// <summary>
        /// SMD controller (~3.94% overhead for spare sectors and bad block remapping).
        /// </summary>
        Smd,

        /// <summary>
        /// Winchester controller (~0% overhead, minimal or no spare sectors).
        /// </summary>
        Winchester,

        /// <summary>
        /// SCSI controller (~5.93% overhead for spare sectors).
        /// </summary>
        Scsi,

        /// <summary>
        /// Floppy controller (no overhead, exact size matching NDFS pages).
        /// </summary>
        Floppy
    }
}
