//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Framing;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;

namespace RetroIO.Link
{
    /// <summary>
    /// Transport-agnostic LAPB link built from a framing codec (SLIP or HDLC) over any byte transport.
    /// </summary>
    /// <remarks>
    /// Composes an <see cref="ILinkTransport"/> (TCP or serial) with a byte-stuffed framing codec, a
    /// <see cref="CodecLinkBridge"/> (which owns the FCS), a <see cref="LapbStation"/>, and a
    /// <see cref="ProtocolDetector"/> to present the standard <see cref="ILink"/> surface. This single link
    /// covers every combination of {SLIP, HDLC} framing over {TCP, serial} transport — SLIP over a serial
    /// line, HDLC/PPP over TCP, and so on — because framing and transport are independent seams. XOT is not
    /// handled here (it is length-prefixed with no LAPB).
    ///
    /// The class is its own <see cref="IFrameTransport"/>: the codec writes framed bytes back out through
    /// <see cref="IFrameTransport.Send"/>, which forwards to the underlying transport.
    /// </remarks>
    public sealed class CodecLink : ILink, IFrameTransport
    {
        private readonly ILogger _logger;
        private readonly CodecLinkConfig _config;
        private readonly ILinkTransport _transport;
        private readonly FrameCodecBase _codec;
        private readonly LapbStation _lapb;
        private readonly CodecLinkBridge _bridge;
        private readonly LinkStatistics _statistics;
        private readonly object _lock = new object();
        private LinkStatus _status = LinkStatus.Stopped;
        private bool _disposed;

        /// <summary>
        /// Raised when LAPB delivers an I-frame payload on this link.
        /// </summary>
        /// <remarks>
        /// The payload is passed up opaque; protocol classification happens above the link. Each
        /// delivery also adds one to the received frame count in the link statistics.
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

        /// <summary>
        /// Builds a codec-based link over the supplied transport.
        /// </summary>
        /// <param name="logger">
        /// Logger for the link and its sub-components.
        /// </param>
        /// <param name="config">
        /// Framing and LAPB configuration.
        /// </param>
        /// <param name="transport">
        /// The byte transport (TCP or serial) this link runs over.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        public CodecLink(ILogger logger, CodecLinkConfig config, ILinkTransport transport)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));

            _codec = config.Framing switch
            {
                FramingType.Slip => new SlipFrameCodec(config.Name, this),
                FramingType.Hdlc => new HdlcFrameCodec(config.Name, this),
                _ => throw new ArgumentOutOfRangeException(nameof(config), config.Framing, "Unsupported framing type.")
            };

            _lapb = new LapbStation(
                logger,
                config.Name,
                config.WindowSize,
                config.MaxRetries,
                config.T1TimeoutMs,
                config.T2TimeoutMs,
                config.T3TimeoutMs,
                config.IsPrimary);

            _bridge = new CodecLinkBridge(config.Name, _codec, _lapb, logger);

            _statistics = new LinkStatistics
            {
                LinkName = config.Name,
                LinkType = transport.LinkType,
                CurrentStatus = LinkStatus.Stopped
            };

            _transport.DataReceived += OnTransportDataReceived;
            _transport.Opened += OnTransportOpened;
            _transport.Closed += OnTransportClosed;
            _lapb.DataReceived += OnLapbDataReceived;
            _lapb.StateChanged += OnLapbStateChanged;
        }

        /// <summary>
        /// Gets the unique name of this link, taken from the configuration.
        /// </summary>
        public string Name => _config.Name;

        /// <summary>
        /// Gets the type of this link, taken from the underlying transport.
        /// </summary>
        public LinkType Type => _transport.LinkType;

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
        /// Starts the link by opening the underlying transport.
        /// </summary>
        /// <remarks>
        /// Sets the status to Starting first. The status becomes Active only when the transport
        /// reports that it is open; on the primary side LAPB then connects if auto-connect is set.
        /// </remarks>
        /// <returns>
        /// <c>true</c> if the transport open succeeded or was started; <c>false</c> if it failed at
        /// once, in which case the status is set to Error.
        /// </returns>
        public bool Start()
        {
            SetStatus(LinkStatus.Starting, "Starting");
            if (!_transport.Open())
            {
                SetStatus(LinkStatus.Error, "Transport failed to open");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Stops the link.
        /// </summary>
        /// <remarks>
        /// Sends a LAPB disconnect if LAPB is in data transfer, closes the transport, resets LAPB and
        /// the framing codec, and sets the status to Stopped.
        /// </remarks>
        public void Stop()
        {
            if (_lapb.State == LapbState.DataTransfer)
            {
                _lapb.Disconnect();
            }

            _transport.Close();
            _lapb.Reset();
            _codec.Reset();
            SetStatus(LinkStatus.Stopped, "Stopped");
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
        /// <c>false</c> with a logged warning when LAPB is not in data transfer; otherwise the result
        /// of the LAPB station send.
        /// </returns>
        public bool SendData(ReadOnlySpan<byte> payload)
        {
            if (_lapb.State != LapbState.DataTransfer)
            {
                _logger.LogWarning("CodecLink[{Name}]: cannot send - LAPB not connected", _config.Name);
                return false;
            }

            // Send via LAPB I-frame; the payload is opaque (X.25 or SINTRAN decided above the link)
            return _lapb.SendData(payload);
        }

        /// <summary>
        /// Gets the link statistics.
        /// </summary>
        /// <returns>
        /// The live statistics object this link updates, not a copy.
        /// </returns>
        public LinkStatistics GetStatistics()
        {
            return _statistics;
        }

        // IFrameTransport: the codec hands framed bytes down to the transport through here.
        void IFrameTransport.Send(ReadOnlySpan<byte> bytes)
        {
            _transport.Write(bytes);
            _statistics.BytesTransmitted += bytes.Length;
            _statistics.FramesTransmitted++;
        }

        /// <summary>
        /// Releases the link, its bridge, and its LAPB instance.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _transport.DataReceived -= OnTransportDataReceived;
            _transport.Opened -= OnTransportOpened;
            _transport.Closed -= OnTransportClosed;
            _lapb.DataReceived -= OnLapbDataReceived;
            _lapb.StateChanged -= OnLapbStateChanged;

            _bridge.Dispose();
            _lapb.Dispose();
            _transport.Dispose();
        }

        // Raw transport bytes -> framing codec.
        private void OnTransportDataReceived(string transportName, ReadOnlySpan<byte> data)
        {
            _statistics.BytesReceived += data.Length;
            _codec.ProcessBytes(data);
        }

        private void OnTransportOpened(string transportName)
        {
            SetStatus(LinkStatus.Active, "Transport open");

            // The primary side drives link establishment once the medium is ready.
            if (_config.IsPrimary && _config.LapbAutoConnect && _lapb.State == LapbState.Disconnected)
            {
                _lapb.Connect();
            }
        }

        private void OnTransportClosed(string transportName, string reason)
        {
            SetStatus(LinkStatus.Stopped, reason);
        }

        // LAPB delivered an I-frame payload: deliver it opaque; protocol classification happens above.
        private void OnLapbDataReceived(object? sender, byte[] data)
        {
            _statistics.FramesReceived++;
            PayloadReceived?.Invoke(this, data, data.Length);
        }

        private void OnLapbStateChanged(object? sender, LapbStateChangedEventArgs e)
        {
            _logger.LogInformation("CodecLink[{Name}]: LAPB state {OldState} -> {NewState}",
                _config.Name, e.OldState, e.NewState);
        }

        private void SetStatus(LinkStatus newStatus, string reason)
        {
            LinkStatus oldStatus;
            lock (_lock)
            {
                oldStatus = _status;
                _status = newStatus;
                _statistics.CurrentStatus = newStatus;
            }

            // ILink promises events on real transitions only: a repeated status is not one.
            if (oldStatus == newStatus)
            {
                return;
            }

            StatusChanged?.Invoke(this, oldStatus, newStatus, reason);
        }
    }
}
