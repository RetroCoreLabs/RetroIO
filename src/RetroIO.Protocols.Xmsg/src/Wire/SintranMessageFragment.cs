using System;

namespace RetroIO.Protocols.Xmsg
{
    /// <summary>
    /// How a message too long for one frame is split across a
    /// <see cref="SintranPacketSubtype.MessageFirstFragment"/> and a
    /// <see cref="SintranPacketSubtype.MessageContinuation"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>The split point is fixed, and that is measured rather than assumed</b></para>
    /// A first fragment carries <see cref="FirstFragmentBodyLength"/> body bytes and the
    /// continuation carries the rest, whatever the message is. The evidence is three captures of two
    /// different protocols and three different message lengths, all splitting at the same byte:
    ///  - The XFTRA file transfer, message length 1030, over HDLC. Frames 622 and 450.
    ///  - The same transfer of a 167-byte file - a sixth of one block - which still ships whole
    ///    pages and still splits at 594.
    ///  - The file-access <c>ReadFile</c> delivery, message length 1032, over Ethernet:
    ///    <c>2113 000A ... 0408</c> then <c>2113 000C ... 0252</c>, which is 1032 total and a
    ///    continuation resuming at 594.
    /// <para><b>Flags 2 means a different thing in each fragment</b></para>
    /// On the first it is the TOTAL message length; on the continuation it is the byte OFFSET at
    /// which the continuation resumes. So <c>offset + continuationLength == total</c>, which is
    /// checked against the captures in <c>TransferFragmentationTests</c>.
    /// <para><b>Only the first fragment carries addressing</b></para>
    /// The first fragment has the same 28-byte head as an ordinary data frame - SINTRAN header plus
    /// the XMSG sub-header. The continuation does NOT repeat the addressing words: it is the 14-byte
    /// SINTRAN header and then the message picked straight back up. The two are tied together by
    /// sharing a Flags 1.
    /// <para><b>What is still UNKNOWN</b></para>
    /// Whether 594 would change for a message of some length far from these. Every message large
    /// enough to need splitting that anyone has captured is between 1030 and 1032 bytes, so the
    /// question may never arise; a longer one would also need a THIRD fragment, and no capture shows
    /// one, so this class deliberately refuses rather than inventing a chain - see
    /// <see cref="Split"/>.
    /// </remarks>
    public static class SintranMessageFragment
    {
        /// <summary>
        /// Number of message-body bytes carried by the first fragment.
        /// </summary>
        public const int FirstFragmentBodyLength = 594;

        /// <summary>
        /// Byte offset at which the body starts in a first fragment: the SINTRAN header plus the
        /// XMSG sub-header.
        /// </summary>
        public const int FirstFragmentBodyOffset = SintranHeader.Size + XmsgSubHeader.Size;

        /// <summary>
        /// Byte offset at which the body resumes in a continuation: the SINTRAN header alone.
        /// </summary>
        public const int ContinuationBodyOffset = SintranHeader.Size;

        /// <summary>
        /// The longest message that can be sent as two fragments.
        /// </summary>
        /// <remarks>
        /// A first fragment takes a fixed <see cref="FirstFragmentBodyLength"/> and a continuation
        /// resumes at that offset, so two fragments reach at most twice it. Nothing captured comes
        /// near this - the largest real message is 1032 bytes - and no capture shows a third
        /// fragment, so a message past this limit is refused rather than guessed at.
        /// </remarks>
        public const int MaxTwoFragmentMessageLength = FirstFragmentBodyLength * 2;

        /// <summary>
        /// Gets a value indicating whether a message of this length has to be split.
        /// </summary>
        /// <param name="bodyLength">
        /// The message body length in bytes.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the body will not fit in one frame.
        /// </returns>
        public static bool NeedsFragmenting(int bodyLength)
        {
            return bodyLength > FirstFragmentBodyLength;
        }

        /// <summary>
        /// Splits a message body into the part the first fragment carries and the part the
        /// continuation carries.
        /// </summary>
        /// <param name="body">
        /// The whole message body, starting at the message's own first byte.
        /// </param>
        /// <param name="first">
        /// The portion for the first fragment (subtype <c>0x0A</c>).
        /// </param>
        /// <param name="continuation">
        /// The portion for the continuation (subtype <c>0x0C</c>), empty when
        /// <paramref name="body"/> fits in one frame and needs no split at all.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="body"/> is longer than
        /// <see cref="MaxTwoFragmentMessageLength"/>, which would need a third fragment that no
        /// capture has ever shown.
        /// </exception>
        public static void Split(
            ReadOnlySpan<byte> body,
            out ReadOnlySpan<byte> first,
            out ReadOnlySpan<byte> continuation)
        {
            if (!NeedsFragmenting(body.Length))
            {
                first = body;
                continuation = default;
                return;
            }

            if (body.Length > MaxTwoFragmentMessageLength)
            {
                throw new ArgumentException(
                    "A message of " + body.Length.ToString() + " bytes needs more than two fragments, "
                    + "and no capture shows a third. The longest message ever recorded is 1032 bytes.",
                    nameof(body));
            }

            first = body.Slice(0, FirstFragmentBodyLength);
            continuation = body.Slice(FirstFragmentBodyLength);
        }
    }
}
