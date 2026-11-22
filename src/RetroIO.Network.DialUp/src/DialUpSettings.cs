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
    /// Starts checking a caller's name and password; the check gives its answer through
    /// <paramref name="answer"/>, at once or later, from any thread.
    /// </summary>
    /// <param name="name">
    /// The name the caller typed.
    /// </param>
    /// <param name="password">
    /// The password the caller typed.
    /// </param>
    /// <param name="answer">
    /// Where the check says yes or no, once.
    /// </param>
    public delegate void DialUpLoginStart(string name, string password, DialUpLoginAnswer answer);

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
        /// Gets the check for a name and password that may take a while, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// For a slow check, such as a password hash or a question to another server: the session
        /// waits and its thread goes on with other calls. The check answers through the
        /// <see cref="DialUpLoginAnswer"/> from any thread; the session acts on the answer in its
        /// next <see cref="SlipDialUpSession.Poll"/>. Set this or <see cref="LoginCheck"/>, not
        /// both.
        /// </remarks>
        public DialUpLoginStart? LoginStart { get; init; }

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
