//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Globalization;

namespace RetroIO.Protocols.X25.Switch.Management
{
    /// <summary>
    /// Formats the asynchronous management events pushed to subscribed monitor sessions.
    /// </summary>
    /// <remarks>
    /// Each event is a single LF-terminated line with space-delimited <c>key=value</c> fields.
    /// </remarks>
    public static class ManagementEvents
    {
        /// <summary>
        /// Formats a call-setup event.
        /// </summary>
        /// <param name="callingAddress">
        /// Originating X.121 address.
        /// </param>
        /// <param name="calledAddress">
        /// Destination X.121 address.
        /// </param>
        /// <param name="inPort">
        /// Ingress port number.
        /// </param>
        /// <param name="outPort">
        /// Egress port number.
        /// </param>
        /// <returns>
        /// The formatted event line.
        /// </returns>
        public static string CallSetup(string callingAddress, string calledAddress, int inPort, int outPort)
        {
            return "EVENT call_setup calling=" + callingAddress + " called=" + calledAddress
                + " in_port=" + inPort.ToString(CultureInfo.InvariantCulture)
                + " out_port=" + outPort.ToString(CultureInfo.InvariantCulture) + "\n";
        }

        /// <summary>
        /// Formats a call-clear event.
        /// </summary>
        /// <param name="port">
        /// Port number the clear was observed on.
        /// </param>
        /// <param name="lci">
        /// Logical channel that cleared.
        /// </param>
        /// <param name="cause">
        /// Clearing cause code.
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code.
        /// </param>
        /// <returns>
        /// The formatted event line.
        /// </returns>
        public static string CallClear(int port, ushort lci, byte cause, byte diagnostic)
        {
            return "EVENT call_clear port=" + port.ToString(CultureInfo.InvariantCulture)
                + " lci=" + lci.ToString(CultureInfo.InvariantCulture)
                + " cause=" + cause.ToString("X2", CultureInfo.InvariantCulture)
                + " diag=" + diagnostic.ToString("X2", CultureInfo.InvariantCulture) + "\n";
        }
    }
}
