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
        /// Routes an incoming call that a packet-layer engine has already decoded, without putting
        /// anything on the wire.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the path used by every connection that owns an <c>X25Layer</c>: an XOT connection
        /// or a LinkManager link. The engine holds the circuit's state, so the engine sends the Call
        /// Accepted or the Clear Request, from the return value of this method. The older overload
        /// taking an <see cref="ITransportInterface"/> builds those two packets itself and writes
        /// them straight to the connection, which meant a call accepted through an engine was
        /// answered twice. It stays only for <c>SerialTransport</c>, which has no engine yet.
        /// </para>
        /// <para>
        /// The steps are the ones the transport overload does: find a local service for the called
        /// address, allocate the runtime's local channel number, map it to the connection and the
        /// engine's own channel, bind the service, and hand the call to it.
        /// </para>
        /// </remarks>
        /// <param name="callRequest">
        /// The decoded call, with <c>SourceConnection</c> and <c>SourceLcn</c> set to the connection
        /// it arrived on and the channel the engine gave it.
        /// </param>
        /// <param name="sourceName">
        /// The connection's name, for logs.
        /// </param>
        /// <returns>
        /// True when a local service took the call and the caller should accept it; false when the
        /// caller should clear it.
        /// </returns>
        public bool RouteIncomingCall(X25CallRequest callRequest, string sourceName)
        {
            _logger.LogInformation("Incoming call from {CallingAddress} to {CalledAddress} on {Source}",
                callRequest.CallingAddress, callRequest.CalledAddress, sourceName);

            try
            {
                IProtocolService? localService = _protocolServices.FindServiceForCall(
                    callRequest.CalledAddress, callRequest.CallUserData ?? Array.Empty<byte>());

                if (localService == null)
                {
                    _logger.LogWarning("No local service for {CalledAddress} on {Source}", callRequest.CalledAddress, sourceName);
                    return false;
                }

                ushort lcn = _runtime.FindAvailableLCN();
                if (lcn == 0)
                {
                    _logger.LogError("No free local channel for the call to {CalledAddress} on {Source}", callRequest.CalledAddress, sourceName);
                    return false;
                }

                // The mapping has to exist before the service runs: a service may answer inside
                // HandleIncomingCall, and its data is addressed by this local channel number.
                _runtime.RegisterXotConnection(lcn, callRequest.SourceConnection, callRequest.SourceLcn);
                _runtime.BindServiceToLcn(lcn, localService);

                bool accepted = localService.HandleIncomingCall(lcn, callRequest.CalledAddress,
                    callRequest.CallingAddress, callRequest.CallUserData ?? Array.Empty<byte>(),
                    callRequest.Facilities ?? Array.Empty<byte>());

                if (accepted)
                {
                    _logger.LogInformation("Service '{ServiceName}' took the call on local channel {LCN}", localService.ServiceName, lcn);
                    return true;
                }

                _logger.LogWarning("Service '{ServiceName}' refused the call on local channel {LCN}", localService.ServiceName, lcn);
                _runtime.UnregisterXotConnection(lcn);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error routing the incoming call to {CalledAddress} on {Source}", callRequest.CalledAddress, sourceName);
                return false;
            }
        }

        // The overload that took an ITransportInterface was removed on 2026-09-06 with C2 stage 3.
        // It looked up the service, allocated the channel and then built and sent its own Call
        // Accepted or Clear Request straight to the connection - a second packet layer beside the
        // engine, which answered the same call again. Its last live user was the XOT server, and
        // that has gone through the engine since the endpoints were built; the serial transport
        // never reached it, because nothing raises the event it hangs off. Its three private
        // senders went with it, and with them the last call into the retired engine from here.
        // A transport that needs to answer a call gets an X25Endpoint, the way XOT and the links
        // have one. RouteIncomingCall above is the shared half.

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