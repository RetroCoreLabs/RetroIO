using System;

namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The RR-LIB event codes, as signalled by the wait call.
    /// </summary>
    /// <remarks>
    /// Chapter 4 section 4.9. Events are the ONLY way the request-response layer tells a program
    /// that something happened, and exactly one event is delivered per wait call. Callers form a
    /// set of interesting events by OR-ing the flags together.
    /// </remarks>
    [Flags]
    public enum RrEvent : int
    {
        /// <summary>
        /// No event.
        /// </summary>
        None = 0,

        /// <summary>
        /// RREVtime - the wait timed out.
        /// </summary>
        Timeout = 1,

        /// <summary>
        /// RREVcnin - a connection request arrived (server side).
        /// </summary>
        ConnectionIndication = 2,

        /// <summary>
        /// RREVcncf - the connection was accepted (client side).
        /// </summary>
        ConnectionConfirmation = 4,

        /// <summary>
        /// RREVrqin - a request arrived (server side).
        /// </summary>
        RequestIndication = 8,

        /// <summary>
        /// RREVrsin - a response arrived (client side).
        /// </summary>
        ResponseIndication = 16,

        /// <summary>
        /// RREVdcin - the remote party disconnected.
        /// </summary>
        DisconnectIndication = 32,

        /// <summary>
        /// RREVdccf - a disconnect this program initiated has completed.
        /// </summary>
        DisconnectConfirmation = 64,

        /// <summary>
        /// RREVunkn - a message arrived on a port the request-response layer does not own.
        /// </summary>
        /// <remarks>
        /// A program may mix plain XMSG calls with the request-response layer. This event says the
        /// message that woke the wait belongs to one of your own XMSG ports, so handle it yourself.
        /// </remarks>
        UnknownPort = 128,

        /// <summary>
        /// RREVohr - the wait returned for none of the above reasons.
        /// </summary>
        /// <remarks>
        /// The wait call ultimately performs a TMOUT monitor call, so anything else that
        /// reschedules the RT program - another program restarting it, for instance - surfaces
        /// here rather than as a real event.
        /// </remarks>
        Other = 256,
    }
}
