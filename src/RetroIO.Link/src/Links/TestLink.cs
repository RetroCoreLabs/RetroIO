//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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
        private readonly LapbStation _lapbInstance;

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

            _lapbInstance = new LapbStation(
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

        /// <summary>
        /// Gets the name given to this test link.
        /// </summary>
        public string Name => _name;
        /// <summary>
        /// Gets the type of this link, which is always <see cref="LinkType.TcpLapb"/> for the test link.
        /// </summary>
        public LinkType Type => LinkType.TcpLapb;
        /// <summary>
        /// Gets the current operational status of the link.
        /// </summary>
        public LinkStatus Status => _status;
        /// <summary>
        /// Gets the remote machine ID, which is always an empty string for the test link.
        /// </summary>
        public string RemoteMachineId => string.Empty;
        /// <summary>
        /// Gets a new protocol mode configuration set to <see cref="ProtocolDetectionMode.X25Only"/>
        /// on every read.
        /// </summary>
        public ProtocolModeConfig ProtocolMode => new ProtocolModeConfig { Mode = ProtocolDetectionMode.X25Only };

        /// <summary>
        /// Starts the test link by setting its status to Active; no transport is opened.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the link was Stopped and is now Active; <c>false</c> if it was not Stopped.
        /// </returns>
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

        /// <summary>
        /// Stops the test link: sets the status to Stopped and resets the LAPB station.
        /// </summary>
        /// <remarks>
        /// Does nothing if the link is already Stopped. No LAPB disconnect is sent.
        /// </remarks>
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

        /// <summary>
        /// Sends one opaque payload as a LAPB I-frame.
        /// </summary>
        /// <remarks>
        /// The frame goes nowhere; it can only be seen through <see cref="FrameTransmitted"/>.
        /// </remarks>
        /// <param name="payload">
        /// The payload bytes to send.
        /// </param>
        /// <returns>
        /// <c>false</c> when the link is not Active or LAPB is not in data transfer (nothing is
        /// logged); otherwise the result of the LAPB station send.
        /// </returns>
        public bool SendData(ReadOnlySpan<byte> payload)
        {
            if (_status != LinkStatus.Active)
                return false;

            if (_lapbInstance.State != LapbState.DataTransfer)
                return false;

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
        /// Raised when <see cref="Start"/> or <see cref="Stop"/> changes the link's status.
        /// </summary>
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
        /// Starts LAPB link set-up (sends SABM), whatever the link status is.
        /// </summary>
        public void Connect()
        {
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
        /// Hands a raw LAPB frame to the station to transmit as given, without the LAPB state machine
        /// seeing it.
        /// </summary>
        /// <remarks>
        /// The test link has no transport; the frame can only be seen through
        /// <see cref="FrameTransmitted"/>.
        /// </remarks>
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
        /// Raised for every frame the LAPB station puts out. The test link sends it nowhere else.
        /// </summary>
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

            _logger.LogInformation("TestLink[{Name}]: Disposed", _name);
        }

        #endregion
    }
}
