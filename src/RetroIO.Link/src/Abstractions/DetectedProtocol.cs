namespace RetroIO.Link
{
    /// <summary>
    /// Detected protocol type from a LAPB I-frame payload.
    /// </summary>
    public enum DetectedProtocol
    {
        /// <summary>X.25 packet (no SINTRAN markers).</summary>
        X25,

        /// <summary>SINTRAN frame (has 0x21 0x13 markers).</summary>
        Sintran
    }
}
