//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Slip;

namespace RetroIO.Network.DialUp
{
    /// <summary>
    /// Decides whether a caller's name and password may use the line.
    /// </summary>
    /// <param name="name">
    /// The name the caller typed.
    /// </param>
    /// <param name="password">
    /// The password the caller typed.
    /// </param>
    /// <returns>
    /// <c>true</c> to let the caller on.
    /// </returns>
    public delegate bool DialUpLoginCheck(string name, string password);

    /// <summary>
    /// How a <see cref="SlipDialUpSession"/> greets and checks its callers.
    /// </summary>
    public sealed class DialUpSettings
    {
        /// <summary>
        /// Gets a value indicating whether the caller gets a text login before SLIP starts.
        /// Default <c>true</c>. Without it SLIP starts as soon as the call is answered.
        /// </summary>
        public bool LoginPrompt { get; init; } = true;

        /// <summary>
        /// Gets the line shown above the first login prompt. Default <c>RetroIO ISP</c>.
        /// </summary>
        public string Greeting { get; init; } = "RetroIO ISP";

        /// <summary>
        /// Gets the check for a name and password, or <c>null</c> to take any.
        /// </summary>
        /// <remarks>
        /// An emulator's built-in ISP takes any name, as the free trial accounts of the time did.
        /// A server checks its own users here. It runs on the thread that feeds the session.
        /// </remarks>
        public DialUpLoginCheck? LoginCheck { get; init; }

        /// <summary>
        /// Gets the number of wrong logins after which the session hangs up. Default 3.
        /// </summary>
        public int MaxLoginTries { get; init; } = 3;

        /// <summary>
        /// Gets the largest IP packet SLIP carries, the line's MTU. Default 1006, the size RFC 1055
        /// names for the Berkeley SLIP drivers.
        /// </summary>
        public int MaxPacket { get; init; } = SlipLink.DefaultMaxPacket;
    }
}
