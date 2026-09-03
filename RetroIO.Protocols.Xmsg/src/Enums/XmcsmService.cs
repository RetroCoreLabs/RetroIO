namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// The 32-bit XMCSM control/service word in the XMSG sub-header, which selects the message's
    /// service (connect letter, session setup, terminal data, notification) and, for XROUT letters,
    /// carries the service code in its low byte.
    /// </summary>
    /// <remarks>
    /// These values were previously duplicated as private <c>const uint</c> declarations across
    /// TadConnectClient, TadTerminalResponder, TadSession and ListRoutingClient, and compared as bare
    /// literals in TadAskerSession and XmsgFrame. They are consolidated here as the single source of
    /// truth. Cast to <c>uint</c> at the call site (for example <c>(uint)XmcsmService.TerminalData</c>);
    /// the cast of a constant enum member is itself a compile-time constant, so it is valid as a default
    /// parameter value.
    /// </remarks>
    public enum XmcsmService : uint
    {
        /// <summary>
        /// XROUT connect letter / setup (<c>0x04000041</c>); the low byte <c>0x41</c> is the XROUT service code.
        /// </summary>
        XsletLetter = 0x04000041u,

        /// <summary>
        /// Session setup control word (<c>0x04000000</c>).
        /// </summary>
        SessionSetup = 0x04000000u,

        /// <summary>
        /// Terminal data control word (<c>0x01080000</c>) - the TAD terminal-data class.
        /// </summary>
        TerminalData = 0x01080000u,

        /// <summary>
        /// Bare-TAD control word (<c>0x00080000</c>) - TAD control frames with no data class.
        /// </summary>
        BareTadControl = 0x00080000u,

        /// <summary>
        /// Session-state notification control word (<c>0x00060000</c>) - carries the 0xFD notification.
        /// </summary>
        SessionNotify = 0x00060000u,

        /// <summary>
        /// XSGSY list-routing request (<c>0x0100014B</c>); the low byte <c>0x4B</c> is the XROUT XSGSY service code.
        /// </summary>
        XsgsyRequest = 0x0100014Bu,

        /// <summary>
        /// XSGSY list-routing reply (<c>0x01000100</c>).
        /// </summary>
        XsgsyReply = 0x01000100u,
    }

    /// <summary>
    /// Named masks for the XMCSM control/service word.
    /// </summary>
    public static class XmcsmMask
    {
        /// <summary>
        /// The XROUT service-code low byte of an <see cref="XmcsmService.XsletLetter"/> word (<c>0x41</c>).
        /// </summary>
        public const uint XsletServiceLowByte = 0x41u;
    }
}
