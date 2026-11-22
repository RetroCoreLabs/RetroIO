//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// Receives one raw Ethernet frame that arrived on a backend.
    /// </summary>
    /// <param name="data">
    /// The buffer holding the frame, starting at the destination MAC. The backend may reuse this
    /// buffer after the handler returns, so a handler that retains the frame MUST copy it.
    /// </param>
    /// <param name="length">
    /// The number of valid bytes in <paramref name="data"/>.
    /// </param>
    public delegate void EthernetPacketReceived(byte[] data, int length);

    /// <summary>
    /// Carries raw Ethernet frames to and from whatever the emulated segment is made of - a UDP
    /// multicast group, a TCP link or hub, or a real host adapter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The member names deliberately mirror RetroCore's
    /// <c>Emulated.HW.Common.Network.IEthernetBackend</c>, following the same convention as
    /// <c>RetroIO.Protocols.Xmsg.Node.Seam.ILink</c>: identical shapes on both sides of the boundary so moving code
    /// between this library and the emulator is a using-directive change. This is not duplicated
    /// logic - the transports here are an independent client implementation that has to interoperate
    /// with RetroCore's on the wire, and the shared shape is what keeps the two honest.
    /// </para>
    /// <para>
    /// Nothing above this interface knows how frames travel. Nothing below it knows they are
    /// COSMOS.
    /// </para>
    /// </remarks>
    public interface IEthernetBackend : IDisposable
    {
        /// <summary>
        /// Occurs when a complete Ethernet frame arrives.
        /// </summary>
        event EthernetPacketReceived? OnPacketReceived;

        /// <summary>
        /// Gets a value indicating whether the backend is running and able to carry frames.
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Gets a value indicating whether a frame handed to <see cref="SendPacket"/> now actually
        /// reaches the segment.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Not the same as <see cref="IsActive"/>. A TCP backend is active from <see cref="Start"/>,
        /// but it connects on its own thread, and until that connection is made every frame sent is
        /// dropped without a trace. A link that sends its connection request in that gap waits for a
        /// confirm that can never come (live test from D102, 2026-11-22: our reopen was lost this way
        /// and the node never sent a data frame again).
        /// </para>
        /// <para>
        /// The default is <see cref="IsActive"/>, which is right for a backend that carries frames the
        /// moment it is started (UDP multicast, the in-process loopback).
        /// </para>
        /// </remarks>
        bool IsConnected
        {
            get { return IsActive; }
        }

        /// <summary>
        /// Gets a short human-readable description of the transport, for logs.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Starts the backend. Idempotent.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops the backend and releases its transport resources. Idempotent.
        /// </summary>
        void Stop();

        /// <summary>
        /// Sends one raw Ethernet frame.
        /// </summary>
        /// <param name="data">
        /// The buffer holding the frame.
        /// </param>
        /// <param name="offset">
        /// The offset of the first frame byte within <paramref name="data"/>.
        /// </param>
        /// <param name="length">
        /// The number of bytes to send.
        /// </param>
        /// <remarks>
        /// Never throws for a transport failure. A datagram transport may drop the frame and a
        /// stream transport may be mid-reconnect; COSMOS retransmits, so tearing the link down on
        /// a single failed send would do more harm than dropping it.
        /// </remarks>
        void SendPacket(byte[] data, int offset, int length);
    }
}
