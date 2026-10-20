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
        private readonly Func<long>? _clock;
        private SerialLapbLink? _link;
        private System.Threading.Timer? _linkTimer;

        /// <summary>
        /// How often the LAPB timers are looked at, in milliseconds. The smallest timer is T1, which
        /// is seconds, so a quarter of a second is far finer than any timer needs.
        /// </summary>
        private const int LinkTickMs = 250;

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
        /// <param name="clock">
        /// A clock in milliseconds for the LAPB timers, used only when
        /// <see cref="SerialTransportConfig.X25Link"/> is set. Null uses a stopwatch. Tests pass their own.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="logger"/>, <paramref name="runtime"/> or <paramref name="config"/> is null.
        /// </exception>
        public SerialTransport(ILogger logger, ProtocolRuntime runtime, SerialTransportConfig config, Func<long>? clock = null)
        {
            _clock = clock;
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

                    StartLink();

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

                StopLink();

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
        /// <para>
        /// This class never raises it, on purpose, the same as <c>XotClientTransport</c>. A Call
        /// Request on a serial line is carried inside a LAPB I frame. When
        /// <see cref="SerialTransportConfig.X25Link"/> is set, this transport takes the line apart
        /// (HDLC framing, frame check sequence, LAPB) and raises <see cref="X25PacketReceived"/> with each
        /// X.25 packet. The runtime feeds those to the one X.25 engine it keeps for this line; the
        /// engine decodes the Call Request, raises the incoming call once, the runtime routes it
        /// once, and the engine puts the Call Accepted or the Clear Request back through
        /// <see cref="SendPacket"/>.
        /// </para>
        /// <para>
        /// Raising this event as well would hand the same call to the router a second time, and the
        /// runtime has no way to answer a call that comes in through this event (it has no engine
        /// to answer with). The raw bytes are still reported through <see cref="DataReceived"/>.
        /// </para>
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

        /// <summary>
        /// Raised for each X.25 packet that arrived in an I frame on the data link.
        /// </summary>
        /// <remarks>
        /// Raised only when <see cref="SerialTransportConfig.X25Link"/> is set and the transport is
        /// started. The data is one whole X.25 packet (GFI and logical channel first), with the HDLC
        /// flags, the escapes, the frame check sequence and the LAPB address and control already
        /// removed. The runtime feeds it to this line's X.25 engine, the same way it does for an XOT
        /// connection.
        /// </remarks>
        public event EventHandler<TransportDataEventArgs>? X25PacketReceived;

        /// <summary>
        /// Raised with true when the LAPB data link is set up in both directions and with false when
        /// it stops being usable.
        /// </summary>
        /// <remarks>
        /// The runtime drops the line's X.25 engine on false, which reports every call on it cleared;
        /// the next packet after the link is set up again builds a fresh engine. That is the same as
        /// it does for a link of the newer stack.
        /// </remarks>
        public event EventHandler<bool>? LinkStateChanged;

        /// <summary>
        /// Gets the role of this side of the line, or null when no complete X.25 data link configuration is set.
        /// </summary>
        public SerialX25LinkRole? LinkRole
        {
            get { return _config.X25Link != null && _config.X25Link.IsComplete ? _config.X25Link.Role : null; }
        }

        /// <summary>
        /// Gets a value indicating whether the LAPB data link is set up and packets can be sent.
        /// </summary>
        public bool IsLinkUp
        {
            get { return _link != null && _link.IsUp; }
        }

        /// <summary>
        /// Gets the data link of this transport, or null when it is not configured or not started.
        /// </summary>
        public SerialLapbLink? Link
        {
            get { return _link; }
        }

        /// <summary>
        /// Sends one X.25 packet to the peer in a LAPB I frame.
        /// </summary>
        /// <param name="packet">
        /// The whole X.25 packet.
        /// </param>
        /// <returns>
        /// True when LAPB took the packet. False when no X.25 data link is configured or started, or
        /// when the link is not set up.
        /// </returns>
        public bool SendPacket(byte[] packet)
        {
            SerialLapbLink? link = _link;
            return link != null && link.SendPacket(packet);
        }

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

            // The same bytes go to the data link, which takes the frames apart. The raw event above
            // is raised first and stays what it was.
            SerialLapbLink? link = _link;
            if (link != null)
            {
                try
                {
                    link.Feed(e.Data, e.Data.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in the data link of serial transport {Name}", Name);
                }
            }
        }

        /// <summary>
        /// Builds and starts the X.25 data link when the configuration asks for one.
        /// </summary>
        /// <remarks>
        /// With no <see cref="SerialTransportConfig.X25Link"/> nothing is built and the transport
        /// reports raw bytes only, as before. There is no role to fall back on: X.25 does not say
        /// which end of a serial cable is the DCE, so a transport that was not told does not guess.
        /// </remarks>
        private void StartLink()
        {
            SerialX25LinkOptions? options = _config.X25Link;
            if (options == null || !options.IsComplete)
            {
                _logger.LogInformation(
                    "Serial transport {Name} has no complete X25Link options (role and link set-up, no defaults): it reports raw bytes only and runs no LAPB or X.25",
                    Name);
                return;
            }

            if (_link != null)
            {
                return;
            }

            SerialLapbLink link = new SerialLapbLink(_logger, Name, options, WriteToLine, _clock);
            link.PacketReceived += OnLinkPacketReceived;
            link.LinkStateChanged += OnLinkStateChanged;
            _link = link;

            _linkTimer = new System.Threading.Timer(OnLinkTimer, null, LinkTickMs, LinkTickMs);
            _logger.LogInformation("Serial transport {Name} runs X.25 as the {Role}, link set-up {Setup}",
                Name, options.Role, options.InitiateLinkSetup == true ? "sent by this side" : "left to the peer");
            link.Start();
        }

        private void StopLink()
        {
            System.Threading.Timer? timer = _linkTimer;
            _linkTimer = null;
            timer?.Dispose();

            SerialLapbLink? link = _link;
            _link = null;
            if (link != null)
            {
                // Stop() raises LinkStateChanged(false) when the link was up, so the packet layer is
                // told the link is gone, the same as for any other loss. Packets are no longer wanted.
                link.PacketReceived -= OnLinkPacketReceived;
                link.Stop();
                link.Dispose();
            }
        }

        private void OnLinkTimer(object? state)
        {
            SerialLapbLink? link = _link;
            if (link == null)
            {
                return;
            }

            try
            {
                link.Tick();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ticking the data link of serial transport {Name}", Name);
            }
        }

        private bool WriteToLine(byte[] bytes)
        {
            bool sent = _runtime.PhysicalLayer.SendToSerialInterface(Name, bytes);
            if (sent)
            {
                _statistics.BytesTransmitted += bytes.Length;
            }

            return sent;
        }

        private void OnLinkPacketReceived(object? sender, byte[] packet)
        {
            try
            {
                X25PacketReceived?.Invoke(this, new TransportDataEventArgs
                {
                    Data = packet,
                    Length = packet.Length,
                    Transport = this
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in an X25PacketReceived handler of serial transport {Name}", Name);
            }
        }

        private void OnLinkStateChanged(object? sender, bool up)
        {
            _logger.LogInformation("Serial transport {Name}: data link is {State}", Name, up ? "up" : "down");
            LinkStateChanged?.Invoke(this, up);
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
        /// Role and LAPB values for running X.25 on this line, or null for none.
        /// </summary>
        /// <remarks>
        /// Null (the value when nothing is set) means the transport runs no LAPB and no X.25 on the
        /// line and only reports raw bytes. It is not read as DTE or DCE. <see cref="StationType"/>
        /// is not read for this: its values mix X.25's DTE and DCE with LAPB's primary and secondary,
        /// which are not the same thing, and the X.25 text assigns no meaning to "primary".
        /// </remarks>
        public SerialX25LinkOptions? X25Link { get; set; }

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