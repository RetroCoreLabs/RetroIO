//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Client
{
    /// <summary>
    /// A single X.25 virtual circuit as seen by an <see cref="X25Client"/> application.
    /// </summary>
    /// <remarks>
    /// The circuit handle passed to the client callbacks. In this runtime the
    /// logical channel number (<see cref="Lcn"/>) is the circuit's identity; the facade tracks one
    /// <see cref="X25Vc"/> per active LCN.
    /// </remarks>
    public sealed class X25Vc
    {
        /// <summary>
        /// Logical channel number identifying this circuit on the link.
        /// </summary>
        public ushort Lcn { get; }

        /// <summary>
        /// Called (destination) X.121 address for this circuit.
        /// </summary>
        public string CalledAddress { get; }

        /// <summary>
        /// Calling (source) X.121 address for this circuit.
        /// </summary>
        public string CallingAddress { get; }

        /// <summary>
        /// True once the circuit is established (call accepted / connected).
        /// </summary>
        public bool Connected { get; internal set; }

        /// <summary>
        /// True for a circuit created by an inbound call, false for an outbound call we placed.
        /// </summary>
        public bool Incoming { get; }

        /// <summary>
        /// Arbitrary per-circuit context an application may attach.
        /// </summary>
        public object? UserData { get; set; }

        /// <summary>
        /// Set by an application from the incoming-call callback to accept the circuit. Read back by
        /// the facade to decide accept vs reject.
        /// </summary>
        internal bool AcceptRequested { get; set; }

        /// <summary>
        /// Creates a virtual-circuit handle.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel number.
        /// </param>
        /// <param name="calledAddress">
        /// Called X.121 address.
        /// </param>
        /// <param name="callingAddress">
        /// Calling X.121 address.
        /// </param>
        /// <param name="incoming">
        /// True if this circuit arose from an inbound call.
        /// </param>
        internal X25Vc(ushort lcn, string calledAddress, string callingAddress, bool incoming)
        {
            Lcn = lcn;
            CalledAddress = calledAddress ?? string.Empty;
            CallingAddress = callingAddress ?? string.Empty;
            Incoming = incoming;
        }
    }
}
