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
        private bool _initialized = false;

        // Terminal ID management for TAD protocol (auto-assignment)
        private readonly HashSet<int> _availableTerminalIds = new HashSet<int>();
        private readonly Dictionary<ushort, int> _lcnToTerminalId = new Dictionary<ushort, int>();
        private readonly int _maxTerminals = 8; // Default: 8 terminals (IDs 1-8)

        public string Name => "SINTRAN PVC Server";
        public string Description => "Manages SINTRAN permanent virtual circuits for system protocols";
        public string Version => "1.0.0";

        /// <summary>
        /// Event raised when PVC server generates an X.25 packet
        /// </summary>
        public event EventHandler<X25PacketEventArgs>? PacketGenerated;

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
            }

            _initialized = false;
        }

        public void OnBound(ushort lcn)
        {
            _logger.LogDebug("SintranPvcServer: Bound to LCN {LCN}", lcn);
        }

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
            }
        }

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
                // Create X.25 data packet
                var packetData = X25PacketBuilder.CreateDataPacket(
                    lcn: responseArgs.LCN,
                    ps: 0, // Simplified sequencing for PVC
                    pr: 0,
                    userData: responseArgs.Data
                );

                var packet = new X25Packet(packetData);
                packet.Direction = PacketDirection.Transmit;
                packet.Timestamp = responseArgs.Timestamp;

                packet.LCN = responseArgs.LCN;
                var eventArgs = new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Transmit,
                    Timestamp = responseArgs.Timestamp
                };

                PacketGenerated?.Invoke(this, eventArgs);
                
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

        public void OnX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            _logger.LogInformation("SintranPvcServer: Reset on LCN {LCN}, cause: {Cause}, diagnostic: {Diagnostic}", 
                lcn, cause, diagnostic);
        }

        public void OnX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            _logger.LogInformation("SintranPvcServer: Clear on LCN {LCN}, cause: {Cause}, diagnostic: {Diagnostic}", 
                lcn, cause, diagnostic);
            OnUnbound(lcn);
        }

        public void OnX25Interrupt(ushort lcn, byte[] data)
        {
            _logger.LogInformation("SintranPvcServer: Interrupt on LCN {LCN}, {Bytes} bytes", lcn, data.Length);
        }

        public void SendX25Data(ushort lcn, byte[] data)
        {
            // Not implemented - handlers send via events
            throw new NotImplementedException("Use handler ResponseReady events instead");
        }

        public void SendX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            SendResetConfirmation(lcn);
        }

        public void SendX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            SendCallClear(lcn, cause, diagnostic);
        }

        public void SendX25Interrupt(ushort lcn, byte[] data)
        {
            // Not commonly used in SINTRAN protocols
            throw new NotImplementedException("Interrupt not supported by SINTRAN PVC server");
        }

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
        public int ActivePvcs { get; set; }
        public int TotalHandlers { get; set; }
        public int TotalMessagesReceived { get; set; }
        public int TotalMessagesSent { get; set; }
        public long TotalBytesReceived { get; set; }
        public long TotalBytesSent { get; set; }
        public DateTime LastActivity { get; set; }
        public int AvailableTerminals { get; set; }
        public int TotalTerminals { get; set; }
    }
}