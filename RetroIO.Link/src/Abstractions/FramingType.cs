namespace RetroIO.Link
{
    /// <summary>
    /// Byte-stuffed framing method for a codec-based link.
    /// </summary>
    /// <remarks>
    /// Both methods are delimiting/escaping only and carry LAPB frames (with the FCS owned by LAPB via
    /// CodecLinkBridge). XOT is deliberately not listed here because it is length-prefixed and runs with
    /// no LAPB, so it is not a CodecLink framing choice.
    /// </remarks>
    public enum FramingType
    {
        /// <summary>SLIP (RFC 1055) END/ESC byte stuffing.</summary>
        Slip,

        /// <summary>HDLC/PPP (RFC 1662) FLAG/ESC byte stuffing.</summary>
        Hdlc
    }
}
