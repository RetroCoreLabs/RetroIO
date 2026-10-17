//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using Microsoft.Extensions.Logging;

using RetroIO.Protocols.X25.Protocols;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// SINTRAN Routing Protocol Handler
    /// Handles routing information updates between SINTRAN systems
    /// Manages network topology and reachability information
    /// </summary>
    public class SintranRoutingHandler : ISintranProtocolHandler
    {
        private readonly ILogger _logger;
        private SintranHandlerStats _stats;
        private bool _initialized = false;
        private SintranTadHandler? _tadHandler = null;
        private SintranTadHandler? _padHandler = null; // For future PAD implementation

        /// <inheritdoc/>
        public string Protocol => "ROUTING";
        /// <inheritdoc/>
        public string Description => "SINTRAN routing information protocol";
        /// <inheritdoc/>
        public ushort LCN { get; private set; }

        /// <inheritdoc/>
        public event EventHandler<SintranResponseEventArgs>? ResponseReady;

        /// <summary>
        /// Creates the handler.
        /// </summary>
        /// <param name="logger">
        /// The logger to write to.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="logger"/> is null.
        /// </exception>
        public SintranRoutingHandler(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _stats = new SintranHandlerStats
            {
                Protocol = Protocol,
                Status = "Initializing"
            };
        }

        /// <summary>
        /// Register TAD handler for 0xDD protocol frames
        /// </summary>
        public void RegisterTadHandler(SintranTadHandler tadHandler)
        {
            _tadHandler = tadHandler ?? throw new ArgumentNullException(nameof(tadHandler));

            // Subscribe to TAD handler responses and wrap them in routing protocol frames
            _tadHandler.ResponseReady += (sender, args) =>
            {
                // Wrap TAD response in 0x21 0x13 0xDD frame
                byte[] wrappedFrame = WrapInRoutingProtocol(args.Data, 0xDD);
                SendBinaryResponse(wrappedFrame);

                _logger.LogDebug("SintranRoutingHandler: Forwarded TAD response ({Bytes} bytes)", wrappedFrame.Length);
            };

            _logger.LogInformation("SintranRoutingHandler: TAD handler registered");
        }

        /// <summary>
        /// Register PAD handler for 0xDA protocol frames
        /// </summary>
        public void RegisterPadHandler(SintranTadHandler padHandler)
        {
            _padHandler = padHandler ?? throw new ArgumentNullException(nameof(padHandler));

            // Subscribe to PAD handler responses and wrap them in routing protocol frames
            _padHandler.ResponseReady += (sender, args) =>
            {
                // Wrap PAD response in 0x21 0x13 0xDA frame
                byte[] wrappedFrame = WrapInRoutingProtocol(args.Data, 0xDA);
                SendBinaryResponse(wrappedFrame);

                _logger.LogDebug("SintranRoutingHandler: Forwarded PAD response ({Bytes} bytes)", wrappedFrame.Length);
            };

            _logger.LogInformation("SintranRoutingHandler: PAD handler registered");
        }

        /// <summary>
        /// Wrap payload in SINTRAN routing protocol frame (0x21 0x13 [protocol ID])
        /// </summary>
        private byte[] WrapInRoutingProtocol(byte[] payload, byte protocolId)
        {
            byte[] frame = new byte[3 + payload.Length];
            frame[0] = 0x21; // SINTRAN marker
            frame[1] = 0x13; // Routing protocol marker
            frame[2] = protocolId; // Protocol ID (0xDD=TAD, 0xDA=PAD)
            Array.Copy(payload, 0, frame, 3, payload.Length);
            return frame;
        }

        /// <inheritdoc/>
        public void Initialize()
        {
            if (_initialized) return;

            _logger.LogInformation("SintranRoutingHandler: Initializing SINTRAN routing handler");
            _stats.IsActive = true;
            _stats.Status = "Ready";
            _initialized = true;
        }

        /// <inheritdoc/>
        public void OnChannelEstablished(ushort lcn)
        {
            LCN = lcn;
            _stats.LCN = lcn;
            _stats.IsActive = true;
            _stats.LastActivity = DateTime.UtcNow;
            _stats.Status = "Connected";
            
            _logger.LogInformation("SintranRoutingHandler: Routing channel established on LCN {LCN}", lcn);
            
            // Send initial routing announcement
            SendRoutingAnnouncement();
        }

        /// <inheritdoc/>
        public void OnChannelClosed()
        {
            _stats.IsActive = false;
            _stats.Status = "Disconnected";
            _logger.LogInformation("SintranRoutingHandler: Routing channel closed on LCN {LCN}", LCN);
        }

        /// <inheritdoc/>
        public void HandleData(byte[] data)
        {
            if (data.Length == 0) return;

            _stats.MessagesReceived++;
            _stats.BytesReceived += data.Length;
            _stats.LastActivity = DateTime.UtcNow;

            try
            {
                _logger.LogInformation("SintranRoutingHandler: Received {Bytes} bytes on LCN {LCN}: {Data}",
                    data.Length, LCN, BitConverter.ToString(data));

                // Check if this is a LAPB-level routing protocol frame (0x21 0x13)
                if (SintranBinaryProtocol.IsRoutingProtocolFrame(data))
                {
                    ProcessRoutingProtocolFrame(data);
                }
                else
                {
                    // Fall back to legacy X.25 payload parsing
                    ProcessBinaryRoutingMessage(data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SintranRoutingHandler: Error processing routing data on LCN {LCN}", LCN);
                _stats.Status = $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Process LAPB-level SINTRAN routing protocol frame (0x21 0x13)
        /// Handles routing info requests and multiplexes to service protocols
        /// NOTE: LAPB framing is already stripped when data arrives here
        /// </summary>
        private void ProcessRoutingProtocolFrame(byte[] data)
        {
            try
            {
                var frame = SintranBinaryProtocol.ParseRoutingFrameWithoutLapb(data);

                _logger.LogInformation("SintranRoutingHandler: Parsed routing frame: {Frame}", frame);

                // Handle routing info request (0xDE 0x08)
                if (frame.IsRoutingRequest)
                {
                    HandleRoutingInfoRequest(frame);
                }
                // Handle routing info response (0xDE 0x0E)
                else if (frame.IsRoutingResponse)
                {
                    HandleRoutingInfoResponse(frame);
                }
                // Handle TAD protocol frames (0xDD)
                else if (frame.IsTadProtocol)
                {
                    _logger.LogInformation("SintranRoutingHandler: TAD protocol frame detected - routing to TAD handler");

                    if (_tadHandler != null)
                    {
                        // Extract TAD protocol payload (skip 0x21 0x13 0xDD header)
                        int payloadStart = 3; // After markers and protocol ID
                        int payloadLength = data.Length - payloadStart;

                        if (payloadLength > 0)
                        {
                            byte[] tadPayload = new byte[payloadLength];
                            Array.Copy(data, payloadStart, tadPayload, 0, payloadLength);

                            _logger.LogDebug("SintranRoutingHandler: Routing {Bytes} bytes to TAD handler", payloadLength);
                            _tadHandler.HandleData(tadPayload);
                        }
                        else
                        {
                            _logger.LogWarning("SintranRoutingHandler: TAD frame has no payload");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("SintranRoutingHandler: TAD handler not registered - frame dropped");
                    }
                }
                // Handle PAD protocol frames (0xDA)
                else if (frame.IsPadProtocol)
                {
                    _logger.LogInformation("SintranRoutingHandler: PAD protocol frame detected - routing to PAD handler");

                    if (_padHandler != null)
                    {
                        // Extract PAD protocol payload (skip 0x21 0x13 0xDA header)
                        int payloadStart = 3; // After markers and protocol ID
                        int payloadLength = data.Length - payloadStart;

                        if (payloadLength > 0)
                        {
                            byte[] padPayload = new byte[payloadLength];
                            Array.Copy(data, payloadStart, padPayload, 0, payloadLength);

                            _logger.LogDebug("SintranRoutingHandler: Routing {Bytes} bytes to PAD handler", payloadLength);
                            _padHandler.HandleData(padPayload);
                        }
                        else
                        {
                            _logger.LogWarning("SintranRoutingHandler: PAD frame has no payload");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("SintranRoutingHandler: PAD handler not registered - frame dropped");
                    }
                }
                else
                {
                    _logger.LogWarning("SintranRoutingHandler: Unknown protocol ID: 0x{ProtocolId:X2}", frame.ProtocolId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SintranRoutingHandler: Failed to parse routing protocol frame");
            }
        }

        /// <summary>
        /// Handle routing info request (0xDE 0x08)
        /// Must respond with routing info response (0xDE 0x0E)
        /// </summary>
        private void HandleRoutingInfoRequest(SintranRoutingFrame request)
        {
            _logger.LogInformation("SintranRoutingHandler: Routing info request from node {SourceNode} to node {DestNode}",
                request.SourceNode, request.DestinationNode);

            // Create routing info response
            // Note: N(S) and N(R) should be incremented according to LAPB rules
            // For now, using simple increment (should be managed by LAPB layer)
            byte responseNS = (byte)((request.NR + 1) % 8);
            byte responseNR = (byte)((request.NS + 1) % 8);

            var responseFrame = SintranBinaryProtocol.CreateRoutingInfoResponse(
                request.DestinationNode, // Our local node
                request.SourceNode,      // Remote node
                responseNS,
                responseNR
            );

            _logger.LogInformation("SintranRoutingHandler: Sending routing info response to node {DestNode}",
                request.SourceNode);
            _logger.LogDebug("SintranRoutingHandler: Response frame ({Bytes} bytes): {Data}",
                responseFrame.Length, BitConverter.ToString(responseFrame));

            SendBinaryResponse(responseFrame);
        }

        /// <summary>
        /// Handle routing info response (0xDE 0x0E)
        /// </summary>
        private void HandleRoutingInfoResponse(SintranRoutingFrame response)
        {
            _logger.LogInformation("SintranRoutingHandler: Routing info response from node {SourceNode} to node {DestNode}",
                response.SourceNode, response.DestinationNode);

            // Update routing table or process routing information
            // For now, just acknowledge receipt
            _stats.Status = $"Routing OK (Node {response.SourceNode})";
        }

        /// <summary>
        /// Process incoming binary routing message from SINTRAN system (LEGACY)
        /// </summary>
        private void ProcessBinaryRoutingMessage(byte[] data)
        {
            try
            {
                // Parse SINTRAN binary protocol packet
                var packet = SintranBinaryProtocol.ParsePacket(data);
                
                _logger.LogInformation("SintranRoutingHandler: Parsed SINTRAN packet: {Packet}", packet);
                
                switch (packet.Command)
                {
                    case SintranBinaryProtocol.SintranCommand.RoutingList:
                        HandleBinaryRoutingListRequest(packet);
                        break;
                        
                    case SintranBinaryProtocol.SintranCommand.NodeStatus:
                        HandleBinaryNodeStatusRequest(packet);
                        break;
                        
                    default:
                        _logger.LogWarning("SintranRoutingHandler: Unknown SINTRAN command: {Command}", packet.Command);
                        SendBinaryError(packet, "Unknown command");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SintranRoutingHandler: Failed to parse SINTRAN packet");
                // Send generic error response
                SendGenericError();
            }
        }

        /// <summary>
        /// Handle binary routing list request from real SINTRAN machine
        /// </summary>
        private void HandleBinaryRoutingListRequest(SintranPacket packet)
        {
            _logger.LogInformation("SintranRoutingHandler: Processing routing list request from SINTRAN node {SourceNode} to our local service {DestinationNode}", 
                packet.SourceNode, packet.DestinationNode);

            // Extract request data for analysis (skip first 2 bytes)
            byte[] requestData;
            if (packet.Payload.Length > 2)
            {
                requestData = new byte[packet.Payload.Length - 2];
                Array.Copy(packet.Payload, 2, requestData, 0, packet.Payload.Length - 2);
            }
            else
            {
                requestData = Array.Empty<byte>();
            }
            if (requestData.Length > 0)
            {
                _logger.LogInformation("SintranRoutingHandler: Request data: {RequestData}", BitConverter.ToString(requestData));
            }

            // Generate routing response based on our local services
            // Our emulator provides services at addresses 200 (TAD), 300 (ROUTING), 400 (XMSG), 500 (FILE)
            var routingData = CreateLocalServicesRoutingData(packet.DestinationNode);
            
            // Create response packet: 
            // - Source = Our local service address (the destination from request)
            // - Destination = The SINTRAN machine that requested (original source)
            var responsePacket = SintranBinaryProtocol.CreateRoutingListResponse(
                packet.DestinationNode, // Our local service address becomes source
                packet.SourceNode,      // Original SINTRAN machine becomes destination
                packet.Sequence,        // Same sequence number
                routingData
            );

            _logger.LogInformation("SintranRoutingHandler: Sending routing response from local service {LocalAddress} to SINTRAN node {RemoteNode}", 
                packet.DestinationNode, packet.SourceNode);
            _logger.LogDebug("SintranRoutingHandler: Response packet ({Bytes} bytes): {Data}", 
                responsePacket.Length, BitConverter.ToString(responsePacket));

            SendBinaryResponse(responsePacket);
        }

        /// <summary>
        /// Handle binary node status request
        /// </summary>
        private void HandleBinaryNodeStatusRequest(SintranPacket packet)
        {
            _logger.LogInformation("SintranRoutingHandler: Processing node status request from node {SourceNode}", 
                packet.SourceNode);

            // Create status response data
            var statusData = new byte[]
            {
                0x01, // Node is active
                0x00, 0x05, // 5 routes available  
                0x00, 0x02, // 2 interfaces active
                0xFF        // All systems operational
            };

            var responsePacket = SintranBinaryProtocol.CreateRoutingListResponse(
                packet.DestinationNode,
                packet.SourceNode, 
                packet.Sequence,
                statusData
            );

            SendBinaryResponse(responsePacket);
        }

        /// <summary>
        /// Send initial routing announcement when channel establishes
        /// </summary>
        private void SendRoutingAnnouncement()
        {
            // Send binary routing announcement
            var announcementPacket = SintranBinaryProtocol.CreateNodeStatusRequest(100, 0xFFFF, 0x0001); // Broadcast announcement
            SendBinaryResponse(announcementPacket);
            
            _logger.LogInformation("SintranRoutingHandler: Sent routing announcement");
        }

        /// <summary>
        /// Send binary SINTRAN response
        /// </summary>
        private void SendBinaryResponse(byte[] responseData)
        {
            _stats.MessagesSent++;
            _stats.BytesSent += responseData.Length;
            _stats.LastActivity = DateTime.UtcNow;

            var eventArgs = new SintranResponseEventArgs
            {
                LCN = LCN,
                Data = responseData,
                Protocol = Protocol,
                Timestamp = DateTime.UtcNow
            };

            ResponseReady?.Invoke(this, eventArgs);
            
            _logger.LogDebug("SintranRoutingHandler: Sent binary response on LCN {LCN}: {Data}", 
                LCN, BitConverter.ToString(responseData));
        }

        /// <summary>
        /// Send binary error response
        /// </summary>
        private void SendBinaryError(SintranPacket originalPacket, string errorMessage)
        {
            _logger.LogWarning("SintranRoutingHandler: Sending error response: {ErrorMessage}", errorMessage);
            
            // Create error response packet
            var errorData = new byte[] { 0x01 }; // Error status
            var errorPacket = SintranBinaryProtocol.CreateRoutingListResponse(
                originalPacket.DestinationNode,
                originalPacket.SourceNode,
                originalPacket.Sequence,
                errorData
            );
            
            SendBinaryResponse(errorPacket);
        }

        /// <summary>
        /// Send generic error when packet parsing fails
        /// </summary>
        private void SendGenericError()
        {
            // Send minimal error response
            var errorResponse = new byte[] { 0x07, 0x00, 0x64, 0xFF, 0xFF, 0x00, 0x00, 0x01 }; // Corrected format
            SendBinaryResponse(errorResponse);
        }

        /// <summary>
        /// Create routing data for our local X.25 emulator services
        /// DREAMING UP SHIT, VALUES MAY NOT BE CORRECT - Need real SINTRAN routing response format from user
        /// </summary>
        private byte[] CreateLocalServicesRoutingData(ushort localServiceAddress)
        {
            // CRITICAL: Using your real captured response data until proper format is provided
            // Your captured response was: 0xD8 0xDE 0x40 after the 0xFF status byte
            // TODO: Need proper SINTRAN routing response format for each service type
            
            _logger.LogWarning("SintranRoutingHandler: Using placeholder routing data - need real SINTRAN format specification");
            
            // Return the actual captured routing data you provided
            return new byte[] { 0xD8, 0xDE, 0x40 };
        }

        /// <inheritdoc/>
        public SintranHandlerStats GetStats()
        {
            return new SintranHandlerStats
            {
                Protocol = _stats.Protocol,
                LCN = _stats.LCN,
                MessagesReceived = _stats.MessagesReceived,
                MessagesSent = _stats.MessagesSent,
                BytesReceived = _stats.BytesReceived,
                BytesSent = _stats.BytesSent,
                LastActivity = _stats.LastActivity,
                IsActive = _stats.IsActive,
                Status = _stats.Status
            };
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!_initialized) return;

            _logger.LogDebug("SintranRoutingHandler: Disposing routing handler for LCN {LCN}", LCN);
            _stats.IsActive = false;
            _stats.Status = "Disposed";
            _initialized = false;
        }
    }
}