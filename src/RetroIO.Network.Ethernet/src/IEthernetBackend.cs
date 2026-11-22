//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// Delivers an Ethernet frame received from the host "wire" to the emulated
    /// controller chip.
    /// </summary>
    /// <remarks>
    /// Raised by an <see cref="IEthernetBackend"/> when a frame arrives from
    /// outside the emulator (loopback, another emulated machine, or a real host NIC
    /// via pcap). The machine subscribes and forwards the bytes into its NIC chip's
    /// receive path (e.g. <c>Am7990Lance.EnqueueReceivedPacket</c>). The buffer is
    /// owned by the backend and is only valid for the duration of the call — the
    /// handler must copy anything it needs to keep.
    /// </remarks>
    /// <param name="frame">
    /// The frame buffer (no FCS — backends deliver frames with the trailing CRC stripped).
    /// </param>
    /// <param name="offset">
    /// The index of the first frame byte in <paramref name="frame"/>.
    /// </param>
    /// <param name="length">
    /// The number of frame bytes.
    /// </param>
    public delegate void EthernetFrameHandler(byte[] frame, int offset, int length);

    /// <summary>
    /// The host-side Ethernet "wire" an emulated NIC controller transmits onto and
    /// receives from.
    /// </summary>
    /// <remarks>
    /// This is the seam between an emulated controller chip (AMD Am7990 LANCE, Intel
    /// 82586, 3Com E3C400, …) and whatever carries its frames off-box. The chip
    /// already exposes a transmit event and a receive-inject method; the machine wires
    /// those two ends to a backend:
    /// <para>
    ///  - chip transmit event → <see cref="Send"/> (guest → wire),
    /// </para>
    /// <para>
    ///  - <see cref="OnFrameReceived"/> → chip receive-inject (wire → guest).
    /// </para>
    /// Implementations: <c>NullEthernetBackend</c> (default — drops transmits, never
    /// receives; an optional loopback mode echoes transmits back for tests) and
    /// <c>PcapEthernetBackend</c> (bridges to a real host adapter via SharpPcap, in
    /// the opt-in <c>RetroIO.Network.Pcap</c> package). The contract
    /// is deliberately frame-oriented and transport-agnostic so the same NIC chips
    /// work over loopback, machine-to-machine, or a real wire without change.
    /// </remarks>
    public interface IEthernetBackend
    {
        /// <summary>
        /// Raised when a frame arrives from the wire and should be injected into the NIC.
        /// </summary>
        event EthernetFrameHandler? OnFrameReceived;

        /// <summary>
        /// Gets a value indicating whether the backend is started and carrying traffic.
        /// </summary>
        /// <value>
        /// <c>true</c> between <see cref="Start"/> and <see cref="Stop"/>; otherwise <c>false</c>.
        /// </value>
        bool IsRunning { get; }

        /// <summary>
        /// Transmits a frame from the emulated NIC onto the wire.
        /// </summary>
        /// <remarks>
        /// Called from the emulation thread when the guest driver completes a transmit.
        /// The bytes are consumed (copied / sent) before the call returns, so the caller
        /// may reuse its buffer afterwards. A no-op when the backend is not running.
        /// </remarks>
        /// <param name="frame">
        /// The frame buffer (no FCS).
        /// </param>
        /// <param name="offset">
        /// The index of the first frame byte.
        /// </param>
        /// <param name="length">
        /// The number of frame bytes.
        /// </param>
        void Send(byte[] frame, int offset, int length);

        /// <summary>
        /// Drains any frames buffered since the last call, raising <see cref="OnFrameReceived"/>
        /// for each — on the CALLING thread.
        /// </summary>
        /// <remarks>
        /// The threading boundary for receive. A backend that captures on its own thread
        /// (e.g. pcap) must NOT raise <see cref="OnFrameReceived"/> from that thread —
        /// it buffers, and the host calls <see cref="Poll"/> once per frame from the
        /// emulation thread so the frame is injected into the NIC chip on the same thread
        /// that runs it. Backends that deliver synchronously inside <see cref="Send"/>
        /// (loopback) implement this as a no-op.
        /// </remarks>
        void Poll();

        /// <summary>
        /// Starts carrying traffic (opens the host adapter / enables loopback).
        /// </summary>
        void Start();

        /// <summary>
        /// Stops carrying traffic and releases any host resources. Safe to call when already stopped.
        /// </summary>
        void Stop();
    }
}
