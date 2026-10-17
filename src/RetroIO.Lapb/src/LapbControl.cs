//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Xmsg.Hdlc
{
    /// <summary>
    /// The LAPB control-field constants, shared by the code that BUILDS frames and the code that
    /// PARSES them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this type exists</b></para>
    /// These values used to be declared twice: as private constants in the sending layer and as
    /// bare literals in the parser. Two independent copies of the same numbers is how a build side
    /// and a parse side drift apart - an edit to one is silently not an edit to the other.
    /// <para>
    /// That is not a hypothetical here. On 2026-08-06 a hand-copied frame builder was found to
    /// carry an identical header-checksum defect to the original it was copied from, and both had
    /// to be fixed separately. Naming a wire constant once is the cheapest defence against the
    /// same shape of bug.
    /// </para>
    /// <para><b>Layout of the control byte, spec 2.2</b></para>
    /// The low bits pick the frame family, and the poll/final bit is shared by all three:
    /// <code>
    ///  I-frame   bit 0 clear      N(R) 7..5 | P 4 | N(S) 3..1 | 0
    ///  S-frame   bits 1..0 = 01   N(R) 7..5 | P/F 4 | subtype 3..0
    ///  U-frame   bits 1..0 = 11   type with the P/F bit masked off
    /// </code>
    /// </remarks>
    public static class LapbControl
    {
        /// <summary>
        /// The bit that is clear on an I-frame and set on every other family.
        /// </summary>
        public const byte FormatMaskI = 0x01;

        /// <summary>
        /// The two low bits that distinguish an S-frame from a U-frame.
        /// </summary>
        public const byte FormatMaskS = 0x03;

        /// <summary>
        /// The value of <see cref="FormatMaskS"/> that means an S-frame.
        /// </summary>
        public const byte FormatS = 0x01;

        /// <summary>
        /// The poll/final bit, shared by all three frame families (spec 2.2).
        /// </summary>
        public const byte PollFinalBit = 0x10;

        /// <summary>
        /// The modulo-8 sequence mask applied to N(S) and N(R) once shifted down.
        /// </summary>
        public const byte SequenceMask = 0x07;

        /// <summary>
        /// How far N(S) sits above bit 0 in the control byte.
        /// </summary>
        public const int NsShift = 1;

        /// <summary>
        /// How far N(R) sits above bit 0 in the control byte.
        /// </summary>
        public const int NrShift = 5;

        /// <summary>
        /// N(S) in place, for assembling a control byte.
        /// </summary>
        public const byte NsFieldMask = 0x0E;

        /// <summary>
        /// N(R) in place, for assembling a control byte.
        /// </summary>
        public const byte NrFieldMask = 0xE0;

        /// <summary>
        /// The low nibble that carries the supervisory subtype (spec 2.2.2).
        /// </summary>
        public const byte SupervisoryNibbleMask = 0x0F;

        /// <summary>
        /// Receive Ready.
        /// </summary>
        public const byte RrNibble = 0x01;

        /// <summary>
        /// Receive Not Ready.
        /// </summary>
        public const byte RnrNibble = 0x05;

        /// <summary>
        /// Reject.
        /// </summary>
        public const byte RejNibble = 0x09;

        /// <summary>
        /// Set Asynchronous Balanced Mode, without the poll bit.
        /// </summary>
        public const byte SabmBase = 0x2F;

        /// <summary>
        /// Disconnect, without the poll bit.
        /// </summary>
        public const byte DiscBase = 0x43;

        /// <summary>
        /// Unnumbered Acknowledgement, without the final bit.
        /// </summary>
        public const byte UaBase = 0x63;

        /// <summary>
        /// Disconnected Mode, without the final bit.
        /// </summary>
        public const byte DmBase = 0x0F;

        /// <summary>
        /// Frame Reject, without the final bit.
        /// </summary>
        public const byte FrmrBase = 0x87;
    }

    /// <summary>
    /// The reasons a Frame Reject names, OR-combined into the third byte of its diagnostic
    /// (spec 2.3.3).
    /// </summary>
    /// <remarks>
    /// <para><b>The gap at bit 1 is deliberate</b></para>
    /// The wire positions are fixed by the specification, not sequential. Standard LAPB puts
    /// reason X - "information field not permitted in this frame type" - at
    /// <c>0x02</c>. It is NOT declared here because we neither send nor recognise it, and because
    /// its bit position is derived from standard LAPB rather than read out of the ND spec.
    /// Confirm against section 2.3.3 before adding it.
    /// </remarks>
    [System.Flags]
    public enum LapbFrmrReason : byte
    {
        /// <summary>
        /// No reason bits set.
        /// </summary>
        None = 0,

        /// <summary>
        /// W: the rejected control field is invalid or not implemented. Wire <c>0x01</c>.
        /// </summary>
        ControlFieldInvalid = 1 << 0,

        /// <summary>
        /// Y: the information field exceeded the maximum length. Wire <c>0x04</c>.
        /// </summary>
        InformationFieldTooLong = 1 << 2,

        /// <summary>
        /// Z: N(R) was outside the window between V(A) and V(S). Wire <c>0x08</c>.
        /// </summary>
        ReceiveSequenceInvalid = 1 << 3,
    }
}
