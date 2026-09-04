namespace RetroIO.Link.Models
{
    /// <summary>
    /// LAPB frame types.
    /// </summary>
    public enum LapbFrameType
    {
        /// <summary>Information frame.</summary>
        Information = 0,

        /// <summary>Supervisory frame.</summary>
        Supervisory = 1,

        /// <summary>Unnumbered frame.</summary>
        Unnumbered = 2,

        /// <summary>Unknown frame type.</summary>
        Unknown = 3
    }
}
