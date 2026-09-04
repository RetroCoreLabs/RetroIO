namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// One registered name and how many free connections it still has.
    /// </summary>
    /// <remarks>
    /// A readonly struct because a listing builds one per name and they never outlive the call.
    /// </remarks>
    public readonly struct XroutNameEntry
    {
        /// <summary>
        /// Creates an entry.
        /// </summary>
        /// <param name="name">
        /// The registered name.
        /// </param>
        /// <param name="freeConnections">
        /// How many free connections remain, or a negative value when the name has no connection
        /// port at all.
        /// </param>
        public XroutNameEntry(string name, int freeConnections)
        {
            Name = name;
            FreeConnections = freeConnections;
        }

        /// <summary>
        /// Gets the registered name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets how many free connections remain.
        /// </summary>
        /// <remarks>
        /// Negative means the name was registered WITHOUT a connection port - it can be written to
        /// but has no seats, so "full" does not apply to it. That is a different thing from zero,
        /// which means a room that has seats and none left.
        /// </remarks>
        public int FreeConnections { get; }

        /// <summary>
        /// Gets whether this name has seats and at least one is free.
        /// </summary>
        public bool HasRoom
        {
            get { return FreeConnections > 0; }
        }
    }
}
