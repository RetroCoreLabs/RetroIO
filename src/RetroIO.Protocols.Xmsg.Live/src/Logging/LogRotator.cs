//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Globalization;
using System.IO;

namespace RetroIO.Protocols.Xmsg.Live.Logging
{
    /// <summary>
    /// Linux-syslog-style numbered log rotation: the live <c>file</c> is shifted to <c>file.1</c>,
    /// <c>file.1</c> to <c>file.2</c>, and so on, with the oldest retained version deleted.
    /// </summary>
    /// <remarks>
    /// This is a pure file-system operation with no writer state, which keeps it trivially unit-testable
    /// against a temp directory. The size-triggered and startup-triggered rotations in
    /// <see cref="RotatingFileWriter"/> both call this.
    /// </remarks>
    public static class LogRotator
    {
        /// <summary>
        /// Rotates the numbered log files for <paramref name="path"/>, keeping at most
        /// <paramref name="keep"/> archived versions.
        /// </summary>
        /// <param name="path">
        /// The live log file path (the un-suffixed name).
        /// </param>
        /// <param name="keep">
        /// The number of archived versions to retain (<c>file.1</c>..<c>file.keep</c>); <c>0</c> keeps none.
        /// </param>
        /// <remarks>
        /// The shift runs highest-first so no rename clobbers a version that has not yet moved:
        ///  - <c>file.keep</c> is deleted (the oldest).
        ///  - <c>file.k</c> becomes <c>file.(k+1)</c> for k from <c>keep-1</c> down to 1.
        ///  - the live <c>file</c> becomes <c>file.1</c>.
        /// After the call the live path does not exist, so the caller opens a fresh empty file.
        /// </remarks>
        public static void Rotate(string path, int keep)
        {
            if (keep <= 0)
            {
                // No archives kept: just remove the live file so a fresh empty one is opened.
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            // Delete the oldest archive so the shift below does not overflow past 'keep'.
            string oldest = path + "." + keep.ToString(CultureInfo.InvariantCulture);
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            // Shift .k -> .(k+1) from the top down so each destination is free before the move.
            for (int i = keep - 1; i >= 1; i--)
            {
                string from = path + "." + i.ToString(CultureInfo.InvariantCulture);
                string to = path + "." + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (File.Exists(from))
                {
                    File.Move(from, to);
                }
            }

            // The live file becomes .1.
            if (File.Exists(path))
            {
                File.Move(path, path + ".1");
            }
        }
    }
}
