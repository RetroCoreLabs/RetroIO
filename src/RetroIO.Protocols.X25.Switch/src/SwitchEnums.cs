namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// The role of a switch port. Values are assigned explicitly for stable configuration.
    /// </summary>
    public enum PortType
    {
        /// <summary>
        /// Access port: faces a DTE (PAD, host, router). Runs in DCE mode by default.
        /// </summary>
        Access = 0,

        /// <summary>
        /// Trunk port: connects to another switch (switch-to-switch).
        /// </summary>
        Trunk = 1
    }

    /// <summary>
    /// The link transport a port uses. Values are assigned explicitly for stable configuration.
    /// </summary>
    public enum PortTransport
    {
        /// <summary>
        /// X.25-over-TCP (RFC 1613). The default.
        /// </summary>
        Xot = 0,

        /// <summary>
        /// LAPB over SLIP framing (legacy "hdlc-tcp").
        /// </summary>
        HdlcTcp = 1,

        /// <summary>
        /// LAPB over a serial device.
        /// </summary>
        HdlcSerial = 2,

        /// <summary>
        /// LAPB over RFC 1662 HDLC framing ("hdlc-ppp").
        /// </summary>
        HdlcPpp = 3
    }

    /// <summary>
    /// The X.25 role a port takes (<c>1 = DTE</c>, <c>0 = DCE</c>).
    /// </summary>
    public enum PortMode
    {
        /// <summary>
        /// Data circuit-terminating equipment (network side). The default for access ports.
        /// </summary>
        Dce = 0,

        /// <summary>
        /// Data terminal equipment.
        /// </summary>
        Dte = 1
    }
}
