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

        /// <summary>
        /// Gets the unique name of this transport interface.
        /// </summary>
        /// <value>
        /// The name from the configuration.
        /// </value>
        public string Name => _config.Name;
        /// <summary>
        /// Gets the type of transport.
        /// </summary>
        /// <value>
        /// Always <see cref="TransportType.Serial"/>.
        /// </value>
        public TransportType Type => TransportType.Serial;
        /// <summary>
        /// Gets the current status of the transport.
        /// </summary>
        public TransportStatus Status => _status;

        /// <summary>
        /// Always throws, because the serial send path of this transport is not implemented.
        /// </summary>
        /// <remarks>
        /// The old LAPB send over the serial link was retired and nothing replaced it in this class:
        /// serial output belongs to the links of the newer stack. Earlier code built a Call Request
        /// packet on a fixed LCN 1, reported it to the packet history as transmitted, sent nothing, and
        /// returned <c>true</c>. That hid the missing send path, so the call now fails loudly.
        /// </remarks>
        /// <param name="callRequest">
        /// The X.25 call request that would be placed.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown: placing a call on the serial transport is not implemented.
        /// </exception>
        public bool PlaceCall(X25CallRequest callRequest)
        {
            throw new NotSupportedException(
                "SerialTransport.PlaceCall is not implemented: the serial send path was retired and no Call Request is put on the serial link.");
        }

        /// <summary>
        /// Always throws, because answering a call with a Call Accepted packet is not implemented.
        /// </summary>
        /// <remarks>
        /// Earlier code logged "Would accept", counted the call as accepted and returned <c>true</c>
        /// without sending anything on the serial link; that hid the missing mechanism.
        /// </remarks>
        /// <param name="callRequest">
        /// The call request that would be accepted.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown: accepting an incoming call on the serial transport is not implemented.
        /// </exception>
        public bool AcceptCall(X25CallRequest callRequest)
        {
            throw new NotSupportedException(
                "SerialTransport.AcceptCall is not implemented: no Call Accepted packet is put on the serial link.");
        }

        /// <summary>
        /// Always throws, because clearing a call with a Clear Request packet is not implemented.
        /// </summary>
        /// <remarks>
        /// Earlier code logged "Would reject", counted the call as rejected and returned <c>true</c>
        /// without sending anything on the serial link; that hid the missing mechanism.
        /// </remarks>
        /// <param name="callRequest">
        /// The call request that would be rejected.
        /// </param>
        /// <param name="cause">
        /// The clear cause code that would be sent.
        /// </param>
        /// <returns>
        /// Never returns.
        /// </returns>
        /// <exception cref="NotSupportedException">
        /// Always thrown: rejecting an incoming call on the serial transport is not implemented.
        /// </exception>
        public bool RejectCall(X25CallRequest callRequest, byte cause)
        {
            throw new NotSupportedException(
                "SerialTransport.RejectCall is not implemented: no Clear Request packet is put on the serial link.");
        }

        /// <summary>
        /// Starts the transport interface.
        /// </summary>
        /// <remarks>
        /// The transport becomes active only when the physical layer of the runtime reports the serial
        /// interface with the configured name as active; otherwise the status becomes Error. When the
        /// transport is not stopped, nothing is changed. Any exception is logged and sets the status
        /// to Error.
        /// </remarks>
        /// <returns>
        /// <c>true</c> when the transport is active after the call; otherwise <c>false</c>.
        /// </returns>
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
                    // Listen for the bytes that arrive on this interface. Subscribed once: a second
                    // Start after Stop must not deliver every frame twice.
                    _runtime.PhysicalLayer.DataReceived -= OnPhysicalLayerDataReceived;
                    _runtime.PhysicalLayer.DataReceived += OnPhysicalLayerDataReceived;

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

        /// <summary>
        /// Stops the transport interface.
        /// </summary>
        /// <remarks>
        /// Only changes the status to Stopping and then Stopped, raising <see cref="StatusChanged"/>
        /// for each; the runtime and the physical layer are not touched. Does nothing when the
        /// transport is already stopped or stopping.
        /// </remarks>
        public void Stop()
        {
            if (_status == TransportStatus.Stopped || _status == TransportStatus.Stopping)
            {
                return;
            }

            try
            {
                SetStatus(TransportStatus.Stopping, "Stopping serial transport");

                // A stopped transport delivers nothing.
                _runtime.PhysicalLayer.DataReceived -= OnPhysicalLayerDataReceived;

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

        /// <summary>
        /// Gets the statistics of this transport.
        /// </summary>
        /// <remarks>
        /// The counters are kept by this class; they are not read from the physical layer.
        /// </remarks>
        /// <returns>
        /// The statistics object itself, not a copy, with its current status updated.
        /// </returns>
        public TransportStatistics GetStatistics()
        {
            _statistics.CurrentStatus = _status;
            
            // Statistics are managed by the physical layer
            // TODO: Update statistics from PhysicalLayer serial interface

            return _statistics;
        }

        /// <summary>
        /// Raised when an incoming call is received.
        /// </summary>
        /// <remarks>
        /// This class never raises it, on purpose. A Call Request on a serial line is carried inside a
        /// LAPB I frame, so reading it needs HDLC deframing, the frame check sequence and the LAPB
        /// sequence numbers and acknowledgements, and the answer (Call Accepted or Clear) has to
        /// go back through the same LAPB link. None of that is in this class: serial links with LAPB
        /// belong to the links of the newer stack, and the runtime says this transport gets its X.25
        /// engine later. Decoding frames here without LAPB would report calls that
        /// could not be answered. The raw bytes are reported through <see cref="DataReceived"/>.
        /// </remarks>
#pragma warning disable CS0067 // Required by the interface; see the remarks above for why it is not raised.
        public event EventHandler<X25CallRequest>? IncomingCall;
#pragma warning restore CS0067
        /// <summary>
        /// Raised each time the transport status changes.
        /// </summary>
        public event EventHandler<TransportStatusEventArgs>? StatusChanged;
        /// <summary>
        /// Raised when bytes arrive on the serial interface of this transport.
        /// </summary>
        /// <remarks>
        /// The bytes are what the serial line delivered, unchanged: HDLC flags, escapes and the frame
        /// check sequence are still in them. One event is one read from the line, which may be part
        /// of a frame or several frames. It is raised only while the transport is started, and only
        /// for the interface whose name this transport carries.
        /// </remarks>
        public event EventHandler<TransportDataEventArgs>? DataReceived;

        #endregion

        #region Private Methods

        /// <summary>
        /// Turns the bytes the physical layer got on this transport's interface into
        /// <see cref="DataReceived"/>; bytes from any other interface or from TCP are ignored.
        /// </summary>
        private void OnPhysicalLayerDataReceived(object? sender, PhysicalLayerDataEventArgs e)
        {
            if (e.TransportType != PhysicalTransportType.Serial ||
                !string.Equals(e.InterfaceName, Name, StringComparison.Ordinal))
            {
                return;
            }

            _statistics.BytesReceived += e.Data.Length;

            try
            {
                DataReceived?.Invoke(this, new TransportDataEventArgs
                {
                    Data = e.Data,
                    Length = e.Data.Length,
                    Transport = this
                });
            }
            catch (Exception ex)
            {
                // A handler that throws must not stop the read thread of the serial port.
                _logger.LogError(ex, "Error in a DataReceived handler of serial transport {Name}", Name);
            }
        }

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