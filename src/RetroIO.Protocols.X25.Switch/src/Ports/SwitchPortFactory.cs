//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// Makes the switch port a configuration asks for. The one place that turns a port's
    /// <see cref="PortConfig.Transport"/> into a class.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both programs that run a switch (the <c>x25switch</c> program and the Desktop's built-in switch) make
    /// their ports here, so a new kind of link is added in one place and both programs get it.
    /// </para>
    /// <para>
    /// A transport that this build cannot run is refused with an exception that names the port and the
    /// transport. It is never replaced by a different transport. (Until 2026-11-12 both programs built an XOT
    /// port for every configured port whatever the transport said, so a port written as <c>hdlc-tcp</c> ran
    /// as XOT and failed against a real HDLC peer with nothing in the log to say why.)
    /// </para>
    /// </remarks>
    public static class SwitchPortFactory
    {
        /// <summary>
        /// Makes the port for a configuration.
        /// </summary>
        /// <param name="config">
        /// The port's configuration.
        /// </param>
        /// <param name="logger">
        /// Where the port reports what happens to its link. Null reports nothing.
        /// </param>
        /// <returns>
        /// A port that has not been started.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="config"/> is null.
        /// </exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when the port's transport is not one this build can run. The message names the port and
        /// the transport.
        /// </exception>
        public static ISwitchPort Create(PortConfig config, ILogger? logger = null)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            switch (config.Transport)
            {
                case PortTransport.Xot:
                    return new XotSwitchPort(config, logger);

                case PortTransport.HdlcTcp:
                case PortTransport.HdlcPpp:
                    // hdlc-ppp is the same thing under an older name (as in the C emulator): LAPB over 0x7E
                    // framing on TCP.
                    return new HdlcTcpSwitchPort(config, logger);

                case PortTransport.HdlcSerial:
                    return new HdlcSerialSwitchPort(config, logger);

                default:
                    throw new NotSupportedException(
                        "Port " + config.Id + " (" + config.Type + "): the transport '" + Describe(config.Transport)
                        + "' is not supported by this build, so the port cannot be run. "
                        + "It was not replaced by another transport.");
            }
        }

        /// <summary>
        /// Gives a transport the name the configuration file uses for it.
        /// </summary>
        /// <param name="transport">
        /// The transport.
        /// </param>
        /// <returns>
        /// The configuration name, or the number for a value that is not one of the known transports.
        /// </returns>
        public static string Describe(PortTransport transport)
        {
            switch (transport)
            {
                case PortTransport.Xot: return "xot";
                case PortTransport.HdlcTcp: return "hdlc-tcp";
                case PortTransport.HdlcPpp: return "hdlc-ppp";
                case PortTransport.HdlcSerial: return "hdlc-serial";
                default: return "unknown transport value " + ((int)transport).ToString();
            }
        }
    }
}
