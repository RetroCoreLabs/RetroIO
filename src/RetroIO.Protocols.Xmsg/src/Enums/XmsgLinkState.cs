namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Inter-system link states driven by the XSLKI "Start-Link" operation and
    /// reported in parameter 1 of a link status request.
    /// </summary>
    /// <remarks>
    /// <para><b>VERIFIED against an official ND publication, 2026-08-07</b></para>
    /// ND's own X-MESSAGE version-L program description (210373L, 1988-02-02) states all six
    /// values verbatim, twice - in section 7.2 for <c>XSLKI</c> and again in 7.3 for
    /// <c>XSNET</c>: "Link state (0=Dead, 1=Init, 2=Call, 3=Conn, 4=Run, 5=Kill)". Imported at
    /// <c>Installation/Installation-Description/ND-210373L-EN.md</c>.
    /// <para>
    /// That matters because until then there was NO ND source for these numbers. They came from
    /// this project's own <c>XMSG-API.md</c> section 5.1, and the audit note below records that
    /// neither version-L symbol file defines a link-state symbol at all - the symbol files only
    /// carry the <c>XSLKI</c> service code that starts a link. So the values were prose we had
    /// written, believed but unconfirmed, and they are now confirmed by ND themselves without a
    /// single change.
    /// </para>
    /// <para>
    /// The audit stands and is kept: no ND SYMBOL NAME exists for these states, so none is
    /// attached here. A published table is not a symbol file.
    /// </para>
    /// </remarks>
    public enum XmsgLinkState : int
    {
        /// <summary>
        /// Link is dead (crashed).
        /// </summary>
        Dead = 0,

        /// <summary>
        /// Link is initialising.
        /// </summary>
        Init = 1,

        /// <summary>
        /// Link is calling (sending SABM frames).
        /// </summary>
        Call = 2,

        /// <summary>
        /// SABM has been seen; the link sends RR frames.
        /// </summary>
        Conn = 3,

        /// <summary>
        /// RR has been seen; data phase active and the neighbour is marked reachable.
        /// </summary>
        Run = 4,

        /// <summary>
        /// Link is being killed.
        /// </summary>
        Kill = 5,
    }
}
