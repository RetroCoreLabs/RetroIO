//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.RegularExpressions;
using System.Threading;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// A single X.121 routing-table entry.
    /// </summary>
    /// <remarks>
    /// Which fields are meaningful depends on <see cref="Type"/>:
    ///  - <see cref="X121RouteType.Exact"/> / <see cref="X121RouteType.Hunt"/> use <see cref="Address"/>.
    ///  - <see cref="X121RouteType.Prefix"/> uses <see cref="Address"/> + <see cref="PrefixLen"/>.
    ///  - <see cref="X121RouteType.Dnic"/> uses <see cref="Dnic"/> (4 digits, or 5 with the digit after the DNIC).
    ///  - <see cref="X121RouteType.Dcc"/> uses <see cref="Dcc"/> (3 digits).
    ///  - <see cref="X121RouteType.Regex"/> uses <see cref="Pattern"/>.
    ///  - <see cref="X121RouteType.Pvc"/> uses <see cref="SourcePort"/>/<see cref="SourceLci"/>/<see cref="DestLci"/>.
    /// The target is <see cref="DestPort"/> (or the hunt group for a hunt route), with an optional
    /// <see cref="BackupPort"/>. <see cref="X121RoutingTable.LocalPort"/> as the target means "this
    /// node": a local service of the runtime takes the call.
    /// </remarks>
    public sealed class X121Route
    {
        private string _pattern = string.Empty;
        private Regex? _compiled;
        private bool _patternBad;
        private string _cudPattern = string.Empty;
        private Regex? _cudCompiled;
        private bool _cudBad;
        private long _hits;

        /// <summary>
        /// The name people know the route by (for example <c>Sweden</c>); empty when it has none.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// A free note about the route; empty when there is none. Never used by the lookup.
        /// </summary>
        public string Note { get; set; } = string.Empty;

        /// <summary>
        /// False to keep the route in the table without using it. The lookup skips it.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The kind of match this entry performs.
        /// </summary>
        public X121RouteType Type { get; set; }

        /// <summary>
        /// Destination port the call is forwarded to when this entry matches, or
        /// <see cref="X121RoutingTable.LocalPort"/> for this node.
        /// </summary>
        public int DestPort { get; set; }

        /// <summary>
        /// Exact or prefix address to match.
        /// </summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Number of leading digits of <see cref="Address"/> to compare for a
        /// <see cref="X121RouteType.Prefix"/> entry; 0 means all of them.
        /// </summary>
        public int PrefixLen { get; set; }

        /// <summary>
        /// DNIC to match for a <see cref="X121RouteType.Dnic"/> entry: four digits, or five when the digit
        /// after the DNIC picks one of several networks that share it (X.121 7.12.2, Annex A.2).
        /// </summary>
        public string Dnic { get; set; } = string.Empty;

        /// <summary>
        /// Data Country Code to match for a <see cref="X121RouteType.Dcc"/> entry: three digits (X.121 7.12.1).
        /// </summary>
        public string Dcc { get; set; } = string.Empty;

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
        /// The first backup port, used when <see cref="DestPort"/> is out of order; -1 when none. Setting it
        /// replaces the whole <see cref="BackupPorts"/> list with this one port (or empties it for -1).
        /// </summary>
        public int BackupPort
        {
            get => BackupPorts.Count > 0 ? BackupPorts[0] : -1;
            set
            {
                BackupPorts.Clear();
                if (value >= 0 || value == X121RoutingTable.LocalPort)
                {
                    BackupPorts.Add(value);
                }
            }
        }

        /// <summary>
        /// The backup ports in the order they are tried when <see cref="DestPort"/> is out of order.
        /// </summary>
        /// <remarks>
        /// CCITT X.110 (11/88), text <c>Specs\X25\T-REC-X.110-198811-S!!PDF-E.txt</c>: "Traffic routes will
        /// normally comprise direct call routes and alternative call routes" (3.5, line 144), and "The routing
        /// algorithm will normally be: high usage route (direct), alternative route 1, alternative route 2"
        /// (Appendix I.1, line 368). The direct route is <see cref="DestPort"/>; these are the alternatives.
        /// </remarks>
        public System.Collections.Generic.List<int> BackupPorts { get; } = new System.Collections.Generic.List<int>();

        /// <summary>
        /// Which calls the route is for; every call by default.
        /// </summary>
        public X121RouteDirection Direction { get; set; } = X121RouteDirection.Both;

        /// <summary>
        /// An optional regular expression the call user data (read as ASCII) must match as well; empty
        /// means any call user data. Kept from the runtime's older route list, where it was a field.
        /// </summary>
        public string CallUserDataPattern
        {
            get => _cudPattern;
            set
            {
                _cudPattern = value ?? string.Empty;
                _cudCompiled = null;
                _cudBad = false;
            }
        }

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
                _patternBad = false;
            }
        }

        /// <summary>
        /// How many calls this route has decided since the table was built (or since
        /// <see cref="ResetHits"/>). Counted by <see cref="X121RoutingTable.Resolve"/>.
        /// </summary>
        public long Hits => Interlocked.Read(ref _hits);

        /// <summary>
        /// Sets <see cref="Hits"/> back to zero.
        /// </summary>
        public void ResetHits()
        {
            Interlocked.Exchange(ref _hits, 0);
        }

        /// <summary>
        /// Counts one decided call.
        /// </summary>
        internal void CountHit()
        {
            Interlocked.Increment(ref _hits);
        }

        /// <summary>
        /// The digits a prefix entry compares: the first <see cref="PrefixLen"/> digits of
        /// <see cref="Address"/>, or all of it when <see cref="PrefixLen"/> is 0 or longer than it.
        /// </summary>
        public string PrefixDigits
        {
            get
            {
                if (PrefixLen > 0 && PrefixLen < Address.Length)
                {
                    return Address.Substring(0, PrefixLen);
                }

                return Address;
            }
        }

        /// <summary>
        /// The digits or pattern this route matches on, whatever its kind: the address, the prefix, the
        /// DNIC, the DCC or the pattern; empty for a default route.
        /// </summary>
        public string MatchText
        {
            get
            {
                switch (Type)
                {
                    case X121RouteType.Prefix: return PrefixDigits;
                    case X121RouteType.Dnic: return Dnic;
                    case X121RouteType.Dcc: return Dcc;
                    case X121RouteType.Regex: return Pattern;
                    case X121RouteType.Default: return string.Empty;
                    default: return Address;
                }
            }
        }

        /// <summary>
        /// True when the pattern of a regex route is not a valid regular expression. Such a route never
        /// matches; the configuration loaders refuse it, so this is only seen on a table built in code.
        /// </summary>
        public bool PatternIsInvalid
        {
            get
            {
                if (Type != X121RouteType.Regex)
                {
                    return false;
                }

                EnsureCompiled();
                return _patternBad;
            }
        }

        /// <summary>
        /// Tests whether a called address matches this entry's <see cref="Pattern"/>, compiling and caching
        /// the regex on first use. Case-insensitive, matching the semantics of the runtime's older route list.
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

            EnsureCompiled();
            return _compiled != null && _compiled.IsMatch(calledAddress);
        }

        /// <summary>
        /// Tests the call user data against <see cref="CallUserDataPattern"/>.
        /// </summary>
        /// <param name="callUserData">
        /// The call user data of the call; null when the caller does not know it.
        /// </param>
        /// <returns>
        /// True when there is no pattern, when the call user data is not known, or when it matches.
        /// False when it does not match, or when the pattern is not a valid regular expression.
        /// </returns>
        public bool CallUserDataMatches(byte[]? callUserData)
        {
            if (_cudPattern.Length == 0 || callUserData == null)
            {
                return true;
            }

            if (_cudCompiled == null && !_cudBad)
            {
                try
                {
                    _cudCompiled = new Regex(_cudPattern, RegexOptions.CultureInvariant);
                }
                catch (System.ArgumentException)
                {
                    // A route with a pattern that cannot be read must not let calls through as if it had none.
                    _cudBad = true;
                }
            }

            if (_cudCompiled == null)
            {
                return false;
            }

            return _cudCompiled.IsMatch(System.Text.Encoding.ASCII.GetString(callUserData));
        }

        private void EnsureCompiled()
        {
            if (_compiled != null || _patternBad || _pattern.Length == 0)
            {
                return;
            }

            // A pattern that cannot be read never matches. The loaders check every pattern with
            // X121RouteRules.Check first, so this only happens for a route made in code.
            try
            {
                _compiled = new Regex(_pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (System.ArgumentException)
            {
                _patternBad = true;
            }
        }

        /// <summary>
        /// Decides between two routes that are otherwise equal (same kind, same match length): the higher
        /// one wins, before the table order. 0 for a route that has none. It comes from the runtime's older
        /// route list, where it was the only ranking; it never puts a less exact route ahead of a more exact
        /// one. Not in any ITU text.
        /// </summary>
        public int Priority { get; set; }
        /// <summary>
        /// A copy of the route with the same settings and a hit count of zero.
        /// </summary>
        /// <returns>
        /// The copy.
        /// </returns>
        public X121Route Clone()
        {
            X121Route copy = new X121Route
            {
                Name = Name,
                Note = Note,
                Enabled = Enabled,
                Type = Type,
                DestPort = DestPort,
                Address = Address,
                PrefixLen = PrefixLen,
                Dnic = Dnic,
                Dcc = Dcc,
                SourcePort = SourcePort,
                SourceLci = SourceLci,
                DestLci = DestLci,
                HuntGroupId = HuntGroupId,
                Direction = Direction,
                CallUserDataPattern = CallUserDataPattern,
                Pattern = Pattern,
                Priority = Priority,
            };
            copy.BackupPorts.AddRange(BackupPorts);
            return copy;
        }
    }
}
