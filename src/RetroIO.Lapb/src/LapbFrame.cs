//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Hdlc
{
    /// <summary>
    /// LAPB frame classification by control-field format.
    /// </summary>
    public enum LapbFrameKind
    {
        /// <summary>
        /// Information transfer frame (control bit 0 = 0); carries an info field.
        /// </summary>
        Information,

        /// <summary>
        /// Supervisory frame (control bits 1..0 = <c>0b01</c>): RR / RNR / REJ.
        /// </summary>
        Supervisory,

        /// <summary>
        /// Unnumbered frame (control bits 1..0 = <c>0b11</c>): SABM / UA / DISC / DM / FRMR.
        /// </summary>
        Unnumbered,
    }

    /// <summary>
    /// Supervisory-frame subtype, decoded from the low nibble of the control byte.
    /// </summary>
    /// <remarks>
    /// Per the ND LAPB spec section 2.2.2 the subtype MUST be decoded via <c>ctrl AND 0x0F</c>;
    /// <c>ctrl AND 0x03</c> alone cannot tell RR, RNR and REJ apart. The low nibble values are:
    ///  - <c>0x1</c> - Receive Ready (RR).
    ///  - <c>0x5</c> - Receive Not Ready (RNR).
    ///  - <c>0x9</c> - Reject (REJ).
    /// </remarks>
    public enum LapbSupervisoryKind
    {
        /// <summary>
        /// The frame is not a supervisory frame, or its low nibble matches no known subtype.
        /// </summary>
        Unknown,

        /// <summary>
        /// Receive Ready (RR), low nibble <c>0x1</c>: acknowledges up to N(R) and clears peer-busy.
        /// </summary>
        ReceiveReady,

        /// <summary>
        /// Receive Not Ready (RNR), low nibble <c>0x5</c>: acknowledges up to N(R) and sets peer-busy.
        /// </summary>
        ReceiveNotReady,

        /// <summary>
        /// Reject (REJ), low nibble <c>0x9</c>: requests go-back-N retransmission from N(R).
        /// </summary>
        Reject,
    }

    /// <summary>
    /// Unnumbered-frame type, decoded from the control byte with the poll/final bit masked off.
    /// </summary>
    /// <remarks>
    /// Per the ND LAPB spec section 2.2.3 the type is the control byte with the P/F bit
    /// (<c>0x10</c>) cleared, giving the base pattern:
    ///  - <c>0x2F</c> - SABM (Set Asynchronous Balanced Mode).
    ///  - <c>0x43</c> - DISC (Disconnect).
    ///  - <c>0x63</c> - UA (Unnumbered Acknowledgement).
    ///  - <c>0x0F</c> - DM (Disconnected Mode).
    ///  - <c>0x87</c> - FRMR (Frame Reject).
    /// </remarks>
    public enum LapbUnnumberedKind
    {
        /// <summary>
        /// The frame is not an unnumbered frame, or its pattern matches no known type.
        /// </summary>
        Unknown,

        /// <summary>
        /// SABM, base <c>0x2F</c>: sets Asynchronous Balanced Mode and resets sequence state.
        /// </summary>
        Sabm,

        /// <summary>
        /// DISC, base <c>0x43</c>: requests link teardown.
        /// </summary>
        Disc,

        /// <summary>
        /// UA, base <c>0x63</c>: acknowledges a SABM or DISC.
        /// </summary>
        Ua,

        /// <summary>
        /// DM, base <c>0x0F</c>: reports disconnected mode.
        /// </summary>
        Dm,

        /// <summary>
        /// FRMR, base <c>0x87</c>: reports an unrecoverable frame error.
        /// </summary>
        Frmr,
    }

    /// <summary>
    /// A single FCS-valid LAPB frame de-framed from a capture: its address, control
    /// classification, and - for information frames - the information field.
    /// </summary>
    /// <remarks>
    /// The information field is the bytes between the control byte and the two-byte FCS.
    /// For a SINTRAN frame it begins with the 13-byte SINTRAN header and can be fed
    /// directly to the XMSG frame decoder.
    /// </remarks>
    public sealed class LapbFrame
    {
        private readonly byte[] _info;

        /// <summary>
        /// Decodes a frame that came off a live link, where there is no capture flow to name.
        /// </summary>
        /// <param name="frameBytes">
        /// The frame: address, control, then any information field. No flags, no FCS.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="frameBytes"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// The frame is too short to decode.
        /// </exception>
        /// <remarks>
        /// A frame that arrived on a live link, so the framer has already checked and stripped the
        /// FCS. Use <see cref="FromCapture"/> for one read out of a capture, which still has its two
        /// FCS octets on the end.
        /// </remarks>
        public LapbFrame(byte[] frameBytes)
            : this(frameBytes, LiveInfoLength(frameBytes))
        {
        }

        /// <summary>
        /// The information length of a frame that arrived on a live link: everything after the
        /// address and control octets. A captured frame still has its two FCS octets on the end and
        /// a live one does not, because the framer has already checked and stripped them.
        /// </summary>
        private static int LiveInfoLength(byte[] frameBytes)
        {
            if (frameBytes == null)
            {
                throw new ArgumentNullException(nameof(frameBytes));
            }

            if (frameBytes.Length < 2)
            {
                throw new ArgumentException("A LAPB frame needs at least an address and a control octet.", nameof(frameBytes));
            }

            return frameBytes.Length - 2;
        }

        /// <summary>
        /// Initialises a LAPB frame from its unstuffed, FCS-valid bytes.
        /// </summary>
        /// <param name="frameBytes">
        /// The full unstuffed frame: <c>addr + ctrl + info + fcs_lo + fcs_hi</c>.
        /// </param>
        /// <returns>
        /// The decoded frame.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="frameBytes"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="frameBytes"/> is shorter than four bytes.
        /// </exception>
        public static LapbFrame FromCapture(byte[] frameBytes)
        {
            return new LapbFrame(frameBytes, CapturedInfoLength(frameBytes));
        }

        /// <summary>
        /// The information length of a captured frame: everything after the address and control
        /// octets except the trailing two-octet FCS.
        /// </summary>
        private static int CapturedInfoLength(byte[] frameBytes)
        {
            if (frameBytes == null)
            {
                throw new ArgumentNullException(nameof(frameBytes));
            }

            if (frameBytes.Length < 4)
            {
                throw new ArgumentException("A LAPB frame needs at least address, control and a 2-byte FCS.", nameof(frameBytes));
            }

            return frameBytes.Length - 4;
        }

        private LapbFrame(byte[] frameBytes, int infoLength)
        {
            if (frameBytes == null)
            {
                throw new ArgumentNullException(nameof(frameBytes));
            }

            Address = frameBytes[0];
            Control = frameBytes[1];

            _info = new byte[infoLength];
            Array.Copy(frameBytes, 2, _info, 0, infoLength);

            if ((Control & LapbControl.FormatMaskI) == 0)
            {
                // I-frame: control bit 0 is clear. N(S)/N(R)/P are packed in the control byte.
                Kind = LapbFrameKind.Information;
                SendSequence = (Control >> LapbControl.NsShift) & LapbControl.SequenceMask;
                ReceiveSequence = (Control >> LapbControl.NrShift) & LapbControl.SequenceMask;
                SupervisoryKind = LapbSupervisoryKind.Unknown;
                UnnumberedKind = LapbUnnumberedKind.Unknown;
            }
            else if ((Control & LapbControl.FormatMaskS) == LapbControl.FormatS)
            {
                // S-frame: control bits 1..0 == 01. Subtype is the low nibble (spec 2.2.2).
                Kind = LapbFrameKind.Supervisory;
                SendSequence = -1;
                ReceiveSequence = (Control >> LapbControl.NrShift) & LapbControl.SequenceMask;
                SupervisoryKind = DecodeSupervisory(Control);
                UnnumberedKind = LapbUnnumberedKind.Unknown;
            }
            else
            {
                // U-frame: control bits 1..0 == 11. Type is the control byte with P/F masked off.
                Kind = LapbFrameKind.Unnumbered;
                SendSequence = -1;
                ReceiveSequence = -1;
                SupervisoryKind = LapbSupervisoryKind.Unknown;
                UnnumberedKind = DecodeUnnumbered(Control);
            }

            PollFinal = (Control & LapbControl.PollFinalBit) != 0;
        }

        /// <summary>
        /// Gets the LAPB address byte.
        /// </summary>
        public byte Address { get; }

        /// <summary>
        /// Gets the LAPB control byte.
        /// </summary>
        public byte Control { get; }

        /// <summary>
        /// Gets the frame classification derived from the control byte.
        /// </summary>
        public LapbFrameKind Kind { get; }

        /// <summary>
        /// Gets the supervisory subtype (RR / RNR / REJ) for a supervisory frame.
        /// </summary>
        /// <remarks>
        /// Decoded by field position from the control byte's low nibble (spec 2.2.2), never by
        /// scanning bytes: the REJ low nibble <c>0x9</c> deliberately collides with the data-transfer
        /// address <c>0x09</c>. This is <see cref="LapbSupervisoryKind.Unknown"/> for any frame whose
        /// <see cref="Kind"/> is not <see cref="LapbFrameKind.Supervisory"/>.
        /// </remarks>
        public LapbSupervisoryKind SupervisoryKind { get; }

        /// <summary>
        /// Gets the unnumbered type (SABM / DISC / UA / DM / FRMR) for an unnumbered frame.
        /// </summary>
        /// <remarks>
        /// Decoded from the control byte with the poll/final bit (<c>0x10</c>) masked off (spec 2.2.3).
        /// This is <see cref="LapbUnnumberedKind.Unknown"/> for any frame whose <see cref="Kind"/> is
        /// not <see cref="LapbFrameKind.Unnumbered"/>.
        /// </remarks>
        public LapbUnnumberedKind UnnumberedKind { get; }

        /// <summary>
        /// Gets the send sequence number N(S) for an information frame, or -1 otherwise.
        /// </summary>
        public int SendSequence { get; }

        /// <summary>
        /// Gets the receive sequence number N(R) for information/supervisory frames, or
        /// -1 for unnumbered frames.
        /// </summary>
        public int ReceiveSequence { get; }

        /// <summary>
        /// Gets a value indicating whether the poll/final bit (control bit 4) is set.
        /// </summary>
        public bool PollFinal { get; }

        /// <summary>
        /// Gets the information field bytes (between the control byte and the FCS).
        /// </summary>
        /// <remarks>
        /// Empty for supervisory frames and for unnumbered frames without an info field.
        /// </remarks>
        public ReadOnlyMemory<byte> Info
        {
            get { return _info; }
        }

        /// <summary>
        /// Gets a value indicating whether the information field is a SINTRAN frame, that is its
        /// first word is a plausible <c>XDROU</c>: version/protocol byte <c>0x21</c> followed by a
        /// hop count no greater than its initial value.
        /// </summary>
        /// <remarks>
        /// <para><b>Word 0 is NOT two magic markers</b></para>
        /// This used to demand <c>0x13</c> or <c>0x12</c> in byte 1, an allow-list of the only two
        /// values we had ever seen. The L03 kernel tables name the field:
        ///  - <c>INTEGER XDROU % NETWORK INFO (VERSION, PROTOCOL, HOP COUNT)</c>.
        ///  - <c>SYMBOL X5VRS=20400 % L.H. BYTE OF XDROU (OP=0, VERSION=2, PROTOCOL=1)</c>.
        /// <para>
        /// <c>20400</c> octal is <c>0x2100</c>, so byte 0 is the version/protocol byte and byte 1 is
        /// a HOP COUNT. That is why a relayed frame reads <c>0x12</c> where a direct one reads
        /// <c>0x13</c> - they are one hop apart, which is also exactly what
        /// <c>SintranDatagramRelay</c> already does when it "decrements marker 2".
        /// </para>
        /// <para>
        /// The old allow-list therefore capped us at ONE relay hop: a frame that had been forwarded
        /// twice carries <c>0x11</c> and was classified as not-SINTRAN, silently. Nothing in the
        /// captures showed it because nothing has yet been relayed twice.
        /// </para>
        /// <para>
        /// The upper bound is kept deliberately. <c>0x13</c> is the initial value every observed
        /// sender emits and the count only falls, so a byte above it is not a hop count and this is
        /// not a SINTRAN frame. Keeping the bound also keeps the test strong enough to be useful
        /// against X.25, where <c>0x21 0x13</c> is a valid GFI/LCN.
        /// </para>
        /// </remarks>
        public bool IsSintranInfo
        {
            get
            {
                return _info.Length >= 2
                    && _info[0] == SintranVersionProtocol
                    && _info[1] <= SintranInitialHopCount;
            }
        }

        /// <summary>
        /// The version/protocol byte of <c>XDROU</c>, the high byte of header word 0.
        /// </summary>
        /// <remarks>
        /// <c>X5VRS = 20400</c> octal = <c>0x2100</c>, whose left-hand byte is this: OP=0,
        /// VERSION=2, PROTOCOL=1. Carved from <c>XMSG-POFTABS-L03.SYMB</c>.
        /// </remarks>
        public const byte SintranVersionProtocol = 0x21;

        /// <summary>
        /// The hop count a sender puts in the low byte of <c>XDROU</c> before any relay.
        /// </summary>
        /// <remarks>
        /// Every observed direct frame carries <c>0x13</c>; each relay decrements it.
        /// </remarks>
        public const byte SintranInitialHopCount = 0x13;

        /// <summary>
        /// Decodes a supervisory-frame subtype from its control byte's low nibble.
        /// </summary>
        /// <param name="control">
        /// The control byte of a frame already classified as supervisory.
        /// </param>
        /// <returns>
        /// The matching <see cref="LapbSupervisoryKind"/>, or <see cref="LapbSupervisoryKind.Unknown"/>
        /// when the low nibble matches no known subtype.
        /// </returns>
        private static LapbSupervisoryKind DecodeSupervisory(byte control)
        {
            // Spec 2.2.2: subtype is the low nibble. RR = 0x1, RNR = 0x5, REJ = 0x9.
            switch (control & LapbControl.SupervisoryNibbleMask)
            {
                case LapbControl.RrNibble:
                    return LapbSupervisoryKind.ReceiveReady;
                case LapbControl.RnrNibble:
                    return LapbSupervisoryKind.ReceiveNotReady;
                case LapbControl.RejNibble:
                    return LapbSupervisoryKind.Reject;
                default:
                    return LapbSupervisoryKind.Unknown;
            }
        }

        /// <summary>
        /// Decodes an unnumbered-frame type from its control byte with the poll/final bit masked off.
        /// </summary>
        /// <param name="control">
        /// The control byte of a frame already classified as unnumbered.
        /// </param>
        /// <returns>
        /// The matching <see cref="LapbUnnumberedKind"/>, or <see cref="LapbUnnumberedKind.Unknown"/>
        /// when the masked pattern matches no known type.
        /// </returns>
        private static LapbUnnumberedKind DecodeUnnumbered(byte control)
        {
            // Spec 2.2.3: mask off the P/F bit (0x10), then match the base pattern.
            switch (control & ~LapbControl.PollFinalBit)
            {
                case LapbControl.SabmBase:
                    return LapbUnnumberedKind.Sabm;
                case LapbControl.DiscBase:
                    return LapbUnnumberedKind.Disc;
                case LapbControl.UaBase:
                    return LapbUnnumberedKind.Ua;
                case LapbControl.DmBase:
                    return LapbUnnumberedKind.Dm;
                case LapbControl.FrmrBase:
                    return LapbUnnumberedKind.Frmr;
                default:
                    return LapbUnnumberedKind.Unknown;
            }
        }
    }
}
