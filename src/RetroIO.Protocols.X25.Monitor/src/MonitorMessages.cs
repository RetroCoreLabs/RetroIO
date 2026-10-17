//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

namespace RetroIO.Protocols.X25.Monitor
{
    /// <summary>
    /// The direction a captured frame travelled relative to the observing port.
    /// </summary>
    public enum CaptureDirection
    {
        /// <summary>
        /// Received by the observing port (inbound).
        /// </summary>
        Rx = 0,

        /// <summary>
        /// Transmitted by the observing port (outbound).
        /// </summary>
        Tx = 1
    }

    /// <summary>
    /// One decoded field within a protocol layer: a name/value pair with an optional byte range into the
    /// captured frame so a viewer can highlight the corresponding hex.
    /// </summary>
    public sealed class MonitorField
    {
        /// <summary>
        /// The field name (for example <c>LCN</c> or <c>Called</c>).
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The decoded field value rendered as text.
        /// </summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Byte offset of this field within the captured frame, or -1 when it does not map to a byte range.
        /// </summary>
        public int Offset { get; set; } = -1;

        /// <summary>
        /// Byte length of this field within the captured frame, or 0 when not applicable.
        /// </summary>
        public int Length { get; set; }
    }

    /// <summary>
    /// One decoded protocol layer of a captured frame (for example Physical, HDLC, LAPB, X.25, SINTRAN),
    /// carrying a short summary and its decoded fields.
    /// </summary>
    public sealed class MonitorLayer
    {
        /// <summary>
        /// The layer name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// A short one-line summary of the layer for a packet-list view.
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// The decoded fields of this layer.
        /// </summary>
        public List<MonitorField> Fields { get; set; } = new List<MonitorField>();
    }

    /// <summary>
    /// A fully decoded capture record for one frame: envelope metadata, the raw hex, and the per-layer
    /// decode. The server decodes; the client only renders.
    /// </summary>
    public sealed class CaptureRecord
    {
        /// <summary>
        /// Monotonic sequence number assigned by the server, unique within a session.
        /// </summary>
        public long Seq { get; set; }

        /// <summary>
        /// Capture timestamp in milliseconds (server clock).
        /// </summary>
        public long TimestampMs { get; set; }

        /// <summary>
        /// The observing port/link identifier.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// The travel direction relative to the observing port.
        /// </summary>
        public CaptureDirection Direction { get; set; }

        /// <summary>
        /// The raw captured X.25 frame bytes. This is what travels on the wire under X25MON/1; the viewer
        /// decodes it into <see cref="PacketType"/>, <see cref="Called"/>, <see cref="Layers"/> and the hex.
        /// </summary>
        public byte[] Frame { get; set; } = System.Array.Empty<byte>();

        /// <summary>
        /// The total captured frame length in bytes.
        /// </summary>
        public int TotalLength { get; set; }

        /// <summary>
        /// Space-separated uppercase hex of the captured frame bytes.
        /// </summary>
        public string Hex { get; set; } = string.Empty;

        /// <summary>
        /// The called X.121 address when the frame carries one (call setup), otherwise empty. Duplicated at
        /// the envelope level so the server can filter without walking the layer fields.
        /// </summary>
        public string Called { get; set; } = string.Empty;

        /// <summary>
        /// The calling X.121 address when the frame carries one (call setup), otherwise empty.
        /// </summary>
        public string Calling { get; set; } = string.Empty;

        /// <summary>
        /// The X.25 packet type name when applicable (for example <c>CallRequest</c>, <c>Data</c>),
        /// otherwise empty. Used for server-side packet-type filtering.
        /// </summary>
        public string PacketType { get; set; } = string.Empty;

        /// <summary>
        /// The decoded protocol layers, outermost first.
        /// </summary>
        public List<MonitorLayer> Layers { get; set; } = new List<MonitorLayer>();
    }

    /// <summary>
    /// A server-side capture filter: a monitor client sends this to narrow the stream. Every dimension is
    /// optional; an empty dimension matches everything.
    /// </summary>
    public sealed class MonitorFilter
    {
        /// <summary>
        /// Ports to include; empty matches all ports.
        /// </summary>
        public List<int> Ports { get; set; } = new List<int>();

        /// <summary>
        /// Calling-address prefix to match; empty matches any calling address.
        /// </summary>
        public string Src { get; set; } = string.Empty;

        /// <summary>
        /// Called-address prefix to match; empty matches any called address.
        /// </summary>
        public string Dst { get; set; } = string.Empty;

        /// <summary>
        /// X.25 packet-type names to include; empty matches all packet types.
        /// </summary>
        public List<string> PacketTypes { get; set; } = new List<string>();

        /// <summary>
        /// Tests whether a capture record passes this filter.
        /// </summary>
        /// <param name="record">
        /// The record to test.
        /// </param>
        /// <returns>
        /// True if the record matches every set dimension of the filter.
        /// </returns>
        public bool Matches(CaptureRecord record)
        {
            if (Ports.Count > 0 && !ContainsInt(Ports, record.Port))
            {
                return false;
            }

            if (Src.Length > 0 && !record.Calling.StartsWith(Src, System.StringComparison.Ordinal))
            {
                return false;
            }

            if (Dst.Length > 0 && !record.Called.StartsWith(Dst, System.StringComparison.Ordinal))
            {
                return false;
            }

            if (PacketTypes.Count > 0 && !ContainsString(PacketTypes, record.PacketType))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Tests only the port dimension of the filter. Under X25MON/1 the switch ships raw frames and does
        /// not decode them, so it can only filter captures by port; the address and packet-type dimensions
        /// are applied by the viewer after it decodes (see docs/MONITOR_PROTOCOL_V1.md section 5).
        /// </summary>
        /// <param name="port">
        /// The capture's port.
        /// </param>
        /// <returns>
        /// True when the port passes (or no ports are set).
        /// </returns>
        public bool MatchesPort(int port)
        {
            return Ports.Count == 0 || ContainsInt(Ports, port);
        }

        /// <summary>
        /// Builds a filter from free-text UI fields: comma/space separated ports and packet types, and plain
        /// source/destination address prefixes. Blank fields become empty (match-all) dimensions.
        /// </summary>
        /// <param name="ports">
        /// Comma/space separated port numbers, or empty.
        /// </param>
        /// <param name="src">
        /// Calling-address prefix, or empty.
        /// </param>
        /// <param name="dst">
        /// Called-address prefix, or empty.
        /// </param>
        /// <param name="packetTypes">
        /// Comma/space separated packet-type names, or empty.
        /// </param>
        /// <returns>
        /// The parsed filter.
        /// </returns>
        public static MonitorFilter FromText(string ports, string src, string dst, string packetTypes)
        {
            MonitorFilter filter = new MonitorFilter
            {
                Src = (src ?? string.Empty).Trim(),
                Dst = (dst ?? string.Empty).Trim()
            };

            string[] portTokens = SplitTokens(ports);
            for (int i = 0; i < portTokens.Length; i++)
            {
                if (int.TryParse(portTokens[i], out int p))
                {
                    filter.Ports.Add(p);
                }
            }

            string[] typeTokens = SplitTokens(packetTypes);
            for (int i = 0; i < typeTokens.Length; i++)
            {
                filter.PacketTypes.Add(typeTokens[i]);
            }

            return filter;
        }

        private static string[] SplitTokens(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return System.Array.Empty<string>();
            }

            return text.Split(new[] { ',', ' ', ';' }, System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        }

        private static bool ContainsInt(List<int> list, int value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsString(List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The status and traffic counters for one switch/tap port, published periodically for the link-status
    /// view.
    /// </summary>
    public sealed class PortStatus
    {
        /// <summary>
        /// The port identifier.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The port role/mode as text (for example <c>DCE</c> or <c>DTE</c>).
        /// </summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>
        /// A summary of the routes that send traffic out this port (for example
        /// <c>prefix 2352, default</c>), or empty if none target it.
        /// </summary>
        public string Routes { get; set; } = string.Empty;

        /// <summary>
        /// True when the port's transport is up (for a listener, bound).
        /// </summary>
        public bool Active { get; set; }

        /// <summary>
        /// True when a peer is actually connected over the port (dialed in for DCE, or the outbound
        /// connection is up for DTE). For a trunk, false here means the trunk is not linked.
        /// </summary>
        public bool PeerConnected { get; set; }

        /// <summary>
        /// Calls received on the port.
        /// </summary>
        public uint CallsIn { get; set; }

        /// <summary>
        /// Calls placed from the port.
        /// </summary>
        public uint CallsOut { get; set; }

        /// <summary>
        /// Calls cleared on the port.
        /// </summary>
        public uint CallsCleared { get; set; }

        /// <summary>
        /// Packets received on the port.
        /// </summary>
        public uint PacketsIn { get; set; }

        /// <summary>
        /// Packets sent from the port.
        /// </summary>
        public uint PacketsOut { get; set; }

        /// <summary>
        /// Times the port has attempted to open/dial its transport.
        /// </summary>
        public int ConnectAttempts { get; set; }

        /// <summary>
        /// Server-clock milliseconds when a peer last connected, or 0.
        /// </summary>
        public long LastConnectedMs { get; set; }

        /// <summary>
        /// Server-clock milliseconds when a peer last disconnected, or 0.
        /// </summary>
        public long LastDisconnectedMs { get; set; }

        /// <summary>
        /// Total wire bytes sent on the port.
        /// </summary>
        public long BytesSent { get; set; }

        /// <summary>
        /// Total wire bytes received on the port.
        /// </summary>
        public long BytesReceived { get; set; }
    }

    /// <summary>
    /// A point-in-time snapshot of the server's ports and overall circuit count, pushed to viewers for the
    /// link-status view.
    /// </summary>
    public sealed class StatusSnapshot
    {
        /// <summary>
        /// Snapshot timestamp in milliseconds (server clock).
        /// </summary>
        public long TimestampMs { get; set; }

        /// <summary>
        /// The number of active virtual circuits across the switch.
        /// </summary>
        public int ActiveCircuits { get; set; }

        /// <summary>
        /// The per-port status entries.
        /// </summary>
        public List<PortStatus> Ports { get; set; } = new List<PortStatus>();
    }

    /// <summary>
    /// A single log record captured at its source and streamed to viewers. The timestamp and level are set
    /// where the log is emitted, not in transport or display.
    /// </summary>
    public sealed class LogRecord
    {
        /// <summary>
        /// Source timestamp in Unix milliseconds (UTC), stamped when the log was emitted.
        /// </summary>
        public long TimestampMs { get; set; }

        /// <summary>
        /// Severity level: <c>Trace</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>,
        /// or <c>Critical</c>.
        /// </summary>
        public string Level { get; set; } = string.Empty;

        /// <summary>
        /// The logger category (component that emitted the record).
        /// </summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// The rendered log message.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// A neighbouring node reachable over a trunk, as declared/known by a switch. Carries the neighbour's
    /// monitor endpoint so a viewer can discover and connect to it — all on the management plane, without
    /// touching the X.25 protocol.
    /// </summary>
    public sealed class NeighbourInfo
    {
        /// <summary>
        /// The neighbour's node name (as declared in the trunk config).
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The neighbour's monitor host.
        /// </summary>
        public string MonitorHost { get; set; } = string.Empty;

        /// <summary>
        /// The neighbour's monitor TCP port, or 0 if unknown.
        /// </summary>
        public int MonitorPort { get; set; }

        /// <summary>
        /// The local trunk port id the neighbour is reached through.
        /// </summary>
        public int ViaPortId { get; set; }

        /// <summary>
        /// True when the trunk to this neighbour is currently linked.
        /// </summary>
        public bool Up { get; set; }
    }

    /// <summary>
    /// A switch's self-identity plus its neighbours, published on the monitor plane so a viewer can build a
    /// network map by discovery. The X.25 protocol is unaffected; this is purely a management overlay.
    /// </summary>
    public sealed class TopologySnapshot
    {
        /// <summary>
        /// Stable node identifier (DNIC or name).
        /// </summary>
        public string NodeId { get; set; } = string.Empty;

        /// <summary>
        /// Friendly node name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The node's DNIC.
        /// </summary>
        public string Dnic { get; set; } = string.Empty;

        /// <summary>
        /// The node's own monitor TCP port.
        /// </summary>
        public int MonitorPort { get; set; }

        /// <summary>
        /// The node's management (telnet) TCP port.
        /// </summary>
        public int MgmtPort { get; set; }

        /// <summary>
        /// Node uptime in milliseconds.
        /// </summary>
        public long UptimeMs { get; set; }

        /// <summary>
        /// The node's neighbours (one per configured trunk with a declared peer).
        /// </summary>
        public List<NeighbourInfo> Neighbours { get; set; } = new List<NeighbourInfo>();
    }

    /// <summary>
    /// The envelope for one monitoring-protocol message. The <see cref="Type"/> discriminator selects which
    /// payload is present.
    /// </summary>
    public sealed class MonitorMessage
    {
        /// <summary>
        /// The message type discriminator: <c>capture</c> or <c>status</c> (server to client), <c>filter</c>
        /// (client to server), or <c>hello</c> (client greeting).
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// The capture record for a <c>capture</c> message, otherwise null.
        /// </summary>
        public CaptureRecord? Capture { get; set; }

        /// <summary>
        /// The filter for a <c>filter</c> message, otherwise null.
        /// </summary>
        public MonitorFilter? Filter { get; set; }

        /// <summary>
        /// The status snapshot for a <c>status</c> message, otherwise null.
        /// </summary>
        public StatusSnapshot? Status { get; set; }

        /// <summary>
        /// The log record for a <c>log</c> message, otherwise null.
        /// </summary>
        public LogRecord? Log { get; set; }

        /// <summary>
        /// The topology snapshot for a <c>topology</c> message, otherwise null.
        /// </summary>
        public TopologySnapshot? Topology { get; set; }
    }
}
