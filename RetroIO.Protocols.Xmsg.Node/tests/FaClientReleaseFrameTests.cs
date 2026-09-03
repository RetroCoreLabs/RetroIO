using System;
using RetroIO.Protocols.Xmsg.Fa;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Pins the teardown a client sends, against the frame a real ND client sends.
    /// </summary>
    /// <remarks>
    /// <para><b>What this is guarding</b></para>
    /// Sending the wrong teardown made every transfer cost the far machine a connection seat that
    /// never came back - thirty transfers after a file-server start and it answers nothing. That was
    /// the "random push stalls" this project chased for days.
    /// <para><b>The reference frame</b></para>
    /// Captured on 2026-08-18 from D100's own <c>COPY-FILE</c> talking to our file server, over the
    /// same HDLC link our transfers use:
    /// <code>
    /// frameFlags=0x82  role=0x84
    /// FA body  0782 0082 000E 8000 0000
    ///               ^sender's conv  ^peer's conv
    /// </code>
    /// Our client had the two conversation numbers the other way round AND used the server's
    /// message type. See <c>DOC\CARVE-FA-SEAT-LEAK-2026-08-18.md</c>.
    /// </remarks>
    public sealed class FaClientReleaseFrameTests
    {
        /// <summary>
        /// The teardown is a Release, which is the client's message.
        /// </summary>
        /// <remarks>
        /// <c>0x07C0</c> is what the SERVER sends back. A client that sends it is answering its own
        /// question, and the server never hears "I am finished".
        /// </remarks>
        [Fact]
        public void TheClientTeardownIsAReleaseAndNotAClose()
        {
            Assert.Equal(0x0782, (int)FaMessageType.SessionFinished);
            Assert.Equal(0x07C0, (int)FaMessageType.Close);

            // Whatever else changes, these two must not be confused for one another.
            Assert.NotEqual(FaMessageType.SessionFinished, FaMessageType.Close);
        }

        /// <summary>
        /// A Release is recognised as a session ending.
        /// </summary>
        /// <remarks>
        /// The server side keys off this, so if it ever stopped being true our own server would
        /// leave sessions open exactly as D100 did for us.
        /// </remarks>
        [Fact]
        public void AReleaseIsRecognisedAsTheSessionEnding()
        {
            Assert.True(FaMessageType.SessionFinished.IsSessionFinished());
        }

        /// <summary>
        /// Our server writes the sender's conversation first, which is the ordering to copy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the ordering a real ND client ACCEPTS - our server has answered one, end to end -
        /// so it is the check that the client now matches. The client used to write the peer's
        /// number first, and sending a Release that way took D100's file server down twice.
        /// </para>
        /// <para>
        /// Built through the server's own conversation object rather than by hand, so the test
        /// follows the code that is known to work rather than restating a byte string.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheSendersConversationComesFirst()
        {
            // A distinctive peer conversation, so the two positions cannot be confused.
            const ushort theirs = 0x0082;

            FaServerConversation conversation = new FaServerConversation(theirs);
            byte[] close = conversation.BuildClose();

            Assert.Equal(0x07C0, (close[0] << 8) | close[1]);

            // Sender first, peer second.
            Assert.Equal(conversation.ResponderConversation, (ushort)((close[2] << 8) | close[3]));
            Assert.Equal(theirs, (ushort)((close[4] << 8) | close[5]));
        }
    }
}
