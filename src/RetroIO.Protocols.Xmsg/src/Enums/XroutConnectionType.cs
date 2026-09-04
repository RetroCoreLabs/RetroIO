namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// Connection-type enumeration returned as parameter 2 of an XSGSY
    /// "get routing info for system N" reply.
    /// </summary>
    /// <remarks>
    /// Values from XMSG-API.md section 4.3 (COSMOS Programmer Guide lines 10724-10756).
    /// Audited against the version-L symbol files (XMSG-VALUES-L.SYMB and
    /// XMSG-PL-VALUES-L.INCL): neither file defines any connection-type symbol,
    /// so there is nothing to add here and no ND symbol name to attach. The
    /// symbol files only define the XSGSY/XSGMC service code that returns it.
    /// </remarks>
    public enum XroutConnectionType : int
    {
        /// <summary>
        /// The system is not reachable.
        /// </summary>
        Unavailable = 0,

        /// <summary>
        /// The system is a directly connected neighbour.
        /// </summary>
        Neighbour = 1,

        /// <summary>
        /// The system is reached via a relay system.
        /// </summary>
        Via = 2,

        /// <summary>
        /// The system is reached via a network server.
        /// </summary>
        ViaNetworkServer = 3,

        /// <summary>
        /// The system is the local system.
        /// </summary>
        Local = 4,
    }
}
