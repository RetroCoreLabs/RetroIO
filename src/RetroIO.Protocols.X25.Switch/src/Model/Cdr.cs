//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Switch.Model
{
    /// <summary>
    /// A Call Detail Record: a summary of one completed circuit, produced when a stitch is torn down.
    /// </summary>
    public sealed class Cdr
    {
        /// <summary>
        /// Start timestamp (milliseconds).
        /// </summary>
        public long StartTimeMs { get; set; }

        /// <summary>
        /// End timestamp (milliseconds).
        /// </summary>
        public long EndTimeMs { get; set; }

        /// <summary>
        /// Originating X.121 address.
        /// </summary>
        public string Calling { get; set; } = string.Empty;

        /// <summary>
        /// Destination X.121 address.
        /// </summary>
        public string Called { get; set; } = string.Empty;

        /// <summary>
        /// Ingress port number.
        /// </summary>
        public int InPort { get; set; }

        /// <summary>
        /// Egress port number.
        /// </summary>
        public int OutPort { get; set; }

        /// <summary>
        /// Ingress logical channel.
        /// </summary>
        public ushort InLci { get; set; }

        /// <summary>
        /// Egress logical channel.
        /// </summary>
        public ushort OutLci { get; set; }

        /// <summary>
        /// Packets forwarded ingress to egress.
        /// </summary>
        public uint PacketsForward { get; set; }

        /// <summary>
        /// Packets forwarded egress to ingress.
        /// </summary>
        public uint PacketsReverse { get; set; }

        /// <summary>
        /// Bytes forwarded ingress to egress.
        /// </summary>
        public ulong BytesForward { get; set; }

        /// <summary>
        /// Bytes forwarded egress to ingress.
        /// </summary>
        public ulong BytesReverse { get; set; }

        /// <summary>
        /// Clearing cause code.
        /// </summary>
        public byte ClearCause { get; set; }

        /// <summary>
        /// Diagnostic code.
        /// </summary>
        public byte ClearDiagnostic { get; set; }

        /// <summary>
        /// When the far end answered (milliseconds); 0 for a call that was never connected.
        /// </summary>
        public long ConnectedTimeMs { get; set; }

        /// <summary>
        /// When the call started, by the wall clock: seconds since 1970-01-01 UTC. The millisecond times are on the
        /// switch's own clock, which says how long a call took but not when it happened.
        /// </summary>
        public long StartWallSeconds { get; set; }

        /// <summary>
        /// Charging segments the calling DTE sent (ITU-T D.12: 64 octets, per message, rounded up).
        /// </summary>
        public ulong SegmentsForward { get; set; }

        /// <summary>
        /// Charging segments the called DTE sent.
        /// </summary>
        public ulong SegmentsReverse { get; set; }

        /// <summary>
        /// The account the caller's side was billed to (by NUI, the port's default NUI, or the calling address); empty
        /// when no account applies.
        /// </summary>
        public string Account { get; set; } = string.Empty;

        /// <summary>
        /// The network user identifier the call was billed by (X.25 6.21): the one it named, or the port's default NUI
        /// when it named none; empty when billed by calling address or not at all.
        /// </summary>
        public string Nui { get; set; } = string.Empty;

        /// <summary>
        /// True when the caller asked for reverse charging (X.25 6.18).
        /// </summary>
        public bool ReverseCharging { get; set; }

        /// <summary>
        /// True when the DTE to be charged asked for, and was given, the charging answer (X.25 6.22).
        /// </summary>
        public bool ChargingRequested { get; set; }

        /// <summary>
        /// The account the called side was billed to (by the called address, or by the NUI the called DTE named in its
        /// Call Accepted, X.25 6.21.3); empty when none applies. With reverse charging (6.18) this is the account that
        /// pays, so a bill made from the record file alone needs it. Column 24.
        /// </summary>
        public string CalledAccount { get; set; } = string.Empty;

        /// <summary>
        /// The X.75 call identifier, six hex digits (column 14). This switch has no X.75 and writes <c>000000</c>; a line
        /// read back from x25emu's record file carries the one its call had (X.75 5.3.2).
        /// </summary>
        public string CallIdentifier { get; set; } = "000000";
    }
}
