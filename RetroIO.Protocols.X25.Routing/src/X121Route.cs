using System.Text.RegularExpressions;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// A single X.121 routing-table entry.
    /// </summary>
    /// <remarks>
    /// Which fields are meaningful depends on <see cref="Type"/>:
    ///  - <see cref="X121RouteType.Exact"/> / <see cref="X121RouteType.Hunt"/> use <see cref="Address"/>.
    ///  - <see cref="X121RouteType.Prefix"/> uses <see cref="Address"/> + <see cref="PrefixLen"/>.
    ///  - <see cref="X121RouteType.Dnic"/> uses <see cref="Dnic"/>.
    ///  - <see cref="X121RouteType.Pvc"/> uses <see cref="SourcePort"/>/<see cref="SourceLci"/>/<see cref="DestLci"/>.
    /// </remarks>
    public sealed class X121Route
    {
        /// <summary>
        /// The kind of match this entry performs.
        /// </summary>
        public X121RouteType Type { get; set; }

        /// <summary>
        /// Destination port the call is forwarded to when this entry matches.
        /// </summary>
        public int DestPort { get; set; }

        /// <summary>
        /// Exact or prefix address to match.
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Number of leading digits to compare for a <see cref="X121RouteType.Prefix"/> entry.
        /// </summary>
        public int PrefixLen { get; set; }

        /// <summary>
        /// DNIC (first four digits) to match for a <see cref="X121RouteType.Dnic"/> entry.
        /// </summary>
        public string Dnic { get; set; } = string.Empty;

        /// <summary>
        /// Source port for a <see cref="X121RouteType.Pvc"/> cross-connect.
        /// </summary>
        public int SourcePort { get; set; }

        /// <summary>
        /// Source logical channel for a <see cref="X121RouteType.Pvc"/> cross-connect.
        /// </summary>
        public ushort SourceLci { get; set; }

        /// <summary>
        /// Destination logical channel for a <see cref="X121RouteType.Pvc"/> cross-connect.
        /// </summary>
        public ushort DestLci { get; set; }

        /// <summary>
        /// Hunt-group index this entry resolves to (for <see cref="X121RouteType.Hunt"/>).
        /// </summary>
        public int HuntGroupId { get; set; } = -1;

        /// <summary>
        /// Backup destination port for failover, or -1 when none.
        /// </summary>
        public int BackupPort { get; set; } = -1;

        /// <summary>
        /// Regular expression to match the called address for a <see cref="X121RouteType.Regex"/> entry.
        /// </summary>
        /// <remarks>
        /// Only used when <see cref="Type"/> is <see cref="X121RouteType.Regex"/>. Setting this invalidates
        /// the compiled cache so the next match recompiles.
        /// </remarks>
        public string Pattern
        {
            get => _pattern;
            set
            {
                _pattern = value ?? string.Empty;
                _compiled = null;
            }
        }

        private string _pattern = string.Empty;
        private Regex? _compiled;

        /// <summary>
        /// Tests whether a called address matches this entry's <see cref="Pattern"/>, compiling and caching
        /// the regex on first use. Case-insensitive, matching the semantics of the existing endpoint router.
        /// </summary>
        /// <param name="calledAddress">
        /// The X.121 address being routed.
        /// </param>
        /// <returns>
        /// True if the pattern matches; false when the pattern is empty or invalid.
        /// </returns>
        public bool RegexMatches(string calledAddress)
        {
            if (string.IsNullOrEmpty(_pattern))
            {
                return false;
            }

            if (_compiled == null)
            {
                // Invalid patterns must not throw during a routing decision; a bad route simply never matches.
                try
                {
                    _compiled = new Regex(_pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
                catch (System.ArgumentException)
                {
                    return false;
                }
            }

            return _compiled.IsMatch(calledAddress);
        }
    }
}
