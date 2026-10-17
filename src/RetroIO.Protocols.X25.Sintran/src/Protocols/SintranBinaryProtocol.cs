//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.X25.Protocols;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// SINTRAN Binary Protocol Parser and Builder
    /// Handles the actual binary protocol format used by SINTRAN systems
    /// Supports both LAPB-level routing protocol (0x21 0x13) and X.25 payload format
    /// </summary>
    public static class SintranBinaryProtocol
    {
        /// <summary>
        /// SINTRAN protocol header identifier
        /// </summary>
        public static readonly byte[] SINTRAN_HEADER = { 0x09, 0x66, 0x21 };

        /// <summary>
        /// SINTRAN routing protocol fixed markers (LAPB level)
        /// All SINTRAN routing frames have 0x21 0x13 after LAPB address/control
        /// </summary>
        public const byte ROUTING_MARKER_1 = 0x21;
        /// <summary>
        /// Second routing marker byte, 0x13. The first is 0x21.
        /// </summary>
        public const byte ROUTING_MARKER_2 = 0x13;

        /// <summary>
        /// SINTRAN protocol IDs (byte after routing/node info)
        /// </summary>
        public const byte PROTOCOL_ID_PAD = 0xDA;  // 218 - PAD service
        /// <summary>
        /// Protocol ID for the TAD service, 0xDD (221).
        /// </summary>
        public const byte PROTOCOL_ID_TAD = 0xDD;  // 221 - TAD service
        /// <summary>
        /// Protocol ID for routing commands, 0xDE (222).
        /// </summary>
        public const byte PROTOCOL_ID_ROUTING = 0xDE; // 222 - Routing commands

        /// <summary>
        /// Routing command codes (used with PROTOCOL_ID_ROUTING)
        /// </summary>
        public const byte ROUTING_CMD_REQUEST = 0x08;  // Request routing info
        /// <summary>
        /// Routing command code for a response with routing information, 0x0E.
        /// </summary>
        public const byte ROUTING_CMD_RESPONSE = 0x0E; // Response with routing info

        /// <summary>
        /// SINTRAN protocol command types
        /// </summary>
        public enum SintranCommand : byte
        {
            /// <summary>
            /// Routing list request/response
            /// </summary>
            RoutingList = 0x13,
            
            /// <summary>
            /// Node status request/response
            /// </summary>
            NodeStatus = 0x14,
            
            /// <summary>
            /// XMSG message
            /// </summary>
            XmsgMessage = 0x20,
            
            /// <summary>
            /// File transfer request
            /// </summary>
            FileTransfer = 0x30
        }

        /// <summary>
        /// SINTRAN response status codes
        /// </summary>
        public enum SintranStatus : byte
        {
            /// <summary>
            /// Success, data follows
            /// </summary>
            Success = 0xFF,
            
            /// <summary>
            /// Request accepted
            /// </summary>
            Accepted = 0x00,
            
            /// <summary>
            /// Error occurred
            /// </summary>
            Error = 0x01,
            
            /// <summary>
            /// Node not found
            /// </summary>
            NodeNotFound = 0x02
        }

        /// <summary>
        /// Parse SINTRAN binary packet from X.25 data payload
        /// Based on real captured packets: length(1) + source(2) + dest(2) + seq(2) + cmd/data(N)
        /// </summary>
        public static SintranPacket ParsePacket(byte[] data)
        {
            if (data == null || data.Length < 7)
                throw new ArgumentException("SINTRAN packet too short", nameof(data));

            var packet = new SintranPacket
            {
                Length = data[0],                                    // Byte 0: Length
                SourceNode = (ushort)((data[1] << 8) | data[2]),     // Bytes 1-2: Source node  
                DestinationNode = (ushort)((data[3] << 8) | data[4]), // Bytes 3-4: Dest node
                Sequence = (ushort)((data[5] << 8) | data[6])        // Bytes 5-6: Sequence
            };

            // Extract payload if present (everything after the 7-byte header)
            int headerSize = 7;
            if (data.Length > headerSize)
            {
                packet.Payload = new byte[data.Length - headerSize];
                Array.Copy(data, headerSize, packet.Payload, 0, packet.Payload.Length);
                
                // Determine command type from payload
                if (packet.Payload.Length >= 2)
                {
                    // Request format: 0x01 0x00 + data
                    // Response format: 0xFF + data  
                    if (packet.Payload[0] == 0x01 && packet.Payload[1] == 0x00)
                        packet.Command = SintranCommand.RoutingList; // Request
                    else if (packet.Payload[0] == 0xFF)
                        packet.Command = SintranCommand.RoutingList; // Response
                }
            }
            else
            {
                packet.Payload = Array.Empty<byte>();
            }

            return packet;
        }

        /// <summary>
        /// Build SINTRAN binary packet for X.25 data payload
        /// Format: length(1) + source(2) + dest(2) + seq(2) + payload(N)
        /// </summary>
        public static byte[] BuildPacket(SintranPacket packet)
        {
            int totalLength = 7 + packet.Payload.Length;
            var data = new byte[totalLength];

            // Length (total packet length)
            data[0] = (byte)totalLength;
            
            // Source node (big-endian)
            data[1] = (byte)(packet.SourceNode >> 8);
            data[2] = (byte)(packet.SourceNode & 0xFF);
            
            // Destination node (big-endian)
            data[3] = (byte)(packet.DestinationNode >> 8);
            data[4] = (byte)(packet.DestinationNode & 0xFF);
            
            // Sequence (big-endian)
            data[5] = (byte)(packet.Sequence >> 8);
            data[6] = (byte)(packet.Sequence & 0xFF);
            
            // Payload
            if (packet.Payload.Length > 0)
            {
                Array.Copy(packet.Payload, 0, data, 7, packet.Payload.Length);
            }

            return data;
        }

        /// <summary>
        /// Create routing list request packet
        /// </summary>
        public static byte[] CreateRoutingListRequest(ushort sourceNode, ushort destinationNode, ushort sequence)
        {
            var packet = new SintranPacket
            {
                Command = SintranCommand.RoutingList,
                SourceNode = sourceNode,
                DestinationNode = destinationNode,
                Sequence = sequence,
                Payload = new byte[] { 0x01, 0x00 } // Request parameters
            };

            return BuildPacket(packet);
        }

        /// <summary>
        /// Create routing list response packet matching real format
        /// Response format: 0xFF + routing_data (matching your captured TX packet)
        /// </summary>
        public static byte[] CreateRoutingListResponse(ushort sourceNode, ushort destinationNode, ushort sequence, byte[] routingData)
        {
            var payload = new byte[1 + routingData.Length];
            payload[0] = 0xFF; // Success status (matches captured packet)
            if (routingData.Length > 0)
                Array.Copy(routingData, 0, payload, 1, routingData.Length);

            var packet = new SintranPacket
            {
                Command = SintranCommand.RoutingList,
                SourceNode = sourceNode,
                DestinationNode = destinationNode,
                Sequence = sequence,
                Payload = payload
            };

            return BuildPacket(packet);
        }

        /// <summary>
        /// Create node status request
        /// </summary>
        public static byte[] CreateNodeStatusRequest(ushort sourceNode, ushort destinationNode, ushort sequence)
        {
            var packet = new SintranPacket
            {
                Command = SintranCommand.NodeStatus,
                SourceNode = sourceNode,
                DestinationNode = destinationNode,
                Sequence = sequence,
                Payload = new byte[] { 0x01 } // Status request
            };

            return BuildPacket(packet);
        }

        /// <summary>
        /// Create XMSG message packet
        /// </summary>
        public static byte[] CreateXmsgPacket(ushort sourceNode, ushort destinationNode, ushort sequence, string message)
        {
            var messageBytes = System.Text.Encoding.ASCII.GetBytes(message);
            var payload = new byte[1 + messageBytes.Length];
            payload[0] = 0x01; // XMSG message type
            Array.Copy(messageBytes, 0, payload, 1, messageBytes.Length);

            var packet = new SintranPacket
            {
                Command = SintranCommand.XmsgMessage,
                SourceNode = sourceNode,
                DestinationNode = destinationNode,
                Sequence = sequence,
                Payload = payload
            };

            return BuildPacket(packet);
        }

        /// <summary>
        /// Generate routing response data matching your captured TX packet
        /// Your captured response payload was: 0xFF 0xD8 0xDE 0x40
        /// </summary>
        public static byte[] CreateSampleRoutingData()
        {
            // Based on your actual captured response data after the 0xFF status byte
            return new byte[] { 0xD8, 0xDE, 0x40 };
        }

        // ============================================
        // LAPB-Level Routing Protocol (0x21 0x13)
        // ============================================

        /// <summary>
        /// Check if frame is a SINTRAN routing protocol frame
        /// Frame format: [0x21] [0x13] [Protocol ID] ...
        /// NOTE: When called from protocol handlers, LAPB framing is already stripped
        /// Minimum size: 3 bytes (markers + protocol ID)
        /// </summary>
        public static bool IsRoutingProtocolFrame(byte[] frame)
        {
            if (frame == null || frame.Length < 3)
                return false;

            // Check for 0x21 0x13 markers at offset 0-1 (LAPB framing already stripped)
            return frame[0] == ROUTING_MARKER_1 && frame[1] == ROUTING_MARKER_2;
        }

        /// <summary>
        /// Check if frame is a SINTRAN routing protocol frame WITH LAPB framing
        /// Frame format: [LAPB Addr] [LAPB Ctrl] [0x21] [0x13] ...
        /// Minimum size: 17 bytes (full routing frame with LAPB)
        /// </summary>
        public static bool IsRoutingProtocolFrameWithLapb(byte[] frame)
        {
            if (frame == null || frame.Length < 17)
                return false;

            // Check for 0x21 0x13 markers at offset 2-3 (after LAPB address and control)
            return frame[2] == ROUTING_MARKER_1 && frame[3] == ROUTING_MARKER_2;
        }

        /// <summary>
        /// Parse SINTRAN routing protocol frame without LAPB framing
        /// This is used by protocol handlers where LAPB framing has already been stripped
        /// Frame format: [0x21] [0x13] [Protocol ID] [Payload...]
        /// </summary>
        public static SintranRoutingFrame ParseRoutingFrameWithoutLapb(byte[] frame)
        {
            if (!IsRoutingProtocolFrame(frame))
                throw new ArgumentException("Not a valid SINTRAN routing protocol frame", nameof(frame));

            var routing = new SintranRoutingFrame
            {
                // No LAPB framing
                LapbAddress = 0,
                LapbControl = 0,
                NS = 0,
                NR = 0,

                // Markers at offsets 0-1
                Marker1 = frame[0],
                Marker2 = frame[1],

                // Protocol ID at offset 2
                ProtocolId = frame[2]
            };

            // Extract payload (starts at offset 3)
            if (frame.Length > 3)
            {
                int payloadSize = frame.Length - 3;
                routing.Payload = new byte[payloadSize];
                Array.Copy(frame, 3, routing.Payload, 0, payloadSize);
            }
            else
            {
                routing.Payload = Array.Empty<byte>();
            }

            // For routing protocol frames, check command code
            if (routing.ProtocolId == PROTOCOL_ID_ROUTING && routing.Payload.Length > 0)
            {
                routing.CommandCode = routing.Payload[0];
                routing.IsRoutingRequest = routing.CommandCode == ROUTING_CMD_REQUEST;
                routing.IsRoutingResponse = routing.CommandCode == ROUTING_CMD_RESPONSE;
            }

            return routing;
        }

        /// <summary>
        /// Parse LAPB-level SINTRAN routing protocol frame
        /// Frame structure (from real traffic analysis):
        /// [0] LAPB Address (0x09)
        /// [1] LAPB Control (N(S), N(R))
        /// [2] Marker 1 (0x21)
        /// [3] Marker 2 (0x13)
        /// [4] Packet Type (0x00 for data)
        /// [5-6] Length (little-endian)
        /// [7] Separator (0x00)
        /// [8] Destination Node
        /// [9] Separator (0x00)
        /// [10] Source Node
        /// [11+] Protocol-specific data
        /// </summary>
        public static SintranRoutingFrame ParseRoutingFrame(byte[] frame)
        {
            if (!IsRoutingProtocolFrameWithLapb(frame))
                throw new ArgumentException("Not a valid SINTRAN routing protocol frame", nameof(frame));

            var routing = new SintranRoutingFrame
            {
                LapbAddress = frame[0],
                LapbControl = frame[1],
                Marker1 = frame[2],
                Marker2 = frame[3],
                PacketType = frame[4],
                Length = (ushort)(frame[5] | (frame[6] << 8)), // Little-endian
                DestinationNode = frame[8],
                SourceNode = frame[10]
            };

            // Extract N(S) and N(R) from LAPB control byte
            routing.NS = (byte)((frame[1] >> 1) & 0x07); // Bits 1-3
            routing.NR = (byte)((frame[1] >> 5) & 0x07); // Bits 5-7

            // Extract protocol-specific payload (starts at offset 11)
            if (frame.Length > 11)
            {
                int payloadSize = frame.Length - 11;
                routing.Payload = new byte[payloadSize];
                Array.Copy(frame, 11, routing.Payload, 0, payloadSize);

                // Try to identify protocol and command
                // Format varies: Routing protocol has broadcast flag, TAD/PAD don't
                if (routing.Payload.Length >= 2)
                {
                    // Check for broadcast flag at offset 0-1 (0xFF 0xFF)
                    routing.IsBroadcast = routing.Payload[0] == 0xFF && routing.Payload[1] == 0xFF;

                    if (routing.IsBroadcast && routing.Payload.Length >= 5)
                    {
                        // Routing protocol format: [FF FF] [00 01] [Protocol ID] [Command]
                        // Protocol ID at offset 4
                        routing.ProtocolId = routing.Payload[4];

                        // Command code at offset 5
                        if (routing.Payload.Length > 5)
                        {
                            routing.CommandCode = routing.Payload[5];
                        }

                        routing.IsRoutingRequest = (routing.ProtocolId == PROTOCOL_ID_ROUTING &&
                                                     routing.CommandCode == ROUTING_CMD_REQUEST);
                        routing.IsRoutingResponse = (routing.ProtocolId == PROTOCOL_ID_ROUTING &&
                                                      routing.CommandCode == ROUTING_CMD_RESPONSE);
                    }
                    else if (!routing.IsBroadcast && routing.Payload.Length >= 5)
                    {
                        // TAD/PAD protocol format: [00 seq] [01 08] [Protocol ID] [SubID]
                        // Protocol ID at offset 4
                        routing.ProtocolId = routing.Payload[4];
                    }
                }
            }
            else
            {
                routing.Payload = Array.Empty<byte>();
            }

            return routing;
        }

        /// <summary>
        /// Create SINTRAN routing info response frame (0xDE 0x0E)
        /// Based on real traffic: Response to routing info request
        /// </summary>
        public static byte[] CreateRoutingInfoResponse(byte localNode, byte remoteNode, byte ns, byte nr)
        {
            // Response format from real traffic analysis:
            // 09 20 21 13 00 13 00 64 00 66 FF FF 00 01 DE 0E
            return new byte[]
            {
                0x09,                           // [0] LAPB address
                (byte)((nr << 5) | (ns << 1)),  // [1] LAPB control: N(S), N(R)
                ROUTING_MARKER_1,               // [2] Marker 1: 0x21
                ROUTING_MARKER_2,               // [3] Marker 2: 0x13
                0x00,                           // [4] Packet type: data
                0x13, 0x00,                     // [5-6] Length: 19 bytes (little-endian)
                0x00,                           // [7] Separator
                remoteNode,                     // [8] Destination (remote node)
                0x00,                           // [9] Separator
                localNode,                      // [10] Source (local node)
                0xFF, 0xFF,                     // [11-12] Broadcast flag
                0x00, 0x01,                     // [13-14] Protocol version
                PROTOCOL_ID_ROUTING,            // [15] Protocol ID: 0xDE (routing)
                ROUTING_CMD_RESPONSE            // [16] Command code: 0x0E (response)
            };
        }

        /// <summary>
        /// Create SINTRAN routing info request frame (0xDE 0x08)
        /// Based on real traffic analysis
        /// </summary>
        public static byte[] CreateRoutingInfoRequest(byte localNode, byte remoteNode, byte ns, byte nr)
        {
            // Request format from real traffic analysis:
            // 09 00 21 13 00 19 00 66 00 64 FF FF 00 01 DE 08
            return new byte[]
            {
                0x09,                           // [0] LAPB address
                (byte)((nr << 5) | (ns << 1)),  // [1] LAPB control: N(S), N(R)
                ROUTING_MARKER_1,               // [2] Marker 1: 0x21
                ROUTING_MARKER_2,               // [3] Marker 2: 0x13
                0x00,                           // [4] Packet type: data
                0x19, 0x00,                     // [5-6] Length: 25 bytes (little-endian)
                0x00,                           // [7] Separator
                remoteNode,                     // [8] Destination (remote node)
                0x00,                           // [9] Separator
                localNode,                      // [10] Source (local node)
                0xFF, 0xFF,                     // [11-12] Broadcast flag
                0x00, 0x01,                     // [13-14] Protocol version
                PROTOCOL_ID_ROUTING,            // [15] Protocol ID: 0xDE (routing)
                ROUTING_CMD_REQUEST             // [16] Command code: 0x08 (request)
            };
        }

        /// <summary>
        /// Parse your specific captured packets for testing
        /// </summary>
        public static void TestCapturedPackets()
        {
            // Your captured RX packet (X.25 data payload only):
            // 0x0E 0x00 0x64 0x00 0x66 0x00 0x02 0x01 0x00 0xDD 0x12
            var rxPayload = new byte[] { 0x0E, 0x00, 0x64, 0x00, 0x66, 0x00, 0x02, 0x01, 0x00, 0xDD, 0x12 };
            
            // Your captured TX packet (X.25 data payload only):
            // 0x07 0x00 0x66 0x00 0x64 0x00 0x02 0xFF 0xD8 0xDE 0x40
            var txPayload = new byte[] { 0x07, 0x00, 0x66, 0x00, 0x64, 0x00, 0x02, 0xFF, 0xD8, 0xDE, 0x40 };
            
            try
            {
                var requestPacket = ParsePacket(rxPayload);
                // Should parse as: Length=14, Source=100, Dest=102, Seq=2, Payload={0x01, 0x00, 0xDD, 0x12}
                
                var responsePacket = ParsePacket(txPayload);  
                // Should parse as: Length=7, Source=102, Dest=100, Seq=2, Payload={0xFF, 0xD8, 0xDE, 0x40}
            }
            catch (Exception)
            {
                // Parser validation
            }
        }
    }

    /// <summary>
    /// SINTRAN binary packet structure (X.25 data payload format)
    /// </summary>
    public class SintranPacket
    {
        /// <summary>
        /// Command type (derived from payload analysis)
        /// </summary>
        public SintranBinaryProtocol.SintranCommand Command { get; set; }

        /// <summary>
        /// Packet length (first byte)
        /// </summary>
        public byte Length { get; set; }

        /// <summary>
        /// Source node ID (bytes 1-2)
        /// </summary>
        public ushort SourceNode { get; set; }

        /// <summary>
        /// Destination node ID (bytes 3-4)
        /// </summary>
        public ushort DestinationNode { get; set; }

        /// <summary>
        /// Sequence number (bytes 5-6)
        /// </summary>
        public ushort Sequence { get; set; }

        /// <summary>
        /// Packet payload data (bytes 7+)
        /// </summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Check if this is a request packet
        /// </summary>
        public bool IsRequest => Payload.Length >= 2 && Payload[0] == 0x01 && Payload[1] == 0x00;

        /// <summary>
        /// Check if this is a response packet
        /// </summary>
        public bool IsResponse => Payload.Length >= 1 && Payload[0] == 0xFF;

        /// <summary>
        /// Get string representation
        /// </summary>
        public override string ToString()
        {
            var type = IsRequest ? "REQ" : IsResponse ? "RESP" : "UNKNOWN";
            return $"SINTRAN {Command} {type}: {SourceNode}→{DestinationNode} Seq={Sequence} Len={Length}";
        }
    }

    /// <summary>
    /// SINTRAN Routing Protocol Frame (LAPB level)
    /// Represents a complete LAPB I-frame with SINTRAN routing protocol (0x21 0x13)
    /// </summary>
    public class SintranRoutingFrame
    {
        /// <summary>
        /// LAPB address byte (always 0x09 for SINTRAN)
        /// </summary>
        public byte LapbAddress { get; set; }

        /// <summary>
        /// LAPB control byte containing N(S) and N(R)
        /// </summary>
        public byte LapbControl { get; set; }

        /// <summary>
        /// N(S) - Send sequence number (extracted from control byte)
        /// </summary>
        public byte NS { get; set; }

        /// <summary>
        /// N(R) - Receive sequence number (extracted from control byte)
        /// </summary>
        public byte NR { get; set; }

        /// <summary>
        /// First routing protocol marker (always 0x21)
        /// </summary>
        public byte Marker1 { get; set; }

        /// <summary>
        /// Second routing protocol marker (always 0x13)
        /// </summary>
        public byte Marker2 { get; set; }

        /// <summary>
        /// Packet type (usually 0x00 for data packets)
        /// </summary>
        public byte PacketType { get; set; }

        /// <summary>
        /// Payload length field (little-endian)
        /// </summary>
        public ushort Length { get; set; }

        /// <summary>
        /// Destination node ID
        /// </summary>
        public byte DestinationNode { get; set; }

        /// <summary>
        /// Source node ID
        /// </summary>
        public byte SourceNode { get; set; }

        /// <summary>
        /// Protocol-specific payload (starts at offset 11)
        /// </summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Protocol ID extracted from payload (if available)
        /// 0xDA = PAD, 0xDD = TAD, 0xDE = Routing
        /// </summary>
        public byte ProtocolId { get; set; }

        /// <summary>
        /// Command code extracted from payload (for routing protocol)
        /// </summary>
        public byte CommandCode { get; set; }

        /// <summary>
        /// True if this frame has broadcast addressing (0xFF 0xFF)
        /// </summary>
        public bool IsBroadcast { get; set; }

        /// <summary>
        /// True if this is a routing info request (0xDE 0x08)
        /// </summary>
        public bool IsRoutingRequest { get; set; }

        /// <summary>
        /// True if this is a routing info response (0xDE 0x0E)
        /// </summary>
        public bool IsRoutingResponse { get; set; }

        /// <summary>
        /// Check if frame markers are valid SINTRAN routing protocol
        /// </summary>
        public bool IsValid => Marker1 == SintranBinaryProtocol.ROUTING_MARKER_1 &&
                               Marker2 == SintranBinaryProtocol.ROUTING_MARKER_2;

        /// <summary>
        /// Check if this is a TAD protocol frame (0xDD)
        /// </summary>
        public bool IsTadProtocol => ProtocolId == SintranBinaryProtocol.PROTOCOL_ID_TAD;

        /// <summary>
        /// Check if this is a PAD protocol frame (0xDA)
        /// </summary>
        public bool IsPadProtocol => ProtocolId == SintranBinaryProtocol.PROTOCOL_ID_PAD;

        /// <summary>
        /// Check if this is a routing protocol frame (0xDE)
        /// </summary>
        public bool IsRoutingProtocol => ProtocolId == SintranBinaryProtocol.PROTOCOL_ID_ROUTING;

        /// <summary>
        /// Get protocol name string
        /// </summary>
        public string ProtocolName
        {
            get
            {
                if (IsTadProtocol) return "TAD";
                if (IsPadProtocol) return "PAD";
                if (IsRoutingProtocol) return "ROUTING";
                return $"UNKNOWN(0x{ProtocolId:X2})";
            }
        }

        /// <summary>
        /// Get string representation
        /// </summary>
        public override string ToString()
        {
            var direction = $"{SourceNode}→{DestinationNode}";
            var seq = $"N(S)={NS},N(R)={NR}";

            if (IsRoutingRequest)
                return $"ROUTING REQ: {direction} {seq} [0x{CommandCode:X2}]";
            if (IsRoutingResponse)
                return $"ROUTING RESP: {direction} {seq} [0x{CommandCode:X2}]";
            if (IsTadProtocol || IsPadProtocol)
                return $"{ProtocolName}: {direction} {seq} Len={Length}";

            return $"SINTRAN: {direction} {seq} Proto=0x{ProtocolId:X2} Len={Length}";
        }
    }
}