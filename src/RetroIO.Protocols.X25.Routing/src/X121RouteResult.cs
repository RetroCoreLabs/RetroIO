//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// What the one lookup decided for a call.
    /// </summary>
    public enum X121RouteOutcome
    {
        /// <summary>
        /// No route takes the number, or the winning route names a port that does not exist. The caller
        /// clears the call with Not obtainable, diagnostic 67 (X.25 10/96 Table C.1 row 24).
        /// </summary>
        NoRoute = 0,

        /// <summary>
        /// The call goes out of <see cref="X121RouteResult.Port"/>.
        /// </summary>
        Forward = 1,

        /// <summary>
        /// The call is for this node (the route's port is <see cref="X121RoutingTable.LocalPort"/>).
        /// </summary>
        Local = 2,

        /// <summary>
        /// A route takes the number but its port, its backup and every hunt member are out of order.
        /// The caller clears with Out of order (X.25 4.6.4 item 1, Table C.1).
        /// </summary>
        OutOfOrder = 3
    }

    /// <summary>
    /// What a port can do right now, as the lookup needs to know it.
    /// </summary>
    public readonly struct X121PortStatus
    {
        /// <summary>
        /// Creates a port status.
        /// </summary>
        /// <param name="exists">
        /// True when the node has the port.
        /// </param>
        /// <param name="inService">
        /// True when the port can carry a call now.
        /// </param>
        /// <param name="form">
        /// How the port wants the called number written.
        /// </param>
        public X121PortStatus(bool exists, bool inService, X121AddressForm form)
        {
            Exists = exists;
            InService = exists && inService;
            Form = form;
        }

        /// <summary>
        /// True when the node has the port.
        /// </summary>
        public bool Exists { get; }

        /// <summary>
        /// True when the port can carry a call now (it is started and its line is up).
        /// </summary>
        public bool InService { get; }

        /// <summary>
        /// How the port wants the called number written.
        /// </summary>
        public X121AddressForm Form { get; }

        /// <summary>
        /// A port that exists, is in service, and takes the number as it came in.
        /// </summary>
        public static X121PortStatus Up => new X121PortStatus(true, true, X121AddressForm.AsReceived);

        /// <summary>
        /// A port that exists but is out of order.
        /// </summary>
        public static X121PortStatus Down => new X121PortStatus(true, false, X121AddressForm.AsReceived);

        /// <summary>
        /// A port number the node does not have.
        /// </summary>
        public static X121PortStatus Missing => new X121PortStatus(false, false, X121AddressForm.AsReceived);
    }

    /// <summary>
    /// The answer of <see cref="X121RoutingTable.Resolve"/>: the outcome, the route that decided it, the
    /// port, the number to send, and why, in words.
    /// </summary>
    public sealed class X121RouteResult
    {
        /// <summary>
        /// What happens to the call.
        /// </summary>
        public X121RouteOutcome Outcome { get; internal set; }

        /// <summary>
        /// The route that decided; null when no route takes the number.
        /// </summary>
        public X121Route? Route { get; internal set; }

        /// <summary>
        /// The port the call goes out of for <see cref="X121RouteOutcome.Forward"/>, the port that is out of
        /// order for <see cref="X121RouteOutcome.OutOfOrder"/>; <see cref="X121RoutingTable.LocalPort"/> for a
        /// local call; -1 for no route.
        /// </summary>
        public int Port { get; internal set; } = -1;

        /// <summary>
        /// True when the route's own port was out of order and the call goes to its backup port.
        /// </summary>
        public bool UsedBackup { get; internal set; }

        /// <summary>
        /// The called number exactly as it came in.
        /// </summary>
        public string ReceivedAddress { get; internal set; } = string.Empty;

        /// <summary>
        /// The called number in full international form: the international prefix taken off, a local
        /// number completed with the first own DNIC (X.121 7.8.1, 7.6.2). The lookup matches on this.
        /// </summary>
        public string InternationalAddress { get; internal set; } = string.Empty;

        /// <summary>
        /// The called number to put in the call that goes out of <see cref="Port"/>, in the form that port
        /// asks for (<see cref="X121AddressForm"/>). The number as received for a local call or a refusal.
        /// </summary>
        public string CalledAddress { get; internal set; } = string.Empty;

        /// <summary>
        /// Why, in plain words: which route won and what it matched, or why there is none.
        /// </summary>
        public string Explanation { get; internal set; } = string.Empty;

        /// <summary>
        /// True for <see cref="X121RouteOutcome.Forward"/> and <see cref="X121RouteOutcome.Local"/>.
        /// </summary>
        public bool IsRouted => Outcome == X121RouteOutcome.Forward || Outcome == X121RouteOutcome.Local;

        /// <summary>
        /// A result for a call that is for this node without any route deciding it (the switch's own
        /// management address, for example).
        /// </summary>
        /// <param name="calledAddress">
        /// The called number.
        /// </param>
        /// <param name="explanation">
        /// Why, in words.
        /// </param>
        /// <returns>
        /// A local result.
        /// </returns>
        public static X121RouteResult ForThisNode(string calledAddress, string explanation)
        {
            return new X121RouteResult
            {
                Outcome = X121RouteOutcome.Local,
                Port = X121RoutingTable.LocalPort,
                ReceivedAddress = calledAddress ?? string.Empty,
                InternationalAddress = calledAddress ?? string.Empty,
                CalledAddress = calledAddress ?? string.Empty,
                Explanation = explanation ?? string.Empty,
            };
        }

        /// <summary>
        /// The explanation.
        /// </summary>
        /// <returns>
        /// <see cref="Explanation"/>.
        /// </returns>
        public override string ToString()
        {
            return Explanation;
        }
    }
}
