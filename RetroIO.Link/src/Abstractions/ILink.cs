using System;
using RetroIO.Link.Models;

namespace RetroIO.Link
{
    /// <summary>
    /// Link types supported by the system
    /// </summary>
    public enum LinkType
    {
        /// <summary>Serial port link with LAPB</summary>
        Serial,
        /// <summary>TCP link with HDLC byte-stuffing and LAPB</summary>
        TcpLapb,
        /// <summary>XOT link (RFC 1613) - no LAPB</summary>
        Xot
    }

    /// <summary>
    /// Link operational status
    /// </summary>
    public enum LinkStatus
    {
        /// <summary>Link is stopped/not running</summary>
        Stopped,
        /// <summary>Link is starting up</summary>
        Starting,
        /// <summary>Link is active and operational</summary>
        Active,
        /// <summary>Link is stopping</summary>
        Stopping,
        /// <summary>Link is in error state</summary>
        Error
    }

    /// <summary>
    /// Statistics for a link
    /// </summary>
    public class LinkStatistics
    {
        public string LinkName { get; set; } = string.Empty;
        public LinkType LinkType { get; set; }
        public LinkStatus CurrentStatus { get; set; }
        public DateTime StartTime { get; set; }
        public long BytesTransmitted { get; set; }
        public long BytesReceived { get; set; }
        public long FramesTransmitted { get; set; }
        public long FramesReceived { get; set; }
        public long Errors { get; set; }
    }

    /// <summary>
    /// Handler for an opaque payload delivered up from a link (a LAPB I-frame information field, or an
    /// X.25 packet for XOT links).
    /// </summary>
    /// <param name="link">The link that received the payload.</param>
    /// <param name="payload">
    /// The opaque payload bytes (not interpreted by the link). The buffer may be reused by the link
    /// after the handler returns; handlers that retain the payload must copy it inside the callback.
    /// </param>
    /// <param name="length">Number of valid bytes in <paramref name="payload"/>.</param>
    public delegate void LinkPayloadReceived(ILink link, byte[] payload, int length);

    /// <summary>
    /// Handler for a link operational status transition.
    /// </summary>
    /// <param name="link">The link whose status changed.</param>
    /// <param name="oldStatus">The status before the transition.</param>
    /// <param name="newStatus">The status after the transition.</param>
    /// <param name="reason">Human-readable reason for the transition.</param>
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
        /// <returns>True if started successfully</returns>
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
        /// <param name="payload">The payload bytes to send.</param>
        /// <returns>
        /// True when queued for transmission; false when the link is not Active or the send window
        /// is full (logged, never thrown).
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
