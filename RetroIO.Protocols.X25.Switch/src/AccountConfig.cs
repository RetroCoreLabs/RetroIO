namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// A billing/access-control account: a name and the X.121 address range it covers. This is the
    /// configuration portion only (lifetime counters live on the runtime account model).
    /// </summary>
    public sealed class AccountConfig
    {
        /// <summary>
        /// Account name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Start of the account's X.121 address range.
        /// </summary>
        public string AddrStart { get; set; } = string.Empty;

        /// <summary>
        /// End of the account's X.121 address range.
        /// </summary>
        public string AddrEnd { get; set; } = string.Empty;
    }
}
