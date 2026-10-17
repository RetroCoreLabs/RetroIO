//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// A file's size and last-write time, the two things that say whether it is still being
    /// written.
    /// </summary>
    /// <remarks>
    /// A struct because a pass builds one per file per cycle and they never outlive the cycle.
    /// Deliberately NOT the same thing as <see cref="LocalFileState"/>: that one carries the
    /// content hash and answers "is this different from what we carried", while this one carries
    /// no content at all and answers "has it stopped moving yet".
    /// </remarks>
    public readonly struct FileSizeAndTime
    {
        /// <summary>
        /// Creates a stamp.
        /// </summary>
        /// <param name="size">
        /// The file's length in bytes.
        /// </param>
        /// <param name="lastWriteTicks">
        /// The last-write time in ticks.
        /// </param>
        public FileSizeAndTime(long size, long lastWriteTicks)
        {
            Size = size;
            LastWriteTicks = lastWriteTicks;
        }

        /// <summary>
        /// Gets the file's length in bytes.
        /// </summary>
        public long Size { get; }

        /// <summary>
        /// Gets the last-write time in ticks.
        /// </summary>
        public long LastWriteTicks { get; }
    }
}
