using System;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Transport;
using RetroIO.Protocols.X25.Services;

using RetroIO.Link.Lapb;
namespace RetroIO.Protocols.X25.Layers
{
    /// <summary>
    /// X.25 Routing Layer - Handles routing of X.25 packets according to RFC 1613
    /// Separates routing logic from protocol processing
    /// </summary>
    public class X25RoutingLayer
    {
        private readonly ILogger _logger;
        private readonly X25Router _router;
        private readonly IProtocolRuntime _runtime;
        private readonly ProtocolServiceRegistry _protocolServices;

        public X25RoutingLayer(ILogger logger, X25Router router, IProtocolRuntime runtime, ProtocolServiceRegistry protocolServices)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _protocolServices = protocolServices ?? throw new ArgumentNullException(nameof(protocolServices));

            _logger.LogInformation("X.25 routing layer initialized with protocol service integration");
        }

        #region Outbound Call Routing (RFC 1613)

        /// <summary>
        /// Place Call Request with module binding support
        /// Routes outbound X.25 call through local services or transport interfaces
        /// </summary>
        /// <param name="calledAddress">Called X.121 address</param>
        /// <param name="callingAddress">Calling X.121 address (optional)</param>
        /// <param name="callUserData">Call User Data (optional)</param>
        /// <param name="facilities">X.25 facilities (optional)</param>
        /// <param name="callingModule">Module requesting the call (optional)</param>
        /// <returns>True if call was successfully routed</returns>
        public bool PlaceCallRequest(string calledAddress, string? callingAddress = null,
                                   byte[]? callUserData = null, byte[]? facilities = null,
                                   IProtocolModule? callingModule = null)
        {
            _logger.LogDebug("PlaceCallRequest: to={CalledAddress}, from={CallingAddress}, module={Module}",
                calledAddress, callingAddress ?? "(none)", callingModule?.Name ?? "(none)");

            var callRequest = new X25CallRequest(calledAddress, callingAddress ?? "", facilities, callUserData);

            try
            {
                // Step 1: Check for local services first (highest priority)
                var localService = _protocolServices.FindServiceForCall(calledAddress, callUserData ?? Array.Empty<byte>());
                if (localService != null)
                {
                    _logger.LogInformation("Routing call to {CalledAddress} via local service '{ServiceName}'",
                        calledAddress, localService.ServiceName);
                    return RouteToLocalService(callRequest, localService, callingModule);
                }

                // Step 2: Check transport interfaces (XOT, Serial)
                _logger.LogDebug("No local service for {CalledAddress}, checking transport interfaces", calledAddress);

                // Allocate LCN and bind calling module before placing the outbound call
                ushort lcn = 0;
                if (callingModule != null)
                {
                    lcn = _runtime.FindAvailableLCN();
                    if (lcn == 0)
                    {
                        _logger.LogError("No available LCN for outbound call to {CalledAddress}", calledAddress);
                        return false;
                    }
                    _runtime.BindLcnToModule(lcn, callingModule);
                    callRequest.SourceLcn = lcn;
                    _logger.LogInformation("Allocated LCN {LCN} and bound module '{Module}' for outbound call",
                        lcn, callingModule.Name);
                }

                // Pre-register so Call Accepted can find the mapping (PlaceCall is synchronous
                // but the receive loop processes Call Accepted on a background thread)
                // Remote LCN = local LCN for DTE-originated calls (we choose the LCN)
                if (lcn > 0)
                    _runtime.RegisterOutboundXotCallDeferred(lcn, lcn);

                var success = _router.RouteOutboundCall(callRequest);

                if (success && lcn > 0 && callRequest.UsedTransport != null)
                {
                    // Now set the actual transport on the pre-registered mapping
                    _runtime.FinalizeOutboundXotCall(lcn, callRequest.UsedTransport);
                    _logger.LogInformation("Outbound XOT call to {CalledAddress} placed on LCN {LCN}", calledAddress, lcn);
                }
                else if (!success && lcn > 0)
                {
                    // Unbind on failure
                    _runtime.UnbindModule(lcn);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error routing call to {CalledAddress}", calledAddress);
                return false;
            }
        }

        /// <summary>
        /// Place Call Request (backward compatibility method)
        /// Routes outbound X.25 call through local services or transport interfaces
        /// </summary>
        /// <param name="calledAddress">Called X.121 address</param>
        /// <param name="callingAddress">Calling X.121 address (optional)</param>
        /// <param name="callUserData">Call User Data (optional)</param>
        /// <param name="facilities">X.25 facilities (optional)</param>
        /// <returns>True if call was successfully routed</returns>
        public bool PlaceCallRequest(string calledAddress, string? callingAddress = null,
                                   byte[]? callUserData = null, byte[]? facilities = null)
        {
            return PlaceCallRequest(calledAddress, callingAddress, callUserData, facilities, null);
        }

        /// <summary>
        /// Route call to local protocol service
        /// </summary>
        /// <param name="callRequest">X.25 call request</param>
        /// <param name="service">Target protocol service</param>
        /// <param name="callingModule">Module requesting the call</param>
        /// <returns>True if successfully routed</returns>
        private bool RouteToLocalService(X25CallRequest callRequest, IProtocolService service, IProtocolModule? callingModule)
        {
            try
            {
                // Find available LCN for the call
                ushort lcn = _runtime.FindAvailableLCN();
                if (lcn == 0)
                {
                    _logger.LogError("No available LCN for call to {CalledAddress}", callRequest.CalledAddress);
                    return false;
                }

                _logger.LogDebug("Allocated LCN {LCN} for call to {CalledAddress}", lcn, callRequest.CalledAddress);

                // Bind the calling module to the LCN if provided
                if (callingModule != null)
                {
                    _runtime.BindLcnToModule(lcn, callingModule);
                    _logger.LogDebug("Bound module '{ModuleName}' to LCN {LCN}", callingModule.Name, lcn);
                }

                // Generate TX CALL REQUEST packet for packet logging and protocol compliance
                var callRequestPacket = X25PacketBuilder.CreateCallRequest(
                    lcn,
                    callRequest.CalledAddress,
                    callRequest.CallingAddress,
                    callRequest.Facilities,
                    callRequest.CallUserData);

                var txCallRequestEvent = new X25PacketEventArgs
                {
                    Packet = new X25Packet(callRequestPacket) { Direction = PacketDirection.Transmit },
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow
                };

                // Add TX CALL REQUEST to packet history
                _runtime.RouteX25EventToBoundModule(lcn, txCallRequestEvent);

                // Route the call to the local service
                var success = service.HandleIncomingCall(lcn, callRequest.CalledAddress,
                    callRequest.CallingAddress, callRequest.CallUserData ?? Array.Empty<byte>(),
                    callRequest.Facilities ?? Array.Empty<byte>());

                if (success)
                {
                    _logger.LogInformation("Local service '{ServiceName}' accepted call on LCN {LCN}",
                        service.ServiceName, lcn);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Local service '{ServiceName}' rejected call on LCN {LCN}",
                        service.ServiceName, lcn);
                    // TODO: Generate TX CLEAR REQUEST packet for rejected calls
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error routing to local service '{ServiceName}'", service.ServiceName);
                return false;
            }
        }

        #endregion

        #region Inbound Call Handling

        /// <summary>
        /// Handle incoming Call Request from internal X.25 processing
        /// Routes calls to local services or forwards to other transports
        /// </summary>
        /// <param name="lcn">Logical Channel Number</param>
        /// <param name="calledAddress">Called X.121 address</param>
        /// <param name="callingAddress">Calling X.121 address</param>
        /// <param name="callUserData">Call User Data</param>
        /// <param name="facilities">X.25 facilities</param>
        /// <returns>Protocol module to bind to LCN, or null if call should be rejected</returns>
        public IProtocolModule? HandleIncomingCall(ushort lcn, string calledAddress, string callingAddress,
                                                  byte[] callUserData, byte[]? facilities = null)
        {
            _logger.LogDebug("Handling incoming call: LCN={LCN}, from={CallingAddress}, to={CalledAddress}",
                lcn, callingAddress, calledAddress);

            try
            {
                // Step 1: Check for local services first (highest priority)
                var localService = _protocolServices.FindServiceForCall(calledAddress, callUserData ?? Array.Empty<byte>());
                if (localService != null)
                {
                    _logger.LogDebug("Found local service '{ServiceName}' for address {CalledAddress}",
                        localService.ServiceName, calledAddress);

                    // Handle the call using the protocol service
                    var success = localService.HandleIncomingCall(lcn, calledAddress, callingAddress, callUserData ?? Array.Empty<byte>(), facilities ?? Array.Empty<byte>());

                    if (success)
                    {
                        _logger.LogInformation("Local service '{ServiceName}' accepted call on LCN {LCN}",
                            localService.ServiceName, lcn);

                        // Return the associated module if available
                        var modules = _runtime.GetRegisteredModules();
                        var moduleKey = localService.ServiceName + "Server";

                        if (modules.ContainsKey(moduleKey))
                        {
                            _logger.LogDebug("Binding module '{ModuleKey}' to LCN {LCN}", moduleKey, lcn);
                            return modules[moduleKey];
                        }
                        else
                        {
                            _logger.LogDebug("No module found for key '{ModuleKey}'", moduleKey);
                            return null; // Service handled call but no module to bind
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Local service '{ServiceName}' rejected call on LCN {LCN}",
                            localService.ServiceName, lcn);
                        return null; // Call rejected
                    }
                }

                // Step 2: Check transport interfaces for forwarding
                // TODO: Implement transport interface forwarding
                _logger.LogWarning("No route found for address {CalledAddress} - rejecting call", calledAddress);
                return null; // No route found
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling incoming call to {CalledAddress} on LCN {LCN}",
                    calledAddress, lcn);
                return null; // Error - reject call
            }
        }

        /// <summary>
        /// Handle incoming Call Request from transport interface (XOT, Serial, etc.)
        /// Looks up local services for the called address, allocates LCN, and binds.
        /// </summary>
        public bool HandleIncomingCall(X25CallRequest callRequest, ITransportInterface sourceTransport)
        {
            _logger.LogInformation("Handling incoming Call Request from {CallingAddress} to {CalledAddress} via {Transport}",
                                 callRequest.CallingAddress, callRequest.CalledAddress, sourceTransport.Name);

            try
            {
                // Step 1: Look up local service for the called address
                var localService = _protocolServices.FindServiceForCall(
                    callRequest.CalledAddress, callRequest.CallUserData ?? Array.Empty<byte>());

                if (localService == null)
                {
                    _logger.LogWarning("No local service found for address {CalledAddress} - clearing call",
                                     callRequest.CalledAddress);
                    SendClearRequest(callRequest, sourceTransport, 0x01); // Number busy / not obtainable
                    return false;
                }

                // Step 2: Allocate LCN
                ushort lcn = _runtime.FindAvailableLCN();
                if (lcn == 0)
                {
                    _logger.LogError("No available LCN for incoming call to {CalledAddress}", callRequest.CalledAddress);
                    SendClearRequest(callRequest, sourceTransport, 0x05); // Network congestion
                    return false;
                }

                _logger.LogInformation("Allocated LCN {LCN} for incoming call to {CalledAddress} via service '{ServiceName}'",
                                     lcn, callRequest.CalledAddress, localService.ServiceName);

                // Step 3: Store the XOT connection mapping so we can send data back
                _runtime.RegisterXotConnection(lcn, callRequest.SourceConnection, callRequest.SourceLcn);

                // Step 3b: Bind the service to the LCN for data routing
                _runtime.BindServiceToLcn(lcn, localService);

                // Step 4: Handle the call via the local service
                var success = localService.HandleIncomingCall(lcn, callRequest.CalledAddress,
                    callRequest.CallingAddress, callRequest.CallUserData ?? Array.Empty<byte>(),
                    callRequest.Facilities ?? Array.Empty<byte>());

                if (success)
                {
                    _logger.LogInformation("Service '{ServiceName}' accepted call on LCN {LCN}, sending Call Accepted",
                                         localService.ServiceName, lcn);

                    // Send Call Accepted back to caller
                    SendCallAccepted(callRequest, sourceTransport);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Service '{ServiceName}' rejected call on LCN {LCN}", localService.ServiceName, lcn);
                    _runtime.UnregisterXotConnection(lcn);
                    SendClearRequest(callRequest, sourceTransport, 0x01);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling incoming Call Request from {CallingAddress}",
                               callRequest.CallingAddress);
                SendClearRequest(callRequest, sourceTransport, 0x0B);
                return false;
            }
        }

        #endregion

        #region X.25 Packet Type Handlers (RFC 1613)

        /// <summary>
        /// Send Call Accepted back to the source connection.
        /// Uses SourceConnection from callRequest to find the XOT connection.
        /// </summary>
        private bool SendCallAccepted(X25CallRequest originalCall, ITransportInterface transport)
        {
            _logger.LogDebug("Sending Call Accepted for call to {CalledAddress}", originalCall.CalledAddress);

            // Build Call Accepted with the source LCN and set GFI modulo 8 bit for XOT
            ushort responseLcn = originalCall.SourceLcn;
            var callAcceptedPacket = X25PacketBuilder.CreateCallAccepted(responseLcn);
            // Fix GFI: add modulo 8 bit (0x10) for XOT interop
            if (callAcceptedPacket.Length > 0)
                callAcceptedPacket[0] = (byte)((callAcceptedPacket[0] & 0xC0) | 0x10 | (callAcceptedPacket[0] & 0x0F));

            return SendXotPacket(originalCall, transport, callAcceptedPacket, "Call Accepted");
        }

        /// <summary>
        /// Send Clear Request back to the source connection
        /// </summary>
        private bool SendClearRequest(X25CallRequest originalCall, ITransportInterface transport, byte cause)
        {
            _logger.LogDebug("Sending Clear Request for call to {CalledAddress} with cause 0x{Cause:X2}",
                           originalCall.CalledAddress, cause);

            ushort responseLcn = originalCall.SourceLcn;
            var clearPacket = X25PacketBuilder.CreateClearRequest(responseLcn, cause);
            if (clearPacket.Length > 0)
                clearPacket[0] = (byte)((clearPacket[0] & 0xC0) | 0x10 | (clearPacket[0] & 0x0F));

            return SendXotPacket(originalCall, transport, clearPacket, "Clear Request");
        }

        /// <summary>
        /// Send an X.25 packet back to the originating connection
        /// </summary>
        private bool SendXotPacket(X25CallRequest originalCall, ITransportInterface transport, byte[] packet, string packetType)
        {
            // Try the SourceConnection first (XOT server connections)
            if (originalCall.SourceConnection is XotServerConnection xotConn)
            {
                return xotConn.SendPacket(packet);
            }

            if (transport is XotClientTransport xotClient)
            {
                return xotClient.SendX25Packet("", packet);
            }

            // Check the transport abstraction (Type), NOT the concrete SerialTransport class:
            // SerialTransport lives in the top-level Runtime assembly, so a concrete type-check here
            // would create an illegal X25 -> Runtime dependency. ITransportInterface.Type is the
            // architecture-safe discriminator and preserves identical behaviour.
            if (transport.Type == TransportType.Serial)
            {
                _runtime.X25Layer.SendPacket(packet, packet.Length);
                return true;
            }

            _logger.LogWarning("Cannot send {PacketType}: unknown transport type {Type}", packetType, transport.GetType().Name);
            return false;
        }

        #endregion

        #region Packet Creation

        /// <summary>
        /// Create Data packet (Data, DTE Data, DCE Data)
        /// RFC 1613 packet type for user data transfer
        /// </summary>
        public byte[]? CreateDataPacket(ushort lcn, byte[] userData, bool mBit = false, bool dBit = false)
        {
            try
            {
                if (userData == null || userData.Length == 0)
                {
                    _logger.LogWarning("Cannot create Data packet with empty user data");
                    return null;
                }

                // X.25 Data packet format:
                // [GFI & LCN (3 bytes)] [P(R)/P(S) sequence numbers (1 byte)] [User Data...]
                
                var packet = new byte[4 + userData.Length];
                int offset = 0;
                
                // General Format Identifier and Logical Channel Number
                byte gfi = 0x10; // Q=0, D=dBit, M=mBit, sequence=01 (modulo 8)
                if (dBit) gfi |= 0x40;
                if (mBit) gfi |= 0x20;
                
                packet[offset++] = gfi;
                packet[offset++] = (byte)((lcn >> 8) & 0x0F); // LCN high
                packet[offset++] = (byte)(lcn & 0xFF); // LCN low
                
                // P(R) and P(S) sequence numbers (placeholder - should track real sequence numbers)
                packet[offset++] = 0x00; // P(R)=0, P(S)=0
                
                // User data
                Array.Copy(userData, 0, packet, offset, userData.Length);
                
                return packet;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Data packet");
                return null;
            }
        }

        // TODO: Implement remaining RFC 1613 packet types:
        // - Clear Confirm (Clear Confirm, DTE Clear Confirmation, DCE Clear Confirmation) - 0x17
        // - Interrupt (Interrupt, DTE Interrupt, DCE Interrupt) - 0x23  
        // - Interrupt Confirm (DTE Interrupt Confirmation, DCE Interrupt Confirmation) - 0x27
        // - RR (RR, DTE RR, DCE RR) - 0x01
        // - RNR (RNR, DTE RNR, DCE RNR) - 0x05
        // - REJ (Reject, DTE REJ) - 0x09
        // - Reset Request (Reset, Reset Request, Reset Indication) - 0x1B
        // - Reset Confirm (DTE Reset Confirmation, DCE Reset Confirmation) - 0x1F
        // - Restart Request (Restart, Restart Request, Restart Indication) - 0xFB
        // - Restart Confirm (DTE Restart Confirmation, DCE Restart Confirmation) - 0xFF

        #endregion
    }
}