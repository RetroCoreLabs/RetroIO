using System;

namespace RetroIO.Protocols.Xmsg.Codec
{
    /// <summary>
    /// The downward seam out of the codec: a sink for one complete XMSG information field.
    /// </summary>
    /// <remarks>
    /// This is the interface the codec holds and calls down; nothing here knows about
    /// HDLC, LAPB or TCP. In the live composition it is implemented by a link adapter
    /// (<c>LinkXmsgTransport</c> -> <c>ILink.SendData</c>); in tests it is a fake that
    /// records the bytes. One <see cref="Send"/> call carries exactly one information field -
    /// framing/byte-stuffing/FCS belong to the layer below the adapter.
    /// </remarks>
    public interface IXmsgTransport
    {
        /// <summary>
        /// Sends one complete XMSG information field downward toward the wire.
        /// </summary>
        /// <param name="bytes">
        /// The information-field bytes (SINTRAN header onward).
        /// </param>
        void Send(ReadOnlySpan<byte> bytes);
    }
}
