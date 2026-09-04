using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// The XMSG sub-header frame-flags byte (offset 3), a bitfield. Only partially decoded; the safe
    /// normative rule is to mirror the captured value per frame type, which the canonical combos below
    /// encode.
    /// </summary>
    /// <remarks>
    /// From the capture sweep (601 frames): <see cref="SystemMode"/> and <see cref="Marker01"/> are set
    /// on every frame; <see cref="DataPhase"/> tracks established terminal-phase frames (238/242 fit);
    /// <see cref="Letter"/> is the one bit whose rule is UNKNOWN (set on setup 0x86 and on 0x96 terminal
    /// frames, clear on 0x92 and 0x82). Because the individual-bit rule for <see cref="Letter"/> is not
    /// established, emit one of the canonical combos per frame type rather than composing bits ad hoc.
    /// See XMSG-PROTOCOL.md section 18.4 and TAD-Message-Formats.md section 22.6.
    /// </remarks>
    [Flags]
    public enum XmsgFrameFlags : byte
    {
        /// <summary>
        /// No bits set (<c>0x00</c>).
        /// </summary>
        None = 0,

        /// <summary>
        /// Bit 1 (<c>0x02</c>) - set on every observed frame; meaning UNKNOWN.
        /// </summary>
        Marker01 = 1 << 1,

        /// <summary>
        /// Bit 2 (<c>0x04</c>) - set on setup and on the 0x96 terminal frames; semantics UNKNOWN.
        /// </summary>
        Letter = 1 << 2,

        /// <summary>
        /// Bit 4 (<c>0x10</c>) - set on established terminal-phase frames (INFERRED).
        /// </summary>
        DataPhase = 1 << 4,

        /// <summary>
        /// Bit 7 (<c>0x80</c>) - set on every observed frame (INFERRED XFSYS system-mode call).
        /// </summary>
        SystemMode = 1 << 7,

        /// <summary>
        /// Setup / control-letter combo (<c>0x86</c>) = <see cref="SystemMode"/> | <see cref="Marker01"/> | <see cref="Letter"/>.
        /// </summary>
        Setup = SystemMode | Marker01 | Letter,

        /// <summary>
        /// Bare-TAD control combo (<c>0x82</c>) = <see cref="SystemMode"/> | <see cref="Marker01"/> - ESCA, DCON, 0xFD notify.
        /// </summary>
        ControlBare = SystemMode | Marker01,

        /// <summary>
        /// Terminal-data variant A (<c>0x96</c>) = <see cref="SystemMode"/> | <see cref="Marker01"/> | <see cref="Letter"/> | <see cref="DataPhase"/>.
        /// </summary>
        DataA = SystemMode | Marker01 | Letter | DataPhase,

        /// <summary>
        /// Terminal-data variant B (<c>0x92</c>) = <see cref="SystemMode"/> | <see cref="Marker01"/> | <see cref="DataPhase"/>.
        /// </summary>
        DataB = SystemMode | Marker01 | DataPhase,
    }
}
