namespace RetroIO.Link
{
    /// <summary>
    /// Configuration for a codec-based LAPB link (SLIP or HDLC framing over any transport).
    /// </summary>
    /// <remarks>
    /// The transport (TCP or serial) is supplied separately as an <see cref="ILinkTransport"/>, keeping the
    /// link transport-agnostic. This config carries only the framing choice and the LAPB parameters
    /// inherited from <see cref="LapbLinkConfigBase"/>.
    /// </remarks>
    public sealed class CodecLinkConfig : LapbLinkConfigBase
    {
        /// <summary>
        /// The byte-stuffed framing method for this link.
        /// </summary>
        public FramingType Framing { get; set; } = FramingType.Slip;

        /// <summary>
        /// Whether the primary side automatically issues the LAPB connect (SABM) once the transport opens.
        /// </summary>
        public bool LapbAutoConnect { get; set; } = true;
    }
}
