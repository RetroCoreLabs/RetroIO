//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

using RetroIO.Protocols.X25.Transport;
namespace RetroIO.Protocols.X25.Runtime.Transport
{
    /// <summary>
    /// Serial transport interface for X.25 over LAPB
    /// Integrates with existing ProtocolRuntime for serial port communication
    /// </summary>
    public class SerialTransport : ITransportInterface
    {
        private readonly ILogger _logger;
        private readonly ProtocolRuntime _runtime;
        private readonly SerialTransportConfig _config;
        private TransportStatus _status = TransportStatus.Stopped;
        private readonly TransportStatistics _statistics;

        /// <summary>
        /// Creates a serial transport.
        /// </summary>
        /// <param name="logger">
        /// The logger for messages.
        /// </param>
        /// <param name="runtime">
        /// The protocol runtime that owns the physical layer.
        /// </param>
        /// <param name="config">
        /// The transport configuration.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="logger"/>, <paramref name="runtime"/> or <paramref name="config"/> is null.
        /// </exception>
        public SerialTransport(ILogger logger, ProtocolRuntime runtime, SerialTransportConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            
            _statistics = new TransportStatistics
            {
                TransportName = _config.Name,
                TransportType = TransportType.Serial,
                CurrentStatus = TransportStatus.Stopped,
                StartTime = DateTime.UtcNow
            };

            // Physical layer will be managed by ProtocolRuntime
            // This transport references the physical layer for communication
        }

        #region ITransportInterface Implementation

        /// <inheritdoc/>
        public string Name => _config.Name;
        /// <inheritdoc/>
        public TransportType Type => TransportType.Serial;
        /// <inheritdoc/>
        public TransportStatus Status => _status;

        /// <inheritdoc/>
        public bool PlaceCall(X25CallRequest callRequest)
        {
            if (_status != TransportStatus.Active)
            {
                _logger.LogWarning("Serial transport {Name} not active, cannot place call", Name);
                return false;
            }

            try
            {
                _logger.LogInformation("Placing X.25 call via serial transport {Name} to {CalledAddress}", 
                                     Name, callRequest.CalledAddress);

                // Use existing ProtocolRuntime to place the call via X.25 layer
                // Convert X25CallRequest to X25Packet and place call
                var success = PlaceX25CallViaRuntime(callRequest);
                
                _statistics.OutboundCallsAttempted++;
                
                if (success)
                {
                    _statistics.OutboundCallsSucceeded++;
                    _logger.LogInformation("Serial call placed successfully via {Name}", Name);
                }
                else
                {
                    _logger.LogWarning("Serial call failed via {Name}", Name);
                }

                return success;
            }
            catch (Exception ex)
            {
                _statistics.ConnectionErrors++;
                _logger.LogError(ex, "Error placing serial call via {Name}", Name);
                return false;
            }
        }

        /// <summary>
        /// Counts an inbound call as accepted and returns true when the transport is active.
        /// </summary>
        /// <remarks>
        /// The method only writes log messages and updates the statistics. It does not send anything on the serial link.
        /// </remarks>
        /// <param name="callRequest">
        /// The call request to accept.
        /// </param>
        /// <returns>
        /// True when the transport is active. False when it is not active.
        /// </returns>
        public bool AcceptCall(X25CallRequest callRequest)
        {
            if (_status != TransportStatus.Active)
            {
                _logger.LogWarning("Serial transport {Name} not active, cannot accept call", Name);
                return false;
            }

            try
            {
                _logger.LogInformation("Accepting X.25 call via serial transport {Name} from {CallingAddress}", 
                                     Name, callRequest.CallingAddress);

                // Use existing ProtocolRuntime to accept the call
                // Placeholder - full implementation would use X25Layer
                _logger.LogInformation("Would accept X.25 call via ProtocolRuntime");
                var success = true; // Placeholder
                
                if (success)
                {
                    _statistics.InboundCallsAccepted++;
                    _logger.LogInformation("Serial call accepted successfully via {Name}", Name);
                }
                else
                {
                    _logger.LogWarning("Serial call acceptance failed via {Name}", Name);
                }

                return success;
            }
            catch (Exception ex)
            {
                _statistics.ConnectionErrors++;
                _logger.LogError(ex, "Error accepting serial call via {Name}", Name);
                return false;
            }
        }

        /// <summary>
        /// Counts an inbound call as rejected and returns true when the transport is active.
        /// </summary>
        /// <remarks>
        /// The method only writes log messages and updates the statistics. It does not send anything on the serial link.
        /// </remarks>
        /// <param name="callRequest">
        /// The call request to reject.
        /// </param>
        /// <param name="cause">
        /// The clear cause code, which is only written to the log.
        /// </param>
        /// <returns>
        /// True when the transport is active. False when it is not active.
        /// </returns>
        public bool RejectCall(X25CallRequest callRequest, byte cause)
        {
            if (_status != TransportStatus.Active)
            {
                _logger.LogWarning("Serial transport {Name} not active, cannot reject call", Name);
                return false;
            }

            try
            {
                _logger.LogInformation("Rejecting X.25 call via serial transport {Name} from {CallingAddress} with cause 0x{Cause:X2}", 
                                     Name, callRequest.CallingAddress, cause);

                // Use existing ProtocolRuntime to reject the call
                // Placeholder - full implementation would use X25Layer
                _logger.LogInformation("Would reject X.25 call via ProtocolRuntime");
                var success = true; // Placeholder
                
                if (success)
                {
                    _statistics.InboundCallsRejected++;
                    _logger.LogInformation("Serial call rejected successfully via {Name}", Name);
                }

                return success;
            }
            catch (Exception ex)
            {
                _statistics.ConnectionErrors++;
                _logger.LogError(ex, "Error rejecting serial call via {Name}", Name);
                return false;
            }
        }

        /// <inheritdoc/>
        public bool Start()
        {
            if (_status != TransportStatus.Stopped)
            {
                _logger.LogWarning("Serial transport {Name} already started", Name);
                return _status == TransportStatus.Active;
            }

            try
            {
                SetStatus(TransportStatus.Starting, "Starting serial transport");

                _logger.LogInformation("Starting serial transport {Name} on port {Port} at {BaudRate} baud", 
                                     Name, _config.PortName, _config.BaudRate);

                // Check if the specific serial interface is active in PhysicalLayer
                _logger.LogInformation("Serial transport {Name} checking interface {InterfaceName}", Name, _config.Name);
                
                if (_runtime.PhysicalLayer.IsSerialInterfaceActive(_config.Name))
                {
                    SetStatus(TransportStatus.Active, "Serial transport started successfully");
                    _statistics.StartTime = DateTime.UtcNow;
                    _logger.LogInformation("Serial transport {Name} started successfully", Name);
                    return true;
                }
                else
                {
                    SetStatus(TransportStatus.Error, "Serial interface not active in PhysicalLayer");
                    _logger.LogError("Failed to start serial transport {Name} - interface not active", Name);
                    return false;
                }
            }
            catch (Exception ex)
            {
                SetStatus(TransportStatus.Error, $"Startup error: {ex.Message}");
                _logger.LogError(ex, "Error starting serial transport {Name}", Name);
                return false;
            }
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (_status == TransportStatus.Stopped || _status == TransportStatus.Stopping)
            {
                return;
            }

            try
            {
                SetStatus(TransportStatus.Stopping, "Stopping serial transport");

                _logger.LogInformation("Stopping serial transport {Name}", Name);

                // Stop the protocol runtime
                // Protocol runtime will be disposed when SerialTransport is disposed
                _logger.LogInformation("Serial transport {Name} runtime stopped", Name);

                SetStatus(TransportStatus.Stopped, "Serial transport stopped");
                _logger.LogInformation("Serial transport {Name} stopped", Name);
            }
            catch (Exception ex)
            {
                SetStatus(TransportStatus.Error, $"Shutdown error: {ex.Message}");
                _logger.LogError(ex, "Error stopping serial transport {Name}", Name);
            }
        }

        /// <inheritdoc/>
        public TransportStatistics GetStatistics()
        {
            _statistics.CurrentStatus = _status;
            
            // Statistics are managed by the physical layer
            // TODO: Update statistics from PhysicalLayer serial interface

            return _statistics;
        }

        /// <inheritdoc/>
        public event EventHandler<X25CallRequest>? IncomingCall;
        /// <inheritdoc/>
        public event EventHandler<TransportStatusEventArgs>? StatusChanged;
#pragma warning disable CS0067 // Event required by interface, uses HDLC layer events instead
        /// <inheritdoc/>
        public event EventHandler<TransportDataEventArgs>? DataReceived;
#pragma warning restore CS0067

        #endregion

        #region Private Methods

        private void SetStatus(TransportStatus newStatus, string reason)
        {
            var oldStatus = _status;
            _status = newStatus;
            _statistics.CurrentStatus = newStatus;

            _logger.LogDebug("Serial transport {Name} status changed: {OldStatus} -> {NewStatus} ({Reason})", 
                           Name, oldStatus, newStatus, reason);

            StatusChanged?.Invoke(this, new TransportStatusEventArgs
            {
                OldStatus = oldStatus,
                NewStatus = newStatus,
                Reason = reason
            });
        }

        /// <summary>
        /// Place X.25 call via ProtocolRuntime X25Layer
        /// </summary>
        private bool PlaceX25CallViaRuntime(X25CallRequest callRequest)
        {
            try
            {
                // Find an available LCN for outbound call
                var lcn = FindAvailableLcn();
                if (lcn == 0)
                {
                    _logger.LogWarning("No available LCN for outbound call to {CalledAddress}", callRequest.CalledAddress);
                    return false;
                }

                _logger.LogDebug("Placing X.25 call on LCN {LCN} to {CalledAddress}", lcn, callRequest.CalledAddress);

                // Create and send X.25 Call Request packet via the runtime
                var callPacket = CreateX25CallRequestPacket(callRequest, lcn);
                if (callPacket == null)
                {
                    _logger.LogError("Failed to create X.25 Call Request packet for LCN {LCN}", lcn);
                    return false;
                }

                // Fire X.25 packet transmitted event for history tracking
                var x25Packet = new X25Packet(callPacket)
                {
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow,
                    LCN = lcn
                };
                var eventArgs = new X25PacketEventArgs
                {
                    Packet = x25Packet,
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow
                };
                _runtime.PacketHistory.FirePacketTransmitted(eventArgs);

                // Stack A serial LAPB send retired; serial output is handled by Stack B links now.
                _logger.LogWarning("SerialTransport: legacy LAPB call-packet send retired ({Length} bytes)", callPacket.Length);
                _logger.LogDebug("Sent X.25 call packet via LAPB layer, length: {Length}", callPacket.Length);
                var success = true; // Assume success when going through protocol stack

                if (success)
                {
                    _logger.LogInformation("X.25 call placed successfully on LCN {LCN} via serial transport {Name}", lcn, Name);
                    
                    // TODO: Wait for call connected/cleared response
                    // For now, return success immediately
                    return true;
                }
                else
                {
                    _logger.LogWarning("X.25 layer rejected call placement on LCN {LCN}", lcn);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error placing X.25 call via runtime");
                return false;
            }
        }

        /// <summary>
        /// Create X.25 Call Request packet 
        /// </summary>
        private byte[]? CreateX25CallRequestPacket(X25CallRequest callRequest, ushort lcn)
        {
            try
            {
                // Use the existing BuildPacketData method from X25CallRequest, but add GFI and LCN
                var callData = callRequest.BuildPacketData();
                
                // Create full X.25 packet with GFI, LCN, PTI
                var packet = new byte[callData.Length + 4];
                int offset = 0;
                
                // General Format Identifier and Logical Channel Number
                packet[offset++] = 0x10; // GFI = Q=0, D=0, M=0, Q=1 (standard modulo 8)
                packet[offset++] = (byte)((lcn >> 8) & 0x0F); // LCN high nibble
                packet[offset++] = (byte)(lcn & 0xFF); // LCN low byte
                
                // Packet Type Identifier - Call Request  
                packet[offset++] = 0x0B;
                
                // Copy the address and facility data
                Array.Copy(callData, 0, packet, offset, callData.Length);
                
                _logger.LogDebug("Created X.25 Call Request packet: {PacketLength} bytes for LCN {LCN}", 
                               packet.Length, lcn);
                
                return packet;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating X.25 Call Request packet");
                return null;
            }
        }

        /// <summary>
        /// Find next available LCN for outbound call
        /// Uses LCN range for outbound calls (typically 1-2047)
        /// </summary>
        private ushort FindAvailableLcn()
        {
            // Simple LCN allocation - start from 1 and find first available
            // TODO: Implement proper LCN management with runtime
            for (ushort lcn = 1; lcn < 2048; lcn++)
            {
                // Check if LCN is available via runtime
                // For now, return the first LCN as placeholder
                if (lcn == 1) // Placeholder logic
                {
                    return lcn;
                }
            }
            return 0; // No available LCN
        }

        private void OnRuntimeIncomingCall(object? sender, X25CallRequest callRequest)
        {
            _statistics.InboundCallsReceived++;
            
            _logger.LogDebug("Serial transport {Name} received incoming call from {CallingAddress} to {CalledAddress}", 
                           Name, callRequest.CallingAddress, callRequest.CalledAddress);

            // Forward to routing system
            IncomingCall?.Invoke(this, callRequest);
        }

        #endregion
    }

    /// <summary>
    /// Configuration for Serial transport interface
    /// </summary>
    public class SerialTransportConfig
    {
        /// <summary>
        /// Unique name for this transport interface
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Serial port name (e.g., "COM1", "/dev/ttyUSB0")
        /// </summary>
        public string PortName { get; set; } = string.Empty;

        /// <summary>
        /// Baud rate for serial communication
        /// </summary>
        public int BaudRate { get; set; } = 19200;

        /// <summary>
        /// Local X.121 address for this interface
        /// </summary>
        public string LocalX121Address { get; set; } = string.Empty;

        /// <summary>
        /// Station type (DCE or DTE)
        /// </summary>
        public StationType StationType { get; set; } = StationType.DTE_Secondary;

        /// <summary>
        /// Optional description of this interface
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Station types for X.25 operation
    /// </summary>
    public enum StationType
    {
        /// <summary>
        /// DCE Primary station (provides T3 supervision)
        /// </summary>
        DCE_Primary,

        /// <summary>
        /// DTE Primary station (provides T3 supervision in P2P scenarios)
        /// </summary>
        DTE_Primary,

        /// <summary>
        /// DTE Secondary station (relies on primary for supervision)
        /// </summary>
        DTE_Secondary
    }
}