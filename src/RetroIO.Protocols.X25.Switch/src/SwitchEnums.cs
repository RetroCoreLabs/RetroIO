//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// The role of a switch port. Values are assigned explicitly for stable configuration.
    /// </summary>
    public enum PortType
    {
        /// <summary>
        /// Access port: faces a DTE (PAD, host, router). Runs in DCE mode by default.
        /// </summary>
        Access = 0,

        /// <summary>
        /// Trunk port: connects to another switch (switch-to-switch).
        /// </summary>
        Trunk = 1
    }

    /// <summary>
    /// The link transport a port uses. Values are assigned explicitly for stable configuration.
    /// </summary>
    public enum PortTransport
    {
        /// <summary>
        /// X.25-over-TCP (RFC 1613). The default.
        /// </summary>
        Xot = 0,

        /// <summary>
        /// LAPB over SLIP framing (legacy "hdlc-tcp").
        /// </summary>
        HdlcTcp = 1,

        /// <summary>
        /// LAPB over a serial device.
        /// </summary>
        HdlcSerial = 2,

        /// <summary>
        /// LAPB over RFC 1662 HDLC framing ("hdlc-ppp").
        /// </summary>
        HdlcPpp = 3
    }

    /// <summary>
    /// Which end of an <c>hdlc-tcp</c> port opens the TCP connection (configuration key <c>tcp</c>).
    /// </summary>
    /// <remarks>
    /// The TCP connection is the emulated cable. Which end plugs it in has nothing to do with the X.25 role:
    /// a DTE can wait for the cable as well as a DCE can. Until 2026-11-16 the role decided it (DCE listened,
    /// DTE dialled), so a DTE port could not be used against an emulator that also dials - the SINTRAN K X.25
    /// option on RetroCore, whose link only accepts replies addressed the DTE way, was the case that needed it.
    /// </remarks>
    public enum PortTcpRole
    {
        /// <summary>
        /// Follow the X.25 role: a DCE port listens, a DTE port dials. What every port did before this setting.
        /// </summary>
        FromMode = 0,

        /// <summary>
        /// Listen on <c>host:port</c> and take the peer's connection.
        /// </summary>
        Listen = 1,

        /// <summary>
        /// Dial <c>host:port</c>, and dial again when the connection is lost.
        /// </summary>
        Dial = 2,
    }

    /// <summary>
    /// The X.25 role a port takes (<c>1 = DTE</c>, <c>0 = DCE</c>).
    /// </summary>
    public enum PortMode
    {
        /// <summary>
        /// Data circuit-terminating equipment (network side). The default for access ports.
        /// </summary>
        Dce = 0,

        /// <summary>
        /// Data terminal equipment.
        /// </summary>
        Dte = 1
    }
}
