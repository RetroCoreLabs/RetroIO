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
    /// The client write session's sequencing.
    /// </summary>
    /// <remarks>
    /// No frames and no machine: this drives the state machine directly, which is why the whole
    /// exchange model can be exercised - including the failure paths a live test could only reach
    /// by breaking a real conversation.
    /// </remarks>
    public sealed class FaClientWriteSessionTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaClientWriteSessionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Runs one ladder step the way a correct client would.
        /// </summary>
        /// <param name="session">
        /// The session to advance.
        /// </param>
        private static void CompleteOneStep(FaClientWriteSession session)
        {
            session.OnRequestSent();
            session.OnShortAckReceived();
            session.OnReplyReceived(session.CurrentOperation, session.CurrentSequence);
            session.OnShortAckSent();
        }

        /// <summary>
        /// A whole write runs connect, four ladder steps, the content, then the close.
        /// </summary>
        [Fact]
        public void AWholeWriteWalksTheCapturedLadderWithABlockAfterEachWrite()
        {
            const int Blocks = 9;   // what the capture wrote
            FaClientWriteSession session = new FaClientWriteSession(Blocks);

            Assert.Equal(FaClientAction.SendConnectLetter, session.NextAction());
            session.OnConnectionConfirmed();

            FaOperation[] expected = FaWriteLadder.ForBlockCount(Blocks);
            int blocksSent = 0;

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(FaClientAction.SendRequest, session.NextAction());
                Assert.Equal(expected[i], session.CurrentOperation);

                bool isWrite = expected[i] == FaOperation.WriteFile;
                CompleteOneStep(session);

                if (isWrite)
                {
                    // The block follows its write exchange, before the next request.
                    Assert.Equal(FaClientAction.SendData, session.NextAction());
                    session.OnDataSent();
                    blocksSent++;
                }
            }

            Assert.Equal(Blocks, blocksSent);
            Assert.Equal(FaClientAction.SendRelease, session.NextAction());
            session.OnReleaseSent();

            Assert.Equal(FaClientAction.Done, session.NextAction());
            Assert.Equal(string.Empty, session.Failure);
            _output.WriteLine("ladder length " + expected.Length + ", blocks " + blocksSent);
        }

        /// <summary>
        /// The session ends with close and release, not with the last block.
        /// </summary>
        /// <remarks>
        /// The failure this guards against: a client that stops after its content leaves the file
        /// unclosed and the entry reserved on the server.
        /// </remarks>
        [Fact]
        public void TheLastThreeStepsAreTheEpilogue()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();

            FaOperation[] ladder = FaWriteLadder.ForBlockCount(1);
            List<FaOperation> walked = new List<FaOperation>();

            for (int i = 0; i < ladder.Length; i++)
            {
                walked.Add(session.CurrentOperation);
                bool isWrite = session.CurrentOperation == FaOperation.WriteFile;
                CompleteOneStep(session);
                if (isWrite) { session.OnDataSent(); }
            }

            Assert.Equal(FaOperation.SiiiSpecial, walked[walked.Count - 3]);
            Assert.Equal(FaOperation.CloseFile, walked[walked.Count - 2]);
            Assert.Equal(FaOperation.ReleaseFileEntry, walked[walked.Count - 1]);
        }

        /// <summary>
        /// Content nobody asked for is refused.
        /// </summary>
        [Fact]
        public void ContentWithoutAWriteRequestIsRefused()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();

            session.OnDataSent();

            Assert.Equal(FaClientAction.Failed, session.NextAction());
            Assert.Contains("announced", session.Failure);
        }

        /// <summary>
        /// Nothing happens before the connection is confirmed.
        /// </summary>
        [Fact]
        public void NoRequestGoesOutBeforeTheConfirmation()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);

            Assert.Equal(FaClientAction.SendConnectLetter, session.NextAction());

            // Still the connect letter - the ladder has not started.
            Assert.Equal(FaClientAction.SendConnectLetter, session.NextAction());
        }

        /// <summary>
        /// After sending a request the session waits, and keeps waiting until BOTH the short
        /// acknowledgement and the reply have arrived.
        /// </summary>
        /// <remarks>
        /// This is the heart of it. A request is answered by a short acknowledgement, and the reply
        /// is a separate exchange afterwards. A client that treats the reply as the answer, or that
        /// moves on after the acknowledgement, falls behind the peer's exchange count and draws an
        /// XENSE sequencing reject.
        /// </remarks>
        [Fact]
        public void AStepWaitsForTheShortAckAndThenTheReply()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();

            session.OnRequestSent();
            Assert.Equal(FaClientAction.Wait, session.NextAction());

            session.OnShortAckReceived();
            Assert.Equal(FaClientAction.Wait, session.NextAction());   // the reply is still owed

            session.OnReplyReceived(session.CurrentOperation, session.CurrentSequence);
            Assert.Equal(FaClientAction.SendShortAck, session.NextAction());

            session.OnShortAckSent();
            Assert.Equal(FaClientAction.SendRequest, session.NextAction());
        }

        /// <summary>
        /// The step only advances once we have acknowledged the reply.
        /// </summary>
        [Fact]
        public void TheStepAdvancesOnlyAfterWeAcknowledgeTheReply()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();

            Assert.Equal(0, session.Step);
            session.OnRequestSent();
            session.OnShortAckReceived();
            session.OnReplyReceived(session.CurrentOperation, session.CurrentSequence);

            Assert.Equal(0, session.Step);
            session.OnShortAckSent();
            Assert.Equal(1, session.Step);
        }

        /// <summary>
        /// A reply for the wrong operation fails the session rather than being carried on with.
        /// </summary>
        /// <remarks>
        /// Continuing would send the next request against a state the server does not share, and
        /// the symptom would appear several exchanges later - the expensive kind of bug.
        /// </remarks>
        [Fact]
        public void AReplyForTheWrongOperationFailsTheSession()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();
            session.OnRequestSent();
            session.OnShortAckReceived();

            session.OnReplyReceived(FaOperation.DeleteFile, 1);

            Assert.Equal(FaClientAction.Failed, session.NextAction());
            Assert.Contains("ReserveFileEntry", session.Failure);
            Assert.Contains("DeleteFile", session.Failure);
        }

        /// <summary>
        /// A reply with the wrong sequence number also fails.
        /// </summary>
        [Fact]
        public void AReplyWithTheWrongSequenceFailsTheSession()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();
            session.OnRequestSent();
            session.OnShortAckReceived();

            session.OnReplyReceived(FaOperation.ReserveFileEntry, 99);

            Assert.Equal(FaClientAction.Failed, session.NextAction());
            Assert.Contains("99", session.Failure);
        }

        /// <summary>
        /// A reply arriving before the short acknowledgement is a slipped conversation.
        /// </summary>
        [Fact]
        public void AReplyBeforeTheShortAckFails()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();
            session.OnRequestSent();

            session.OnReplyReceived(FaOperation.ReserveFileEntry, 1);

            Assert.Equal(FaClientAction.Failed, session.NextAction());
            Assert.Contains("acknowledged", session.Failure);
        }

        /// <summary>
        /// The FIRST failure is the one reported.
        /// </summary>
        /// <remarks>
        /// Later messages arriving against a broken session would otherwise overwrite the cause
        /// with a consequence, and the diagnosis would describe the symptom.
        /// </remarks>
        [Fact]
        public void TheFirstFailureIsKept()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();
            session.OnRequestSent();
            session.OnReplyReceived(FaOperation.ReserveFileEntry, 1);   // too early

            string first = session.Failure;

            session.OnRejected("something else went wrong later");

            Assert.Equal(first, session.Failure);
        }

        /// <summary>
        /// A peer rejection fails the session with the peer's reason.
        /// </summary>
        [Fact]
        public void APeerRejectionIsReported()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();
            session.OnRejected("XENSE -34");

            Assert.Equal(FaClientAction.Failed, session.NextAction());
            Assert.Contains("XENSE -34", session.Failure);
        }

        /// <summary>
        /// Asking about the current step past the end of the ladder is refused.
        /// </summary>
        [Fact]
        public void TheLadderEndIsNotWalkedPast()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);
            session.OnConnectionConfirmed();

            FaOperation[] ladder = FaWriteLadder.ForBlockCount(1);
            for (int i = 0; i < ladder.Length; i++)
            {
                bool isWrite = session.CurrentOperation == FaOperation.WriteFile;
                CompleteOneStep(session);
                if (isWrite) { session.OnDataSent(); }
            }

            Assert.Throws<InvalidOperationException>(() => session.CurrentOperation);
            Assert.Throws<InvalidOperationException>(() => session.CurrentSequence);

            // The ladder already contains the close and the release, so what is left after walking
            // it is the conversation close - not another operation and not more content.
            Assert.Equal(FaClientAction.SendRelease, session.NextAction());
        }

        /// <summary>
        /// A null rejection reason is refused rather than recorded.
        /// </summary>
        [Fact]
        public void NullRejectionReasonsAreRejected()
        {
            FaClientWriteSession session = new FaClientWriteSession(1);

            Assert.Throws<ArgumentNullException>(() => session.OnRejected(null!));
        }
    
        /// <summary>
        /// A push that dies after OpenFile still closes the file on the peer.
        /// </summary>
        /// <remarks>
        /// This is the D100 lesson of 2026-08-17 written down as a test. A transfer stalled part
        /// way through and simply stopped; CHAT:PLNC was then stuck OPEN on the machine, could not
        /// be rewritten, and could not even be deleted - SINTRAN answered FILE ALREADY OPEN. It
        /// took a file-server restart to clear, because the file belongs to the server's RT
        /// program rather than to any terminal, so it does not even show in LIST-OPEN-FILES.
        /// <para>
        /// Without <see cref="FaClientWriteSession.AbandonAfterOpenFile"/> the session reports
        /// <see cref="FaClientAction.Failed"/> and there is no way to reach the epilogue, so this
        /// test cannot be made to pass by tightening the assertions - the capability has to exist.
        /// </para>
        /// </remarks>
        [Fact]
        public void AbandoningAfterOpenFileStillSendsTheEpilogueThatClosesTheFile()
        {
            FaClientWriteSession session = new FaClientWriteSession(9);

            session.OnConnectionConfirmed();
            CompleteOneStep(session);                       // ReserveFileEntry
            Assert.False(session.FileOpenOnPeer);

            Assert.Equal(FaOperation.OpenFile, session.CurrentOperation);
            CompleteOneStep(session);                       // OpenFile - the file is now open
            Assert.True(session.FileOpenOnPeer);

            // The peer goes quiet in the middle and the caller gives up.
            session.OnRejected("the peer stopped answering");
            Assert.Equal(FaClientAction.Failed, session.NextAction());

            Assert.True(session.AbandonAfterOpenFile());

            // What follows must be the same epilogue a finished write sends, in order.
            FaOperation[] epilogue = FaWriteLadder.Epilogue();
            for (int i = 0; i < epilogue.Length; i++)
            {
                Assert.Equal(FaClientAction.SendRequest, session.NextAction());
                Assert.Equal(epilogue[i], session.CurrentOperation);
                CompleteOneStep(session);
            }

            Assert.False(session.FileOpenOnPeer);
            Assert.Equal(FaClientAction.SendRelease, session.NextAction());
            session.OnReleaseSent();
            Assert.Equal(FaClientAction.Done, session.NextAction());

            _output.WriteLine("abandoned push still walked " + epilogue.Length
                + " epilogue steps, so the peer closed the file");
        }

        /// <summary>
        /// Abandoning before OpenFile has nothing to put down and says so.
        /// </summary>
        [Fact]
        public void AbandoningBeforeOpenFileReportsThereIsNothingToClose()
        {
            FaClientWriteSession session = new FaClientWriteSession(2);

            session.OnConnectionConfirmed();
            CompleteOneStep(session);                       // ReserveFileEntry only

            Assert.False(session.FileOpenOnPeer);
            Assert.False(session.AbandonAfterOpenFile());
        }
}
}
