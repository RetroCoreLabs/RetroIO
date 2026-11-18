//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// What a route matches, in plain words, for the routing screen, the explanations and the logs.
    /// </summary>
    public static class X121RouteText
    {
        /// <summary>
        /// A short name for a kind of route, as the screen's badge shows it.
        /// </summary>
        /// <param name="type">
        /// The kind.
        /// </param>
        /// <returns>
        /// The name.
        /// </returns>
        public static string KindName(X121RouteType type)
        {
            switch (type)
            {
                case X121RouteType.Exact: return "Number";
                case X121RouteType.Hunt: return "Hunt group";
                case X121RouteType.Prefix: return "Prefix";
                case X121RouteType.Dnic: return "Network";
                case X121RouteType.Dcc: return "Country";
                case X121RouteType.Default: return "Default";
                case X121RouteType.Regex: return "Pattern";
                case X121RouteType.Pvc: return "PVC";
                default: return type.ToString();
            }
        }

        /// <summary>
        /// The country X.121 Annex J names for the first three digits, in brackets, or empty.
        /// </summary>
        /// <param name="digits">
        /// A DCC, a DNIC or a number.
        /// </param>
        /// <returns>
        /// For example <c> (Sweden)</c>, with a leading space; empty when Annex J names none.
        /// </returns>
        public static string CountrySuffix(string digits)
        {
            return X121CountryCodes.TryGetCountry(digits, out string name) ? " (" + name + ")" : string.Empty;
        }

        /// <summary>
        /// Writes digits in groups the way people read them: the four DNIC digits, then the rest.
        /// </summary>
        /// <param name="digits">
        /// The digits.
        /// </param>
        /// <returns>
        /// For example <c>2422 01</c>.
        /// </returns>
        public static string Grouped(string digits)
        {
            if (digits.Length <= X121RoutingTable.DnicLength)
            {
                return digits;
            }

            return digits.Substring(0, X121RoutingTable.DnicLength) + " " + digits.Substring(X121RoutingTable.DnicLength);
        }

        /// <summary>
        /// What the route takes, in words: for example "every call to network 2402 (Sweden)" or
        /// "numbers starting 2422 01".
        /// </summary>
        /// <param name="route">
        /// The route.
        /// </param>
        /// <returns>
        /// The words.
        /// </returns>
        public static string Describe(X121Route route)
        {
            switch (route.Type)
            {
                case X121RouteType.Exact:
                    return "calls to the number " + route.Address;
                case X121RouteType.Hunt:
                    return "calls to the number " + route.Address + ", shared by a hunt group";
                case X121RouteType.Prefix:
                    return "numbers starting " + Grouped(route.PrefixDigits);
                case X121RouteType.Dnic:
                    if (route.Dnic.Length > X121RoutingTable.DnicLength)
                    {
                        return "every call to network " + route.Dnic.Substring(0, X121RoutingTable.DnicLength)
                            + " starting " + route.Dnic.Substring(X121RoutingTable.DnicLength) + CountrySuffix(route.Dnic);
                    }

                    return "every call to network " + route.Dnic + CountrySuffix(route.Dnic);
                case X121RouteType.Dcc:
                    return "every call to country code " + route.Dcc + CountrySuffix(route.Dcc);
                case X121RouteType.Default:
                    return "every call no other route takes";
                case X121RouteType.Regex:
                    return "numbers matching the pattern " + route.Pattern;
                case X121RouteType.Pvc:
                    return "a permanent circuit";
                default:
                    return route.MatchText;
            }
        }

        /// <summary>
        /// Where a route sends calls, in words: the port, then the backup ports in the order they are tried.
        /// </summary>
        /// <param name="route">
        /// The route.
        /// </param>
        /// <param name="table">
        /// The table, for the port names.
        /// </param>
        /// <returns>
        /// For example <c>x75-gw, then backup 1 nordic</c>.
        /// </returns>
        public static string Target(X121Route route, X121RoutingTable table)
        {
            StringBuilder sb = new StringBuilder();
            if (route.Type == X121RouteType.Hunt)
            {
                sb.Append("hunt group");
                if (route.HuntGroupId >= 0 && route.HuntGroupId < table.HuntGroups.Count)
                {
                    X121HuntGroup hg = table.HuntGroups[route.HuntGroupId];
                    sb.Append(": ");
                    for (int i = 0; i < hg.Members.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(", ");
                        }

                        sb.Append(table.GetPortName(hg.Members[i]));
                    }
                }

                return sb.ToString();
            }

            sb.Append(table.GetPortName(route.DestPort));
            for (int i = 0; i < route.BackupPorts.Count; i++)
            {
                sb.Append(", then backup ").Append(i + 1).Append(' ').Append(table.GetPortName(route.BackupPorts[i]));
            }

            return sb.ToString();
        }
    }
}
