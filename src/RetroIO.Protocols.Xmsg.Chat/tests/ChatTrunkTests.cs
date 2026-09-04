using System;

using RetroIO.Protocols.Xmsg.Chat;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Chat.Tests
{
    /// <summary>
    /// The trunk rules, against the behaviour measured on the real machines.
    /// </summary>
    /// <remarks>
    /// <para><b>These are not invented expectations</b></para>
    /// <para>
    /// The ND server keeps the same table with the same rules, and the two must agree about state
    /// as well as about bytes or they will not interoperate. Where a test cites a measurement, it
    /// is a measurement from D100 and D102 on 2026-08-22, not a decision taken here.
    /// </para>
    /// </remarks>
    public sealed class ChatTrunkTests
    {
        /// <summary>
        /// A new peer is unknown, not down.
        /// </summary>
        /// <remarks>
        /// The distinction is the whole reason there are three states. Unknown usually means a
        /// trunk that has never been answered - a configuration mistake - where down means a
        /// machine that was there and went away.
        /// </remarks>
        [Fact]
        public void ANewPeerStartsUnknownRatherThanDown()
        {
            ChatTrunks trunks = new ChatTrunks(100);

            Assert.True(trunks.Add(102));
            Assert.Equal(ChatTrunkState.Unknown, trunks.StateOf(102));
        }

        /// <summary>
        /// Anything arriving from a peer brings the trunk up.
        /// </summary>
        /// <remarks>
        /// Not just a greeting: a forwarded line proves a peer is there just as well, so a busy
        /// pair of machines never needs to greet at all.
        /// </remarks>
        [Fact]
        public void AnythingHeardFromAPeerBringsItUp()
        {
            ChatTrunks trunks = new ChatTrunks(100);
            trunks.Add(102);

            Assert.True(trunks.MarkHeard(102));
            Assert.Equal(ChatTrunkState.Up, trunks.StateOf(102));

            // Already up - still heard, but nothing changed.
            Assert.False(trunks.MarkHeard(102));
        }

        /// <summary>
        /// A node will not enrol itself, however convincing the message looks.
        /// </summary>
        /// <remarks>
        /// <para><b>This is a real defect, caught live</b></para>
        /// <para>
        /// A hello sent to a peer with no chat server BOUNCES and arrives back on our own trunk
        /// port wearing our own magic. The ND server believed it and reported "100 up" on machine
        /// 100 - it had enrolled itself. A returned letter is the opposite of good news.
        /// </para>
        /// </remarks>
        [Fact]
        public void ANodeRefusesToEnrolItself()
        {
            ChatTrunks trunks = new ChatTrunks(100);

            Assert.False(trunks.Add(100));
            Assert.False(trunks.MarkHeard(100));
            Assert.Equal(ChatTrunkState.Unknown, trunks.StateOf(100));
        }

        /// <summary>
        /// A peer that was up and goes quiet is declared down, and its people are dropped.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured on D100, 2026-08-22</b></para>
        /// <para>
        /// Stopping D102's server made LIST-TRUNKS say "102 down" in about fifty seconds and its
        /// members vanished from /who. Sixty ticks is the rule; the live figure was inside it
        /// because the first poll happened after the abort.
        /// </para>
        /// </remarks>
        [Fact]
        public void APeerThatGoesQuietIsDeclaredDownAndReported()
        {
            ChatTrunks trunks = new ChatTrunks(100);
            trunks.Add(102);
            trunks.MarkHeard(102);

            ChatTrunkTick tick = new ChatTrunkTick();

            for (int second = 0; second < ChatTrunks.DeadAfterSeconds; second++)
            {
                trunks.Tick(tick);
                Assert.Equal(0, tick.WentDownCount);
                Assert.Equal(ChatTrunkState.Up, trunks.StateOf(102));
            }

            trunks.Tick(tick);

            Assert.Equal(1, tick.WentDownCount);
            Assert.Equal(102, tick.WentDownAt(0));
            Assert.Equal(ChatTrunkState.Down, trunks.StateOf(102));
        }

        /// <summary>
        /// A peer that is not up keeps being greeted, on a doubling wait.
        /// </summary>
        /// <remarks>
        /// <para><b>The cap bounds the wait, not the tries</b></para>
        /// <para>
        /// 15, 30, 60, 120, 240, then 300 for ever. A machine away for a week keeps being greeted
        /// every five minutes, which is what makes it rejoin without anybody noticing it returned.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnAbsentPeerIsGreetedOnADoublingWaitThatCapsAndNeverStops()
        {
            ChatTrunks trunks = new ChatTrunks(100);
            trunks.Add(102);

            ChatTrunkTick tick = new ChatTrunkTick();

            int[] expected = new int[] { 15, 30, 60, 120, 240, 300, 300, 300 };

            for (int step = 0; step < expected.Length; step++)
            {
                int gap = expected[step];

                for (int second = 0; second < gap - 1; second++)
                {
                    trunks.Tick(tick);
                    Assert.Equal(0, tick.GreetCount);
                }

                trunks.Tick(tick);

                Assert.Equal(1, tick.GreetCount);
                Assert.Equal(102, tick.GreetAt(0));
            }
        }

        /// <summary>
        /// Coming back up resets the backoff, every time and not just the first.
        /// </summary>
        /// <remarks>
        /// A peer that flaps would otherwise inherit a five-minute wait from its last absence and
        /// take five minutes to be missed again.
        /// </remarks>
        [Fact]
        public void ComingBackUpResetsTheBackoffEveryTime()
        {
            ChatTrunks trunks = new ChatTrunks(100);
            trunks.Add(102);

            ChatTrunkTick tick = new ChatTrunkTick();

            // Let the wait grow past the first step.
            for (int second = 0; second < 15 + 30; second++)
            {
                trunks.Tick(tick);
            }

            // It answers, then goes quiet again.
            trunks.MarkHeard(102);
            for (int second = 0; second <= ChatTrunks.DeadAfterSeconds; second++)
            {
                trunks.Tick(tick);
            }

            Assert.Equal(ChatTrunkState.Down, trunks.StateOf(102));

            // The very next greeting is at the FIRST wait again, not at the grown one - which is
            // the whole point: a peer that flaps must not inherit a five-minute wait.
            //
            // THE SECOND IT WENT DOWN IS ALSO THE FIRST SECOND OF THE NEW WAIT. Both halves of
            // the tick run in the same pass: the peer is declared down and its countdown reset,
            // and then the "not up" branch immediately spends one second of it. So fourteen more
            // ticks are silent, not fifteen.
            //
            // That is not an accident to be tidied away - the PLANC does exactly the same, and
            // this test exists to pin the two implementations to the same answer. The first
            // version of it expected fifteen and failed, and the CODE was right.
            for (int second = 0; second < ChatTrunks.FirstWaitSeconds - 2; second++)
            {
                trunks.Tick(tick);
                Assert.Equal(0, tick.GreetCount);
            }

            trunks.Tick(tick);
            Assert.Equal(1, tick.GreetCount);
            Assert.Equal(102, tick.GreetAt(0));
        }

        /// <summary>
        /// One side is enough to bring a trunk up.
        /// </summary>
        /// <remarks>
        /// <para><b>Proved live, and it fell out for free</b></para>
        /// <para>
        /// D102 was never told to trunk. D100 greeted it, D102's server marked D100 up and
        /// answered, and D100 marked 102 up. A peer that hears from a machine it does not have in
        /// its table simply adds it - which is what this checks.
        /// </para>
        /// </remarks>
        [Fact]
        public void HearingFromAnUnknownPeerAfterAddingItBringsTheTrunkUp()
        {
            ChatTrunks unTold = new ChatTrunks(102);

            // Nothing configured: the greeting is the first this node has heard of 100.
            Assert.Equal(ChatTrunkState.Unknown, unTold.StateOf(100));

            Assert.True(unTold.Add(100));
            Assert.True(unTold.MarkHeard(100));
            Assert.Equal(ChatTrunkState.Up, unTold.StateOf(100));
        }

        /// <summary>
        /// The table holds eight peers and refuses a ninth rather than overwriting one.
        /// </summary>
        [Fact]
        public void TheTableRefusesANinthPeer()
        {
            ChatTrunks trunks = new ChatTrunks(100);

            for (int i = 0; i < ChatTrunks.MaxPeers; i++)
            {
                Assert.True(trunks.Add(200 + i));
            }

            Assert.False(trunks.Add(999));
            Assert.Equal(ChatTrunkState.Unknown, trunks.StateOf(999));

            int[] peers = new int[ChatTrunks.MaxPeers];
            Assert.Equal(ChatTrunks.MaxPeers, trunks.ListPeers(peers));
        }

        /// <summary>
        /// Removing a peer frees its slot for another.
        /// </summary>
        [Fact]
        public void RemovingAPeerFreesItsSlot()
        {
            ChatTrunks trunks = new ChatTrunks(100);

            for (int i = 0; i < ChatTrunks.MaxPeers; i++)
            {
                trunks.Add(200 + i);
            }

            Assert.False(trunks.Add(999));
            Assert.True(trunks.Remove(203));
            Assert.True(trunks.Add(999));

            Assert.False(trunks.Remove(203));
        }

        /// <summary>
        /// Adding a peer twice is not an error and does not take a second slot.
        /// </summary>
        /// <remarks>
        /// START-TRUNK typed twice at an operator's terminal must not quietly consume the table.
        /// </remarks>
        [Fact]
        public void AddingTheSamePeerTwiceTakesOneSlot()
        {
            ChatTrunks trunks = new ChatTrunks(100);

            Assert.True(trunks.Add(102));
            Assert.True(trunks.Add(102));

            int[] peers = new int[ChatTrunks.MaxPeers];
            Assert.Equal(1, trunks.ListPeers(peers));
            Assert.Equal(102, peers[0]);
        }
    }
}
