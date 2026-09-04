using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The options accepted by the reserve-message-buffer function (XFGET).
    /// </summary>
    /// <remarks>
    /// Values are the corresponding <see cref="XmsgOption"/> bits, so a cast converts between the
    /// two; this enum exists only to express which subset the call legally accepts.
    /// </remarks>
    [Flags]
    public enum XmsgBufferOptions : ushort
    {
        /// <summary>
        /// No options.
        /// </summary>
        None = 0,

        /// <summary>
        /// XFWTF - suspend the task until a buffer of the requested size becomes available.
        /// </summary>
        Wait = XmsgOption.XFWTF,

        /// <summary>
        /// XFEXC - take one of the buffers this task pre-allocated exclusively with XFALM.
        /// </summary>
        /// <remarks>
        /// Fails with an error when the task has no exclusive buffer of the requested size, rather
        /// than falling back to the shared pool. Exclusive allocation is how a task guarantees
        /// itself buffer space independent of pool pressure.
        /// </remarks>
        Exclusive = XmsgOption.XFEXC,
    }
}
