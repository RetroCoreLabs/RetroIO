//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// Reads the address patterns of the runtime's older route list (a regular expression per route, in the
    /// style of Cisco's <c>x25 route</c>) as a route of the one table. The common shapes become the kind they
    /// mean, so they rank as that kind; anything else stays a pattern route.
    /// </summary>
    /// <remarks>
    ///  - <c>.*</c>, <c>^.*</c>, <c>^.*$</c> and an empty pattern: the default route;
    ///  - digits only, or <c>^digits$</c>: an exact number;
    ///  - <c>digits.*</c>, <c>^digits.*</c>, <c>^digits</c>: a prefix;
    ///  - anything else: a pattern route, matched as the regular expression it is.
    /// The old matcher was an unanchored search, so plain digits used to match anywhere in the number
    /// ("200" matched "1200"). Read as an exact number they now match only that number; this is the
    /// change a converted file sees.
    /// </remarks>
    public static class X121LegacyPattern
    {
        /// <summary>
        /// Reads an old pattern.
        /// </summary>
        /// <param name="pattern">
        /// The pattern as written.
        /// </param>
        /// <param name="type">
        /// The kind of route it means.
        /// </param>
        /// <param name="match">
        /// The digits (exact, prefix), the pattern (regex), or empty (default).
        /// </param>
        public static void Convert(string? pattern, out X121RouteType type, out string match)
        {
            string p = (pattern ?? string.Empty).Trim();
            if (p.Length == 0 || p == ".*" || p == "^.*" || p == "^.*$" || p == "*")
            {
                type = X121RouteType.Default;
                match = string.Empty;
                return;
            }

            string body = p;
            bool anchoredStart = false;
            if (body[0] == '^')
            {
                anchoredStart = true;
                body = body.Substring(1);
            }

            if (body.Length > 1 && body[body.Length - 1] == '$' && X121RouteRules.IsDigits(body.Substring(0, body.Length - 1)))
            {
                type = X121RouteType.Exact;
                match = body.Substring(0, body.Length - 1);
                return;
            }

            if (!anchoredStart && X121RouteRules.IsDigits(body))
            {
                type = X121RouteType.Exact;
                match = body;
                return;
            }

            if (body.EndsWith(".*", System.StringComparison.Ordinal) && X121RouteRules.IsDigits(body.Substring(0, body.Length - 2)))
            {
                type = X121RouteType.Prefix;
                match = body.Substring(0, body.Length - 2);
                return;
            }

            if (anchoredStart && X121RouteRules.IsDigits(body))
            {
                type = X121RouteType.Prefix;
                match = body;
                return;
            }

            type = X121RouteType.Regex;
            match = p;
        }
    }
}
