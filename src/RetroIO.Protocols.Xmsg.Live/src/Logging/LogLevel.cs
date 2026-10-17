//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Live.Logging
{
    /// <summary>
    /// Severity threshold for runner log output.
    /// </summary>
    /// <remarks>
    /// Ordered least-to-most verbose; a lower ordinal is more severe. <see cref="Off"/> disables logging
    /// entirely. Per-line filtering by level is not yet wired (every console line is mirrored to the file);
    /// only <see cref="Off"/> versus any other value currently changes behaviour. The remaining levels are
    /// carried so the configuration is forward-compatible once message tagging exists.
    /// </remarks>
    public enum LogLevel : byte
    {
        /// <summary>
        /// No logging (<c>0</c>) - the log file is not written at all.
        /// </summary>
        Off = 0,

        /// <summary>
        /// Errors only (<c>1</c>) - failures that stop or corrupt an operation.
        /// </summary>
        Error = 1,

        /// <summary>
        /// Warnings and errors (<c>2</c>) - recoverable anomalies plus errors.
        /// </summary>
        Warn = 2,

        /// <summary>
        /// Informational progress plus warnings and errors (<c>3</c>) - the default.
        /// </summary>
        Info = 3,

        /// <summary>
        /// Full debug detail including per-frame traces (<c>4</c>) - the most verbose.
        /// </summary>
        Debug = 4,
    }
}
