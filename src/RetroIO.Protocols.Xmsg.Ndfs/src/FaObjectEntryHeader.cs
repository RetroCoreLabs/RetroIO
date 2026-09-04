using System;

namespace RetroIO.Protocols.Xmsg.Ndfs
{
    /// <summary>
    /// Builds and splits the packed words of a SINTRAN object entry.
    /// </summary>
    /// <remarks>
    /// The access word and the header word are the two bit-packed fields of the 64-byte listing
    /// record. Both were sent as bare magic numbers until 2026-08-05, and both were wrong.
    /// </remarks>
    public static class FaObjectEntryHeader
    {
        /// <summary>
        /// Masks the terminal number out of the header word, bits 10-0.
        /// </summary>
        public const ushort TerminalNumberMask = 0x07FF;

        /// <summary>
        /// The width in bits of one tier of the access word.
        /// </summary>
        public const int AccessTierBits = 5;

        /// <summary>
        /// The bit position of the OWN tier within the access word.
        /// </summary>
        public const int OwnTierShift = 0;

        /// <summary>
        /// The bit position of the FRIEND tier within the access word.
        /// </summary>
        public const int FriendTierShift = 5;

        /// <summary>
        /// The bit position of the PUBLIC tier within the access word.
        /// </summary>
        public const int PublicTierShift = 10;

        /// <summary>
        /// Packs three access tiers into the 15-bit access word held at record bytes 26-27.
        /// </summary>
        /// <param name="own">
        /// What the owning user may do.
        /// </param>
        /// <param name="friend">
        /// What a friend of the owning user may do.
        /// </param>
        /// <param name="public">
        /// What any other user may do.
        /// </param>
        /// <returns>
        /// The packed access word. Bit 15 is left clear; it is unused.
        /// </returns>
        /// <remarks>
        /// The layout is bit 15 unused, bits 14-10 <paramref name="public"/>, bits 9-5
        /// <paramref name="friend"/>, bits 4-0 <paramref name="own"/>.
        /// </remarks>
        public static ushort PackAccess(
            FaAccessRights own,
            FaAccessRights friend,
            FaAccessRights @public)
        {
            return (ushort)(
                ((int)own << OwnTierShift) |
                ((int)friend << FriendTierShift) |
                ((int)@public << PublicTierShift));
        }

        /// <summary>
        /// Reads one tier back out of a packed access word.
        /// </summary>
        /// <param name="accessWord">
        /// The packed word from record bytes 26-27.
        /// </param>
        /// <param name="tierShift">
        /// The tier to read: <see cref="OwnTierShift"/>, <see cref="FriendTierShift"/> or
        /// <see cref="PublicTierShift"/>.
        /// </param>
        /// <returns>
        /// The rights that tier grants.
        /// </returns>
        public static FaAccessRights UnpackAccess(ushort accessWord, int tierShift)
        {
            return (FaAccessRights)((accessWord >> tierShift) & 0x1F);
        }
    }
}