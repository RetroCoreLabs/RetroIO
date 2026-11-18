//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text.RegularExpressions;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// The checks every route, own DNIC and prefix must pass, in one place: the switch file loader, the
    /// runtime's JSON loader and the routing screen all ask here, so a value one of them takes the others
    /// take too.
    /// </summary>
    /// <remarks>
    /// The limits, from X.121 (10/2000), text <c>Specs\X25\T-REC-X.121-200010-I!!PDF-E.txt</c>:
    ///  - a DNIC is four digits, the first three are the Data Country Code (7.2, 7.7, lines 548-550);
    ///  - the first digit of a DCC is a zone, 2 to 7 (7.2, Annex J lists zones 2 to 7); a DNIC may also
    ///    start with 1 (7.4 and 7.5: 111S for mobile satellite systems, 1000-1999 for global networks);
    ///  - an international data number is 5 to 14 digits (7.7, line 546);
    ///  - a called address in X.25 is at most 15 digits (X.25 10/96 5.2.1; one prefix digit fits in
    ///    front of a 14-digit number, X.121 7.8.2 note, lines 571-573).
    /// A prefix route may be any 1 to 15 digits: it matches the leading part of a number.
    /// </remarks>
    public static class X121RouteRules
    {
        /// <summary>
        /// The most digits in a called address (X.25 5.2.1).
        /// </summary>
        public const int MaxAddressDigits = 15;

        /// <summary>
        /// The most digits in an international data number (X.121 7.7).
        /// </summary>
        public const int MaxInternationalDigits = 14;

        /// <summary>
        /// The fewest digits in an international data number (X.121 7.7).
        /// </summary>
        public const int MinInternationalDigits = 5;

        /// <summary>
        /// True when every character is a decimal digit and there is at least one.
        /// </summary>
        /// <param name="s">
        /// The text.
        /// </param>
        /// <returns>
        /// True for one or more digits and nothing else.
        /// </returns>
        public static bool IsDigits(string? s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks a called address someone typed (for example in the "test an address" box).
        /// </summary>
        /// <param name="address">
        /// The address.
        /// </param>
        /// <param name="error">
        /// Why it is refused; empty when it is accepted.
        /// </param>
        /// <returns>
        /// True for 1 to 15 digits.
        /// </returns>
        public static bool CheckAddress(string? address, out string error)
        {
            if (!IsDigits(address))
            {
                error = "an X.121 address is digits only (0-9)";
                return false;
            }

            if (address!.Length > MaxAddressDigits)
            {
                error = "an X.121 address has at most " + MaxAddressDigits + " digits (X.25 5.2.1); this one has " + address.Length;
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// Checks an own DNIC: four digits, or five with the digit after the DNIC (X.121 7.12.2, A.2).
        /// </summary>
        /// <param name="dnic">
        /// The DNIC.
        /// </param>
        /// <param name="error">
        /// Why it is refused; empty when it is accepted.
        /// </param>
        /// <returns>
        /// True when it is a valid DNIC.
        /// </returns>
        public static bool CheckDnic(string? dnic, out string error)
        {
            if (!IsDigits(dnic) || (dnic!.Length != 4 && dnic.Length != 5))
            {
                error = "a DNIC is 4 digits, or 5 with the digit after it (X.121 7.2, 7.12.2)";
                return false;
            }

            if (dnic[0] < '1' || dnic[0] > '7')
            {
                error = "a DNIC starts with 1 to 7 (X.121 7.2 zones 2-7, 7.4 and 7.5 for 1); " + dnic + " starts with " + dnic[0];
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// Checks a Data Country Code: three digits, the first a zone 2 to 7 (X.121 7.2, Annex J).
        /// </summary>
        /// <param name="dcc">
        /// The code.
        /// </param>
        /// <param name="error">
        /// Why it is refused; empty when it is accepted.
        /// </param>
        /// <returns>
        /// True when it is a valid DCC.
        /// </returns>
        public static bool CheckDcc(string? dcc, out string error)
        {
            if (!IsDigits(dcc) || dcc!.Length != 3)
            {
                error = "a country code (DCC) is 3 digits (X.121 7.2)";
                return false;
            }

            if (dcc[0] < '2' || dcc[0] > '7')
            {
                error = "a country code starts with a zone digit 2 to 7 (X.121 Annex J); " + dcc + " starts with " + dcc[0];
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// Checks an international prefix: one to three digits, or empty for none. X.121 7.8.2 leaves its
        /// form to the network, and its note says that for X.25 only one digit fits in front of a 14-digit
        /// number.
        /// </summary>
        /// <param name="prefix">
        /// The prefix; empty means the network uses none.
        /// </param>
        /// <param name="error">
        /// Why it is refused; empty when it is accepted.
        /// </param>
        /// <returns>
        /// True when it is accepted.
        /// </returns>
        public static bool CheckInternationalPrefix(string? prefix, out string error)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                error = string.Empty;
                return true;
            }

            if (!IsDigits(prefix) || prefix.Length > 3)
            {
                error = "the international prefix is 1 to 3 digits, or empty for none (X.121 7.8)";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// Checks the match part of a route for its kind.
        /// </summary>
        /// <param name="type">
        /// The kind.
        /// </param>
        /// <param name="match">
        /// The digits (or the pattern for a regex route); ignored for a default route.
        /// </param>
        /// <param name="error">
        /// Why it is refused; empty when it is accepted.
        /// </param>
        /// <returns>
        /// True when it is accepted.
        /// </returns>
        public static bool CheckMatch(X121RouteType type, string? match, out string error)
        {
            switch (type)
            {
                case X121RouteType.Exact:
                case X121RouteType.Hunt:
                    return CheckAddress(match, out error);

                case X121RouteType.Prefix:
                    if (!IsDigits(match) || match!.Length > MaxAddressDigits)
                    {
                        error = "a prefix is 1 to " + MaxAddressDigits + " digits";
                        return false;
                    }

                    error = string.Empty;
                    return true;

                case X121RouteType.Dnic:
                    return CheckDnic(match, out error);

                case X121RouteType.Dcc:
                    return CheckDcc(match, out error);

                case X121RouteType.Regex:
                    if (string.IsNullOrEmpty(match))
                    {
                        error = "a pattern route needs a pattern";
                        return false;
                    }

                    try
                    {
                        _ = new Regex(match, RegexOptions.CultureInvariant);
                    }
                    catch (System.ArgumentException ex)
                    {
                        error = "the pattern is not a valid regular expression: " + ex.Message;
                        return false;
                    }

                    error = string.Empty;
                    return true;

                case X121RouteType.Default:
                    error = string.Empty;
                    return true;

                default:
                    error = "a " + type + " entry is not a route a call is looked up in";
                    return false;
            }
        }

        /// <summary>
        /// Checks a whole route: its match, and that it names a port or a hunt group.
        /// </summary>
        /// <param name="route">
        /// The route.
        /// </param>
        /// <param name="error">
        /// Why it is refused; empty when it is accepted.
        /// </param>
        /// <returns>
        /// True when it is accepted.
        /// </returns>
        public static bool Check(X121Route route, out string error)
        {
            if (route.Type == X121RouteType.Pvc)
            {
                error = string.Empty;
                return true;
            }

            if (!CheckMatch(route.Type, route.MatchText, out error))
            {
                return false;
            }

            if (route.Type == X121RouteType.Hunt)
            {
                if (route.HuntGroupId < 0)
                {
                    error = "a hunt route needs a hunt group";
                    return false;
                }
            }
            else if (route.DestPort < 0 && route.DestPort != X121RoutingTable.LocalPort)
            {
                error = "the route names no port";
                return false;
            }

            if (route.CallUserDataPattern.Length > 0)
            {
                try
                {
                    _ = new Regex(route.CallUserDataPattern, RegexOptions.CultureInvariant);
                }
                catch (System.ArgumentException ex)
                {
                    error = "the call user data pattern is not a valid regular expression: " + ex.Message;
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }
    }
}
