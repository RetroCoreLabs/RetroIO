namespace RetroIO.Protocols.Xmsg.Sync
{
    /// <summary>
    /// Which way a file was carried.
    /// </summary>
    /// <remarks>
    /// Not a flags enum: a single transfer goes one way. A watch PAIR may carry files both ways,
    /// but that is two transfers, each with its own direction.
    /// </remarks>
    public enum SyncDirection
    {
        /// <summary>
        /// Nothing recorded.
        /// </summary>
        None = 0,

        /// <summary>
        /// Local to the SINTRAN machine - a source file being sent out to be compiled.
        /// </summary>
        ToMachine = 1,

        /// <summary>
        /// The SINTRAN machine to local - compiler output coming back.
        /// </summary>
        FromMachine = 2,
    }
}
