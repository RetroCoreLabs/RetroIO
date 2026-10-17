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
    /// Drives a client through the captured ladder for writing a file: what to send next, and what
    /// each answer means.
    /// </summary>
    /// <remarks>
    /// <para><b>Decisions only - no bytes, no I/O</b></para>
    /// <para>
    /// This type says WHAT to do and never builds a frame or touches a socket. That is deliberate:
    /// the sequencing is the part that is easy to get wrong and expensive to test against a real
    /// machine, so it is separated out and tested on its own. Byte building stays in
    /// <see cref="FaClientConversation"/> and the transport stays in the node.
    /// </para>
    /// <para><b>The exchange model this encodes</b></para>
    /// <para>
    /// A request is NOT answered by its reply. It is answered by a short acknowledgement at the
    /// request's own Flags 1, and the reply arrives afterwards as a NEW exchange - which the asker
    /// must acknowledge in turn. So one ladder step is four messages:
    /// </para>
    /// <code>
    /// us     -> server   request      (we send)
    /// server -> us       short ack    (the server answers our request)
    /// server -> us       reply        (a new exchange)
    /// us     -> server   short ack    (we answer that)
    /// </code>
    /// <para>
    /// Getting this wrong draws subtype <c>0x07</c> with Flags 2 <c>0xFFDE</c> - XENSE, a
    /// sequencing reject - because the exchange count falls behind the peer's.
    /// </para>
    /// <para><b>The ladder</b></para>
    /// <para>
    /// The operations and their order come from <see cref="FaWriteLadder"/>, which is recorded off
    /// a real client. This type does not choose them.
    /// </para>
    /// </remarks>
    public sealed class FaClientWriteSession
    {
        private int _step;
        private bool _connected;

        /// <summary>
        /// Whether the connect letter has left, so it is not sent again while we wait.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="_connected"/>, which means the server ANSWERED. Without this
        /// the two states were one, and a caller that asks what to do on every tick was told to
        /// send the letter every time: a live run put 333 connect letters onto a real machine in
        /// forty-five seconds. Every other step already had its own "sent" flag; the letter was
        /// the one that did not, and no offline test caught it because a test that feeds a reply
        /// never asks twice without one.
        /// </remarks>
        private bool _letterSent;

        private bool _requestSent;
        private bool _requestAcknowledged;
        private bool _replyReceived;
        private bool _dataOwed;
        private bool _closed;
        private string _failure;
        private readonly FaOperation[] _ladder;

        /// <summary>
        /// Whether the peer currently holds the target file OPEN.
        /// </summary>
        /// <remarks>
        /// True from the moment <see cref="FaOperation.OpenFile"/> is answered until
        /// <see cref="FaOperation.CloseFile"/> is. It exists so that a transfer which dies in the
        /// middle can still put the file down - see <see cref="AbandonAfterOpenFile"/>.
        /// <para>
        /// Measured on D100 on 2026-08-17: a push that stalled part way through left CHAT:PLNC
        /// open on the machine. Every later attempt on that name then failed, and so did deleting
        /// it by hand - SINTRAN answered FILE ALREADY OPEN. LIST-OPEN-FILES does not show it
        /// either, because the file belongs to the file server's RT program and not to any
        /// terminal, so the only ways out were restarting the file server or the whole machine.
        /// Walking away from a half-finished write is therefore not a neutral act: it costs the
        /// peer a file until somebody intervenes.
        /// </para>
        /// </remarks>
        private bool _fileOpenOnPeer;

        /// <summary>
        /// Starts a session for a file of a given size in blocks.
        /// </summary>
        /// <param name="blockCount">
        /// How many content blocks the file needs. Each one costs a WriteFile request followed by
        /// the block itself, so this decides the length of the ladder.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="blockCount"/> is negative.
        /// </exception>
        public FaClientWriteSession(int blockCount)
        {
            _failure = string.Empty;
            _ladder = FaWriteLadder.ForBlockCount(blockCount);
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
        /// repeat rebuilds the conversation and rewinds its message counter mid-session - measured
        /// against D100 on 2026-08-10, which sent its confirmation twice and left our OpenFile
        /// stamped with the counter ReserveFileEntry had already spent.
        /// </remarks>
        public bool IsConnected
        {
            get { return _connected; }
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
                    throw new InvalidOperationException("The write ladder is finished.");
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
                    throw new InvalidOperationException("The write ladder is finished.");
                }

                return FaWriteLadder.SequenceForStep(_step);
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
                // Sent once, then we wait. Whether a real client ever RETRANSMITS an unanswered
                // letter, and after how long, is UNKNOWN - no capture shows one going unanswered -
                // so nothing is invented here. A push that never gets its confirmation stalls, and
                // a stall is visible; a flood is what the caller does when this lies.
                return _letterSent ? FaClientAction.Wait : FaClientAction.SendConnectLetter;
            }

            // A WriteFile exchange is followed by the block itself, before the next request.
            // The capture is explicit: request, short ack, reply, our short ack, then the 594-byte
            // content, then the next WriteFile.
            if (_dataOwed) { return FaClientAction.SendData; }

            if (_step < _ladder.Length)
            {
                if (!_requestSent) { return FaClientAction.SendRequest; }

                // The short acknowledgement to our request comes first, then the reply as a new
                // exchange. Waiting for both, in that order, is the whole point of this type.
                if (!_requestAcknowledged) { return FaClientAction.Wait; }
                if (!_replyReceived) { return FaClientAction.Wait; }

                // The reply is an exchange of its own, so it gets acknowledged before moving on.
                return FaClientAction.SendShortAck;
            }

            return FaClientAction.SendRelease;
        }

        /// <summary>
        /// Records that the connect letter was answered by the server's confirmation.
        /// </summary>
        /// <remarks>
        /// The confirmation's echoed word matters to the caller, which must stamp it on everything
        /// it sends afterwards - see <see cref="FaServerConversation.ResponderConversation"/> for
        /// what happens when that is a constant instead. This type does not need the value, only
        /// that the confirmation arrived.
        /// </remarks>
        public void OnConnectionConfirmed()
        {
            _connected = true;
        }

        /// <summary>
        /// Records that the connect letter has left, so we wait for the confirmation instead of
        /// sending it again.
        /// </summary>
        /// <remarks>
        /// The counterpart of <see cref="OnRequestSent"/>, and it exists for the same reason: a
        /// caller drives this by asking what to do next, over and over, and every step must stop
        /// asking for itself once it has been done.
        /// </remarks>
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

            // A WriteFile owes its block IMMEDIATELY - the content follows the request without
            // waiting for the reply.
            //
            // MEASURED from a real client (DOC/captures/ND-TO-ND-WRITE-2026-08-10/fa-ladder.txt).
            // D102 sends three messages back to back on consecutive Flags 1:
            //     0bcd  8300  WriteFile   ( 26 bytes)
            //     0bce  0400  content #1  (594 bytes)
            //     0bcf  8500  content #2  (594 bytes)
            //     0bd0  ShortAck                        <- only NOW does it acknowledge
            // The server's reply to 8300 arrives later still, as its own exchange.
            //
            // We used to owe the block only after acknowledging that reply, so the push sent
            // WriteFile and then waited - while D100 waited for the data. Both sides stopped,
            // D100 dropped the link, and it looked like an unanswered WriteFile. It was our
            // silence, not D100's.
            if (_step < _ladder.Length && _ladder[_step] == FaWriteLadder.BlockOperation)
            {
                _dataOwed = true;
            }
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
                // arriving after we have already moved on - measured against D100 on 2026-08-10,
                // which repeated both its ShortAck (Flags 1 0x0082) and its reply (0x0083) many
                // times over. Treating that as an error killed the push one step after the
                // duplicate-REPLY tolerance had let it through, with
                // "A short acknowledgement arrived before any request was sent."
                //
                // Ignored only once a step has actually completed. Before that, an acknowledgement
                // really has arrived out of nowhere and must still fail - see OnReplyReceived,
                // which keeps its tolerance equally narrow and for the same reason.
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
        /// request. A mismatch means the conversation has slipped, and it is treated as a failure
        /// rather than carried on with - continuing would send the next request against a state
        /// the server does not share.
        /// </para>
        /// <para><b>Except a duplicate, which is normal and must NOT fail the push</b></para>
        /// <para>
        /// A real server RESENDS a reply it has not seen acknowledged. Measured against D100 on
        /// 2026-08-09: the ReserveFileEntry reply arrived again while we were waiting for
        /// OpenFile's, and the push died with "Expected a reply to OpenFile sequence 2 but got
        /// ReserveFileEntry sequence 1" on a transfer that was otherwise healthy. A reply for the
        /// step we just finished is therefore ignored here.
        /// </para>
        /// <para>
        /// This tolerance is deliberately narrow - ONE step back, not "anything we have already
        /// seen". A peer that repeats everything is telling us something of ours is not being
        /// accepted at all, and that has to stay visible rather than being absorbed.
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

            _replyReceived = true;
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
        /// <remarks>
        /// The ladder is the single source for what each step was, so the previous step is read
        /// back from it rather than remembered in a field that could drift out of step with it.
        /// </remarks>
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
        /// Records that we acknowledged the reply, which completes the step.
        /// </summary>
        public void OnShortAckSent()
        {
            if (!_replyReceived)
            {
                Fail("Acknowledged a reply that has not arrived.");
                return;
            }

            // From the moment OpenFile is answered, the file is OPEN ON THE PEER and stays open
            // until CloseFile. Remembering that is what lets an abandoned transfer still close it
            // - see AbandonAfterOpenFile.
            if (_ladder[_step] == FaOperation.OpenFile) { _fileOpenOnPeer = true; }
            if (_ladder[_step] == FaOperation.CloseFile) { _fileOpenOnPeer = false; }

            // The block is NOT owed here. It was already sent, straight after the request - see
            // OnRequestSent, which carries the capture that settles the ordering. Owing it here
            // made the content wait for the reply, which is the opposite of what a real client
            // does.
            _step++;
            _requestSent = false;
            _requestAcknowledged = false;
            _replyReceived = false;
        }

        /// <summary>
        /// Records that a block of file content has been sent.
        /// </summary>
        /// <remarks>
        /// One block per WriteFile exchange. Sending content that no write request announced is a
        /// failure rather than something to let through - the server would have nowhere to put it.
        /// </remarks>
        public void OnDataSent()
        {
            if (!_dataOwed)
            {
                Fail("Content was sent when no write request had announced it.");
                return;
            }

            _dataOwed = false;
        }

        /// <summary>
        /// Records that the close has been sent, finishing the session.
        /// </summary>
        public void OnReleaseSent()
        {
            _closed = true;
        }

        /// <summary>
        /// Whether the peer is holding the target file open on our behalf.
        /// </summary>
        /// <value>
        /// <c>true</c> between the answered <see cref="FaOperation.OpenFile"/> and the answered
        /// <see cref="FaOperation.CloseFile"/>.
        /// </value>
        public bool FileOpenOnPeer
        {
            get { return _fileOpenOnPeer; }
        }

        /// <summary>
        /// Gives up on the content but still walks the epilogue, so the peer closes the file.
        /// </summary>
        /// <returns>
        /// <c>true</c> when there was an open file to close and the session is now aimed at the
        /// epilogue; <c>false</c> when there was nothing to put down and the caller should simply
        /// stop.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Call this when a transfer cannot be finished - the peer went quiet, the caller timed
        /// out, the process is stopping. It abandons the remaining content and points the ladder
        /// at <see cref="FaOperation.SiiiSpecial"/>, <see cref="FaOperation.CloseFile"/> and
        /// <see cref="FaOperation.ReleaseFileEntry"/>, which is exactly what a completed write
        /// sends. The file then ends up short rather than absent, which is a state the machine can
        /// recover from by itself.
        /// </para>
        /// <para>
        /// The alternative is not "nothing happens". Dropping a write after OpenFile leaves
        /// the file open on the peer for good: it cannot be rewritten, and it cannot be deleted -
        /// SINTRAN answers FILE ALREADY OPEN - because the file server's RT program owns it. That
        /// took a file-server restart to clear on D100.
        /// </para>
        /// <para>
        /// The failure text is kept, so a caller that reports why the transfer died still can. The
        /// session is no longer <see cref="FaClientAction.Failed"/> though, because there is work
        /// left to do; it reports <see cref="FaClientAction.Done"/> once the close has gone.
        /// </para>
        /// </remarks>
        public bool AbandonAfterOpenFile()
        {
            if (!_fileOpenOnPeer || _closed) { return false; }

            // Find the CloseFile step and start the epilogue one step earlier, at SiiiSpecial,
            // which is what declares the real length. Searching rather than computing keeps this
            // honest if the ladder ever changes shape.
            int epilogue = -1;
            for (int i = 0; i < _ladder.Length; i++)
            {
                if (_ladder[i] == FaOperation.SiiiSpecial) { epilogue = i; break; }
            }

            if (epilogue < 0) { return false; }

            _step = epilogue;
            _dataOwed = false;
            _requestSent = false;
            _requestAcknowledged = false;
            _replyReceived = false;

            // Clear the failure so NextAction stops returning Failed and lets the epilogue run.
            // What went wrong is already reported by whoever calls this.
            _failure = string.Empty;
            return true;
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
