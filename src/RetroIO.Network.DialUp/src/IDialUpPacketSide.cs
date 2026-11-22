//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Network.DialUp
{
    /// <summary>
    /// Takes one IP packet.
    /// </summary>
    /// <param name="packet">
    /// The packet, valid only during the call.
    /// </param>
    public delegate void DialUpPacketHandler(ReadOnlySpan<byte> packet);

    /// <summary>
    /// Where a caller's IP packets go once the login is over: a NAT of its own
    /// (<see cref="NatDialUpSide"/>) or a link on a shared router (<see cref="RouterDialUpSide"/>).
    /// </summary>
    /// <remarks>
    /// A <see cref="SlipDialUpSession"/> calls <see cref="Start"/> once, when SLIP starts, so a
    /// caller who fails the login never takes an address. Every call is on the thread that feeds
    /// the session.
    /// </remarks>
    public interface IDialUpPacketSide
    {
        /// <summary>
        /// Gets the caller's address, once <see cref="Start"/> has run.
        /// </summary>
        uint CallerAddress { get; }

        /// <summary>
        /// Gets the gateway address told to the caller.
        /// </summary>
        uint Gateway { get; }

        /// <summary>
        /// Gets the name server address told to the caller, or 0 for none.
        /// </summary>
        uint NameServer { get; }

        /// <summary>
        /// Gets why <see cref="Start"/> refused the caller, or empty.
        /// </summary>
        string RefusedReason { get; }

        /// <summary>
        /// Starts carrying packets.
        /// </summary>
        /// <param name="userName">
        /// The name the caller logged in with, or empty when there was no login.
        /// </param>
        /// <param name="toCaller">
        /// Where packets for the caller go.
        /// </param>
        /// <returns>
        /// <c>true</c> when started; <c>false</c> when there is no room (no free address), and the
        /// session then hangs up.
        /// </returns>
        bool Start(string userName, DialUpPacketHandler toCaller);

        /// <summary>
        /// Takes a packet the caller sent.
        /// </summary>
        /// <param name="packet">
        /// The packet, valid only during the call.
        /// </param>
        void SendFromCaller(ReadOnlySpan<byte> packet);

        /// <summary>
        /// Moves the packet side along by one poll.
        /// </summary>
        void Poll();

        /// <summary>
        /// Stops carrying packets and gives back what <see cref="Start"/> took.
        /// </summary>
        void Stop();
    }
}
