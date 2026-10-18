//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;

namespace RetroIO.Link
{
    /// <summary>
    /// Serial port link with HDLC byte-stuffing and LAPB.
    /// Implements both ILink and ILapbCapable interfaces.
    /// Each SerialLink has its own independent LAPB state machine.
    /// </summary>
    public class SerialLink : ILink, ILapbCapable, IDisposable
    {
        private readonly ILogger _logger;
        private readonly SerialLinkConfig _config;
        private readonly HdlcFramer _hdlcFramer;
        private readonly LapbStation _lapbInstance;

        private SerialPort? _serialPort;
        private LinkStatus _status = LinkStatus.Stopped;
        private readonly LinkStatistics _statistics;
        private readonly object _lock = new object();
        private bool _disposed = false;

        /// <summary>
        /// Create a new serial link
        /// </summary>
        public SerialLink(ILogger logger, SerialLinkConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            if (string.IsNullOrWhiteSpace(config.Name))
                throw new ArgumentException("Link name is required", nameof(config));

            // Create components
            _hdlcFramer = new HdlcFramer(logger, config.Name);
            _lapbInstance = new LapbStation(
                logger,
                config.Name,
                config.WindowSize,
                config.MaxRetries,
                config.T1TimeoutMs,
                config.T2TimeoutMs,
                config.T3TimeoutMs,
                config.IsPrimary);

            // Wire up events
            _hdlcFramer.FrameReceived += OnHdlcFrameReceived;
            _hdlcFramer.BytesToSend += OnHdlcBytesToSend;
            _lapbInstance.FrameToTransmit += OnLapbFrameToTransmit;
            _lapbInstance.FrameReceived += OnLapbFrameReceived;
            _lapbInstance.DataReceived += OnLapbDataReceived;
            _lapbInstance.StateChanged += OnLapbStateChanged;

            _statistics = new LinkStatistics
            {
                LinkName = config.Name,
                LinkType = LinkType.Serial,
                CurrentStatus = LinkStatus.Stopped
            };

            _logger.LogInformation("SerialLink[{Name}]: Created for {Port} at {Baud} baud",
                config.Name, config.PortName, config.BaudRate);
        }

        #region ILink Implementation

        /// <summary>
        /// Gets the unique name of this link, taken from the configuration.
        /// </summary>
        public string Name => _config.Name;
        /// <summary>
        /// Gets the type of this link, which is always <see cref="LinkType.Serial"/>.
        /// </summary>
        public LinkType Type => LinkType.Serial;
        /// <summary>
        /// Gets the current operational status of the link.
        /// </summary>
        public LinkStatus Status => _status;
        /// <summary>
        /// Gets the remote machine ID used for routing, taken from the configuration.
        /// </summary>
        public string RemoteMachineId => _config.RemoteMachineId;
        /// <summary>
        /// Gets the protocol mode configuration for this link, taken from the configuration.
        /// </summary>
        public ProtocolModeConfig ProtocolMode => _config.ProtocolMode;

        /// <summary>
        /// Starts the link by opening the configured serial port.
        /// </summary>
        /// <remarks>
        /// Only works from the Stopped status. On success the status becomes Active and the start time
        /// is recorded. It does not connect LAPB; call <see cref="Connect"/> for that.
        /// </remarks>
        /// <returns>
        /// <c>true</c> if the serial port was opened; <c>false</c> if the link was not stopped (a
        /// warning is logged) or the port could not be opened (the error is logged and the status is
        /// set to Error).
        /// </returns>
        public bool Start()
        {
            lock (_lock)
            {
                if (_status != LinkStatus.Stopped)
                {
                    _logger.LogWarning("SerialLink[{Name}]: Cannot start - status is {Status}",
                        _config.Name, _status);
                    return false;
                }

                try
                {
                    SetStatus(LinkStatus.Starting, "Opening serial port");

                    _serialPort = new SerialPort(
                        _config.PortName,
                        _config.BaudRate,
                        _config.Parity,
                        _config.DataBits,
                        _config.StopBits);

                    _serialPort.Handshake = _config.RtsCtsFlowControl
                        ? Handshake.RequestToSend
                        : Handshake.None;

                    _serialPort.DataReceived += OnSerialDataReceived;
                    _serialPort.ErrorReceived += OnSerialError;

                    _serialPort.Open();

                    _statistics.StartTime = DateTime.UtcNow;
                    SetStatus(LinkStatus.Active, "Serial port opened");

                    _logger.LogInformation("SerialLink[{Name}]: Started on {Port}",
                        _config.Name, _config.PortName);

                    return true;
                }
                catch (Exception ex)
                {
                    SetStatus(LinkStatus.Error, $"Failed to open serial port: {ex.Message}");
                    _logger.LogError(ex, "SerialLink[{Name}]: Failed to start", _config.Name);
                    return false;
                }
            }
        }

        /// <summary>
        /// Stops the link and closes the serial port.
        /// </summary>
        /// <remarks>
        /// Does nothing if the link is already Stopped or Stopping. Otherwise sends a LAPB disconnect
        /// if LAPB is in data transfer, closes and releases the port, resets LAPB and the HDLC framer,
        /// and sets the status to Stopped. An exception on the way is logged and sets the status to
        /// Error; it is not thrown on.
        /// </remarks>
        public void Stop()
        {
            lock (_lock)
            {
                if (_status == LinkStatus.Stopped || _status == LinkStatus.Stopping)
                    return;

                try
                {
                    SetStatus(LinkStatus.Stopping, "Closing serial port");

                    // Disconnect LAPB if connected
                    if (_lapbInstance.State == LapbState.DataTransfer)
                    {
                        _lapbInstance.Disconnect();
                    }

                    // Close serial port
                    if (_serialPort != null)
                    {
                        _serialPort.DataReceived -= OnSerialDataReceived;
                        _serialPort.ErrorReceived -= OnSerialError;

                        if (_serialPort.IsOpen)
                        {
                            _serialPort.Close();
                        }

                        _serialPort.Dispose();
                        _serialPort = null;
                    }

                    // Reset LAPB
                    _lapbInstance.Reset();
                    _hdlcFramer.Reset();

                    SetStatus(LinkStatus.Stopped, "Serial port closed");

                    _logger.LogInformation("SerialLink[{Name}]: Stopped", _config.Name);
                }
                catch (Exception ex)
                {
                    SetStatus(LinkStatus.Error, $"Error stopping: {ex.Message}");
                    _logger.LogError(ex, "SerialLink[{Name}]: Error stopping", _config.Name);
                }
            }
        }

        /// <summary>
        /// Sends one opaque payload over this link as a LAPB I-frame.
        /// </summary>
        /// <remarks>
        /// The link does not classify the payload as X.25 or SINTRAN; that happens above the link.
        /// </remarks>
        /// <param name="payload">
        /// The payload bytes to send.
        /// </param>
        /// <returns>
        /// <c>false</c> with a logged warning when the link is not Active or LAPB is not in data
        /// transfer; otherwise the result of the LAPB station send.
        /// </returns>
        public bool SendData(ReadOnlySpan<byte> payload)
        {
            if (_status != LinkStatus.Active)
            {
                _logger.LogWarning("SerialLink[{Name}]: Cannot send - not active", _config.Name);
                return false;
            }

            if (_lapbInstance.State != LapbState.DataTransfer)
            {
                _logger.LogWarning("SerialLink[{Name}]: Cannot send - LAPB not connected", _config.Name);
                return false;
            }

            // Send via LAPB I-frame; the payload is opaque (X.25 or SINTRAN decided above the link)
            return _lapbInstance.SendData(payload);
        }

        /// <summary>
        /// Gets the link statistics, with the current status copied in first.
        /// </summary>
        /// <returns>
        /// The live statistics object this link updates, not a copy.
        /// </returns>
        public LinkStatistics GetStatistics()
        {
            _statistics.CurrentStatus = _status;
            return _statistics;
        }

        /// <summary>
        /// Raised when LAPB delivers an I-frame payload on this link.
        /// </summary>
        /// <remarks>
        /// The payload is passed up opaque; protocol classification happens above the link.
        /// </remarks>
        public event LinkPayloadReceived? PayloadReceived;
        /// <summary>
        /// Raised when the link's operational status changes.
        /// </summary>
        /// <remarks>
        /// The event is raised only on a real transition. Setting the status the link already has
        /// does not raise it.
        /// </remarks>
        public event LinkStatusChanged? StatusChanged;

        #endregion

        #region ILapbCapable Implementation

        /// <summary>
        /// Gets the current state of this link's LAPB station.
        /// </summary>
        public LapbState LapbState => _lapbInstance.State;

        /// <summary>
        /// Gets the current LAPB sequence variables.
        /// </summary>
        /// <returns>
        /// V(S), V(R) and V(A) from the LAPB station.
        /// </returns>
        public (byte VS, byte VR, byte VA) GetSequenceNumbers()
        {
            return _lapbInstance.GetSequenceNumbers();
        }

        /// <summary>
        /// Starts LAPB link set-up (sends SABM).
        /// </summary>
        /// <remarks>
        /// Does nothing except log a warning when the link is not Active.
        /// </remarks>
        public void Connect()
        {
            if (_status != LinkStatus.Active)
            {
                _logger.LogWarning("SerialLink[{Name}]: Cannot connect LAPB - link not active", _config.Name);
                return;
            }

            _lapbInstance.Connect();
        }

        /// <summary>
        /// Starts LAPB link tear-down (sends DISC).
        /// </summary>
        public void Disconnect()
        {
            _lapbInstance.Disconnect();
        }

        /// <summary>
        /// Gets the LAPB station's counters and sequence variables.
        /// </summary>
        /// <returns>
        /// The LAPB statistics from the station.
        /// </returns>
        public LapbStatistics GetLapbStatistics()
        {
            return _lapbInstance.GetStatistics();
        }

        /// <summary>
        /// Raised when the LAPB station's state changes.
        /// </summary>
        public event EventHandler<LapbStateChangedEventArgs>? LapbStateChanged;

        /// <summary>
        /// Hands a raw frame to the LAPB station as if it had been received (for testing or simulation).
        /// </summary>
        /// <param name="frameData">
        /// Frame data (address, control and information fields).
        /// </param>
        /// <param name="length">
        /// Number of valid bytes in <paramref name="frameData"/>.
        /// </param>
        public void InjectFrame(byte[] frameData, int length)
        {
            _lapbInstance.ReceiveFrame(frameData, length);
        }

        /// <summary>
        /// Sends a raw LAPB frame as given, without the LAPB state machine seeing it.
        /// </summary>
        /// <param name="frame">
        /// The frame to send.
        /// </param>
        /// <param name="length">
        /// Number of valid bytes in <paramref name="frame"/>.
        /// </param>
        public void SendFrame(byte[] frame, int length)
        {
            _lapbInstance.SendFrame(frame, length);
        }

        /// <summary>
        /// Raised for every frame handed to the LAPB station.
        /// </summary>
        public event EventHandler<LapbFrameEventArgs>? FrameReceived;
        /// <summary>
        /// Raised for every frame the LAPB station puts on the wire, before it goes to the HDLC framer.
        /// </summary>
        public event EventHandler<LapbFrameEventArgs>? FrameTransmitted;

        #endregion

        #region Serial Port Events

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
                return;

            try
            {
                int bytesToRead = _serialPort.BytesToRead;
                if (bytesToRead > 0)
                {
                    byte[] buffer = new byte[bytesToRead];
                    int bytesRead = _serialPort.Read(buffer, 0, bytesToRead);

                    if (bytesRead > 0)
                    {
                        _statistics.BytesReceived += bytesRead;

                        // Feed to HDLC framer for unstuffing and frame detection
                        _hdlcFramer.ProcessBytes(buffer, bytesRead);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SerialLink[{Name}]: Error reading serial data", _config.Name);
                _statistics.Errors++;
            }
        }

        private void OnSerialError(object sender, SerialErrorReceivedEventArgs e)
        {
            _logger.LogError("SerialLink[{Name}]: Serial error: {Error}", _config.Name, e.EventType);
            _statistics.Errors++;
        }

        #endregion

        #region HDLC Framer Events

        private void OnHdlcFrameReceived(object? sender, FrameEventArgs e)
        {
            if (!e.FcsValid)
            {
                _logger.LogWarning("SerialLink[{Name}]: Frame FCS error discarded", _config.Name);
                _statistics.Errors++;
                return;
            }

            _statistics.FramesReceived++;

            // Pass to LAPB for processing
            _lapbInstance.ReceiveFrame(e.Data, e.Length);
        }

        private void OnHdlcBytesToSend(object? sender, byte[] stuffedData)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
                return;

            try
            {
                _serialPort.Write(stuffedData, 0, stuffedData.Length);
                _statistics.BytesTransmitted += stuffedData.Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SerialLink[{Name}]: Error writing to serial port", _config.Name);
                _statistics.Errors++;
            }
        }

        #endregion

        #region LAPB Events

        private void OnLapbFrameToTransmit(object? sender, LapbFrameEventArgs e)
        {
            _statistics.FramesTransmitted++;

            // Fire FrameTransmitted event for monitoring/testing
            FrameTransmitted?.Invoke(this, e);

            // Send through HDLC framer (adds FCS and byte-stuffing)
            _hdlcFramer.SendFrame(e.FrameData, e.Length);
        }

        private void OnLapbFrameReceived(object? sender, LapbFrameEventArgs e)
        {
            // Forward to external subscribers for monitoring/testing
            FrameReceived?.Invoke(this, e);
        }

        private void OnLapbDataReceived(object? sender, byte[] data)
        {
            // Deliver the payload opaque; X.25 vs SINTRAN classification happens above the link.
            _statistics.FramesReceived++;
            PayloadReceived?.Invoke(this, data, data.Length);
        }

        private void OnLapbStateChanged(object? sender, LapbStateChangedEventArgs e)
        {
            LapbStateChanged?.Invoke(this, e);

            _logger.LogInformation("SerialLink[{Name}]: LAPB state {OldState} -> {NewState}",
                _config.Name, e.OldState, e.NewState);
        }

        #endregion

        #region Private Methods

        private void SetStatus(LinkStatus newStatus, string reason)
        {
            var oldStatus = _status;
            _status = newStatus;
            _statistics.CurrentStatus = newStatus;

            // ILink promises events on real transitions only: a repeated status is not one.
            if (oldStatus == newStatus)
            {
                return;
            }

            StatusChanged?.Invoke(this, oldStatus, newStatus, reason);
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Stops the link and releases its LAPB station.
        /// </summary>
        /// <remarks>
        /// Calling it again does nothing.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            Stop();

            _lapbInstance.Dispose();

            _logger.LogInformation("SerialLink[{Name}]: Disposed", _config.Name);
        }

        #endregion
    }
}
