namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The RR-LIB error and disconnect codes of appendix E, as Standard Error Code values.
    /// </summary>
    /// <remarks>
    /// <para><b>These are SEC values, not small negatives</b></para>
    /// Unlike the XMSG errors of appendix D, which are small negative numbers in the T register,
    /// RR-LIB reports through SINTRAN's Standard Error Code space - values around 17300 decimal.
    /// A normal return is zero, so a caller tests for zero and only then looks the value up here.
    /// <para><b>Several map onto errors from the layer below</b></para>
    /// The manual gives the equivalence for the ones that pass through: the codes at the end wrap
    /// an XMSG or XROUT failure that RR-LIB could not handle itself. When one of those appears the
    /// underlying problem is the real story - a lost XMSG, an unknown name, no room for a name.
    /// <para><b>The context-loss rule</b></para>
    /// After <see cref="RRERxnru"/> or <see cref="RRERxcra"/> the whole RR context is gone: treat
    /// every connection as closed, assume any outstanding buffers hold indeterminate data, and
    /// call only initialise, which keeps returning the same code until XMSG is running again.
    /// </remarks>
    public enum RrError : int
    {
        /// <summary>
        /// Normal return.
        /// </summary>
        Ok = 0,

        /// <summary>
        /// RRErnttm - not terminated, that is a timeout.
        /// </summary>
        RRErnttm = 17296,

        /// <summary>
        /// RRErdscn - disconnected, by the server or by RR-LIB itself.
        /// </summary>
        RRErdscn = 17297,

        /// <summary>
        /// RRErunev - an unexpected event arrived while waiting for a reply.
        /// </summary>
        RRErunev = 17298,

        /// <summary>
        /// RRErntcl - not initialised as a client.
        /// </summary>
        RRErntcl = 17304,

        /// <summary>
        /// RRErntsr - not initialised as a server.
        /// </summary>
        RRErntsr = 17305,

        /// <summary>
        /// RRErntei - not initialised as either a client or a server.
        /// </summary>
        RRErntei = 17306,

        /// <summary>
        /// RRERbdid - invalid remote identifier.
        /// </summary>
        RRERbdid = 17307,

        /// <summary>
        /// RRERbdst - the call is not valid in the current state.
        /// </summary>
        RRERbdst = 17308,

        /// <summary>
        /// RRERxsin - excess information in a request or response.
        /// </summary>
        RRERxsin = 17309,

        /// <summary>
        /// RRERbdln - a parameter of invalid length.
        /// </summary>
        RRERbdln = 17310,

        /// <summary>
        /// RRERbdnm - an invalid name was specified.
        /// </summary>
        RRERbdnm = 17311,

        /// <summary>
        /// RRERbdbf - a bad buffer specification.
        /// </summary>
        RRERbdbf = 17312,

        /// <summary>
        /// RRERbdpm - some other parameter error.
        /// </summary>
        RRERbdpm = 17313,

        /// <summary>
        /// RRERxscn - the limit on connections has been reached.
        /// </summary>
        RRERxscn = 17314,

        /// <summary>
        /// RRERtslm - the task's buffer-space limit has been reached.
        /// </summary>
        RRERtslm = 17315,

        /// <summary>
        /// RRERmsfl - the communication subsystem has no buffer space left.
        /// </summary>
        RRERmsfl = 17316,

        /// <summary>
        /// RRERprrf - a request for privilege was refused.
        /// </summary>
        RRERprrf = 17317,

        /// <summary>
        /// RRERntpr - the function requires privilege.
        /// </summary>
        RRERntpr = 17318,

        /// <summary>
        /// RRERdcpn - a disconnect is pending.
        /// </summary>
        RRERdcpn = 17319,

        /// <summary>
        /// RRERincp - the received data is incomplete.
        /// </summary>
        RRERincp = 17320,

        /// <summary>
        /// RRERfatal - fatal error; the manual says to contact ND.
        /// </summary>
        RRERfatal = 17343,

        /// <summary>
        /// RRERxnru - XMSG is not running; equivalent to XENRU. The RR context is lost.
        /// </summary>
        RRERxnru = 17344,

        /// <summary>
        /// RRERxcra - XMSG crashed; equivalent to XECRA. The RR context is lost.
        /// </summary>
        RRERxcra = 17345,

        /// <summary>
        /// RRERxnxt - XMSG is out of XT-blocks; equivalent to XENOT.
        /// </summary>
        RRERxnxt = 17346,

        /// <summary>
        /// RRERxnpt - XMSG is out of ports; equivalent to XENOP.
        /// </summary>
        RRERxnpt = 17347,

        /// <summary>
        /// RRERxnsp - XMSG has no space for the name; equivalent to XRNSP.
        /// </summary>
        RRERxnsp = 17348,

        /// <summary>
        /// RRDcuser - a normal disconnect by the user.
        /// </summary>
        RRDcuser = 17281,

        /// <summary>
        /// RRDcrmcg - remote congestion: every connection at the server is busy. Equivalent to
        /// XRBUS.
        /// </summary>
        RRDcrmcg = 17282,

        /// <summary>
        /// RRDcunsr - unknown server: no open port carries that name. Equivalent to XRUNN.
        /// </summary>
        RRDcunsr = 17283,

        /// <summary>
        /// RRDcunsy - unknown system. Equivalent to XRUKS.
        /// </summary>
        RRDcunsy = 17284,

        /// <summary>
        /// RRDcngfl - connection negotiation failed because the two RR-LIBs are incompatible.
        /// </summary>
        RRDcngfl = 17285,

        /// <summary>
        /// RRDcref - the connection request was refused on this network connection: the target is
        /// not a connect port. Equivalent to XRNSE.
        /// </summary>
        RRDcref = 17286,

        /// <summary>
        /// RRDcnoac - no access to the remote system. Equivalent to XRNRO or XRNCO.
        /// </summary>
        RRDcnoac = 17287,
    }
}
