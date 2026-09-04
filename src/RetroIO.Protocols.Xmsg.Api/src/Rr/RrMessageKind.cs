namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The kind byte of a request-response message, carried in the user header.
    /// </summary>
    /// <remarks>
    /// <para><b>This framing is OURS, not ND's</b></para>
    /// The COSMOS Programmer Guide describes RR-LIB's programming model - the calls, the phases
    /// and the events - but not the bytes it puts on the wire, and no RR-LIB traffic has been
    /// captured. So the six-byte header used by <see cref="RrServer"/> and <see cref="RrClient"/>
    /// is a format of our own and must not be mistaken for the real product's.
    /// It does at least sit where the manual says a program's own protocol header belongs: the
    /// first six bytes of USER data, which is exactly what the XFWHD and XFRHD functions transfer
    /// (appendix A sections 3.2.7 and 3.2.9).
    /// <code>
    ///  byte 0    message kind (this enum)
    ///  byte 1    reserved, zero
    ///  byte 2-3  connection identifier, big-endian
    ///  byte 4-5  reserved, zero
    ///  byte 6..  the caller's data
    /// </code>
    /// Two ends built on this library interoperate. Talking to a real ND RR-LIB program would need
    /// the genuine format, which has to be captured first.
    /// </remarks>
    public enum RrMessageKind : byte
    {
        /// <summary>
        /// Not a request-response message.
        /// </summary>
        None = 0,

        /// <summary>
        /// A client asking to open a connection; the payload is the client's identification data.
        /// </summary>
        ConnectRequest = 1,

        /// <summary>
        /// A server accepting a connection; the payload is the server's answer data.
        /// </summary>
        ConnectAccept = 2,

        /// <summary>
        /// A server refusing a connection.
        /// </summary>
        ConnectReject = 3,

        /// <summary>
        /// A client request awaiting a response.
        /// </summary>
        Request = 4,

        /// <summary>
        /// A server's response to a request.
        /// </summary>
        Response = 5,

        /// <summary>
        /// Either party asking to close a connection.
        /// </summary>
        DisconnectRequest = 6,

        /// <summary>
        /// The acknowledgement that completes a disconnect.
        /// </summary>
        DisconnectConfirm = 7,
    }
}
