using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// Carries a message that the kernel cannot deliver locally out to another system.
    /// </summary>
    /// <remarks>
    /// The manual draws the line for us (section 1.2.4): when the two tasks are in the same
    /// system, sending a message TRANSFERS OWNERSHIP of the buffer to the receiver and nothing is
    /// copied; when they are not, the CONTENT is transported and the buffer goes back to the pool.
    /// <see cref="XmsgKernel"/> implements the first case completely and hands the second to this
    /// interface, which is where the node's transport, envelope and link sequencing live.
    /// A kernel constructed without a sink is a purely local one: an out-of-system send fails with
    /// XENOS ("no such system") rather than pretending to succeed.
    /// </remarks>
    public interface IXmsgDatagramSink
    {
        /// <summary>
        /// Sends a message to a port on another system.
        /// </summary>
        /// <param name="destination">
        /// The magic number of the receiving port.
        /// </param>
        /// <param name="sender">
        /// The magic number of the sending port, so the receiver can answer.
        /// </param>
        /// <param name="userData">
        /// The message buffer's user data.
        /// </param>
        /// <param name="flags">
        /// The send options the caller asked for; the secure and high-priority bits in particular
        /// change how the far side must treat the message.
        /// </param>
        /// <returns>
        /// A success status when the transport accepted the message, or a failure status which the
        /// kernel passes straight back to the caller.
        /// </returns>
        XmsgStatus Send(
            XmsgMagicNumber destination,
            XmsgMagicNumber sender,
            ReadOnlySpan<byte> userData,
            XmsgSendFlags flags);
    }
}
