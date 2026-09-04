using System;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

using RetroIO.Protocols.X25.Services;
namespace RetroIO.Protocols.X25.Sintran.Services
{
    /// <summary>
    /// SINTRAN TAD Protocol Service
    /// Handles X.25 connections for SINTRAN TAD terminal access
    /// Routes calls to SintranPvcServer which manages protocol multiplexing and terminal ID assignment
    /// </summary>
    public class SintranTadProtocolService : IProtocolService
    {
        private readonly ILogger _logger;
        private readonly SintranPvcServer _pvcServer;
        private readonly string[] _localAddresses;
        private readonly IProtocolRuntime _runtime;

        public SintranTadProtocolService(ILogger logger, SintranPvcServer pvcServer, IProtocolRuntime runtime, params string[] localAddresses)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pvcServer = pvcServer ?? throw new ArgumentNullException(nameof(pvcServer));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _localAddresses = localAddresses ?? throw new ArgumentNullException(nameof(localAddresses));

            if (_localAddresses.Length == 0)
                throw new ArgumentException("At least one local address must be specified", nameof(localAddresses));

            // Subscribe to SintranPvcServer events
            _pvcServer.PacketGenerated += OnPvcPacketGenerated;

            _logger.LogInformation("SINTRAN TAD Protocol Service initialized for addresses: [{Addresses}]",
                string.Join(", ", _localAddresses));
        }

        public string ServiceName => "SINTRAN_TAD";
        public string[] LocalAddresses => _localAddresses;
        public int Priority => 90; // Slightly lower than standard TAD (100) but higher than generic services

        /// <summary>
        /// Check if this service can handle the given CUD
        /// SINTRAN TAD service handles:
        /// 1. Empty CUD (default for SINTRAN terminals)
        /// 2. CUD with 0xDD (TAD protocol ID from SINTRAN routing)
        /// 3. SINTRAN-specific CUD patterns
        /// </summary>
        public bool CanHandleCUD(byte[] cud)
        {
            if (cud == null || cud.Length == 0)
            {
                _logger.LogTrace("SINTRAN TAD service accepting empty CUD (default SINTRAN terminal)");
                return true;
            }

            // Check for SINTRAN TAD protocol ID (0xDD)
            if (cud.Length >= 1 && cud[0] == 0xDD)
            {
                _logger.LogTrace("SINTRAN TAD service accepting CUD with TAD protocol ID (0xDD)");
                return true;
            }

            // Check for SINTRAN routing protocol markers (0x21 0x13 0xDD)
            if (cud.Length >= 3 && cud[0] == 0x21 && cud[1] == 0x13 && cud[2] == 0xDD)
            {
                _logger.LogTrace("SINTRAN TAD service accepting CUD with routing protocol markers");
                return true;
            }

            // Reject protocols that should not go to SINTRAN TAD
            if (IsXMSGProtocol(cud) || IsIPProtocol(cud) || IsStandardTADProtocol(cud))
            {
                _logger.LogTrace("SINTRAN TAD service rejecting non-SINTRAN protocol CUD");
                return false;
            }

            // Default: accept unknown patterns (SINTRAN systems may use various CUD formats)
            _logger.LogTrace("SINTRAN TAD service accepting unrecognized CUD as SINTRAN terminal");
            return true;
        }

        /// <summary>
        /// Handle incoming X.25 call request
        /// Routes to SintranPvcServer which will detect protocol and assign terminal ID
        /// </summary>
        public bool HandleIncomingCall(ushort lcn, string calledAddress, string callingAddress,
            byte[] cud, byte[] facilities)
        {
            try
            {
                _logger.LogInformation("SINTRAN TAD service handling incoming call from {CallingAddress} to {CalledAddress} on LCN {LCN}",
                    callingAddress, calledAddress, lcn);

                // Create synthetic Call Request packet for PVC server
                var callRequest = X25PacketBuilder.CreateCallRequest(
                    lcn: lcn,
                    calledAddress: calledAddress,
                    callingAddress: callingAddress,
                    facilities: facilities,
                    callUserData: cud
                );

                var packet = new X25Packet(callRequest);
                packet.LCN = lcn;
                packet.Direction = PacketDirection.Receive;
                packet.Timestamp = DateTime.UtcNow;

                var eventArgs = new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow
                };

                // Route to PVC server which will handle protocol detection and terminal assignment
                _pvcServer.OnX25Event(eventArgs);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling SINTRAN TAD incoming call on LCN {LCN}", lcn);
                return false;
            }
        }

        /// <summary>
        /// Handle data received on established connection
        /// Strips SINTRAN routing wrapper (0x21 0x13 0xDD) before forwarding to PVC server
        /// </summary>
        public void HandleData(ushort lcn, byte[] data)
        {
            try
            {
                byte[] tadData = data;

                // Check if data has SINTRAN routing wrapper (0x21 0x13 protocol_id)
                if (SintranBinaryProtocol.IsRoutingProtocolFrame(data))
                {
                    _logger.LogDebug("SINTRAN TAD: Stripping routing wrapper from incoming data on LCN {LCN}", lcn);

                    // Parse and extract TAD payload (without routing wrapper)
                    var routingFrame = SintranBinaryProtocol.ParseRoutingFrameWithoutLapb(data);
                    tadData = routingFrame.Payload;

                    _logger.LogDebug("SINTRAN TAD: Extracted {PayloadBytes} bytes of TAD data (Protocol ID: 0x{ProtocolId:X2})",
                        tadData.Length, routingFrame.ProtocolId);
                }

                // Create synthetic Data packet for PVC server (with unwrapped TAD data)
                var dataPacket = X25PacketBuilder.CreateDataPacket(
                    lcn: lcn,
                    ps: 0, // Simplified sequencing
                    pr: 0,
                    userData: tadData
                );

                var packet = new X25Packet(dataPacket);
                packet.LCN = lcn;
                packet.Direction = PacketDirection.Receive;
                packet.Timestamp = DateTime.UtcNow;

                var eventArgs = new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow
                };

                // Forward to PVC server
                _pvcServer.OnX25Event(eventArgs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling SINTRAN TAD data on LCN {LCN}", lcn);
            }
        }

        /// <summary>
        /// Handle call clearing
        /// </summary>
        public void HandleClear(ushort lcn, byte cause, byte diagnostic)
        {
            try
            {
                _logger.LogInformation("SINTRAN TAD service handling clear on LCN {LCN}, cause={Cause}, diagnostic={Diagnostic}",
                    lcn, cause, diagnostic);

                // Create synthetic Clear Request packet
                var clearPacket = X25PacketBuilder.CreateClearRequest(lcn, cause, diagnostic);
                var packet = new X25Packet(clearPacket);
                packet.LCN = lcn;
                packet.Direction = PacketDirection.Receive;
                packet.Timestamp = DateTime.UtcNow;

                var eventArgs = new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow
                };

                // Forward to PVC server
                _pvcServer.OnX25Event(eventArgs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling SINTRAN TAD clear on LCN {LCN}", lcn);
            }
        }

        /// <summary>
        /// Handle interrupt packets
        /// </summary>
        public void HandleInterrupt(ushort lcn, byte[] data)
        {
            _logger.LogDebug("SINTRAN TAD service received interrupt on LCN {LCN}: {DataLength} bytes",
                lcn, data?.Length ?? 0);
            // SINTRAN TAD protocol doesn't typically use interrupts
        }

        /// <summary>
        /// Handle SintranPvcServer events and route to X25Layer for history and module routing
        /// CRITICAL: PVC server packets must be flipped to client perspective for proper routing
        /// Adds SINTRAN routing wrapper (0x21 0x13 0xDD) to Data packets for SINTRAN clients
        /// </summary>
        private void OnPvcPacketGenerated(object? sender, X25PacketEventArgs e)
        {
            _logger.LogDebug("SINTRAN PVC packet event: {PacketType}", e.Packet.PacketTypeId);

            // Fire X25Layer event to create history items (server perspective)
            if (e.Direction == PacketDirection.Receive)
            {
                _runtime.X25Layer.FirePacketReceived(e);
            }
            else
            {
                _runtime.X25Layer.FirePacketTransmitted(e);
            }

            // For Data packets: Add SINTRAN routing wrapper before sending to client
            var packetToSend = e.Packet;
            if (e.Packet.PacketType == X25PacketType.Data && e.Direction == PacketDirection.Transmit)
            {
                var userData = e.Packet.Data;
                if (userData != null && userData.Length > 0)
                {
                    // Add routing wrapper: 0x21 0x13 0xDD + TAD_data
                    var wrappedData = new byte[3 + userData.Length];
                    wrappedData[0] = SintranBinaryProtocol.ROUTING_MARKER_1;  // 0x21
                    wrappedData[1] = SintranBinaryProtocol.ROUTING_MARKER_2;  // 0x13
                    wrappedData[2] = SintranBinaryProtocol.PROTOCOL_ID_TAD;   // 0xDD
                    Array.Copy(userData, 0, wrappedData, 3, userData.Length);

                    _logger.LogDebug("SINTRAN TAD: Adding routing wrapper to outgoing data on LCN {LCN} ({OriginalBytes} -> {WrappedBytes} bytes)",
                        e.Packet.LCN, userData.Length, wrappedData.Length);

                    // Create new packet with wrapped data
                    var wrappedPacketData = X25PacketBuilder.CreateDataPacket(
                        lcn: e.Packet.LCN,
                        ps: 0,
                        pr: 0,
                        userData: wrappedData
                    );

                    packetToSend = new X25Packet(wrappedPacketData);
                    packetToSend.LCN = e.Packet.LCN;
                    packetToSend.Direction = e.Packet.Direction;
                    packetToSend.Timestamp = e.Packet.Timestamp;
                }
            }

            // CRITICAL: Flip direction for client routing
            // Server TRANSMITS packets that client RECEIVES
            // Server RECEIVES packets that client TRANSMITS
            var clientDirection = e.Direction == PacketDirection.Transmit
                ? PacketDirection.Receive
                : PacketDirection.Transmit;

            var clientEventArgs = new X25PacketEventArgs
            {
                Packet = packetToSend,       // Use wrapped packet for Data, original for others
                Direction = clientDirection,  // Flipped for client perspective
                Timestamp = e.Timestamp,
                PacketId = e.PacketId
            };

            _logger.LogDebug("SINTRAN PVC: Routing {PacketType} to client (Server={ServerDir}, Client={ClientDir})",
                e.Packet.PacketTypeId, e.Direction, clientDirection);

            // Route event to bound module (SINTRAN TAD client) with flipped direction
            _runtime.RouteX25EventToBoundModule(e.Packet.LCN, clientEventArgs);
        }

        /// <summary>
        /// Check if CUD indicates XMSG protocol
        /// </summary>
        private bool IsXMSGProtocol(byte[] cud)
        {
            if (cud.Length >= 6 && cud[0] == 0x4C && cud[1] == 0x03)
                return true;

            if (cud.Length >= 6)
            {
                ushort port = (ushort)((cud[4] << 8) | cud[5]);
                if (port >= 0x1000 && port <= 0x1FFF)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Check if CUD indicates IP protocol
        /// </summary>
        private bool IsIPProtocol(byte[] cud)
        {
            return cud.Length >= 1 && cud[0] == 0xCC;
        }

        /// <summary>
        /// Check if CUD indicates standard X.29 TAD protocol (not SINTRAN)
        /// </summary>
        private bool IsStandardTADProtocol(byte[] cud)
        {
            // X.29 PAD protocol: 0x01 0x00 0x00 0x00
            return cud.Length >= 4 && cud[0] == 0x01 && cud[1] == 0x00 && cud[2] == 0x00 && cud[3] == 0x00;
        }
    }
}
