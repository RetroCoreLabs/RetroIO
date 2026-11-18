//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.X25.PacketLayer;
using RetroIO.Protocols.X25.Routing;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// The one routing decision of a switch: for a call that came in on any port it says whether the
    /// call is for the switch itself (the management address), which port it goes out of, or why it is
    /// refused. The port the call came in on never decides; it only sends the refusal this returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The causes are those of <see cref="X25CallDecision"/>, from X.25 Table C.1 and 4.6.4:
    /// </para>
    ///  - no route in the table, or a route to a port the switch does not have: Not obtainable, 67;
    ///  - a route to a port whose line is out of order (the port is stopped, or no peer is linked on
    ///    it): Out of order, 0. X.25 4.6.4 item 1: during an out of order condition "the DCE will clear
    ///    any incoming virtual call with the cause Out of order".
    /// <para>
    /// Until 2026-11-18 <see cref="SwitchCore"/> looked the route up itself and cleared every failure,
    /// a port that was down included, with 0x0D and diagnostic 0.
    /// </para>
    /// </remarks>
    public sealed class SwitchCallRouter
    {
        private readonly X121RoutingTable _table;
        private readonly Func<int, ISwitchPort?> _portById;

        /// <summary>
        /// Creates the router over a routing table and the switch's ports.
        /// </summary>
        /// <param name="table">
        /// The X.121 routing table.
        /// </param>
        /// <param name="portById">
        /// Finds a port by its number; null when the switch has no such port.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when an argument is null.
        /// </exception>
        public SwitchCallRouter(X121RoutingTable table, Func<int, ISwitchPort?> portById)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _portById = portById ?? throw new ArgumentNullException(nameof(portById));
        }

        /// <summary>
        /// The reserved management address. A call to it is for the switch itself. Null or empty
        /// means the switch has none.
        /// </summary>
        public string? ManagementAddress { get; set; }

        /// <summary>
        /// Whether a called address is the switch's own management address.
        /// </summary>
        /// <param name="calledAddress">
        /// The called X.121 address.
        /// </param>
        /// <returns>
        /// True for the management address.
        /// </returns>
        public bool IsForThisSwitch(string calledAddress)
        {
            return !string.IsNullOrEmpty(ManagementAddress) && calledAddress == ManagementAddress;
        }

        /// <summary>
        /// Decides where a call goes.
        /// </summary>
        /// <remarks>
        /// A hunt group route moves on to its next member each time it is asked (see
        /// <see cref="X121RoutingTable.TryLookup"/>), so this is asked once per call.
        /// </remarks>
        /// <param name="calledAddress">
        /// The called X.121 address.
        /// </param>
        /// <param name="inPortId">
        /// The port the call came in on; a route back out of it is never chosen.
        /// </param>
        /// <returns>
        /// Deliver locally for the management address, forward with the port, or a refusal with the
        /// cause and diagnostic the port the call came in on is to clear it with.
        /// </returns>
        public X25CallDecision Decide(string calledAddress, int inPortId)
        {
            X121RouteResult r = Resolve(calledAddress, inPortId);
            return ToDecision(r);
        }

        /// <summary>
        /// The full answer of the one lookup for a call (<see cref="X121RoutingTable.Resolve"/>): which route won,
        /// why, the port, and the number to send on. Used by <see cref="Decide"/>, and by the console's
        /// "test an address".
        /// </summary>
        /// <param name="calledAddress">
        /// The called X.121 address.
        /// </param>
        /// <param name="inPortId">
        /// The port the call came in on; -1 for none.
        /// </param>
        /// <returns>
        /// The result; for the management address, a local result with no route.
        /// </returns>
        public X121RouteResult Resolve(string calledAddress, int inPortId)
        {
            if (IsForThisSwitch(calledAddress))
            {
                return X121RouteResult.ForThisNode(calledAddress, "A call to " + calledAddress + " is the switch's management address.");
            }

            return _table.Resolve(calledAddress, inPortId, PortStatus);
        }

        /// <summary>
        /// Turns a lookup result into the decision a port acts on.
        /// </summary>
        /// <param name="r">
        /// The lookup result.
        /// </param>
        /// <returns>
        /// The decision.
        /// </returns>
        public static X25CallDecision ToDecision(X121RouteResult r)
        {
            switch (r.Outcome)
            {
                case X121RouteOutcome.Forward:
                    return X25CallDecision.Forward(r.Port, r.CalledAddress == r.ReceivedAddress ? null : r.CalledAddress);
                case X121RouteOutcome.Local:
                    return X25CallDecision.DeliverLocally();
                case X121RouteOutcome.OutOfOrder:
                    return X25CallDecision.DestinationOutOfOrder();
                default:
                    // No route, or a route to a port this switch does not have: there is nowhere the number can be
                    // reached (Table C.1 row 24).
                    return X25CallDecision.NoRoute();
            }
        }

        // What the lookup needs to know of a port: that the switch has it, whether it can carry a call now, and
        // how it wants the called number written (PortConfig.CalledAddressForm).
        private X121PortStatus PortStatus(int portId)
        {
            ISwitchPort? port = _portById(portId);
            if (port == null)
            {
                return X121PortStatus.Missing;
            }

            return new X121PortStatus(true, IsInService(port), port.Config.CalledAddressForm);
        }
        /// <summary>
        /// Whether a port can carry a call now: it is started and a peer is linked on it. These are the
        /// two things every port's <c>PlaceCall</c> checks before it tries.
        /// </summary>
        /// <param name="port">
        /// The port.
        /// </param>
        /// <returns>
        /// False when the port's line is out of order.
        /// </returns>
        public static bool IsInService(ISwitchPort port)
        {
            return port.Active && port.PeerConnected;
        }
    }
}
