//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Host
{
    /// <summary>
    /// Where a <see cref="ScriptedHost"/> sends what its script does. Called synchronously from inside the host's
    /// methods, on the caller's thread.
    /// </summary>
    public interface IHostOutput
    {
        /// <summary>Send these bytes to the caller on <paramref name="session"/>. The span is valid only during the call.</summary>
        /// <param name="session">The session number given to <see cref="ScriptedHost.Connect"/>.</param>
        /// <param name="data">The bytes, line ends already turned into the host's line end.</param>
        void Send(int session, ReadOnlySpan<byte> data);

        /// <summary>Clear the call on <paramref name="session"/>. The host has already forgotten the session.</summary>
        /// <param name="session">The session number.</param>
        void Clear(int session);

        /// <summary>One line for the host's log, without its line end.</summary>
        /// <param name="text">The line.</param>
        void Log(string text);
    }
}
