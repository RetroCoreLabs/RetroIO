using System;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;

namespace RetroIO.Link
{
    /// <summary>
    /// A test link for frame injection and simulation.
    /// Does not connect to any physical transport - all frames are processed locally.
    /// Used for unit testing and as a default link for backwards compatibility.
    /// </summary>
    public class TestLink : ILink, ILapbCapable, IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _name;
        private readonly LapbInstance _lapbInstance;

        private LinkStatus _status = LinkStatus.Stopped;
        private readonly LinkStatistics _statistics;
        private bool _disposed = false;

        /// <summary>
        /// Create a new test link
        /// </summary>
        public TestLink(ILogger logger, string name, byte windowSize = 7, byte maxRetries = 10,
            int t1TimeoutMs = 3000, int t2TimeoutMs = 1500, int t3TimeoutMs = 0, bool isPrimary = true)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _name = name ?? throw new ArgumentNullException(nameof(name));

            _lapbInstance = new LapbInstance(
                logger,
                name,
                windowSize,
                maxRetries,
                t1TimeoutMs,
                t2TimeoutMs,
                t3TimeoutMs,
                isPrimary);

            // Wire up events
            _lapbInstance.FrameToTransmit += OnLapbFrameToTransmit;
            _lapbInstance.FrameReceived += OnLapbFrameReceived;
            _lapbInstance.DataReceived += OnLapbDataReceived;
            _lapbInstance.StateChanged += OnLapbStateChanged;

            _statistics = new LinkStatistics
            {
                LinkName = name,
                LinkType = LinkType.TcpLapb, // Using TcpLapb as placeholder
                CurrentStatus = LinkStatus.Stopped
            };

            _logger.LogInformation("TestLink[{Name}]: Created", name);
        }

        #region ILink Implementation

        public string Name => _name;
        public LinkType Type => LinkType.TcpLapb;
        public LinkStatus Status => _status;
        public string RemoteMachineId => string.Empty;
        public ProtocolModeConfig ProtocolMode => new ProtocolModeConfig { Mode = ProtocolDetectionMode.X25Only };

        public bool Start()
        {
            if (_status != LinkStatus.Stopped)
            {
                return false;
            }

            _status = LinkStatus.Active;
            _statistics.StartTime = DateTime.UtcNow;
            _statistics.CurrentStatus = LinkStatus.Active;

            StatusChanged?.Invoke(this, LinkStatus.Stopped, LinkStatus.Active, "Test link started");

            _logger.LogInformation("TestLink[{Name}]: Started", _name);
            return true;
        }

        public void Stop()
        {
            if (_status == LinkStatus.Stopped)
                return;

            var oldStatus = _status;
            _status = LinkStatus.Stopped;
            _statistics.CurrentStatus = LinkStatus.Stopped;

            _lapbInstance.Reset();

            StatusChanged?.Invoke(this, oldStatus, LinkStatus.Stopped, "Test link stopped");

            _logger.LogInformation("TestLink[{Name}]: Stopped", _name);
        }

        public bool SendData(ReadOnlySpan<byte> payload)
        {
            if (_status != LinkStatus.Active)
                return false;

            if (_lapbInstance.State != LapbState.DataTransfer)
                return false;

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

        #region LAPB Events

        private void OnLapbFrameToTransmit(object? sender, LapbFrameEventArgs e)
        {
            _statistics.FramesTransmitted++;

            // Fire FrameTransmitted event for monitoring/testing
            FrameTransmitted?.Invoke(this, e);

            // In test mode, we don't actually send anywhere
            // The frame can be captured via FrameTransmitted event
        }

        private void OnLapbFrameReceived(object? sender, LapbFrameEventArgs e)
        {
            _statistics.FramesReceived++;

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

            _logger.LogInformation("TestLink[{Name}]: LAPB state {OldState} -> {NewState}",
                _name, e.OldState, e.NewState);
        }

        #endregion

        #region Test Helpers

        /// <summary>
        /// Directly set the link to DataTransfer state (for testing)
        /// </summary>
        public void SetDataTransferState()
        {
            // Simulate connection established by injecting UA response to our SABM
            // This is a simplified version for testing
            _lapbInstance.Connect(); // Will send SABM

            // Inject UA response with F=1
            byte[] ua = new byte[] { 0x01, 0x73 }; // Address=0x01, UA with F=1
            _lapbInstance.ReceiveFrame(ua, ua.Length);
        }

        /// <summary>
        /// Reset the LAPB state machine
        /// </summary>
        public void Reset()
        {
            _lapbInstance.Reset();
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

            _logger.LogInformation("TestLink[{Name}]: Disposed", _name);
        }

        #endregion
    }
}
