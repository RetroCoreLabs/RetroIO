//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Link.Models;

namespace RetroIO.Link
{
    /// <summary>
    /// Link types supported by the system
    /// </summary>
    public enum LinkType
    {
        /// <summary>
        /// Serial port link with LAPB
        /// </summary>
        Serial,
        /// <summary>
        /// TCP link with HDLC byte-stuffing and LAPB
        /// </summary>
        TcpLapb,
        /// <summary>
        /// XOT link (RFC 1613) - no LAPB
        /// </summary>
        Xot
    }

    /// <summary>
    /// Link operational status
    /// </summary>
    public enum LinkStatus
    {
        /// <summary>
        /// Link is stopped/not running
        /// </summary>
        Stopped,
        /// <summary>
        /// Link is starting up
        /// </summary>
        Starting,
        /// <summary>
        /// Link is active and operational
        /// </summary>
        Active,
        /// <summary>
        /// Link is stopping
        /// </summary>
        Stopping,
        /// <summary>
        /// Link is in error state
        /// </summary>
        Error
    }

    /// <summary>
    /// Statistics for a link
    /// </summary>
    public class LinkStatistics
    {
        /// <summary>
        /// Gets or sets the name of the link.
        /// </summary>
        public string LinkName { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the type of the link.
        /// </summary>
        public LinkType LinkType { get; set; }
        /// <summary>
        /// Gets or sets the operational status of the link.
        /// </summary>
        public LinkStatus CurrentStatus { get; set; }
        /// <summary>
        /// Gets or sets the time the link was started.
        /// </summary>
        public DateTime StartTime { get; set; }
        /// <summary>
        /// Gets or sets the number of bytes sent.
        /// </summary>
        public long BytesTransmitted { get; set; }
        /// <summary>
        /// Gets or sets the number of bytes received.
        /// </summary>
        public long BytesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of frames sent.
        /// </summary>
        public long FramesTransmitted { get; set; }
        /// <summary>
        /// Gets or sets the number of frames received.
        /// </summary>
        public long FramesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of errors.
        /// </summary>
        public long Errors { get; set; }
    }

    /// <summary>
    /// Handler for an opaque payload delivered up from a link (a LAPB I-frame information field, or an
    /// X.25 packet for XOT links).
    /// </summary>
    /// <param name="link">
    /// The link that received the payload.
    /// </param>
    /// <param name="payload">
    /// The opaque payload bytes (not interpreted by the link). The buffer may be reused by the link
    /// after the handler returns; handlers that retain the payload must copy it inside the callback.
    /// </param>
    /// <param name="length">
    /// Number of valid bytes in <paramref name="payload"/>.
    /// </param>
    public delegate void LinkPayloadReceived(ILink link, byte[] payload, int length);

    /// <summary>
    /// Handler for a link operational status transition.
    /// </summary>
    /// <param name="link">
    /// The link whose status changed.
    /// </param>
    /// <param name="oldStatus">
    /// The status before the transition.
    /// </param>
    /// <param name="newStatus">
    /// The status after the transition.
    /// </param>
    /// <param name="reason">
    /// Human-readable reason for the transition.
    /// </param>
    public delegate void LinkStatusChanged(ILink link, LinkStatus oldStatus, LinkStatus newStatus, string reason);

    /// <summary>
    /// Base interface for all link types (Serial, TCP-LAPB, XOT).
    /// A link is protocol-agnostic: it delivers an opaque payload upward and does not classify it as
    /// X.25 or SINTRAN. Protocol discrimination happens above the link (in the composition root).
    /// </summary>
    public interface ILink : IDisposable
    {
        /// <summary>
        /// Unique name identifying this link
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Type of link (Serial, TcpLapb, Xot)
        /// </summary>
        LinkType Type { get; }

        /// <summary>
        /// Current operational status
        /// </summary>
        LinkStatus Status { get; }

        /// <summary>
        /// Remote machine ID (for routing purposes)
        /// </summary>
        string RemoteMachineId { get; }

        /// <summary>
        /// Protocol mode configuration for this link
        /// </summary>
        ProtocolModeConfig ProtocolMode { get; }

        /// <summary>
        /// Start the link
        /// </summary>
        /// <returns>
        /// True if started successfully
        /// </returns>
        bool Start();

        /// <summary>
        /// Stop the link
        /// </summary>
        void Stop();

        /// <summary>
        /// Sends one opaque L3 payload (an information field) over this link. The link does not
        /// classify the payload as X.25 or SINTRAN — protocol discrimination happens above the link.
        /// The implementation copies the payload before returning, so the caller's buffer is free
        /// for reuse as soon as the call returns.
        /// </summary>
        /// <param name="payload">
        /// The payload bytes to send.
        /// </param>
        /// <returns>
        /// True when the link took the payload: it was sent, or it was queued behind a full send
        /// window and will be sent as the window opens. False when the link is not Active or the
        /// payload cannot be sent, for example when there is no connection (logged, never thrown).
        /// </returns>
        bool SendData(ReadOnlySpan<byte> payload);

        /// <summary>
        /// Get current link statistics
        /// </summary>
        LinkStatistics GetStatistics();

        /// <summary>
        /// Raised when an opaque payload is received on this link (protocol classification happens above).
        /// </summary>
        event LinkPayloadReceived PayloadReceived;

        /// <summary>
        /// Raised when the link's operational status changes (fires only on actual transitions).
        /// </summary>
        event LinkStatusChanged StatusChanged;
    }
}
