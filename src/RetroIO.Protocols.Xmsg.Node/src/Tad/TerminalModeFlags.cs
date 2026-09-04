using System;

namespace RetroIO.Protocols.Xmsg.Node.Tad
{
    /// <summary>
    /// The bits carried in the single payload byte of a TMOD (<c>0x0C</c>) terminal-mode message.
    /// </summary>
    /// <remarks>
    /// <para><b>Where this decode comes from</b></para>
    /// <para>
    /// It was an undecoded raw byte here until 2026-08-18. Both halves of the SINTRAN III version J
    /// TAD driver agree bit for bit, which is what makes this PROVEN rather than fitted:
    /// </para>
    /// <para>
    ///  - the SENDER, <c>CTMOD</c> in <c>SINTRAN/NPL-SOURCE-2/NPL-CLEAN/06-COS-TAD-RES-CODE.NPL</c>,
    ///    sets bit 0 from 5CAPITAL, bit 1 from 5CRDLY, bit 2 from a non-zero SCREEN and bit 3 from
    ///    5LBLOG
    /// </para>
    /// <para>
    ///  - the RECEIVER, <c>BDTMOD</c> in <c>20-COS-TAD-POF-CODE.NPL</c>, reads the same four bits back
    ///    into the same four flags
    /// </para>
    /// <para>
    /// Bits 4 to 7 are not read by <c>BDTMOD</c> at all in version J. Do not invent meanings for them.
    /// </para>
    /// <para><b>The one value we have measured</b></para>
    /// <para>
    /// Real clients send <c>0x08</c> in the terminal-setup burst, which under this decode reads as
    /// "log me out if the carrier drops" - a sensible thing for a remote terminal to ask for, and a
    /// good independent sanity check on the decode.
    /// </para>
    /// <para><b>Version</b></para>
    /// <para>
    /// PROVEN for J. STRONG for the Release L machines we drive: all four features still exist in L
    /// and no L evidence contradicts it, but no L source was read.
    /// </para>
    /// </remarks>
    [Flags]
    public enum TerminalModeFlags : byte
    {
        /// <summary>
        /// No mode bits set.
        /// </summary>
        None = 0x00,

        /// <summary>
        /// Bit 0 - capital letters only (SINTRAN flag 5CAPITAL): the line folds input to upper case.
        /// </summary>
        CapitalLettersOnly = 0x01,

        /// <summary>
        /// Bit 1 - carriage-return delay (SINTRAN flag 5CRDLY): the terminal needs filler time after a
        /// CR before the next character.
        /// </summary>
        CarriageReturnDelay = 0x02,

        /// <summary>
        /// Bit 2 - stop on a full page (the datafield's SCREEN counter): output pauses when the screen
        /// fills instead of scrolling on.
        /// </summary>
        StopOnFullPage = 0x04,

        /// <summary>
        /// Bit 3 - log out on missing carrier (SINTRAN flag 5LBLOG): drop the user's session when the
        /// line's carrier goes away. This is the bit real clients set.
        /// </summary>
        LogoutOnMissingCarrier = 0x08,
    }
}
