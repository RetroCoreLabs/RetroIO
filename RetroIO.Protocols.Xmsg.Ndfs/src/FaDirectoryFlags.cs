using System;
using System.Text;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// The state bits of a SINTRAN directory entry, bytes 0-1 of the 42-byte record.
    /// </summary>
    /// <remarks>
    /// <para><b>Only the high bits are flags</b></para>
    /// Bits 9-0 are the number of files currently open on the directory, not flags. Mask them off
    /// with <see cref="FaDirectoryEntry.OpenFileCountMask"/> before treating the word as this enum.
    /// <para><b>Source</b></para>
    /// The bit table is quoted from the SINTRAN III Monitor Calls manual appendix C, "DIRECTORY
    /// INFORMATION", which describes the value returned by MON 244B (GDIEN, Get Directory Entry).
    /// </remarks>
    [Flags]
    public enum FaDirectoryFlags : ushort
    {
        /// <summary>
        /// No state bits set.
        /// </summary>
        None = 0,

        /// <summary>
        /// The directory is entered, wire bit <c>0x8000</c>.
        /// </summary>
        Entered = 1 << 15,

        /// <summary>
        /// This is the main directory, wire bit <c>0x4000</c>.
        /// </summary>
        MainDirectory = 1 << 14,

        /// <summary>
        /// This is a tape directory, wire bit <c>0x2000</c>.
        /// </summary>
        TapeDirectory = 1 << 13,

        /// <summary>
        /// This is the default directory, wire bit <c>0x1000</c>.
        /// </summary>
        DefaultDirectory = 1 << 12,

        /// <summary>
        /// Reserved for special use, wire bit <c>0x0800</c>.
        /// </summary>
        ReservedForSpecialUse = 1 << 11,

        /// <summary>
        /// The directory is read-only, or sits on an optical WORM disk, wire bit <c>0x0400</c>.
        /// </summary>
        ReadOnly = 1 << 10,
    }
}
