//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Sintran;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// SINTRAN PVC Server - manages permanent virtual circuits for SINTRAN system protocols
    /// Handles routing information, XMSG messaging, and file transfer protocols
    /// </summary>
    public class SintranPvcServer : IProtocolModule
    {
        private readonly ILogger _logger;
        private readonly Dictionary<ushort, ISintranProtocolHandler> _pvcHandlers = new Dictionary<ushort, ISintranProtocolHandler>();
        private readonly Dictionary<string, Func<ISintranProtocolHandler>> _protocolFactories = new Dictionary<string, Func<ISintranProtocolHandler>>();
        private readonly object _handlersLock = new object();

        // Channels with an interrupt sent and not yet confirmed. X.25 allows one at a time per channel.
        // Guarded by _handlersLock.
        private readonly HashSet<ushort> _unconfirmedInterrupts = new HashSet<ushort>();
        private bool _initialized = false;

        // Terminal ID management for TAD protocol (auto-assignment)
        private readonly HashSet<int> _availableTerminalIds = new HashSet<int>();
        private readonly Dictionary<ushort, int> _lcnToTerminalId = new Dictionary<ushort, int>();
        private readonly int _maxTerminals = 8; // Default: 8 terminals (IDs 1-8)

        /// <summary>
        /// Gets the module name.
        /// </summary>
        /// <value>
        /// Always <c>"SINTRAN PVC Server"</c>.
        /// </value>
        public string Name => "SINTRAN PVC Server";
        /// <summary>
        /// Gets the module description.
        /// </summary>
        /// <value>
        /// Always <c>"Manages SINTRAN permanent virtual circuits for system protocols"</c>.
        /// </value>
        public string Description => "Manages SINTRAN permanent virtual circuits for system protocols";
        /// <summary>
        /// Gets the module version.
        /// </summary>
        /// <value>
        /// Always <c>"1.0.0"</c>.
        /// </value>
        public string Version => "1.0.0";

        /// <summary>
        /// Event raised when PVC server generates an X.25 packet
        /// </summary>
        public event EventHandler<X25PacketEventArgs>? PacketGenerated;

        /// <summary>
        /// Creates the server and registers the handler factories for ROUTING, TAD, XMSG and FILE.
        /// </summary>
        /// <param name="logger">
        /// The logger to write to.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="logger"/> is null.
        /// </exception>
        public SintranPvcServer(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            RegisterProtocolFactories();
        }

        /// <summary>
        /// Register factory methods for creating protocol handlers
        /// </summary>
        private void RegisterProtocolFactories()
        {
            _protocolFactories["ROUTING"] = () => new SintranRoutingHandler(_logger);
            _protocolFactories["TAD"] = () => new SintranTadHandler(_logger);
            _protocolFactories["XMSG"] = () => new SintranXmsgHandler(_logger);
            _protocolFactories["FILE"] = () => new SintranFileHandler(_logger);
        }

        /// <summary>
        /// Fills the terminal ID pool with IDs 1 to 8. Does nothing if already initialized.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;

            _logger.LogInformation("SintranPvcServer: Initializing SINTRAN PVC server");

            // Initialize terminal ID pool (1-8 by default)
            for (int i = 1; i <= _maxTerminals; i++)
            {
                _availableTerminalIds.Add(i);
            }
            _logger.LogInformation("SintranPvcServer: Terminal ID pool initialized ({Count} terminals available)", _maxTerminals);

            _initialized = true;
        }

        /// <summary>
        /// Disposes and removes all handlers. Does nothing if not initialized.
        /// </summary>
        public void Shutdown()
        {
            if (!_initialized) return;

            _logger.LogInformation("SintranPvcServer: Shutting down SINTRAN PVC server");
            
            lock (_handlersLock)
            {
                foreach (var handler in _pvcHandlers.Values)
                {
                    try
                    {
                        handler.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SintranPvcServer: Error disposing handler {Protocol}", handler.Protocol);
                    }
                }
                _pvcHandlers.Clear();
                _unconfirmedInterrupts.Clear();
            }

            _initialized = false;
        }

        /// <summary>
        /// Logs that the module was bound to a channel.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        public void OnBound(ushort lcn)
        {
            _logger.LogDebug("SintranPvcServer: Bound to LCN {LCN}", lcn);
        }

        /// <summary>
        /// Removes the handler of the channel, freeing its terminal ID if it is a TAD handler.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        public void OnUnbound(ushort lcn)
        {
            _logger.LogDebug("SintranPvcServer: Unbound from LCN {LCN}", lcn);

            lock (_handlersLock)
            {
                if (_pvcHandlers.TryGetValue(lcn, out var handler))
                {
                    _logger.LogInformation("SintranPvcServer: Removing handler {Protocol} from LCN {LCN}", handler.Protocol, lcn);

                    // Free terminal ID if this was a TAD handler
                    if (handler.Protocol == "TAD" && _lcnToTerminalId.TryGetValue(lcn, out var terminalId))
                    {
                        FreeTerminalId(lcn, terminalId);
                    }

                    handler.OnChannelClosed();
                    handler.Dispose();
                    _pvcHandlers.Remove(lcn);
                }

                // A cleared channel has no interrupt outstanding any more.
                _unconfirmedInterrupts.Remove(lcn);
            }
        }

        /// <summary>
        /// Called when an X.25 event occurs; dispatches the packet by its type.
        /// </summary>
        /// <remarks>
        /// Handles these packet types:
        ///  - Call Request: picks a protocol from the Call User Data and creates a handler for the channel.
        ///  - Data: passes the data to the handler of the channel.
        ///  - Clear Request and Reset Request: handled for the channel.
        /// Other packet types are logged and ignored, and an event without a packet does nothing.
        /// An exception while handling is logged, not thrown.
        /// </remarks>
        /// <param name="args">
        /// The event data, holding the received packet.
        /// </param>
        public void OnX25Event(X25PacketEventArgs args)
        {
            if (args.Packet == null) return;

            try
            {
                switch (args.Packet.PacketType)
                {
                    case X25PacketType.CallRequest:
                        HandleCallRequest(args);
                        break;

                    case X25PacketType.Data:
                        HandleData(args);
                        break;

                    case X25PacketType.ClearRequest:
                        HandleClearRequest(args);
                        break;

                    case X25PacketType.ResetRequest:
                        HandleResetRequest(args);
                        break;

                    case X25PacketType.InterruptConfirmation:
                        // The peer confirmed our interrupt; the next one may go (X.25 clause 4).
                        lock (_handlersLock)
                        {
                            _unconfirmedInterrupts.Remove(args.Packet.LCN);
                        }
                        break;

                    default:
                        _logger.LogDebug("SintranPvcServer: Ignoring packet type {PacketType} on LCN {LCN}", 
                            args.Packet.PacketType, args.Packet.LCN);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SintranPvcServer: Error handling X.25 event for LCN {LCN}", args.Packet.LCN);
            }
        }

        /// <summary>
        /// Handle incoming call request to establish PVC
        /// </summary>
        private void HandleCallRequest(X25PacketEventArgs args)
        {
            var packet = args.Packet;
            var lcn = packet.LCN;

            // Parse call request to get called address and Call User Data
            string calledAddress = "";
            byte[] callUserData = Array.Empty<byte>();
            try
            {
                if (packet.PacketType == X25PacketType.CallRequest)
                {
                    var callRequest = new X25CallRequest(packet.FullPacket);
                    calledAddress = callRequest.CalledAddress;
                    callUserData = callRequest.CallUserData;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("SintranPvcServer: Failed to parse call request on LCN {LCN}: {Error}", lcn, ex.Message);
            }

            _logger.LogInformation("SintranPvcServer: Processing Call Request for LCN {LCN}, Called Address: {CalledAddress}, CUD: {CallUserData}", 
                lcn, calledAddress, BitConverter.ToString(callUserData));

            // Determine protocol based on Call User Data (not address)
            var protocol = DetermineProtocolFromCallUserData(callUserData, calledAddress, lcn);
            if (protocol == null)
            {
                _logger.LogWarning("SintranPvcServer: Unable to determine protocol for address {CalledAddress}, CUD: {CallUserData}, LCN {LCN}", 
                    calledAddress, BitConverter.ToString(callUserData), lcn);
                SendCallClear(lcn, 0x01, 0x00); // Number busy
                return;
            }

            // Create protocol handler
            var handler = CreateProtocolHandler(protocol);
            if (handler == null)
            {
                _logger.LogError("SintranPvcServer: Failed to create handler for protocol {Protocol}", protocol);
                SendCallClear(lcn, 0x09, 0x00); // Out of order
                return;
            }

            // Register handler
            lock (_handlersLock)
            {
                _pvcHandlers[lcn] = handler;
                handler.ResponseReady += (sender, responseArgs) => HandleHandlerResponse(responseArgs);

                // Assign terminal ID if this is a TAD handler
                if (protocol == "TAD")
                {
                    var terminalId = AllocateTerminalId(lcn);
                    if (terminalId == -1)
                    {
                        _logger.LogWarning("SintranPvcServer: No available terminal IDs for LCN {LCN}, rejecting call", lcn);

                        // Remove handler from dictionary before rejecting call
                        _pvcHandlers.Remove(lcn);

                        SendCallClear(lcn, 0x01, 0x00); // Number busy
                        handler.Dispose();
                        return;
                    }
                    _logger.LogInformation("SintranPvcServer: Assigned Terminal ID {TerminalId} to LCN {LCN}", terminalId, lcn);
                }
            }

            // Initialize handler
            handler.OnChannelEstablished(lcn);

            // Accept the call to establish PVC
            SendCallAccepted(lcn);

            if (protocol == "TAD" && _lcnToTerminalId.TryGetValue(lcn, out var assignedTerminalId))
            {
                _logger.LogInformation("SintranPvcServer: Established TAD PVC on LCN {LCN} (Terminal ID {TerminalId})", lcn, assignedTerminalId);
            }
            else
            {
                _logger.LogInformation("SintranPvcServer: Established PVC on LCN {LCN} for protocol {Protocol}", lcn, protocol);
            }
        }

        /// <summary>
        /// Handle incoming data on established PVC
        /// </summary>
        private void HandleData(X25PacketEventArgs args)
        {
            var lcn = args.Packet.LCN;
            
            lock (_handlersLock)
            {
                if (_pvcHandlers.TryGetValue(lcn, out var handler))
                {
                    try
                    {
                        _logger.LogDebug("SintranPvcServer: Forwarding data to {Protocol} handler on LCN {LCN}", 
                            handler.Protocol, lcn);
                        handler.HandleData(args.Packet.Data);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SintranPvcServer: Error in {Protocol} handler on LCN {LCN}", 
                            handler.Protocol, lcn);
                    }
                }
                else
                {
                    _logger.LogWarning("SintranPvcServer: No handler found for data on LCN {LCN}", lcn);
                }
            }
        }

        /// <summary>
        /// Handle clear request on PVC
        /// </summary>
        private void HandleClearRequest(X25PacketEventArgs args)
        {
            var lcn = args.Packet.LCN;
            _logger.LogInformation("SintranPvcServer: Clear request received on LCN {LCN}", lcn);
            
            // PVCs should typically be persistent, but we'll handle clear requests gracefully
            OnUnbound(lcn);
            SendClearConfirmation(lcn);
        }

        /// <summary>
        /// Handle reset request on PVC
        /// </summary>
        private void HandleResetRequest(X25PacketEventArgs args)
        {
            var lcn = args.Packet.LCN;
            _logger.LogInformation("SintranPvcServer: Reset request received on LCN {LCN}", lcn);
            
            // Send reset confirmation
            SendResetConfirmation(lcn);
        }

        /// <summary>
        /// Handle response from protocol handler
        /// </summary>
        private void HandleHandlerResponse(SintranResponseEventArgs responseArgs)
        {
            if (responseArgs.Data.Length == 0) return;

            try
            {
                EmitDataPacket(responseArgs.LCN, responseArgs.Data, responseArgs.Timestamp);

                _logger.LogDebug("SintranPvcServer: Sent {Protocol} response on LCN {LCN}, {Bytes} bytes", 
                    responseArgs.Protocol, responseArgs.LCN, responseArgs.Data.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SintranPvcServer: Error sending response for {Protocol} on LCN {LCN}", 
                    responseArgs.Protocol, responseArgs.LCN);
            }
        }

        /// <summary>
        /// Builds one X.25 Data packet for the channel and raises <see cref="PacketGenerated"/> with it.
        /// Both the protocol handlers' responses and <see cref="SendX25Data"/> go through here.
        /// </summary>
        /// <remarks>
        /// The packet carries P(S) = 0 and P(R) = 0 every time. This server keeps no send window and no
        /// sequence counters (the "simplified sequencing for PVC" that the handler responses always
        /// had), so numbering the packets is left to whatever layer puts them on the wire.
        /// </remarks>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        /// <param name="userData">
        /// The user data field of the packet. It must not be empty.
        /// </param>
        /// <param name="timestamp">
        /// The time stamp for the packet and the event.
        /// </param>
        private void EmitDataPacket(ushort lcn, byte[] userData, DateTime timestamp)
        {
            var packetData = X25PacketBuilder.CreateDataPacket(
                lcn: lcn,
                ps: 0, // Simplified sequencing for PVC
                pr: 0,
                userData: userData
            );

            var packet = new X25Packet(packetData);
            packet.Direction = PacketDirection.Transmit;
            packet.Timestamp = timestamp;
            packet.LCN = lcn;

            PacketGenerated?.Invoke(this, new X25PacketEventArgs
            {
                Packet = packet,
                Direction = PacketDirection.Transmit,
                Timestamp = timestamp
            });
        }

        /// <summary>
        /// Determine protocol based on Call User Data
        /// SINTRAN systems use Call User Data to specify which service/server they want
        /// DREAMING UP SHIT, VALUES MAY NOT BE CORRECT - Need real SINTRAN Call User Data format from user
        /// </summary>
        private string? DetermineProtocolFromCallUserData(byte[] callUserData, string calledAddress, ushort lcn)
        {
            _logger.LogDebug("SintranPvcServer: Analyzing Call User Data for service detection: {CallUserData}", 
                BitConverter.ToString(callUserData));

            // TODO: Need proper SINTRAN Call User Data format specification
            // This is placeholder logic until the real format is provided

            // CRITICAL: Check called address FIRST (before CUD checks)
            // This allows empty CUD calls to be routed based on address pattern

            // Check if called address looks like a TAD terminal address
            // SINTRAN TAD servers might use specific address ranges
            if (calledAddress.StartsWith("100") || calledAddress.StartsWith("101") || calledAddress.StartsWith("102"))
            {
                _logger.LogInformation("SintranPvcServer: Called address {CalledAddress} matches TAD terminal pattern, using TAD protocol",
                    calledAddress);
                return "TAD";
            }

            // If CUD is empty and address doesn't match TAD pattern, default to ROUTING
            if (callUserData.Length == 0)
            {
                _logger.LogWarning("SintranPvcServer: No Call User Data provided and address doesn't match TAD pattern, defaulting to ROUTING");
                return "ROUTING";
            }

            // PLACEHOLDER LOGIC - DREAMING UP SHIT, VALUES MAY NOT BE CORRECT
            // Need real SINTRAN protocol specification for Call User Data format
            // This might be completely wrong - waiting for user guidance

            // Example placeholder detection (probably incorrect):
            // First byte might indicate service type?
            if (callUserData.Length >= 1)
            {
                switch (callUserData[0])
                {
                    case 0x01: return "ROUTING"; // Routing service request?
                    case 0x02: return "XMSG";    // XMSG service request?
                    case 0x03: return "FILE";    // File service request?
                    case 0xDD: return "TAD";     // TAD (terminal) service request (0xDD matches routing protocol ID)
                }
            }

            // Log unknown Call User Data for analysis
            _logger.LogWarning("SintranPvcServer: Unknown Call User Data format: {CallUserData}, defaulting to ROUTING",
                BitConverter.ToString(callUserData));

            // Default to ROUTING for analysis (most common SINTRAN protocol)
            return "ROUTING";
        }

        /// <summary>
        /// Create protocol handler instance
        /// </summary>
        private ISintranProtocolHandler? CreateProtocolHandler(string protocol)
        {
            if (_protocolFactories.TryGetValue(protocol, out var factory))
            {
                try
                {
                    var handler = factory();
                    handler.Initialize();
                    return handler;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SintranPvcServer: Error creating {Protocol} handler", protocol);
                }
            }
            else
            {
                _logger.LogWarning("SintranPvcServer: No factory registered for protocol {Protocol}", protocol);
            }
            
            return null;
        }

        /// <summary>
        /// Send Call Accepted packet
        /// </summary>
        private void SendCallAccepted(ushort lcn)
        {
            var packetData = X25PacketBuilder.CreateCallAccepted(lcn: lcn, facilities: null);
            var packet = new X25Packet(packetData);
            packet.Direction = PacketDirection.Transmit;
            packet.Timestamp = DateTime.UtcNow;
            packet.LCN = lcn;

            var eventArgs = new X25PacketEventArgs
            {
                Packet = packet,
                Direction = PacketDirection.Transmit,
                Timestamp = DateTime.UtcNow
            };

            PacketGenerated?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Send Clear Request packet
        /// </summary>
        private void SendCallClear(ushort lcn, byte clearingCause, byte diagnosticCode)
        {
            var packetData = X25PacketBuilder.CreateClearRequest(lcn, clearingCause, diagnosticCode);
            var packet = new X25Packet(packetData);
            packet.Direction = PacketDirection.Transmit;
            packet.Timestamp = DateTime.UtcNow;
            packet.LCN = lcn;

            var eventArgs = new X25PacketEventArgs
            {
                Packet = packet,
                Direction = PacketDirection.Transmit,
                Timestamp = DateTime.UtcNow
            };

            PacketGenerated?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Send Clear Confirmation packet
        /// </summary>
        private void SendClearConfirmation(ushort lcn)
        {
            var packetData = X25PacketBuilder.CreateClearConfirmation(lcn);
            var packet = new X25Packet(packetData);
            packet.Direction = PacketDirection.Transmit;
            packet.Timestamp = DateTime.UtcNow;
            packet.LCN = lcn;

            var eventArgs = new X25PacketEventArgs
            {
                Packet = packet,
                Direction = PacketDirection.Transmit,
                Timestamp = DateTime.UtcNow
            };

            PacketGenerated?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Send Reset Confirmation packet
        /// </summary>
        private void SendResetConfirmation(ushort lcn)
        {
            var packetData = X25PacketBuilder.CreateResetConfirmation(lcn);
            var packet = new X25Packet(packetData);
            packet.Direction = PacketDirection.Transmit;
            packet.Timestamp = DateTime.UtcNow;
            packet.LCN = lcn;

            var eventArgs = new X25PacketEventArgs
            {
                Packet = packet,
                Direction = PacketDirection.Transmit,
                Timestamp = DateTime.UtcNow
            };

            PacketGenerated?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Get active PVC handlers
        /// </summary>
        public Dictionary<ushort, ISintranProtocolHandler> GetActiveHandlers()
        {
            lock (_handlersLock)
            {
                return new Dictionary<ushort, ISintranProtocolHandler>(_pvcHandlers);
            }
        }

        /// <summary>
        /// Allocate a terminal ID for a TAD session
        /// Returns -1 if no terminal IDs are available
        /// </summary>
        private int AllocateTerminalId(ushort lcn)
        {
            lock (_handlersLock)
            {
                if (_availableTerminalIds.Count == 0)
                {
                    _logger.LogWarning("SintranPvcServer: No available terminal IDs (all {Max} terminals in use)", _maxTerminals);
                    return -1;
                }

                // Get the lowest available terminal ID (first-come-first-served)
                int terminalId = -1;
                foreach (var id in _availableTerminalIds)
                {
                    if (terminalId == -1 || id < terminalId)
                    {
                        terminalId = id;
                    }
                }

                _availableTerminalIds.Remove(terminalId);
                _lcnToTerminalId[lcn] = terminalId;

                _logger.LogInformation("SintranPvcServer: Allocated Terminal ID {TerminalId} for LCN {LCN} ({Available}/{Total} terminals available)",
                    terminalId, lcn, _availableTerminalIds.Count, _maxTerminals);

                return terminalId;
            }
        }

        /// <summary>
        /// Free a terminal ID when a TAD session ends
        /// </summary>
        private void FreeTerminalId(ushort lcn, int terminalId)
        {
            lock (_handlersLock)
            {
                if (_lcnToTerminalId.Remove(lcn))
                {
                    _availableTerminalIds.Add(terminalId);
                    _logger.LogInformation("SintranPvcServer: Freed Terminal ID {TerminalId} from LCN {LCN} ({Available}/{Total} terminals available)",
                        terminalId, lcn, _availableTerminalIds.Count, _maxTerminals);
                }
            }
        }

        /// <summary>
        /// Get the terminal ID assigned to an LCN (for TAD sessions)
        /// Returns -1 if LCN has no assigned terminal ID
        /// </summary>
        public int GetTerminalId(ushort lcn)
        {
            lock (_handlersLock)
            {
                return _lcnToTerminalId.TryGetValue(lcn, out var terminalId) ? terminalId : -1;
            }
        }

        /// <summary>
        /// Get combined statistics for all handlers
        /// </summary>
        public SintranPvcServerStats GetStats()
        {
            var stats = new SintranPvcServerStats
            {
                ActivePvcs = 0,
                TotalHandlers = 0,
                LastActivity = DateTime.MinValue,
                TotalTerminals = _maxTerminals,
                AvailableTerminals = 0
            };

            lock (_handlersLock)
            {
                stats.TotalHandlers = _pvcHandlers.Count;
                stats.AvailableTerminals = _availableTerminalIds.Count;

                foreach (var handler in _pvcHandlers.Values)
                {
                    var handlerStats = handler.GetStats();
                    if (handlerStats.IsActive)
                        stats.ActivePvcs++;

                    if (handlerStats.LastActivity > stats.LastActivity)
                        stats.LastActivity = handlerStats.LastActivity;

                    stats.TotalMessagesReceived += handlerStats.MessagesReceived;
                    stats.TotalMessagesSent += handlerStats.MessagesSent;
                    stats.TotalBytesReceived += handlerStats.BytesReceived;
                    stats.TotalBytesSent += handlerStats.BytesSent;
                }
            }

            return stats;
        }

        #region IProtocolModule Legacy Interface

        /// <summary>
        /// Wraps the data in a packet for the channel and passes it to <see cref="OnX25Event"/> as a received packet.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        /// <param name="data">
        /// The packet bytes.
        /// </param>
        public void OnX25Data(ushort lcn, byte[] data)
        {
            // Legacy interface - create synthetic event args
            var packet = new X25Packet(data);
            packet.LCN = lcn;
            var eventArgs = new X25PacketEventArgs
            {
                Packet = packet,
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            };
            OnX25Event(eventArgs);
        }

        /// <summary>
        /// Logs the reset. Nothing else is done.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        /// <param name="cause">
        /// The reset cause code.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic code.
        /// </param>
        public void OnX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            _logger.LogInformation("SintranPvcServer: Reset on LCN {LCN}, cause: {Cause}, diagnostic: {Diagnostic}", 
                lcn, cause, diagnostic);
        }

        /// <summary>
        /// Logs the clear and then unbinds the channel with <see cref="OnUnbound"/>.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        /// <param name="cause">
        /// The clear cause code.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic code.
        /// </param>
        public void OnX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            _logger.LogInformation("SintranPvcServer: Clear on LCN {LCN}, cause: {Cause}, diagnostic: {Diagnostic}", 
                lcn, cause, diagnostic);
            OnUnbound(lcn);
        }

        /// <summary>
        /// Logs the interrupt. Nothing else is done.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        /// <param name="data">
        /// The interrupt data.
        /// </param>
        public void OnX25Interrupt(ushort lcn, byte[] data)
        {
            _logger.LogInformation("SintranPvcServer: Interrupt on LCN {LCN}, {Bytes} bytes", lcn, data.Length);
        }

        /// <summary>
        /// Sends the bytes as the user data of one X.25 Data packet on an established channel.
        /// </summary>
        /// <remarks>
        /// The packet is raised through <see cref="PacketGenerated"/>, the same way the responses of
        /// the protocol handlers are. The bytes are sent as they are: this method does not know the
        /// SINTRAN protocol of the channel, so the caller builds any protocol framing. The packet
        /// has P(S) and P(R) set to 0, and its size is not checked against the size negotiated for
        /// the circuit, because this server keeps neither a send window nor the negotiated size; see
        /// <see cref="EmitDataPacket"/>.
        /// </remarks>
        /// <param name="lcn">
        /// The logical channel number. A handler must exist for it, which is made when a Call Request
        /// is accepted.
        /// </param>
        /// <param name="data">
        /// The user data field of the packet.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="data"/> is empty.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no circuit is established on <paramref name="lcn"/>.
        /// </exception>
        public void SendX25Data(ushort lcn, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0) throw new ArgumentException("A data packet needs at least one octet of user data", nameof(data));

            lock (_handlersLock)
            {
                if (!_pvcHandlers.ContainsKey(lcn))
                {
                    throw new InvalidOperationException($"No circuit is established on LCN {lcn}, so no data packet can be sent on it");
                }
            }

            EmitDataPacket(lcn, data, DateTime.UtcNow);
            _logger.LogDebug("SintranPvcServer: Sent {Bytes} bytes of data on LCN {LCN}", data.Length, lcn);
        }

        /// <summary>
        /// Sends a reset confirmation on the channel.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        /// <param name="cause">
        /// The reset cause code. Not used.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic code. Not used.
        /// </param>
        public void SendX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            SendResetConfirmation(lcn);
        }

        /// <summary>
        /// Sends a call clear on the channel.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        /// <param name="cause">
        /// The clear cause code.
        /// </param>
        /// <param name="diagnostic">
        /// The diagnostic code.
        /// </param>
        public void SendX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            SendCallClear(lcn, cause, diagnostic);
        }

        /// <summary>
        /// Sends one X.25 Interrupt packet on an established channel.
        /// </summary>
        /// <remarks>
        /// The format and the rules come from ITU-T X.25 (10/96) 4.3.7 and 5.3.2, the Interrupt packet (PTI 0x23) and
        /// its user data field of 1 to 32 octets. The packet is raised through
        /// <see cref="PacketGenerated"/>. X.25 allows one unconfirmed interrupt per channel: the next
        /// one may be sent only after an Interrupt Confirmation (PTI 0x27) arrived, which
        /// <see cref="OnX25Event"/> records. A clear of the channel forgets the interrupt. Whether the
        /// SINTRAN peer acts on an interrupt is not documented anywhere that was found; this method
        /// sends the packet and cannot tell what the peer does with it.
        /// </remarks>
        /// <param name="lcn">
        /// The logical channel number. A handler must exist for it.
        /// </param>
        /// <param name="data">
        /// The interrupt user data, 1 to 32 octets.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="data"/> is empty or longer than 32 octets.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no circuit is established on <paramref name="lcn"/>, or when the previous
        /// interrupt on it has not been confirmed.
        /// </exception>
        public void SendX25Interrupt(ushort lcn, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < 1 || data.Length > 32)
            {
                throw new ArgumentException($"Interrupt user data holds 1 to 32 octets, not {data.Length}", nameof(data));
            }

            lock (_handlersLock)
            {
                if (!_pvcHandlers.ContainsKey(lcn))
                {
                    throw new InvalidOperationException($"No circuit is established on LCN {lcn}, so no interrupt can be sent on it");
                }

                if (!_unconfirmedInterrupts.Add(lcn))
                {
                    throw new InvalidOperationException($"The previous interrupt on LCN {lcn} is not confirmed yet; X.25 allows one at a time");
                }
            }

            try
            {
                DateTime now = DateTime.UtcNow;
                var packet = new X25Packet(X25PacketBuilder.CreateInterrupt(lcn, data));
                packet.Direction = PacketDirection.Transmit;
                packet.Timestamp = now;
                packet.LCN = lcn;

                PacketGenerated?.Invoke(this, new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Transmit,
                    Timestamp = now
                });
            }
            catch
            {
                // Nothing went out, so nothing is waiting for a confirmation.
                lock (_handlersLock)
                {
                    _unconfirmedInterrupts.Remove(lcn);
                }
                throw;
            }

            _logger.LogDebug("SintranPvcServer: Sent interrupt of {Bytes} bytes on LCN {LCN}", data.Length, lcn);
        }

        /// <summary>
        /// Returns the module status built from the server statistics.
        /// </summary>
        /// <returns>
        /// The status. The error count is always 0.
        /// </returns>
        public ModuleStatus GetStatus()
        {
            var stats = GetStats();
            return new ModuleStatus
            {
                IsActive = _initialized,
                ActiveConnections = stats.ActivePvcs,
                TotalDataReceived = stats.TotalBytesReceived,
                TotalDataTransmitted = stats.TotalBytesSent,
                LastActivity = stats.LastActivity,
                ErrorCount = 0, // TODO: Track errors
                StatusMessage = $"Managing {stats.TotalHandlers} PVC handlers"
            };
        }

        #endregion
    }

    /// <summary>
    /// Statistics for SINTRAN PVC server
    /// </summary>
    public class SintranPvcServerStats
    {
        /// <summary>
        /// Gets or sets the number of handlers that report active.
        /// </summary>
        public int ActivePvcs { get; set; }
        /// <summary>
        /// Gets or sets the number of handlers.
        /// </summary>
        public int TotalHandlers { get; set; }
        /// <summary>
        /// Gets or sets the number of messages received, summed over all handlers.
        /// </summary>
        public int TotalMessagesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of messages sent, summed over all handlers.
        /// </summary>
        public int TotalMessagesSent { get; set; }
        /// <summary>
        /// Gets or sets the number of bytes received, summed over all handlers.
        /// </summary>
        public long TotalBytesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of bytes sent, summed over all handlers.
        /// </summary>
        public long TotalBytesSent { get; set; }
        /// <summary>
        /// Gets or sets the latest last-activity time of any handler.
        /// </summary>
        public DateTime LastActivity { get; set; }
        /// <summary>
        /// Gets or sets the number of terminal IDs not in use.
        /// </summary>
        public int AvailableTerminals { get; set; }
        /// <summary>
        /// Gets or sets the size of the terminal ID pool.
        /// </summary>
        public int TotalTerminals { get; set; }
    }
}