namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The role a <see cref="TadSession"/> plays in a connect-to exchange.
    /// </summary>
    /// <remarks>
    /// The role names follow the NPL source: the client is the Remote Process (RP,
    /// <c>RP-P2-TAD.NPL</c>) that drives the connect-to; the server is the Master Process
    /// (MP, <c>MP-P2-TAD.NPL</c>) that answers on the target system.
    /// </remarks>
    public enum TadSessionRole
    {
        /// <summary>
        /// The connecting side (Remote Process) that initiates the connect-to and builds
        /// the client frames.
        /// </summary>
        Client,

        /// <summary>
        /// The answering side (Master Process) on the target system.
        /// </summary>
        Server,
    }
}
