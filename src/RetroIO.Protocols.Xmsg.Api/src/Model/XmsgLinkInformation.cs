namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// The link-table utilisation an <c>XSLIN</c> reply carries in parameters 16 to 18, and whether
    /// the link goes to a gateway.
    /// </summary>
    /// <remarks>
    /// <para><b>Source</b></para>
    /// ND's X-MESSAGE version-L program description (210373L, 1988-02-02) section 7.5. It adds three
    /// parameters to the reply and one bit to an existing one:
    ///  - P16: size of the link descriptor table, the maximum number of entries available.
    ///  - P17: number of link elements currently in use.
    ///  - P18: maximum number of link elements ever used.
    ///  - P15 bit 2: set when this is a link to a gateway (network server).
    /// <para><b>A short reply is a VERSION, not an error</b></para>
    /// Parameters 16 to 18 are new in version L. ND state plainly that asking a version-K system
    /// means "the link table status information will not and cannot be printed", because a K system
    /// does not return them. So a caller must treat their absence as "the peer is older", never as a
    /// malformed reply - see <see cref="HasUtilisation"/>.
    /// <para><b>The MEANINGS are confirmed live; the wire encoding is not</b></para>
    /// <c>LIST-LINKS</c> run on D100 (XMSG Release L) prints exactly this triple as its own header
    /// line:
    /// <code>
    /// Link table status: 4 entries. 1 in use. Max 1 used.
    /// </code>
    /// Table size, in use, high-water mark - the same three quantities in the same order, and the
    /// 4 matches <c>X5LNK</c>. <c>LIST-UTILIZATION</c> prints the same shape for every kernel table
    /// under the headings <c>Limit / Max used / In use</c>.
    /// <para>
    /// So the field meanings are settled. What is still unconfirmed is the WIRE encoding: that run
    /// asked the LOCAL XROUT, which answers without a datagram, so no <c>XSLIN</c> reply has been
    /// recorded. Driving <c>LIST-LINKS</c> against a REMOTE system is what would capture it.
    /// </para>
    /// </remarks>
    public readonly struct XmsgLinkInformation
    {
        /// <summary>
        /// The bit in parameter 15 that marks a link to a gateway (network server).
        /// </summary>
        /// <remarks>
        /// Bit 2, counting from 0, so the mask is <c>0x0004</c>. New in version L; on a version-K
        /// reply the bit is simply absent from the mask rather than meaning something else.
        /// </remarks>
        public const ushort GatewayLinkBit = 1 << 2;

        /// <summary>
        /// Initialises the link information.
        /// </summary>
        /// <param name="flags">
        /// Parameter 15, the link's bit mask.
        /// </param>
        /// <param name="tableSize">
        /// Parameter 16, the link descriptor table size, or zero when the peer did not send it.
        /// </param>
        /// <param name="inUse">
        /// Parameter 17, link elements currently in use.
        /// </param>
        /// <param name="maximumUsed">
        /// Parameter 18, the high-water mark of link elements used.
        /// </param>
        /// <param name="hasUtilisation">
        /// True when the reply actually carried parameters 16 to 18.
        /// </param>
        public XmsgLinkInformation(
            ushort flags, ushort tableSize, ushort inUse, ushort maximumUsed, bool hasUtilisation)
        {
            Flags = flags;
            TableSize = tableSize;
            InUse = inUse;
            MaximumUsed = maximumUsed;
            HasUtilisation = hasUtilisation;
        }

        /// <summary>
        /// Gets parameter 15, the link's bit mask.
        /// </summary>
        public ushort Flags { get; }

        /// <summary>
        /// Gets parameter 16, the size of the link descriptor table.
        /// </summary>
        /// <remarks>
        /// Zero and meaningless when <see cref="HasUtilisation"/> is false.
        /// </remarks>
        public ushort TableSize { get; }

        /// <summary>
        /// Gets parameter 17, the number of link elements currently in use.
        /// </summary>
        /// <remarks>
        /// Zero and meaningless when <see cref="HasUtilisation"/> is false.
        /// </remarks>
        public ushort InUse { get; }

        /// <summary>
        /// Gets parameter 18, the greatest number of link elements ever in use.
        /// </summary>
        /// <remarks>
        /// Zero and meaningless when <see cref="HasUtilisation"/> is false. ND's default for the
        /// table itself is <c>X5LNK = 4</c> links, so this is normally a very small number.
        /// </remarks>
        public ushort MaximumUsed { get; }

        /// <summary>
        /// Gets a value indicating whether the reply carried parameters 16 to 18.
        /// </summary>
        /// <remarks>
        /// False against a system running XMSG version K or earlier. That is the documented
        /// behaviour, not a fault: the utilisation figures did not exist before version L.
        /// </remarks>
        public bool HasUtilisation { get; }

        /// <summary>
        /// Gets a value indicating whether this link goes to a gateway (network server).
        /// </summary>
        public bool IsGatewayLink
        {
            get { return (Flags & GatewayLinkBit) != 0; }
        }
    }
}
