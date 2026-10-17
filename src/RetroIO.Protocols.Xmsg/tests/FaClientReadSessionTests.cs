//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// The read sequencing, driven the way a live run drives it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests ask <see cref="FaClientReadSession.NextAction"/> repeatedly and feed the answers
    /// back, because that is what the driver does. Asking once per step would hide the class of
    /// defect that actually happens on a live link - a step that keeps asking to be sent again
    /// after it has been sent, which on the write side put 333 connect letters onto a real machine
    /// in forty-five seconds and was invisible to every test that fed a reply after each ask.
    /// </para>
    /// </remarks>
    public sealed class FaClientReadSessionTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// Where to write the message trace, so a failure shows the sequence that produced it.
        /// </param>
        public FaClientReadSessionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// A whole two-block read produces exactly the message sequence the capture shows.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The shape being pinned, per block, from
        /// <c>DOC/captures/ND-TO-ND-WRITE-2026-08-10/readback-10-blocks.pcapng</c>:
        /// </para>
        /// <code>
        /// request, [ack], [reply], our ack, [content], our ack, [content], our ack
        /// </code>
        /// <para>
        /// THREE acknowledgements per block. A reader that sends one leaves the server holding two
        /// unanswered messages, and a server resends what it has not seen answered until it drops
        /// the link - which presents as a transfer that stalls halfway and looks nothing like an
        /// acknowledgement problem.
        /// </para>
        /// </remarks>
        [Fact]
        public void AWholeReadSendsThreeAcknowledgementsPerBlock()
        {
            FaClientReadSession session = new FaClientReadSession(1);
            List<string> trace = new List<string>();

            // The letter, and its confirmation.
            Assert.Equal(FaClientAction.SendConnectLetter, session.NextAction());
            session.OnConnectLetterSent();

            // Asked again before the confirmation arrives, it must WAIT rather than send another.
            Assert.Equal(FaClientAction.Wait, session.NextAction());
            Assert.Equal(FaClientAction.Wait, session.NextAction());

            session.OnConnectionConfirmed();

            // Two blocks, learned from the open reply the way a real read learns it.
            int blockCount = 2;
            int acksSent = 0;
            int blocksRead = 0;

            for (int guard = 0; guard < 200; guard++)
            {
                FaClientAction action = session.NextAction();
                if (action == FaClientAction.Done || action == FaClientAction.Failed)
                {
                    break;
                }

                if (action == FaClientAction.SendRequest)
                {
                    FaOperation operation = session.CurrentOperation;
                    ushort sequence = session.CurrentSequence;
                    bool isBlock = session.OnBlockStep;
                    trace.Add("-> " + operation + " seq " + sequence);
                    session.OnRequestSent();

                    // The open reply is where the block count arrives.
                    if (operation == FaOperation.OpenFile)
                    {
                        session.SetBlockCount(blockCount);
                    }

                    session.OnShortAckReceived();
                    session.OnReplyReceived(operation, sequence);

                    if (isBlock)
                    {
                        blocksRead++;
                    }

                    continue;
                }

                if (action == FaClientAction.SendShortAck)
                {
                    trace.Add("-> ShortAck");
                    acksSent++;
                    session.OnShortAckSent();
                    continue;
                }

                if (action == FaClientAction.SendRelease)
                {
                    trace.Add("-> Close");
                    session.OnReleaseSent();
                    continue;
                }

                // Waiting means the server owes us content. Deliver one message.
                if (action == FaClientAction.Wait)
                {
                    trace.Add("<- content");
                    session.OnContentReceived();
                    continue;
                }

                break;
            }

            for (int i = 0; i < trace.Count; i++)
            {
                _output.WriteLine(trace[i]);
            }

            Assert.Equal(string.Empty, session.Failure);
            Assert.Equal(FaClientAction.Done, session.NextAction());
            Assert.Equal(blockCount, blocksRead);

            // Four prologue steps and two epilogue steps get one acknowledgement each - they have
            // only a reply to answer. Each block gets THREE: the reply and both content messages.
            int expectedAcks = FaReadLadder.PrologueLength + FaReadLadder.EpilogueLength
                + (blockCount * (1 + FaWriteLadder.MessagesPerBlock));
            Assert.Equal(expectedAcks, acksSent);
        }

        /// <summary>
        /// A block step does not finish until BOTH content messages have arrived.
        /// </summary>
        /// <remarks>
        /// The defect this prevents is the one that matters most for a reader: moving to the next
        /// block while the previous one is half delivered silently loses 1024 bytes out of the
        /// middle of the file, and the result is a file of exactly the right length that is wrong
        /// inside. A short file is obvious; this would not be.
        /// </remarks>
        [Fact]
        public void ABlockIsNotFinishedUntilBothContentMessagesHaveArrived()
        {
            FaClientReadSession session = OpenedSessionAtFirstBlock(2);

            int firstBlockStep = session.Step;
            Assert.True(session.OnBlockStep);
            Assert.Equal(0, session.CurrentBlock);

            // Request, acknowledged, replied to, and our acknowledgement sent.
            FaOperation operation = session.CurrentOperation;
            ushort sequence = session.CurrentSequence;
            session.OnRequestSent();
            session.OnShortAckReceived();
            session.OnReplyReceived(operation, sequence);
            Assert.Equal(FaClientAction.SendShortAck, session.NextAction());
            session.OnShortAckSent();

            // One content message only. The step must NOT have moved on.
            session.OnContentReceived();
            Assert.Equal(FaClientAction.SendShortAck, session.NextAction());
            session.OnShortAckSent();

            Assert.Equal(firstBlockStep, session.Step);
            Assert.Equal(0, session.CurrentBlock);

            // The second one completes it.
            session.OnContentReceived();
            session.OnShortAckSent();

            Assert.Equal(firstBlockStep + 1, session.Step);
            Assert.Equal(1, session.CurrentBlock);
            Assert.Equal(string.Empty, session.Failure);
        }

        /// <summary>
        /// A resent content message for a block already filled is ignored, not counted.
        /// </summary>
        /// <remarks>
        /// A real server resends anything it has not seen answered - measured against D100, which
        /// repeated both its acknowledgement and its reply many times over. Counting a repeat would
        /// run the block over and throw away the next one, which is the same "right length, wrong
        /// inside" failure as above.
        /// </remarks>
        [Fact]
        public void AResentContentMessageDoesNotOverfillTheBlock()
        {
            FaClientReadSession session = OpenedSessionAtFirstBlock(2);

            FaOperation operation = session.CurrentOperation;
            ushort sequence = session.CurrentSequence;
            session.OnRequestSent();
            session.OnShortAckReceived();
            session.OnReplyReceived(operation, sequence);
            session.OnShortAckSent();

            session.OnContentReceived();
            session.OnShortAckSent();
            session.OnContentReceived();
            session.OnShortAckSent();

            int afterTheBlock = session.Step;

            // The server repeats the second message. By now we have moved to the next block, which
            // has not been requested yet - so the repeat arrives with no reply outstanding, which
            // is exactly the shape that used to fail the write path.
            session.OnContentReceived();

            Assert.Equal(string.Empty, session.Failure);
            Assert.Equal(afterTheBlock, session.Step);

            // Tolerated, but NOT invisible. This is the only place a real byte could be dropped
            // without anyone noticing, so it is counted.
            Assert.Equal(1, session.IgnoredContentCount);
        }

        /// <summary>
        /// A clean read ignores nothing at all.
        /// </summary>
        /// <remarks>
        /// The counterpart of the test above, and the one that gives it meaning: if content were
        /// being discarded on a healthy transfer, tolerating repeats would be hiding it. The
        /// captured ten-block read has no repeats anywhere in it.
        /// </remarks>
        [Fact]
        public void AHealthyReadDiscardsNoContentAtAll()
        {
            FaClientReadSession session = OpenedSessionAtFirstBlock(3);

            for (int block = 0; block < 3; block++)
            {
                FaOperation operation = session.CurrentOperation;
                ushort sequence = session.CurrentSequence;
                session.OnRequestSent();
                session.OnShortAckReceived();
                session.OnReplyReceived(operation, sequence);
                session.OnShortAckSent();

                for (int i = 0; i < FaWriteLadder.MessagesPerBlock; i++)
                {
                    session.OnContentReceived();
                    session.OnShortAckSent();
                }
            }

            Assert.Equal(string.Empty, session.Failure);
            Assert.Equal(0, session.IgnoredContentCount);
        }

        /// <summary>
        /// Content arriving during the prologue fails the read rather than being kept.
        /// </summary>
        /// <remarks>
        /// It means we and the server disagree about what was asked for. Quietly keeping the bytes
        /// would assemble a file out of blocks nobody requested, and report success.
        /// </remarks>
        [Fact]
        public void ContentArrivingOutsideABlockStepFailsTheRead()
        {
            FaClientReadSession session = new FaClientReadSession(1);
            session.OnConnectLetterSent();
            session.OnConnectionConfirmed();

            // Still on ReserveFileEntry, the first prologue step.
            Assert.Equal(FaOperation.ReserveFileEntry, session.CurrentOperation);

            session.OnContentReceived();

            Assert.NotEqual(string.Empty, session.Failure);
            Assert.Equal(FaClientAction.Failed, session.NextAction());
        }

        /// <summary>
        /// The block count may be set during the prologue and never after it.
        /// </summary>
        /// <remarks>
        /// Rebuilding the ladder mid-prologue is safe - the prologue is identical whatever the
        /// block count is. Doing it once the blocks have started would move the ground under a step
        /// in progress, so the session fails rather than reading a file of the wrong length and
        /// reporting success.
        /// </remarks>
        [Fact]
        public void TheBlockCountIsAcceptedInThePrologueAndRefusedAfterIt()
        {
            FaClientReadSession session = new FaClientReadSession(1);
            session.OnConnectLetterSent();
            session.OnConnectionConfirmed();

            // Where a real read learns it: the reply to the open, which is step 1.
            RunStep(session, FaOperation.ReserveFileEntry);
            Assert.Equal(FaOperation.OpenFile, session.CurrentOperation);
            session.SetBlockCount(10);
            Assert.Equal(10, session.BlockCount);
            Assert.Equal(string.Empty, session.Failure);

            // Past the prologue it is refused.
            FaClientReadSession late = OpenedSessionAtFirstBlock(2);
            late.SetBlockCount(5);
            Assert.NotEqual(string.Empty, late.Failure);
        }

        /// <summary>
        /// The connect letter goes out ONCE however often the caller asks.
        /// </summary>
        /// <remarks>
        /// The write session had exactly this defect and no offline test caught it, because a test
        /// that feeds a reply never asks twice without one. Asked repeatedly with nothing arriving,
        /// the session must say Wait.
        /// </remarks>
        [Fact]
        public void TheConnectLetterIsSentOnceHoweverOftenWeAsk()
        {
            FaClientReadSession session = new FaClientReadSession(1);

            Assert.Equal(FaClientAction.SendConnectLetter, session.NextAction());
            session.OnConnectLetterSent();

            for (int i = 0; i < 50; i++)
            {
                Assert.Equal(FaClientAction.Wait, session.NextAction());
            }
        }

        /// <summary>
        /// A repeated reply for the step just finished is ignored rather than failing the read.
        /// </summary>
        [Fact]
        public void ARepeatedReplyForTheFinishedStepIsIgnored()
        {
            FaClientReadSession session = new FaClientReadSession(1);
            session.OnConnectLetterSent();
            session.OnConnectionConfirmed();

            RunStep(session, FaOperation.ReserveFileEntry);

            // The server resends the reserve's reply while we are on the open.
            Assert.Equal(FaOperation.OpenFile, session.CurrentOperation);
            session.OnRequestSent();
            session.OnShortAckReceived();
            session.OnReplyReceived(FaOperation.ReserveFileEntry, 1);

            Assert.Equal(string.Empty, session.Failure);
        }

        /// <summary>
        /// Builds a session that has finished its prologue and is sitting on the first block.
        /// </summary>
        /// <param name="blockCount">
        /// How many blocks to read.
        /// </param>
        /// <returns>
        /// The session, ready to send its first block request.
        /// </returns>
        private static FaClientReadSession OpenedSessionAtFirstBlock(int blockCount)
        {
            FaClientReadSession session = new FaClientReadSession(1);
            session.OnConnectLetterSent();
            session.OnConnectionConfirmed();

            FaOperation[] prologue = FaReadLadder.Prologue();
            for (int i = 0; i < prologue.Length; i++)
            {
                if (prologue[i] == FaOperation.OpenFile)
                {
                    session.SetBlockCount(blockCount);
                }

                RunStep(session, prologue[i]);
            }

            return session;
        }

        /// <summary>
        /// Takes one non-block ladder step all the way through.
        /// </summary>
        /// <param name="session">
        /// The session to drive.
        /// </param>
        /// <param name="expected">
        /// The operation the step is expected to be, so a ladder change shows up here.
        /// </param>
        private static void RunStep(FaClientReadSession session, FaOperation expected)
        {
            Assert.Equal(expected, session.CurrentOperation);

            ushort sequence = session.CurrentSequence;
            session.OnRequestSent();
            session.OnShortAckReceived();
            session.OnReplyReceived(expected, sequence);
            session.OnShortAckSent();
        }
    }
}
