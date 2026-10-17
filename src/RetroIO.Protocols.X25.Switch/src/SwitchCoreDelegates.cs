//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.X25.Switch.Model;

namespace RetroIO.Protocols.X25.Switch
{
    /// <summary>
    /// Raised when a completed circuit produces a Call Detail Record.
    /// </summary>
    /// <param name="cdr">
    /// The record for the torn-down circuit.
    /// </param>
    public delegate void CdrHandler(Cdr cdr);

    /// <summary>
    /// Raised when the switch establishes a routed circuit between two ports.
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
    public delegate void CallSetupHandler(string callingAddress, string calledAddress, int inPort, int outPort);

    /// <summary>
    /// Raised when the switch clears a circuit.
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
    public delegate void CallClearHandler(int port, ushort lci, byte cause, byte diagnostic);
}
