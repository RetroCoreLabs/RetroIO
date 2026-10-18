//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Link.Hdlc
{
    /// <summary>
    /// Which transparency procedure a link uses.
    /// </summary>
    public enum HdlcTransparency : byte
    {
        /// <summary>
        /// Control-escape <c>0x7D</c> with the following octet XORed by <c>0x20</c>
        /// (ITU-T X.25 clause 2.2.2.2, RFC 1662 section 4.2).
        /// </summary>
        OctetStuffed = 0,

        /// <summary>
        /// Zero-bit insertion after five contiguous ones (ITU-T X.25 clause 2.2.2.1,
        /// ITU-T Q.921 clause 2.6, RFC 1662 section 5.2).
        /// </summary>
        /// <remarks>
        /// Not implemented. Every link in this repository is octet-stuffed, and the whole
        /// capture corpus is octet-stuffed. The value exists so the option is expressible and so a
        /// future bit-synchronous link has an obvious place to attach, but selecting it throws.
        /// Building an untested bit-stuffing engine that nothing exercises would be worse than not
        /// having one.
        /// </remarks>
        BitStuffed = 1,
    }

    /// <summary>
    /// The framing policy of one link.
    /// </summary>
    /// <remarks>
    /// Immutable, and therefore thread-safe: a single instance may be shared by any number of
    /// encoders and decoders on any number of threads.
    /// </remarks>
    public sealed record HdlcFramerOptions
    {
        /// <summary>
        /// The flag octet that bounds every frame (X.25 clause 2.2.1).
        /// </summary>
        public const byte Flag = 0x7E;

        /// <summary>
        /// The control escape octet (X.25 clause 2.2.2.2).
        /// </summary>
        public const byte ControlEscape = 0x7D;

        /// <summary>
        /// The bit complemented in an escaped octet: ISO bit 6, value <c>0x20</c>.
        /// </summary>
        public const byte EscapeMask = 0x20;

        /// <summary>
        /// Transparency procedure.
        /// </summary>
        public HdlcTransparency Transparency { get; init; } = HdlcTransparency.OctetStuffed;

        /// <summary>
        /// Async-Control-Character-Map: bit <c>n</c> set means octet <c>n</c> (<c>0x00</c> to
        /// <c>0x1F</c>) is escaped on transmit, on top of the flag and the escape octet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one genuinely irreconcilable difference between the two stacks that used to
        /// have their own framers, and it is why the option exists rather than a single behaviour:
        /// </para>
        ///  - X.25 side - <see cref="AccmAllControlOctets"/>. The RFC 1662 section 7.1 default, and what the old <c>HdlcFramer</c> and <c>HdlcFrameCodec</c> both did.
        ///  - XMSG / ND side - <see cref="AccmNone"/>. The ITU-T X.25 clause 2.2.2.2 minimum, which escapes only the flag and the escape. MEASURED: across every capture in this repository the only octets ever following <c>0x7D</c> are <c>0x5D</c> and <c>0x5E</c>, so the real ND machines do not ACCM-escape.
        /// <para>
        /// Receive needs no equivalent option: unescaping is a blind XOR <c>0x20</c>, so a receiver
        /// accepts either policy.
        /// </para>
        /// </remarks>
        public uint SendAccm { get; init; } = AccmAllControlOctets;

        /// <summary>
        /// Escape every octet below <c>0x20</c> (RFC 1662 section 7.1 default).
        /// </summary>
        public const uint AccmAllControlOctets = 0xFFFFFFFF;

        /// <summary>
        /// Escape only the flag and the control escape (X.25 clause 2.2.2.2).
        /// </summary>
        public const uint AccmNone = 0x00000000;

        /// <summary>
        /// Whether this framer owns the frame check sequence.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>true</c> - the encoder computes and appends the FCS, and the decoder
        /// verifies the residue and reports <see cref="HdlcDecodeStatus.BadFcs"/>. This is what a
        /// link that terminates HDLC itself wants.
        /// </para>
        /// <para>
        /// <c>false</c> - pure delimiting and transparency, no checksum. One of the
        /// engines this class replaces was framing-only by design, with the FCS owned by the layer
        /// above it (<c>CodecLinkBridge</c>), and that separation is worth keeping: it is the
        /// cleaner layering, and a codec that does not know about the FCS can carry anything.
        /// </para>
        /// </remarks>
        public bool UseFcs { get; init; } = true;

        /// <summary>
        /// Smallest acceptable frame in octets between the flags, FCS included, transparency
        /// excluded. Anything shorter is a runt.
        /// </summary>
        /// <remarks>
        /// Four for X.25 modulo-8 (clause 2.3.5.3 b) and for RFC 1662 (section 4.3); Q.921
        /// clause 2.9 b wants five or six because its address is two octets. A runt is discarded
        /// silently and, per RFC 1662, is explicitly not counted as an FCS error - which
        /// is why <see cref="HdlcDecodeStatus.Runt"/> is a separate status.
        /// </remarks>
        public int MinFrameLength { get; init; } = HdlcFcs.MinimumFrameLength;

        /// <summary>
        /// Largest acceptable frame in octets between the flags, FCS included, transparency
        /// excluded.
        /// </summary>
        /// <remarks>
        /// The standards define no maximum; this is a local guard, which is precisely why it used
        /// to be missing. Two of the five framers this class replaces had no cap at all and grew a
        /// <c>List{byte}</c> without bound, so a peer that opened a frame and never sent a
        /// closing flag could exhaust memory. The limit is enforced as each octet is unstuffed,
        /// not when the frame closes - checking at the close would leave the guard useless for the
        /// case it exists for.
        /// </remarks>
        public int MaxFrameLength { get; init; } = 4096;

        /// <summary>
        /// When <c>true</c>, a frame that fails the FCS check is still handed to the
        /// caller with <see cref="HdlcDecodeStatus.BadFcs"/> rather than dropped.
        /// </summary>
        /// <remarks>
        /// The standards require a silent discard (X.25 clause 2.3.5.3, Q.921 clause 2.9), so this
        /// is off by default. It exists because the old <c>HdlcFramer</c> raised bad frames with an
        /// <c>FcsValid=false</c> flag and its callers counted them; an analyser or a capture tool
        /// wants the same. Runts, aborts and overlong frames are never delivered whatever this says.
        /// </remarks>
        public bool DeliverCorruptFrames { get; init; }

        /// <summary>
        /// The policy the X.25 links use.
        /// </summary>
        public static HdlcFramerOptions X25 { get; } = new HdlcFramerOptions
        {
            SendAccm = AccmAllControlOctets,
            MaxFrameLength = 4096,
        };

        /// <summary>
        /// The policy the XMSG / ND links use.
        /// </summary>
        public static HdlcFramerOptions Xmsg { get; } = new HdlcFramerOptions
        {
            SendAccm = AccmNone,
            MaxFrameLength = 4096,
        };

        /// <summary>
        /// Delimiting and transparency only, with the FCS left to the layer above.
        /// </summary>
        /// <remarks>
        /// The policy the framing-codec sublayer uses. <see cref="MinFrameLength"/> drops to one
        /// because without an FCS there is no four-octet floor - any non-empty run between two
        /// flags is a frame as far as this layer is concerned.
        /// </remarks>
        public static HdlcFramerOptions FramingOnly { get; } = new HdlcFramerOptions
        {
            SendAccm = AccmAllControlOctets,
            UseFcs = false,
            MinFrameLength = 1,
            MaxFrameLength = 4096,
        };

        /// <summary>
        /// Throws when the options cannot describe a working link.
        /// </summary>
        /// <remarks>
        /// Called once, when a decoder is constructed, so nothing on the per-octet path has to
        /// re-check it.
        /// </remarks>
        internal void Validate()
        {
            if (Transparency == HdlcTransparency.BitStuffed)
            {
                throw new NotSupportedException(
                    "Bit stuffing is not implemented. Every link here is octet-stuffed; see the "
                    + "remarks on HdlcTransparency.BitStuffed.");
            }

            if (MinFrameLength < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MinFrameLength), MinFrameLength, "A frame must be at least one octet.");
            }

            if (MaxFrameLength < MinFrameLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxFrameLength), MaxFrameLength,
                    "The maximum frame length cannot be below the minimum.");
            }
        }

        /// <summary>
        /// Whether an octet must be escaped on transmit under this policy.
        /// </summary>
        /// <param name="value">
        /// The octet.
        /// </param>
        /// <returns>
        /// <c>true</c> when it needs a control escape in front of it.
        /// </returns>
        internal bool MustEscape(byte value)
        {
            if (value == Flag || value == ControlEscape)
            {
                return true;
            }

            return value < 0x20 && (SendAccm & (1u << value)) != 0;
        }
    }
}
