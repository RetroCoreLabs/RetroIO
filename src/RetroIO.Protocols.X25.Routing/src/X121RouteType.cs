//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Routing
{
    /// <summary>
    /// The kind of an X.121 routing-table entry: what part of the called number it looks at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values are assigned explicitly so configuration indices and any serialized form remain stable.
    /// The order the kinds win in is not the order of the values; it is set by
    /// <see cref="X121RoutingTable"/> (most exact first, the default route last).
    /// </para>
    /// <para>
    /// The kinds follow the number analysis of X.121 (10/2000) 7.12: an originating network reads
    /// the first three digits (the Data Country Code, 7.12.1), may read the fourth, network, digit
    /// of the DNIC and the first digit after it (7.12.2), and a transit network reads the same
    /// (7.12.6). Text: <c>Specs\X25\T-REC-X.121-200010-I!!PDF-E.txt</c> lines 643-682.
    /// </para>
    /// </remarks>
    public enum X121RouteType
    {
        /// <summary>
        /// Permanent virtual circuit cross-connect. Handled at setup time, skipped by dynamic lookup.
        /// </summary>
        Pvc = 0,

        /// <summary>
        /// One whole number: a local number or a full international address. A local number (the
        /// network terminal number, X.121 7.6.2) also matches the same number written with one of
        /// the table's own DNICs in front.
        /// </summary>
        Exact = 1,

        /// <summary>
        /// Hunt group: one whole number that is answered by any member port of a group, in turn.
        /// </summary>
        Hunt = 2,

        /// <summary>
        /// The leading digits of the full international number. The longest matching prefix wins.
        /// </summary>
        Prefix = 3,

        /// <summary>
        /// A network: the four-digit DNIC (X.121 7.2), or the DNIC with the digit after it when one
        /// DNIC is shared by several networks (X.121 7.12.2, Annex A.2). The five-digit form wins over
        /// the four-digit one.
        /// </summary>
        Dnic = 4,

        /// <summary>
        /// Catch-all default route, used only when nothing else matches: usually the gateway (X.75)
        /// toward other networks.
        /// </summary>
        Default = 5,

        /// <summary>
        /// Regular-expression address match, for the cases the other kinds cannot say. Evaluated like
        /// <see cref="Exact"/> (first match in table order) but after every exact and hunt entry.
        /// </summary>
        Regex = 6,

        /// <summary>
        /// A country: the three-digit Data Country Code, the first three digits of a DNIC
        /// (X.121 7.12.1, Annex J lists them). Ranks below every DNIC.
        /// </summary>
        Dcc = 7
    }

    /// <summary>
    /// Which calls a route is for.
    /// </summary>
    /// <remarks>
    /// X.25 and X.121 route every call the same way; a direction is our own addition for a node that
    /// both places and answers calls (the runtime): a route can be kept for the calls it places, or
    /// for the calls it answers.
    /// </remarks>
    public enum X121RouteDirection
    {
        /// <summary>
        /// Every call, placed or answered. The default.
        /// </summary>
        Both = 0,

        /// <summary>
        /// Only calls that come in to this node.
        /// </summary>
        Incoming = 1,

        /// <summary>
        /// Only calls this node places.
        /// </summary>
        Outgoing = 2
    }

    /// <summary>
    /// How a port wants the called number written when a call is sent out of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// X.121 7.8.1: a prefix is "not signalled across internetwork or international boundaries", and
    /// 7.12.6 and 7.12.7: a transit network must receive, and send on, "the complete international
    /// data number". So a port toward another network (a gateway, a trunk) gets the full international
    /// number without the prefix.
    /// </para>
    /// <para>
    /// Whether a local terminal gets its own number with or without the own DNIC is not in X.121 or
    /// X.25, so it is a setting of the port, and the default is to leave the number as it came in.
    /// </para>
    /// </remarks>
    public enum X121AddressForm
    {
        /// <summary>
        /// The called number goes on exactly as it came in. The default.
        /// </summary>
        AsReceived = 0,

        /// <summary>
        /// The complete international number: the prefix taken off, a local number completed with the
        /// own DNIC (X.121 7.8.1, 7.12.6, 7.12.7).
        /// </summary>
        International = 1,

        /// <summary>
        /// The number inside our own network: the international number with the own DNIC taken off when
        /// it starts with one. A number for another network is sent in its international form.
        /// </summary>
        National = 2
    }
}
