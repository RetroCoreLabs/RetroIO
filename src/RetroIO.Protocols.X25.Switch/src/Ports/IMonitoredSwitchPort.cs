//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// A switch port over a real link that a monitor can watch: it shows every packet that crosses it and
    /// keeps the counts of its connection attempts, failures and losses.
    /// </summary>
    /// <remarks>
    /// <see cref="XotSwitchPort"/>, the HDLC ports (<see cref="HdlcTcpSwitchPort"/> and <see cref="HdlcSerialSwitchPort"/>)
    /// all have it, so a monitor host, the status stream and the Desktop's trunk panel read one shape and do
    /// not care which kind of link is under the port. The counts follow the C emulator's, so the two report
    /// the same numbers for the same traffic.
    /// </remarks>
    public interface IMonitoredSwitchPort : ISwitchPort
    {
        /// <summary>
        /// Raised for every X.25 packet the port receives or writes, as it crossed the link, with no XOT or
        /// HDLC framing. See <see cref="PortPacketCaptured"/> for what a handler may do.
        /// </summary>
        event PortPacketCaptured? PacketCaptured;

        /// <summary>
        /// How many times the port has tried to make a connection: each dial for a port that dials and each
        /// accepted connection for a port that listens. A serial port counts each time it opens the device.
        /// </summary>
        int ConnectAttempts { get; }

        /// <summary>
        /// The time, as <see cref="System.Environment.TickCount64"/>, at which the link last came up; 0 if it has not.
        /// </summary>
        long LastConnectedMs { get; }

        /// <summary>
        /// The time, as <see cref="System.Environment.TickCount64"/>, at which the link last went away; 0 if it has not.
        /// </summary>
        long LastDisconnectedMs { get; }

        /// <summary>
        /// How many dials (or device opens) failed since the port started.
        /// </summary>
        int ConnectFailures { get; }

        /// <summary>
        /// How many times the link went away since the port started.
        /// </summary>
        int Disconnects { get; }

        /// <summary>
        /// The reason the last dial or device open failed, in words; empty if none has. It is not cleared when a
        /// later try works: read it together with <see cref="ISwitchPort.PeerConnected"/>.
        /// </summary>
        string LastError { get; }

        /// <summary>
        /// The time, as <see cref="System.Environment.TickCount64"/>, at which a dial last failed; 0 if none has.
        /// </summary>
        long LastFailureMs { get; }

        /// <summary>
        /// How long the port waits after a failed dial before it tries again, in milliseconds.
        /// </summary>
        int RedialDelay { get; }
    }
}
