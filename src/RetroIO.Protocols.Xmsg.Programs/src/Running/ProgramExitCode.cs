//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Programs.Running
{
    /// <summary>
    /// The exit codes every XMSG program returns, so a script can tell why one stopped.
    /// </summary>
    /// <remarks>
    /// The numbers are the ones the two chat samples already used (2026-11-22), so nothing that
    /// reads them changes. 1 is left out on purpose: .NET returns it for an exception nobody caught.
    /// </remarks>
    public static class ProgramExitCode
    {
        /// <summary>The program stopped because it was asked to (Ctrl+C, a STOP command, the end of its work).</summary>
        public const int Done = 0;

        /// <summary>The command line or the settings file could not be used.</summary>
        public const int BadSettings = 2;

        /// <summary>RetroServer could not be reached, or refused the connection.</summary>
        public const int CannotConnect = 3;

        /// <summary>RetroServer, or the server the program talks to, refused what it asked for (a name, a join).</summary>
        public const int Refused = 4;

        /// <summary>RetroServer closed the connection while the program was running.</summary>
        public const int ConnectionClosed = 5;

        /// <summary>The far side did not answer in time.</summary>
        public const int NoAnswer = 6;
    }
}
