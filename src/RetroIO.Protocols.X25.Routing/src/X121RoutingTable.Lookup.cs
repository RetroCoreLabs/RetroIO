//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// The lookup itself: number completion, the ranking of the routes, and the port checks.
    /// </summary>
    public sealed partial class X121RoutingTable
    {
        // How the kinds rank, most exact first. A higher class wins; inside a class the longer match wins;
        // inside that the higher Priority, then the route written first. The numbers are only used to compare.
        private const int ClassExact = 7;      // exact number and hunt group
        private const int ClassRegex = 6;      // pattern route, ranked after every exact number
        private const int ClassPrefix = 5;     // longest full-address prefix
        private const int ClassDnic5 = 4;      // DNIC with the digit after it (X.121 7.12.2)
        private const int ClassDnic = 3;       // DNIC (X.121 7.12.2 network digit)
        private const int ClassDcc = 2;        // country code (X.121 7.12.1)
        private const int ClassDefault = 1;    // default route: the gateway
        private const int ClassNone = 0;

        /// <summary>
        /// The called number in full international form: the international prefix taken off, or a national
        /// number completed with the first own DNIC.
        /// </summary>
        /// <remarks>
        /// X.121 7.8.1 and 7.8.2: the prefix selects the international format and "does not form a part of
        /// the data number"; 7.6.1 and 7.6.2: international data number = DNIC + NTN, and the NTN is the
        /// number used inside the network. With no prefix set nothing can tell the two formats apart, so the
        /// number is returned as it is. An escape code (8, 9 or 0, X.121 7.9) counts only behind the prefix,
        /// where it is kept with the number after it (Figure 2).
        /// </remarks>
        /// <param name="calledAddress">
        /// The called number as it came in.
        /// </param>
        /// <returns>
        /// The international number.
        /// </returns>
        public string ToInternational(string calledAddress)
        {
            string called = calledAddress ?? string.Empty;
            if (_internationalPrefix.Length == 0)
            {
                return called;
            }

            // The prefix first: X.121 7.8.2 makes it "a national matter", so when a network chose 0 (an escape digit
            // too) a number starting 0 is the prefix. After it may come an escape code, which is kept.
            if (called.Length > _internationalPrefix.Length && called.StartsWith(_internationalPrefix, StringComparison.Ordinal))
            {
                return called.Substring(_internationalPrefix.Length);
            }

            // No prefix: a national data number, whatever its first digit. An escape code (8, 9 or 0, X.121 7.9) is
            // part of the international X.121 format and is dialled behind the prefix (Figure 2), where the branch
            // above keeps it. From 2026-11-21 (BUGS 37) until the fix the same day, a national number starting with
            // 8, 9 or 0 was also taken as an escape code and lost its DNIC, so 9001 (an echo service) was not
            // obtainable.
            if (_ownDnics.Count > 0 && called.Length > 0)
            {
                return _ownDnics[0].Substring(0, DnicLength) + called;
            }

            return called;
        }

        /// <summary>
        /// The national number for an international number of our own network: the own DNIC taken off. A
        /// number of another network is returned as it is.
        /// </summary>
        /// <param name="internationalAddress">
        /// The international number.
        /// </param>
        /// <returns>
        /// The national number, or the number unchanged.
        /// </returns>
        public string ToNational(string internationalAddress)
        {
            string a = internationalAddress ?? string.Empty;
            for (int i = 0; i < _ownDnics.Count; i++)
            {
                string dnic = _ownDnics[i].Substring(0, DnicLength);
                if (a.Length > DnicLength && a.StartsWith(dnic, StringComparison.Ordinal))
                {
                    return a.Substring(DnicLength);
                }
            }

            return a;
        }

        /// <summary>
        /// The class a route ranks in, and the number of digits that decide inside it, regardless of any
        /// call: the order the routing screen lists the routes in.
        /// </summary>
        /// <param name="route">
        /// The route.
        /// </param>
        /// <param name="length">
        /// The digits it matches on.
        /// </param>
        /// <returns>
        /// The class; higher wins.
        /// </returns>
        private static int StaticRank(X121Route route, out int length)
        {
            switch (route.Type)
            {
                case X121RouteType.Exact:
                case X121RouteType.Hunt:
                    length = route.Address.Length;
                    return ClassExact;
                case X121RouteType.Regex:
                    length = 0;
                    return ClassRegex;
                case X121RouteType.Prefix:
                    length = route.PrefixDigits.Length;
                    return ClassPrefix;
                case X121RouteType.Dnic:
                    length = route.Dnic.Length;
                    return route.Dnic.Length > DnicLength ? ClassDnic5 : ClassDnic;
                case X121RouteType.Dcc:
                    length = route.Dcc.Length;
                    return ClassDcc;
                case X121RouteType.Default:
                    length = 0;
                    return ClassDefault;
                default:
                    length = 0;
                    return ClassNone;
            }
        }

        /// <summary>
        /// Whether a route takes an international number, and how it ranks for it.
        /// </summary>
        /// <param name="route">
        /// The route.
        /// </param>
        /// <param name="international">
        /// The called number in international form.
        /// </param>
        /// <param name="length">
        /// The digits it matched.
        /// </param>
        /// <returns>
        /// The class it ranks in, or <see cref="ClassNone"/> when it does not match.
        /// </returns>
        private int MatchRank(X121Route route, string international, out int length)
        {
            length = 0;
            switch (route.Type)
            {
                case X121RouteType.Exact:
                case X121RouteType.Hunt:
                    if (route.Address.Length == 0)
                    {
                        return ClassNone;
                    }

                    if (international == route.Address)
                    {
                        length = international.Length;
                        return ClassExact;
                    }

                    // A local number (the NTN, X.121 7.6.2) is also this number written with an own DNIC in front.
                    for (int i = 0; i < _ownDnics.Count; i++)
                    {
                        string dnic = _ownDnics[i].Substring(0, DnicLength);
                        if (international.Length == DnicLength + route.Address.Length
                            && international.StartsWith(dnic, StringComparison.Ordinal)
                            && string.CompareOrdinal(international, DnicLength, route.Address, 0, route.Address.Length) == 0)
                        {
                            length = international.Length;
                            return ClassExact;
                        }
                    }

                    return ClassNone;

                case X121RouteType.Regex:
                    return route.RegexMatches(international) ? ClassRegex : ClassNone;

                case X121RouteType.Prefix:
                {
                    string digits = route.PrefixDigits;
                    if (digits.Length > 0 && international.StartsWith(digits, StringComparison.Ordinal))
                    {
                        length = digits.Length;
                        return ClassPrefix;
                    }

                    return ClassNone;
                }

                case X121RouteType.Dnic:
                    if (route.Dnic.Length >= DnicLength && international.StartsWith(route.Dnic, StringComparison.Ordinal))
                    {
                        length = route.Dnic.Length;
                        return route.Dnic.Length > DnicLength ? ClassDnic5 : ClassDnic;
                    }

                    return ClassNone;

                case X121RouteType.Dcc:
                    if (route.Dcc.Length == DccLength && international.StartsWith(route.Dcc, StringComparison.Ordinal))
                    {
                        length = DccLength;
                        return ClassDcc;
                    }

                    return ClassNone;

                case X121RouteType.Default:
                    return ClassDefault;

                default:
                    // PVC entries are set up at start, never looked up.
                    return ClassNone;
            }
        }

        private static bool DirectionFits(X121Route route, X121RouteDirection direction)
        {
            return route.Direction == X121RouteDirection.Both
                || direction == X121RouteDirection.Both
                || route.Direction == direction;
        }

        /// <summary>
        /// One route that takes the number, with its rank.
        /// </summary>
        private readonly struct Candidate
        {
            public Candidate(int index, int cls, int length, int priority)
            {
                Index = index;
                Class = cls;
                Length = length;
                Priority = priority;
            }

            public int Priority { get; }

            public int Index { get; }

            public int Class { get; }

            public int Length { get; }

            /// <summary>
            /// True when this one wins over the other: higher class, then longer match, then higher
            /// <see cref="X121Route.Priority"/>, then written first.
            /// </summary>
            public bool Beats(Candidate other)
            {
                if (Class != other.Class)
                {
                    return Class > other.Class;
                }

                if (Length != other.Length)
                {
                    return Length > other.Length;
                }

                if (Priority != other.Priority)
                {
                    return Priority > other.Priority;
                }

                return Index < other.Index;
            }
        }

        /// <summary>
        /// Every enabled route that takes the number, best first.
        /// </summary>
        private List<Candidate> Candidates(string international, X121RouteDirection direction, byte[]? callUserData)
        {
            List<Candidate> list = new List<Candidate>();
            for (int i = 0; i < _routes.Count; i++)
            {
                X121Route r = _routes[i];
                if (!r.Enabled || !DirectionFits(r, direction))
                {
                    continue;
                }

                int cls = MatchRank(r, international, out int len);
                if (cls == ClassNone || !r.CallUserDataMatches(callUserData))
                {
                    continue;
                }

                // Insertion into a list that is short (a handful of routes match one number).
                Candidate c = new Candidate(i, cls, len, _routes[i].Priority);
                int at = list.Count;
                while (at > 0 && c.Beats(list[at - 1]))
                {
                    at--;
                }

                list.Insert(at, c);
            }

            return list;
        }

        /// <summary>
        /// The routes in the order they win: exact numbers and hunt groups, pattern routes, prefixes longest
        /// first, DNICs with a fifth digit, DNICs, countries, and the default route last. Two routes of the
        /// same kind and length keep the order they were written in. This is what the routing screen shows.
        /// </summary>
        /// <returns>
        /// The routes (PVC entries left out), in lookup order.
        /// </returns>
        public List<X121Route> RoutesInLookupOrder()
        {
            List<Candidate> order = new List<Candidate>();
            for (int i = 0; i < _routes.Count; i++)
            {
                int cls = StaticRank(_routes[i], out int len);
                if (cls == ClassNone)
                {
                    continue;
                }

                Candidate c = new Candidate(i, cls, len, _routes[i].Priority);
                int at = order.Count;
                while (at > 0 && c.Beats(order[at - 1]))
                {
                    at--;
                }

                order.Insert(at, c);
            }

            List<X121Route> result = new List<X121Route>(order.Count);
            for (int i = 0; i < order.Count; i++)
            {
                result.Add(_routes[order[i].Index]);
            }

            return result;
        }

        /// <summary>
        /// The route that wins for a number, looking only at the routes, not at whether their ports are up.
        /// Does not move a hunt group on and counts no hit.
        /// </summary>
        /// <param name="calledAddress">
        /// The called number as it came in.
        /// </param>
        /// <param name="inPort">
        /// The port the call came in on; a route back out of it is skipped. -1 for none.
        /// </param>
        /// <param name="direction">
        /// Whether the call comes in or is placed here; <see cref="X121RouteDirection.Both"/> for either.
        /// </param>
        /// <param name="callUserData">
        /// The call user data, or null when not known.
        /// </param>
        /// <returns>
        /// The winning route, or null.
        /// </returns>
        public X121Route? FindWinner(string calledAddress, int inPort = -1,
            X121RouteDirection direction = X121RouteDirection.Both, byte[]? callUserData = null)
        {
            string international = ToInternational(calledAddress);
            List<Candidate> list = Candidates(international, direction, callUserData);
            for (int i = 0; i < list.Count; i++)
            {
                X121Route r = _routes[list[i].Index];
                if (!OnlyLeadsBack(r, inPort))
                {
                    return r;
                }
            }

            return null;
        }

        /// <summary>
        /// True when every port a route could use is the port the call came in on, so the route can only send
        /// the call back to its caller.
        /// </summary>
        private bool OnlyLeadsBack(X121Route r, int inPort)
        {
            if (inPort < 0)
            {
                return false;
            }

            if (r.Type == X121RouteType.Hunt)
            {
                if (r.HuntGroupId < 0 || r.HuntGroupId >= _huntGroups.Count)
                {
                    return false;
                }

                List<int> members = _huntGroups[r.HuntGroupId].Members;
                for (int i = 0; i < members.Count; i++)
                {
                    if (members[i] != inPort)
                    {
                        return false;
                    }
                }

                return true;
            }

            if (r.DestPort != inPort)
            {
                return false;
            }

            for (int i = 0; i < r.BackupPorts.Count; i++)
            {
                if (r.BackupPorts[i] != inPort)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The one lookup: decides where a call goes, or why it cannot, and the number to send on.
        /// </summary>
        /// <remarks>
        /// In this order (see the class remarks for the X.121 clauses):
        ///  - the number is put in international form (<see cref="ToInternational"/>);
        ///  - the enabled routes that take it are ranked, best first; a route whose every port is the arrival
        ///    port is skipped and the next one is tried;
        ///  - the first one left decides. Its port must exist, otherwise there is no route. When its port is
        ///    out of order its backup ports are tried in their order (X.110 3.5 and Appendix I.1: direct
        ///    route, alternative 1, alternative 2); a hunt group takes its next member in service. If none
        ///    is in service the call is out of order;
        ///  - the number to send is written the way the chosen port asks (<see cref="X121AddressForm"/>).
        /// A hunt group moves on to its next member each time it is asked, so this is asked once per call.
        /// </remarks>
        /// <param name="calledAddress">
        /// The called number as it came in.
        /// </param>
        /// <param name="inPort">
        /// The port the call came in on; never chosen. -1 for a call placed by this node.
        /// </param>
        /// <param name="ports">
        /// What each port can do now; null takes every port to exist, be in service and keep the number as
        /// received.
        /// </param>
        /// <param name="direction">
        /// Whether the call comes in or is placed here; <see cref="X121RouteDirection.Both"/> for either.
        /// </param>
        /// <param name="callUserData">
        /// The call user data, or null when not known.
        /// </param>
        /// <returns>
        /// The decision.
        /// </returns>
        public X121RouteResult Resolve(string calledAddress, int inPort, Func<int, X121PortStatus>? ports,
            X121RouteDirection direction = X121RouteDirection.Both, byte[]? callUserData = null)
        {
            return Resolve(calledAddress, inPort, ports, false, direction, callUserData);
        }

        /// <summary>
        /// As <see cref="Resolve(string, int, Func{int, X121PortStatus}?, X121RouteDirection, byte[])"/>, for a call
        /// whose called number may already be in international form.
        /// </summary>
        /// <remarks>
        /// A call that came in on a link between networks (X.75) carries the international data number: X.75 joins
        /// networks, and a number crossing one is DNIC + NTN (X.121 7.6.1), with no prefix. The international prefix is
        /// what a user dials on an access line (X.121 7.8.1) to select that format. So such a number is taken as it is;
        /// completing it with the own DNIC would turn 31109001 arriving at England into 2342 31109001. Until 2026-11-19
        /// every number without the prefix was completed, which broke transit through any switch that also had a prefix
        /// for its own users (found by demo/large-network in RetroX25).
        /// </remarks>
        /// <param name="calledAddress">The called number as it came in.</param>
        /// <param name="inPort">The port the call came in on; -1 for none.</param>
        /// <param name="ports">The state of each port, or null.</param>
        /// <param name="receivedInternational">The call came in on a link between networks: the number is international.</param>
        /// <param name="direction">Which routes may take it.</param>
        /// <param name="callUserData">The call user data, for routes that look at it.</param>
        /// <returns>The result.</returns>
        public X121RouteResult Resolve(string calledAddress, int inPort, Func<int, X121PortStatus>? ports, bool receivedInternational,
            X121RouteDirection direction = X121RouteDirection.Both, byte[]? callUserData = null)
        {
            string received = calledAddress ?? string.Empty;
            string international = receivedInternational ? received : ToInternational(received);
            X121RouteResult result = new X121RouteResult
            {
                ReceivedAddress = received,
                InternationalAddress = international,
                CalledAddress = received,
            };

            List<Candidate> list = Candidates(international, direction, callUserData);
            X121Route? winner = null;
            Candidate win = default;
            for (int i = 0; i < list.Count; i++)
            {
                X121Route r = _routes[list[i].Index];
                if (!OnlyLeadsBack(r, inPort))
                {
                    winner = r;
                    win = list[i];
                    break;
                }
            }

            if (winner == null)
            {
                result.Outcome = X121RouteOutcome.NoRoute;
                result.Explanation = "No route takes " + Shown(received, international)
                    + (list.Count > 0 ? " (the only matching routes lead back out of the port the call came in on)" : string.Empty)
                    + ": the call is cleared, not obtainable (diagnostic 67).";
                return result;
            }

            winner.CountHit();
            result.Route = winner;
            string why = "route '" + RouteLabel(winner) + "' wins: " + X121RouteText.Describe(winner) + WhyText(win);

            if (winner.Type == X121RouteType.Hunt)
            {
                return ResolveHunt(winner, inPort, ports, result, why);
            }

            if (winner.DestPort == LocalPort)
            {
                result.Outcome = X121RouteOutcome.Local;
                result.Port = LocalPort;
                result.Explanation = "A call to " + Shown(received, international) + " is for this node (" + why + ").";
                return result;
            }

            X121PortStatus primary = Status(ports, winner.DestPort);
            if (!primary.Exists)
            {
                result.Outcome = X121RouteOutcome.NoRoute;
                result.Port = winner.DestPort;
                result.Explanation = "A call to " + Shown(received, international) + ": " + why
                    + ", but this node has no port '" + GetPortName(winner.DestPort) + "': cleared, not obtainable (diagnostic 67).";
                return result;
            }

            if (primary.InService && winner.DestPort != inPort)
            {
                return Forward(result, winner.DestPort, primary, false, why);
            }

            StringBuilder tried = new StringBuilder();
            tried.Append(GetPortName(winner.DestPort)).Append(winner.DestPort == inPort ? " (the arrival port)" : " (out of order)");
            for (int b = 0; b < winner.BackupPorts.Count; b++)
            {
                int backup = winner.BackupPorts[b];
                if (backup == inPort)
                {
                    tried.Append(", ").Append(GetPortName(backup)).Append(" (the arrival port)");
                    continue;
                }

                if (backup == LocalPort)
                {
                    result.Outcome = X121RouteOutcome.Local;
                    result.Port = LocalPort;
                    result.UsedBackup = true;
                    result.Explanation = "A call to " + Shown(received, international) + ": " + why + "; tried " + tried
                        + ", then backup " + (b + 1) + ": this node.";
                    return result;
                }

                X121PortStatus s = Status(ports, backup);
                if (s.InService)
                {
                    return Forward(result, backup, s, true, why + "; tried " + tried + ", then backup " + (b + 1));
                }

                tried.Append(", ").Append(GetPortName(backup)).Append(s.Exists ? " (out of order)" : " (no such port)");
            }

            result.Outcome = X121RouteOutcome.OutOfOrder;
            result.Port = winner.DestPort;
            result.Explanation = "A call to " + Shown(received, international) + ": " + why + "; tried " + tried
                + ": cleared, out of order.";
            return result;
        }

        private X121RouteResult ResolveHunt(X121Route winner, int inPort, Func<int, X121PortStatus>? ports, X121RouteResult result, string why)
        {
            if (winner.HuntGroupId < 0 || winner.HuntGroupId >= _huntGroups.Count || _huntGroups[winner.HuntGroupId].MemberCount == 0)
            {
                result.Outcome = X121RouteOutcome.NoRoute;
                result.Explanation = why + ", but its hunt group has no members: cleared, not obtainable (diagnostic 67).";
                return result;
            }

            X121HuntGroup hg = _huntGroups[winner.HuntGroupId];
            int count = hg.MemberCount;

            // Round-robin over the members, skipping the arrival port and any member that is down. The cursor
            // moves for every member tried, so a busy group keeps turning rather than stopping on the one that
            // cannot be used.
            for (int tried = 0; tried < count; tried++)
            {
                int cursor = Interlocked.Increment(ref hg.Cursor) - 1;
                int member = hg.Members[(int)((uint)cursor % (uint)count)];
                if (member == inPort)
                {
                    continue;
                }

                if (member == LocalPort)
                {
                    result.Outcome = X121RouteOutcome.Local;
                    result.Port = LocalPort;
                    result.Explanation = why + "; hunt member: this node.";
                    return result;
                }

                X121PortStatus s = Status(ports, member);
                if (s.InService)
                {
                    return Forward(result, member, s, false, why + "; hunt member " + GetPortName(member));
                }
            }

            result.Outcome = X121RouteOutcome.OutOfOrder;
            result.Port = hg.Members[0];
            result.Explanation = why + "; no member of its hunt group is in service: cleared, out of order.";
            return result;
        }

        private X121RouteResult Forward(X121RouteResult result, int port, X121PortStatus status, bool backup, string why)
        {
            result.Outcome = X121RouteOutcome.Forward;
            result.Port = port;
            result.UsedBackup = backup;
            result.CalledAddress = Written(result.ReceivedAddress, result.InternationalAddress, status.Form);
            result.Explanation = "A call to " + Shown(result.ReceivedAddress, result.InternationalAddress)
                + " would go to: port " + GetPortName(port) + " (" + why + ")"
                + (result.CalledAddress != result.ReceivedAddress ? ", sent on as " + result.CalledAddress : string.Empty) + ".";
            return result;
        }

        private string Written(string received, string international, X121AddressForm form)
        {
            switch (form)
            {
                case X121AddressForm.International:
                    return international;
                case X121AddressForm.National:
                    return ToNational(international);
                default:
                    return received;
            }
        }

        private static X121PortStatus Status(Func<int, X121PortStatus>? ports, int port)
        {
            return ports == null ? X121PortStatus.Up : ports(port);
        }

        private static string Shown(string received, string international)
        {
            return received == international ? received : received + " (" + international + " in international form)";
        }

        private static string WhyText(Candidate c)
        {
            switch (c.Class)
            {
                case ClassExact: return " (the whole number)";
                case ClassRegex: return " (a pattern route, tried after the exact numbers)";
                case ClassPrefix: return " (the longest prefix that matches, " + c.Length + " digits)";
                case ClassDnic5: return " (network and the digit after it, 5 digits)";
                case ClassDnic: return " (network, 4 digits)";
                case ClassDcc: return " (country, 3 digits)";
                case ClassDefault: return " (nothing more exact matches)";
                default: return string.Empty;
            }
        }

        private static string RouteLabel(X121Route r)
        {
            return r.Name.Length > 0 ? r.Name : X121RouteText.KindName(r.Type);
        }

        /// <summary>
        /// The winner's port and then the backup ports of every route that takes the number, in lookup order,
        /// without repeats and never the arrival port. Used by the runtime to try the next port when placing
        /// a call fails.
        /// </summary>
        /// <param name="calledAddress">
        /// The X.121 address to route.
        /// </param>
        /// <param name="inPort">
        /// The ingress port.
        /// </param>
        /// <param name="maxPorts">
        /// Maximum number of candidate ports to return.
        /// </param>
        /// <param name="direction">
        /// Whether the call comes in or is placed here.
        /// </param>
        /// <param name="callUserData">
        /// The call user data, or null when not known.
        /// </param>
        /// <returns>
        /// The candidate ports, primary first.
        /// </returns>
        public IReadOnlyList<int> LookupMulti(string calledAddress, int inPort, int maxPorts,
            X121RouteDirection direction, byte[]? callUserData)
        {
            List<int> result = new List<int>();
            X121RouteResult primary = Resolve(calledAddress, inPort, null, direction, callUserData);
            if (primary.IsRouted)
            {
                result.Add(primary.Port);
            }

            List<Candidate> list = Candidates(ToInternational(calledAddress), direction, callUserData);
            for (int i = 0; i < list.Count && result.Count < maxPorts; i++)
            {
                X121Route r = _routes[list[i].Index];
                for (int b = 0; b < r.BackupPorts.Count && result.Count < maxPorts; b++)
                {
                    int port = r.BackupPorts[b];
                    if (port != inPort && !result.Contains(port))
                    {
                        result.Add(port);
                    }
                }
            }

            return result;
        }
    }
}
