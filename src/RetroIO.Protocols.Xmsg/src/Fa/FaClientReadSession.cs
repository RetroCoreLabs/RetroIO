//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Fa
{
    /// <summary>
    /// Drives a client through the captured ladder for reading a file: what to send next, and what
    /// each answer means.
    /// </summary>
    /// <remarks>
    /// <para><b>Decisions only - no bytes, no I/O</b></para>
    /// <para>
    /// Same split as <see cref="FaClientWriteSession"/>: this says WHAT to do and never builds a
    /// frame or touches a socket, so the sequencing - the part that is expensive to test against a
    /// real machine - can be tested on its own. It does not even hold the file content; it counts
    /// the messages and lets the driver keep the bytes.
    /// </para>
    /// <para><b>Why this is a separate class from the write session</b></para>
    /// <para>
    /// The two share the connect handshake and the request/acknowledge/reply cycle, and a shared
    /// base for that is worth doing one day. It is NOT done here, deliberately: the write session
    /// carries nine live defects' worth of hard-won behaviour and is the only path proved against a
    /// real ND, so refactoring it while adding an unproved direction would put both at risk in one
    /// change. Read first, then share, with both directions passing.
    /// </para>
    /// <para><b>Where the two directions genuinely differ</b></para>
    /// <para>
    /// It is not just which way the bytes go. On a WRITE the content follows the REQUEST and the
    /// reply completes the operation. On a READ the reply completes the request and the content
    /// follows the REPLY. Measured in
    /// <c>DOC/captures/ND-TO-ND-WRITE-2026-08-10/readback-10-blocks.pcapng</c>, one block:
    /// </para>
    /// <code>
    /// us     -> server   8400  ReadFile block 0   our request
    /// server -> us       ShortAck                 the server answers the request
    /// server -> us       8400  reply              a new exchange
    /// us     -> server   ShortAck                 we answer the reply
    /// server -> us       0500  content message 1  1024 bytes, as a fragment pair
    /// us     -> server   ShortAck                 we answer it
    /// server -> us       8600  content message 2  1024 bytes, as a fragment pair
    /// us     -> server   ShortAck                 we answer it
    /// us     -> server   8500  ReadFile block 1   only now does the next block start
    /// </code>
    /// <para>
    /// So a block step costs THREE short acknowledgements from us, where a write's costs one. A
    /// reader that acknowledges only the reply leaves the server holding two unanswered messages;
    /// it will resend them and eventually drop the link, which looks exactly like a transfer that
    /// stalls halfway.
    /// </para>
    /// <para><b>Every acknowledgement we send ORIGINATES</b></para>
    /// <para>
    /// An FA short acknowledgement travels as an ordinary Data message and spends one of OUR
    /// Flags 1 numbers. Only the datagram acknowledgement, subtype <c>0x03</c>, echoes. Getting
    /// this backwards put our acknowledgements behind the peer's expectation, where they were
    /// dropped in silence - it cost days on the write side and is not repeated here.
    /// </para>
    /// </remarks>
    public sealed class FaClientReadSession
    {
        private int _step;
        private bool _connected;
        private bool _letterSent;
        private bool _requestSent;
        private bool _requestAcknowledged;
        private bool _replyReceived;
        private bool _closed;
        private string _failure;

        /// <summary>
        /// The operations this session will send, in order.
        /// </summary>
        /// <remarks>
        /// NOT readonly, because the length of a read ladder is not known when the read starts -
        /// see <see cref="SetBlockCount"/>. It is replaced at most once, and only before the first
        /// block request.
        /// </remarks>
        private FaOperation[] _ladder;

        /// <summary>
        /// How many short acknowledgements we owe the server.
        /// </summary>
        /// <remarks>
        /// A count rather than a flag, because on a block step three separate messages each want
        /// one and they can arrive faster than we send. A flag would silently collapse two
        /// acknowledgements into one and leave the server waiting for a message it never gets.
        /// </remarks>
        private int _acksOwed;

        /// <summary>
        /// How many content messages have arrived for the block in progress.
        /// </summary>
        private int _contentReceived;

        /// <summary>
        /// Whether our acknowledgement of the current step's reply has gone out.
        /// </summary>
        private bool _replyAcknowledged;

        /// <summary>
        /// How many content messages were taken as repeats and not kept.
        /// </summary>
        private int _ignoredContent;

        /// <summary>
        /// Gets how many content messages were treated as repeats and discarded.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Expected to be zero on a healthy transfer, and it is zero across the whole captured
        /// ten-block read. A non-zero count means the server repeated itself, which is normal after
        /// a lost acknowledgement - but it is also the ONLY way a silent data loss could happen
        /// here, so it is exposed rather than kept private. See <see cref="OnContentReceived"/> for
        /// what cannot be told apart and why.
        /// </para>
        /// <para>
        /// A caller that finds this non-zero AND the file wrong has its answer immediately, instead
        /// of starting from "the bytes are corrupted somewhere".
        /// </para>
        /// </remarks>
        public int IgnoredContentCount
        {
            get { return _ignoredContent; }
        }

        /// <summary>
        /// Starts a session for a file of a given size in blocks.
        /// </summary>
        /// <param name="blockCount">
        /// How many content blocks the file holds. It is not chosen - it comes from the byte length
        /// in the OPEN reply, through <see cref="FaReadLadder.BlockCountForLength"/>.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="blockCount"/> is negative.
        /// </exception>
        /// <remarks>
        /// The block count is not known when the session starts. The open reply that carries
        /// the length is the SECOND step of the ladder, so a caller has to build the ladder before
        /// it can know how long the ladder is. That is why <see cref="SetBlockCount"/> exists.
        /// </remarks>
        public FaClientReadSession(int blockCount)
        {
            _failure = string.Empty;
            _ladder = FaReadLadder.ForBlockCount(blockCount);
        }

        /// <summary>
        /// Starts a DIAGNOSTIC session that reserves a file entry and sets the block size without
        /// opening the file.
        /// </summary>
        /// <returns>
        /// A session running <see cref="FaReadLadder.ProbeWithoutOpen"/>.
        /// </returns>
        /// <remarks>
        /// <para>Not a transfer. It reads nothing and writes nothing.</para>
        /// It exists to separate the two readings of the follow-on refusal <c>A2 4104</c> - see
        /// <see cref="FaReadLadder.ProbeWithoutOpen"/> for the question and
        /// <c>DOC/CARVE-FA-READ-REFUSAL-2026-08-18.md</c> for the working.
        /// <para>
        /// <see cref="SetBlockCount"/> must never be called on one of these. It would replace the
        /// probe ladder with a real transfer ladder, and nothing here can call it: the block count
        /// arrives in the OPEN reply, and this session never sends an open.
        /// </para>
        /// </remarks>
        public static FaClientReadSession CreateProbeWithoutOpen()
        {
            FaClientReadSession session = new FaClientReadSession(0);
            session._ladder = FaReadLadder.ProbeWithoutOpen();
            return session;
        }

        /// <summary>
        /// Gets why the session failed, or an empty string while it has not.
        /// </summary>
        public string Failure
        {
            get { return _failure; }
        }

        /// <summary>
        /// Gets which ladder step the session is on, counting from zero.
        /// </summary>
        public int Step
        {
            get { return _step; }
        }

        /// <summary>
        /// Gets whether the server has ANSWERED our connect letter.
        /// </summary>
        /// <remarks>
        /// A caller needs this to tell a first confirmation from a RETRANSMITTED one. Acting on a
        /// repeat rebuilds the conversation and rewinds its message counter mid-session, which is
        /// the defect that presented as "stalls at OpenFile" on the write side.
        /// </remarks>
        public bool IsConnected
        {
            get { return _connected; }
        }

        /// <summary>
        /// Gets whether the step in progress is a block request.
        /// </summary>
        public bool OnBlockStep
        {
            get { return _step < _ladder.Length && _ladder[_step] == FaReadLadder.BlockOperation; }
        }

        /// <summary>
        /// Gets how many blocks this ladder reads.
        /// </summary>
        public int BlockCount
        {
            get { return _ladder.Length - FaReadLadder.PrologueLength - FaReadLadder.EpilogueLength; }
        }

        /// <summary>
        /// Sets how many blocks to read, once the OPEN reply has said how long the file is.
        /// </summary>
        /// <param name="blockCount">
        /// The block count, from <see cref="FaReadLadder.BlockCountForLength"/>. Must not be
        /// negative.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="blockCount"/> is negative.
        /// </exception>
        /// <remarks>
        /// <para><b>Why a read cannot know its own length up front</b></para>
        /// <para>
        /// A write knows how many blocks it is sending before it sends anything, because it holds
        /// the file. A read does not: the file's byte length arrives in the reply to
        /// <see cref="FaOperation.OpenFile"/>, which is the SECOND step of the ladder. So the
        /// ladder has to exist before its length can be known, and this is where the real length
        /// lands.
        /// </para>
        /// <para>
        /// Rebuilding the ladder is safe here because the prologue is identical whatever the block
        /// count is, and the steps already taken are all prologue steps. Calling it after the first
        /// block request would move the ground under a step in progress, so that FAILS the session
        /// rather than being allowed - a caller that gets this wrong would otherwise read a file of
        /// the wrong length and report success.
        /// </para>
        /// </remarks>
        public void SetBlockCount(int blockCount)
        {
            if (blockCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(blockCount), "A file cannot have a negative number of blocks.");
            }

            if (_step >= FaReadLadder.PrologueLength)
            {
                Fail("The block count arrived at step " + _step
                    + ", after the ladder had already left the prologue.");
                return;
            }

            _ladder = FaReadLadder.ForBlockCount(blockCount);
        }

        /// <summary>
        /// Gets the operation the current step sends.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the ladder is already finished.
        /// </exception>
        public FaOperation CurrentOperation
        {
            get
            {
                if (_step >= _ladder.Length)
                {
                    throw new InvalidOperationException("The read ladder is finished.");
                }

                return _ladder[_step];
            }
        }

        /// <summary>
        /// Gets the sequence number the current step sends.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the ladder is already finished.
        /// </exception>
        public ushort CurrentSequence
        {
            get
            {
                if (_step >= _ladder.Length)
                {
                    throw new InvalidOperationException("The read ladder is finished.");
                }

                return FaWriteLadder.SequenceForStep(_step);
            }
        }

        /// <summary>
        /// Gets which block the current step is reading, counting from zero.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the current step is not a block request.
        /// </exception>
        /// <remarks>
        /// Derived from the step's position rather than counted in a field of its own, so it cannot
        /// drift out of step with the ladder.
        /// </remarks>
        public int CurrentBlock
        {
            get
            {
                if (!OnBlockStep)
                {
                    throw new InvalidOperationException(
                        "The read ladder is not on a block request; it is on step " + _step + ".");
                }

                return _step - FaReadLadder.PrologueLength;
            }
        }

        /// <summary>
        /// Decides what to do next.
        /// </summary>
        /// <returns>
        /// The next action. <see cref="FaClientAction.Wait"/> means the peer owes us something.
        /// </returns>
        public FaClientAction NextAction()
        {
            if (_failure.Length != 0) { return FaClientAction.Failed; }
            if (_closed) { return FaClientAction.Done; }

            if (!_connected)
            {
                // Sent once, then we wait. Whether a real client retransmits an unanswered letter
                // is UNKNOWN - no capture shows one going unanswered - so nothing is invented. A
                // read that never gets its confirmation stalls, and a stall is visible; a flood is
                // what happens when this lies. The write session learned that the hard way, with
                // 333 connect letters onto a real machine in forty-five seconds.
                return _letterSent ? FaClientAction.Wait : FaClientAction.SendConnectLetter;
            }

            // Anything we owe an acknowledgement for goes out BEFORE the next request. The server
            // will resend a message it has not seen answered, and a resend arriving mid-request is
            // how a conversation gets out of step.
            if (_acksOwed > 0) { return FaClientAction.SendShortAck; }

            if (_step < _ladder.Length)
            {
                if (!_requestSent) { return FaClientAction.SendRequest; }

                // The short acknowledgement to our request comes first, then the reply as a new
                // exchange. Waiting for both, in that order, is the whole point of this type.
                if (!_requestAcknowledged) { return FaClientAction.Wait; }
                if (!_replyReceived) { return FaClientAction.Wait; }

                // The reply is in and acknowledged; on a block step the content is still coming.
                return FaClientAction.Wait;
            }

            return FaClientAction.SendRelease;
        }

        /// <summary>
        /// Records that the connect letter was answered by the server's confirmation.
        /// </summary>
        public void OnConnectionConfirmed()
        {
            _connected = true;
        }

        /// <summary>
        /// Records that the connect letter has left, so we wait rather than sending it again.
        /// </summary>
        public void OnConnectLetterSent()
        {
            _letterSent = true;
        }

        /// <summary>
        /// Records that the request just sent has left.
        /// </summary>
        public void OnRequestSent()
        {
            _requestSent = true;
        }

        /// <summary>
        /// Records the short acknowledgement that answers our request.
        /// </summary>
        public void OnShortAckReceived()
        {
            if (!_requestSent)
            {
                // A REPEAT of the acknowledgement for the step we just finished. A real server
                // resends anything it has not seen answered, and its short acknowledgement keeps
                // arriving after we have moved on. Treating that as an error killed the write path
                // one step after the duplicate-reply tolerance had let it through.
                //
                // Tolerated only once a step has actually completed. Before that, an
                // acknowledgement really has arrived out of nowhere and must still fail.
                if (_step > 0)
                {
                    return;
                }

                Fail("A short acknowledgement arrived before any request was sent.");
                return;
            }

            _requestAcknowledged = true;
        }

        /// <summary>
        /// Records the server's reply to the current step.
        /// </summary>
        /// <param name="operation">
        /// The operation the reply echoes.
        /// </param>
        /// <param name="sequence">
        /// The sequence number the reply echoes.
        /// </param>
        /// <remarks>
        /// <para>
        /// A reply ECHOES the request's operation and sequence, which is what matches it to its
        /// request. A mismatch means the conversation has slipped and is treated as a failure
        /// rather than carried on with.
        /// </para>
        /// <para><b>Except a duplicate, which is normal</b></para>
        /// <para>
        /// A real server resends a reply it has not seen acknowledged, so a reply echoing the step
        /// we have just finished is ignored. The tolerance is deliberately ONE step back, not
        /// "anything already seen" - a peer repeating everything is telling us something of ours is
        /// not getting through at all, and that has to stay visible.
        /// </para>
        /// </remarks>
        public void OnReplyReceived(FaOperation operation, ushort sequence)
        {
            if (!_requestAcknowledged)
            {
                Fail("A reply arrived before our request was acknowledged.");
                return;
            }

            if (IsRepeatOfPreviousStep(operation, sequence))
            {
                return;
            }

            if (operation != CurrentOperation || sequence != CurrentSequence)
            {
                Fail("Expected a reply to " + CurrentOperation + " sequence " + CurrentSequence
                    + " but got " + operation + " sequence " + sequence + ".");
                return;
            }

            if (_replyReceived)
            {
                // The same reply again, for the step we are still on. Nothing to do, and it must
                // NOT owe another acknowledgement counted against the content.
                return;
            }

            _replyReceived = true;
            _acksOwed++;
        }

        /// <summary>
        /// Records the arrival of one content message.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two per block, each carrying <see cref="FaFileDataCodec.BlockLength"/> bytes, and each
        /// wanting an acknowledgement of its own.
        /// </para>
        /// <para>
        /// Content arriving when no block request is outstanding is a failure rather than something
        /// to absorb: it means we and the server disagree about what was asked for, and quietly
        /// keeping the bytes would assemble a file out of blocks nobody asked for. The one thing
        /// that is NOT an error is content arriving before we have sent our acknowledgement of the
        /// reply - the server pipelines, and the capture shows it doing so.
        /// </para>
        /// </remarks>
        public void OnContentReceived()
        {
            if (!OnBlockStep)
            {
                Fail("Content arrived while the ladder was on " + DescribeStep() + ".");
                return;
            }

            if (!_replyReceived)
            {
                // A RESENT content message for the block we have already finished. A real server
                // repeats anything it has not seen answered, and by the time the repeat lands we
                // have moved on to the next block - which has not been requested yet, so no reply
                // has arrived for it.
                //
                // This must NOT fail the read. The write side failed on exactly this shape of
                // event, twice, and both times the transfer was otherwise healthy.
                //
                // WHAT WE CANNOT TELL APART, stated plainly: from inside this class a repeat looks
                // identical to content arriving EARLY, before its own reply. The captures show the
                // server always sending the reply first, so a repeat is the reading that fits -
                // but if that is ever wrong, this discards 1024 bytes of a real file. So it is
                // counted rather than dropped in silence, and a driver that logs
                // IgnoredContentCount turns a silent data loss into a visible oddity.
                _ignoredContent++;
                return;
            }

            if (_contentReceived >= FaWriteLadder.MessagesPerBlock)
            {
                // A resent content message for the block we have already filled. Counting it would
                // run the block over and throw away the next one. Same reasoning as above.
                _ignoredContent++;
                return;
            }

            _contentReceived++;
            _acksOwed++;
            TryCompleteStep();
        }

        /// <summary>
        /// Records that we sent one short acknowledgement.
        /// </summary>
        public void OnShortAckSent()
        {
            if (_acksOwed <= 0)
            {
                Fail("A short acknowledgement was sent when nothing was owed one.");
                return;
            }

            _acksOwed--;

            if (!_replyAcknowledged && _replyReceived)
            {
                _replyAcknowledged = true;
            }

            TryCompleteStep();
        }

        /// <summary>
        /// Moves to the next ladder step once everything the current one needs has happened.
        /// </summary>
        /// <remarks>
        /// One place decides a step is finished, called from every event that could finish it. The
        /// alternative - each handler deciding for itself - is how a step gets advanced twice, and
        /// a double advance skips a whole block without any error.
        /// </remarks>
        private void TryCompleteStep()
        {
            if (_step >= _ladder.Length) { return; }
            if (!_replyReceived || !_replyAcknowledged) { return; }

            // A block step is not finished until BOTH content messages have arrived and every
            // acknowledgement they owe has gone out.
            if (OnBlockStep)
            {
                if (_contentReceived < FaWriteLadder.MessagesPerBlock) { return; }
                if (_acksOwed > 0) { return; }
            }

            _step++;
            _requestSent = false;
            _requestAcknowledged = false;
            _replyReceived = false;
            _replyAcknowledged = false;
            _contentReceived = 0;
        }

        /// <summary>
        /// Whether a reply echoes the step we have just completed, rather than the one we are on.
        /// </summary>
        /// <param name="operation">
        /// The operation the reply echoes.
        /// </param>
        /// <param name="sequence">
        /// The sequence the reply echoes.
        /// </param>
        /// <returns>
        /// <c>true</c> when this is the server resending the previous step's reply.
        /// </returns>
        private bool IsRepeatOfPreviousStep(FaOperation operation, ushort sequence)
        {
            if (_step == 0)
            {
                return false;
            }

            int previous = _step - 1;
            return operation == _ladder[previous]
                && sequence == FaWriteLadder.SequenceForStep(previous);
        }

        /// <summary>
        /// Describes where the ladder is, for a failure message.
        /// </summary>
        /// <returns>
        /// The current operation, or a note that the ladder has finished.
        /// </returns>
        private string DescribeStep()
        {
            if (_step >= _ladder.Length)
            {
                return "the finished ladder";
            }

            return "step " + _step + " (" + _ladder[_step] + ")";
        }

        /// <summary>
        /// Records that the close has been sent, finishing the session.
        /// </summary>
        public void OnReleaseSent()
        {
            _closed = true;
        }

        /// <summary>
        /// Records that the peer rejected something.
        /// </summary>
        /// <param name="reason">
        /// What the peer said, for the failure text.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="reason"/> is null.
        /// </exception>
        public void OnRejected(string reason)
        {
            if (reason == null)
            {
                throw new ArgumentNullException(nameof(reason));
            }

            Fail("The peer rejected the session: " + reason);
        }

        /// <summary>
        /// Puts the session into its failed state, keeping the FIRST reason.
        /// </summary>
        /// <param name="reason">
        /// What went wrong.
        /// </param>
        /// <remarks>
        /// The first failure is the useful one. A later message arriving against a broken session
        /// would otherwise overwrite the reason with a consequence, which is how a diagnosis ends
        /// up describing the symptom instead of the cause.
        /// </remarks>
        private void Fail(string reason)
        {
            if (_failure.Length == 0)
            {
                _failure = reason;
            }
        }
    }
}
