//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// What a built-in service (the TAD) wants done on a call.
    /// </summary>
    public enum TadOutputKind
    {
        /// <summary>
        /// The service accepts the call.
        /// </summary>
        CallAccepted,

        /// <summary>
        /// The service sends user data to the caller.
        /// </summary>
        Data,

        /// <summary>
        /// The service clears the call.
        /// </summary>
        Clear,
    }

    /// <summary>
    /// One request from the TAD to the packet layer: send this data, or clear this call.
    /// </summary>
    /// <remarks>
    /// The TAD does not build X.25 packets and does not count P(S)/P(R). Those belong to the one
    /// packet-layer engine that carries the call (an XOT circuit's engine), which turns this
    /// request into a packet with the right sequence numbers. What the TAD does decide is the
    /// part of a data packet that carries meaning for the PAD: the Q bit (an X.29 control
    /// message rather than text), the M bit (more of the same message follows) and the D bit.
    /// </remarks>
    public sealed class TadOutputEventArgs : EventArgs
    {
        /// <summary>
        /// What is wanted.
        /// </summary>
        public TadOutputKind Kind { get; init; }

        /// <summary>
        /// The logical channel of the call, as the runtime numbers it.
        /// </summary>
        public ushort Lcn { get; init; }

        /// <summary>
        /// The user data, for <see cref="TadOutputKind.Data"/>.
        /// </summary>
        public byte[] Data { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Qualifier bit: set on an X.29 control message.
        /// </summary>
        public bool QBit { get; init; }

        /// <summary>
        /// More-data bit: set when the next packet continues this message.
        /// </summary>
        public bool MBit { get; init; }

        /// <summary>
        /// Delivery-confirmation bit.
        /// </summary>
        public bool DBit { get; init; }

        /// <summary>
        /// Clearing cause, for <see cref="TadOutputKind.Clear"/>.
        /// </summary>
        public byte Cause { get; init; }

        /// <summary>
        /// Diagnostic code, for <see cref="TadOutputKind.Clear"/>.
        /// </summary>
        public byte Diagnostic { get; init; }
    }
}
