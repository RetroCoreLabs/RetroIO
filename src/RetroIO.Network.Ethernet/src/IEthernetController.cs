//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// The standard contract an emulated Ethernet controller (or a thin adapter over
    /// one) implements so a machine's <c>EthernetManager</c> can route its traffic to
    /// and from a host <see cref="IEthernetBackend"/> uniformly — regardless of the
    /// underlying chip (AMD Am7990 LANCE, Intel 82586, 3Com E3C400, …).
    /// </summary>
    /// <remarks>
    /// Each chip exposes its own transmit event + receive-inject method with slightly
    /// different signatures; implementing this interface (directly on the chip, or via
    /// a small adapter such as <c>Am7990EthernetController</c>) normalises them to one
    /// shape. A machine registers its controller(s) with
    /// <c>MachineBase.Ethernet.Register(controller)</c> and the manager does the wiring:
    /// <para>
    ///  - <see cref="FrameTransmitted"/> → the backend's <see cref="IEthernetBackend.Send"/>,
    /// </para>
    /// <para>
    ///  - the backend's <see cref="IEthernetBackend.OnFrameReceived"/> → <see cref="DeliverFrame"/>.
    /// </para>
    /// This keeps frame routing in one place (the manager) and lets the host swap the
    /// backend (drop / loopback / pcap) centrally without any machine or chip change.
    /// </remarks>
    public interface IEthernetController
    {
        /// <summary>
        /// Gets a short stable name for this controller (e.g. the device alias <c>"le"</c>
        /// / <c>"ie"</c>), used in diagnostics and to label the port.
        /// </summary>
        /// <value>
        /// The controller name.
        /// </value>
        string Name { get; }

        /// <summary>
        /// Raised when the guest driver transmits a frame — the manager forwards it to the wire.
        /// </summary>
        event EthernetFrameHandler? FrameTransmitted;

        /// <summary>
        /// Injects a frame received from the wire into the controller's receive path.
        /// </summary>
        /// <remarks>
        /// Called by the manager on the emulation thread when the backend reports a frame.
        /// The buffer is only valid for the duration of the call — copy anything kept.
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
        void DeliverFrame(byte[] frame, int offset, int length);
    }
}
