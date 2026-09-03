namespace RetroIO.Protocols.Xmsg.ListRouting
{
    /// <summary>
    /// A routing table that can answer an XROUT <c>XSGSY</c> "get routing info for
    /// system N" query.
    /// </summary>
    /// <remarks>
    /// VERIFIED (COSMOS Programmer Guide ND-60.164, <c>XSGSY</c>; XMSG-PROTOCOL.md
    /// section 9.1): the lookup rule is "first routing-table entry whose system
    /// number is greater than or equal to the requested one". A miss is reported to
    /// the caller as system <c>0</c> / <see cref="XroutConnectionType.Unavailable"/>.
    /// </remarks>
    public interface IRoutingTable
    {
        /// <summary>
        /// Finds the first routing-table entry whose system number is greater than
        /// or equal to <paramref name="querySystem"/>.
        /// </summary>
        /// <param name="querySystem">
        /// The system number being queried.
        /// </param>
        /// <param name="entry">
        /// When this method returns <c>true</c>, the matching entry; otherwise a
        /// default entry (system <c>0</c>).
        /// </param>
        /// <returns>
        /// <c>true</c> if a matching entry exists; otherwise <c>false</c>.
        /// </returns>
        bool TryLookup(ushort querySystem, out RoutingTableEntry entry);
    }
}
