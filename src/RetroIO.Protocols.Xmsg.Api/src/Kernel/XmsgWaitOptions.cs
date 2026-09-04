using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The options accepted by the status and receive functions (XFPST, XFGST, XFRCV, XFRRH, XFRRE).
    /// </summary>
    /// <remarks>
    /// Values are the corresponding <see cref="XmsgOption"/> bits, so a cast converts between the
    /// two; this enum exists only to express which subset a given call legally accepts. The manual
    /// is explicit that options not described under a call must not be set (section 1.7.1).
    /// </remarks>
    [Flags]
    public enum XmsgWaitOptions : ushort
    {
        /// <summary>
        /// No options.
        /// </summary>
        None = 0,

        /// <summary>
        /// XFWTF - suspend the task until the operation can complete.
        /// </summary>
        /// <remarks>
        /// Without it, a call that finds nothing returns a not-terminated status immediately.
        /// </remarks>
        Wait = XmsgOption.XFWTF,

        /// <summary>
        /// XFWAK - arm the wake-up bit so the next arriving message restarts the task.
        /// </summary>
        /// <remarks>
        /// This is how a timed-out wait is built: poll without Wait, arm WakeUp, sleep on your own
        /// timer. Note that when the wake-up fires the message is NOT received - repeat the call.
        /// A task already suspended inside XMSG cannot be woken this way; the bit is cleared but
        /// the wake-up is lost.
        /// </remarks>
        WakeUp = XmsgOption.XFWAK,

        /// <summary>
        /// XFHIP - report only high-priority messages.
        /// </summary>
        /// <remarks>
        /// When a high-priority message is waiting it is reported with type XMTHI. When none is,
        /// this behaves exactly like <see cref="WakeUp"/>.
        /// </remarks>
        HighPriority = XmsgOption.XFHIP,
    }
}
