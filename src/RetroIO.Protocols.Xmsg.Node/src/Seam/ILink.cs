using System;

namespace RetroIO.Protocols.Xmsg.Node.Seam
{
    /// <summary>
    /// Operational status of a link.
    /// </summary>
    public enum LinkStatus
    {
        /// <summary>
        /// The link is stopped / not running.
        /// </summary>
        Stopped,
        /// <summary>
        /// The link is starting up (LAPB establishment in progress).
        /// </summary>
        Starting,
        /// <summary>
        /// The link is established and can carry L3 traffic.
        /// </summary>
        Active,
        /// <summary>
        /// The link is shutting down.
        /// </summary>
        Stopping,
        /// <summary>
        /// The link failed and is unusable.
        /// </summary>
        Error
    }

    /// <summary>
    /// Handler for an opaque payload delivered up from a link (a LAPB I-frame information field).
    /// The link does not classify the payload.
    /// </summary>
    /// <param name="link">
    /// The link that received the payload (sender-first).
    /// </param>
    /// <param name="payload">
    /// The opaque payload bytes. The buffer may be reused by the link after the handler returns;
    /// handlers that retain the payload MUST copy it inside the callback.
    /// </param>
    /// <param name="length">
    /// Number of valid bytes in <paramref name="payload"/>.
    /// </param>
    public delegate void LinkPayloadReceived(ILink link, byte[] payload, int length);

    /// <summary>
    /// Handler for a link operational status transition. Both the previous and the new status are
    /// carried (plus a short human-readable reason) so no information is lost - consumers that only
    /// care about the new value ignore the rest.
    /// </summary>
    /// <param name="link">
    /// The link whose status changed (sender-first).
    /// </param>
    /// <param name="oldStatus">
    /// The status before the transition.
    /// </param>
    /// <param name="newStatus">
    /// The status after the transition.
    /// </param>
    /// <param name="reason">
    /// A short human-readable reason for the transition (for logs).
    /// </param>
    public delegate void LinkStatusChanged(ILink link, LinkStatus oldStatus, LinkStatus newStatus, string reason);

    /// <summary>
    /// The bottom seam of the stack: an established data link that delivers opaque L3 payloads up as
    /// events and accepts opaque L3 payloads down as method calls. Nothing above this interface knows
    /// about HDLC framing, byte-stuffing, FCS, or LAPB; the link knows nothing about which L3 protocol
    /// it carries.
    /// </summary>
    /// <remarks>
    /// These type names are deliberately identical to the consuming emulator's link interface so the
    /// port is a using-directive swap; code above the seam must depend only on the members declared
    /// here. Protocol classification (X.25 vs XMSG) is NOT the link's job - it lives above the link in
    /// the composition root (see <see cref="IProtocolDetector"/>).
    /// </remarks>
    public interface ILink : IDisposable
    {
        /// <summary>
        /// Gets the unique name identifying this link (stamped on log lines).
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the current operational status.
        /// </summary>
        LinkStatus Status { get; }

        /// <summary>
        /// Starts the link: initiates LAPB establishment and begins delivering payloads.
        /// </summary>
        /// <returns>
        /// True when the link started; false when it could not start.
        /// </returns>
        bool Start();

        /// <summary>
        /// Stops the link and tears it down. Idempotent.
        /// </summary>
        void Stop();

        /// <summary>
        /// Sends one opaque L3 payload (an information field) as a LAPB I-frame.
        /// </summary>
        /// <param name="payload">
        /// The payload bytes to send.
        /// </param>
        /// <returns>
        /// True when queued for transmission; false when the link is not Active or the send window is
        /// full (logged, never thrown).
        /// </returns>
        /// <remarks>
        /// The parameter is a <see cref="ReadOnlySpan{T}"/> deliberately: the compiler forbids storing
        /// the span, which FORCES the implementation to copy the bytes into its own storage before
        /// returning. The caller's buffer is therefore free for reuse (pooled/reused buffers, zero
        /// per-send allocation) as soon as this call returns.
        /// </remarks>
        bool SendData(ReadOnlySpan<byte> payload);

        /// <summary>
        /// Occurs when a complete L3 payload is delivered up.
        /// </summary>
        event LinkPayloadReceived PayloadReceived;

        /// <summary>
        /// Occurs when the link status changes (actual transitions only).
        /// </summary>
        event LinkStatusChanged StatusChanged;
    }
}
