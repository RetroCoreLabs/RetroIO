using System;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;
using RetroIO.Link.Lapb.DeleteMe;

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
        private readonly LapbInstance _lapbInstance;

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
            _lapbInstance = new LapbInstance(
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

        public string Name => _config.Name;
        public LinkType Type => LinkType.Serial;
        public LinkStatus Status => _status;
        public string RemoteMachineId => _config.RemoteMachineId;
        public ProtocolModeConfig ProtocolMode => _config.ProtocolMode;

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

        public LinkStatistics GetStatistics()
        {
            _statistics.CurrentStatus = _status;
            return _statistics;
        }

        public event LinkPayloadReceived? PayloadReceived;
        public event LinkStatusChanged? StatusChanged;

        #endregion

        #region ILapbCapable Implementation

        public LapbState LapbState => _lapbInstance.State;

        public (byte VS, byte VR, byte VA) GetSequenceNumbers()
        {
            return _lapbInstance.GetSequenceNumbers();
        }

        public void Connect()
        {
            if (_status != LinkStatus.Active)
            {
                _logger.LogWarning("SerialLink[{Name}]: Cannot connect LAPB - link not active", _config.Name);
                return;
            }

            _lapbInstance.Connect();
        }

        public void Disconnect()
        {
            _lapbInstance.Disconnect();
        }

        public LapbStatistics GetLapbStatistics()
        {
            return _lapbInstance.GetStatistics();
        }

        public event EventHandler<LapbStateChangedEventArgs>? LapbStateChanged;

        public void InjectFrame(byte[] frameData, int length)
        {
            _lapbInstance.ReceiveFrame(frameData, length);
        }

        public void SendFrame(byte[] frame, int length)
        {
            _lapbInstance.SendFrame(frame, length);
        }

        public event EventHandler<LapbFrameEventArgs>? FrameReceived;
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

            StatusChanged?.Invoke(this, oldStatus, newStatus, reason);
        }

        #endregion

        #region IDisposable

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
