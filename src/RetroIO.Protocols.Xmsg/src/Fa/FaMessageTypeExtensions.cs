namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Classifies FA message types that come in families rather than as single values.
    /// </summary>
    public static class FaMessageTypeExtensions
    {
        /// <summary>
        /// The bits an "I am finished" message shares with the rest of its family.
        /// </summary>
        public const ushort SessionFinishedFamily = 0x0780;

        /// <summary>
        /// The bits that select a member within the finished family.
        /// </summary>
        public const ushort SessionFinishedVariantMask = 0x000F;

        /// <summary>
        /// Says whether a message type is an "I am finished" message.
        /// </summary>
        /// <param name="messageType">
        /// The message type read from the wire.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the message means the client has finished with the
        /// conversation and expects a Close.
        /// </returns>
        /// <remarks>
        /// <para><b>Why a family and not a list of values</b></para>
        /// Three variants have now been seen at exactly the same point in a conversation, carrying
        /// the same ten bytes and the same layout:
        ///  - <c>0x0782</c> captured between two real ND machines at the end of a full listing.
        ///  - <c>0x0781</c> from D100 on 2026-08-05, when it gave up part-way through.
        ///  - <c>0x0788</c> from D100 on 2026-08-05, after a listing that ran to completion.
        /// <para><b>What the low nibble means is UNKNOWN</b></para>
        /// It plainly varies with how the conversation ended, but three samples is not enough to say
        /// what each value means, and no manual we hold documents these message types at all. What
        /// IS established is the consequence of getting it wrong: a finished message carries no
        /// operation/sequence pair, so a server that only knows how to parse requests answers it
        /// with an error, and the client then abandons the session and reconnects - which is exactly
        /// what <c>0x0788</c> caused before this test was widened.
        /// </remarks>
        public static bool IsSessionFinished(this FaMessageType messageType)
        {
            return ((ushort)messageType & ~SessionFinishedVariantMask) == SessionFinishedFamily;
        }
    }
}
