namespace RetroIO.Link.Lapb
{
    /// <summary>
    /// LAPB connection state, shared across the link layer (per-link instances and the legacy layer).
    /// </summary>
    public enum LapbState
    {
        /// <summary>Link down, no connection.</summary>
        Disconnected,

        /// <summary>SABM sent, awaiting UA.</summary>
        Connecting,

        /// <summary>ABM established; information transfer.</summary>
        DataTransfer,

        /// <summary>DISC sent, awaiting UA.</summary>
        Disconnecting
    }
}
