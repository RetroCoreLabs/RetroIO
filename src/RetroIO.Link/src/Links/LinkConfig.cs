//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Link
{
    /// <summary>
    /// Protocol mode configuration for a link
    /// </summary>
    public class ProtocolModeConfig
    {
        /// <summary>
        /// Protocol detection mode
        /// </summary>
        public ProtocolDetectionMode Mode { get; set; } = ProtocolDetectionMode.X25Only;
    }

    /// <summary>
    /// Base configuration for all link types
    /// </summary>
    public abstract class LinkConfigBase
    {
        /// <summary>
        /// Unique name for this link
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Remote machine ID for routing
        /// </summary>
        public string RemoteMachineId { get; set; } = string.Empty;

        /// <summary>
        /// Protocol mode configuration
        /// </summary>
        public ProtocolModeConfig ProtocolMode { get; set; } = new ProtocolModeConfig();

        /// <summary>
        /// Whether to auto-start this link when runtime starts
        /// </summary>
        public bool AutoStart { get; set; } = true;

        /// <summary>
        /// Optional description
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Configuration for LAPB-capable links (Serial and TCP-LAPB)
    /// </summary>
    public abstract class LapbLinkConfigBase : LinkConfigBase
    {
        /// <summary>
        /// T1 timer (retransmission) in milliseconds
        /// </summary>
        public int T1TimeoutMs { get; set; } = 3000;

        /// <summary>
        /// T2 timer (acknowledgment delay) in milliseconds
        /// </summary>
        public int T2TimeoutMs { get; set; } = 1500;

        /// <summary>
        /// T3 timer (idle supervision) in milliseconds (0 = disabled)
        /// </summary>
        public int T3TimeoutMs { get; set; } = 0;

        /// <summary>
        /// Window size K (1-7 for modulo 8)
        /// </summary>
        public byte WindowSize { get; set; } = 7;

        /// <summary>
        /// Maximum retry count N2
        /// </summary>
        public byte MaxRetries { get; set; } = 10;

        /// <summary>
        /// Whether this side is primary (provides T3 supervision)
        /// </summary>
        public bool IsPrimary { get; set; } = true;
    }

    /// <summary>
    /// Configuration for serial port links
    /// </summary>
    public class SerialLinkConfig : LapbLinkConfigBase
    {
        /// <summary>
        /// Serial port name (e.g., "COM1", "/dev/ttyUSB0")
        /// </summary>
        public string PortName { get; set; } = "COM1";

        /// <summary>
        /// Baud rate
        /// </summary>
        public int BaudRate { get; set; } = 115200;

        /// <summary>
        /// Data bits (5, 6, 7, or 8)
        /// </summary>
        public int DataBits { get; set; } = 8;

        /// <summary>
        /// Stop bits (1, 1.5, or 2)
        /// </summary>
        public System.IO.Ports.StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;

        /// <summary>
        /// Parity
        /// </summary>
        public System.IO.Ports.Parity Parity { get; set; } = System.IO.Ports.Parity.None;

        /// <summary>
        /// Hardware flow control (RTS/CTS)
        /// </summary>
        public bool RtsCtsFlowControl { get; set; } = false;
    }

    /// <summary>
    /// Configuration for TCP-LAPB links (HDLC byte-stuffing over TCP)
    /// </summary>
    public class TcpLapbLinkConfig : LapbLinkConfigBase
    {
        /// <summary>
        /// TCP mode (Client or Server)
        /// </summary>
        public TcpMode Mode { get; set; } = TcpMode.Client;

        /// <summary>
        /// Remote IP address (for client mode)
        /// </summary>
        public string RemoteAddress { get; set; } = "127.0.0.1";

        /// <summary>
        /// Remote/Listen port
        /// </summary>
        public int Port { get; set; } = 2000;

        /// <summary>
        /// Connection timeout in milliseconds (for client mode)
        /// </summary>
        public int ConnectTimeoutMs { get; set; } = 30000;

        /// <summary>
        /// Reconnect delay in milliseconds (0 = no auto-reconnect)
        /// </summary>
        public int ReconnectDelayMs { get; set; } = 5000;
    }

    /// <summary>
    /// TCP connection mode
    /// </summary>
    public enum TcpMode
    {
        /// <summary>
        /// Connect to remote server
        /// </summary>
        Client,
        /// <summary>
        /// Listen for incoming connections
        /// </summary>
        Server
    }

    /// <summary>
    /// Configuration for XOT links (X.25 over TCP).
    /// Supports two modes:
    /// - RFC 1613 mode (default): XOT header + raw X.25 PLP packet
    /// - Cisco mode: XOT header + full LAPB frame (0x7E flags, byte-stuffing, FCS)
    /// </summary>
    public class XotLinkConfig : LapbLinkConfigBase
    {
        /// <summary>
        /// If true: Cisco-style XOT with full LAPB frames (0x7E, byte-stuffing, FCS).
        /// If false: RFC 1613-style XOT with raw X.25 packets (default).
        /// LAPB config options (T1, T2, WindowSize, etc.) are only used when this is true.
        /// </summary>
        public bool UseLapbFraming { get; set; } = false;

        /// <summary>
        /// TCP mode (Client or Server)
        /// </summary>
        public TcpMode Mode { get; set; } = TcpMode.Client;

        /// <summary>
        /// Remote IP address (for client mode)
        /// </summary>
        public string RemoteAddress { get; set; } = "127.0.0.1";

        /// <summary>
        /// XOT port (default 1998 per RFC 1613)
        /// </summary>
        public int Port { get; set; } = 1998;

        /// <summary>
        /// Connection timeout in milliseconds (for client mode)
        /// </summary>
        public int ConnectTimeoutMs { get; set; } = 30000;

        /// <summary>
        /// Reconnect delay in milliseconds (0 = no auto-reconnect)
        /// </summary>
        public int ReconnectDelayMs { get; set; } = 5000;

        /// <summary>
        /// Keep-alive interval in seconds (0 = disabled)
        /// </summary>
        public int KeepAliveIntervalSeconds { get; set; } = 60;

        /// <summary>
        /// Maximum number of concurrent connections (for server mode)
        /// </summary>
        public int MaxConnections { get; set; } = 100;
    }
}
