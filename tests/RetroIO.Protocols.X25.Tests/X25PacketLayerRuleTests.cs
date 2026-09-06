using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.PacketLayer;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Characterisation tests for <see cref="X25Layer"/> (the PacketLayer engine that becomes the ONE
    /// C# X.25 stack), one group per rule R1-R19 of <c>Migration/X25 one-stack plan.md</c> section 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written BEFORE the engine is changed (Phase A2), so most of these tests PIN WHAT THE CODE DOES
    /// TODAY rather than what X.25 says it should do. Where today's behaviour is wrong, the test name
    /// ends in <c>_WHICH_IS_WRONG</c>, the comment quotes the clause, and the assertion is the wrong
    /// behaviour. When Phase B fixes a rule, its test goes red and has to be rewritten on purpose -
    /// the same fence the HDLC merge used, so no behaviour change on the wire can pass unnoticed.
    /// </para>
    /// <para>
    /// The engine is driven single-threaded through a recording <see cref="IX25PacketSink"/>; time is
    /// injected through <see cref="X25Layer.Tick"/>. Spec text: <c>E:\Dev\RCL\Specs\X25</c>,
    /// T-REC-X.25-199610-I (10/96) unless another document is named.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class X25PacketLayerRuleTests
    {
        /// <summary>
        /// Records every packet the layer sends. Nothing is looped back; the test feeds replies by hand.
        /// </summary>
        private sealed class RecordingSink : IX25PacketSink
        {
            public readonly List<byte[]> Sent = new List<byte[]>();

            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                Sent.Add(packet.ToArray());
                return X25SendResult.Sent;
            }

            public byte[] Last => Sent[Sent.Count - 1];

            public int CountOfType(X25PacketType type)
            {
                int n = 0;
                for (int i = 0; i < Sent.Count; i++)
                {
                    if (X25PacketParser.ParsePacket(Sent[i]).PacketType == type)
                    {
                        n++;
                    }
                }

                return n;
            }

            public X25PacketInfo LastInfo => X25PacketParser.ParsePacket(Last);
        }

        private const string LocalAddress = "2340";
        private const string RemoteAddress = "2341";

        private RecordingSink _sink = null!;
        private X25Layer _layer = null!;
        private X25Config _config = null!;

        // Far-future tick that expires every timer the config can arm.
        private static long FarFuture => Environment.TickCount64 + 1_000_000;

        [SetUp]
        public void SetUp()
        {
            _sink = new RecordingSink();
            _config = new X25Config { LocalAddress = LocalAddress, LciBase = 1, LciCount = 16, IsDte = true };
            _layer = new X25Layer(_sink, _config);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Places a call and feeds a Call Connected, leaving the circuit in data transfer.</summary>
        private ushort ConnectOutgoing()
        {
            ushort lci = _layer.PlaceCall(RemoteAddress);
            Assert.That(lci, Is.Not.EqualTo((ushort)0), "no LCI allocated");
            _layer.ProcessPacket(X25PacketBuilder.CreateCallAccepted(lci));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.DataTransfer));
            _sink.Sent.Clear();
            return lci;
        }

        /// <summary>Feeds an Incoming Call on the given LCI and accepts it.</summary>
        private void ConnectIncoming(ushort lci)
        {
            _layer.ProcessPacket(X25PacketBuilder.CreateCallRequest(lci, LocalAddress, RemoteAddress));
            _layer.AcceptCall(lci);
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.DataTransfer));
            _sink.Sent.Clear();
        }

        private static byte[] Data(ushort lci, byte ps, byte pr, int length = 4, bool m = false)
        {
            byte[] payload = new byte[length];
            for (int i = 0; i < length; i++)
            {
                payload[i] = (byte)i;
            }

            return X25PacketBuilder.CreateDataPacket(lci, ps, pr, payload, m);
        }

        // ================================================================== R1  P(S) sequencing
        //
        // X.25 4.4.1.3 "Flow control principles": "... a duplicate or a gap in the P(S) numbering
        // ... is considered by the DCE as a local procedure error" - the circuit is reset, cause
        // 00000101 (Table 5-8), diagnostic 1 "Invalid P(S)" (Annex E Table E.1).

        [Test]
        public void R1_InSequenceData_IsDeliveredAndAcknowledgedWithTheNextPR()
        {
            ushort lci = ConnectOutgoing();
            int delivered = 0;
            _layer.DataReceived += (l, c, d, n, q, m, db) => delivered++;

            _layer.ProcessPacket(Data(lci, ps: 0, pr: 0));
            Assert.That(_sink.LastInfo.PacketType, Is.EqualTo(X25PacketType.RR));
            Assert.That(_sink.LastInfo.PR, Is.EqualTo(1));

            _layer.ProcessPacket(Data(lci, ps: 1, pr: 0));
            Assert.That(_sink.LastInfo.PR, Is.EqualTo(2));
            Assert.That(delivered, Is.EqualTo(2));
        }

        [Test]
        public void R1_DataWithAGapInPS_IsNotDeliveredAndTheCircuitIsReset()
        {
            ushort lci = ConnectOutgoing();
            int delivered = 0;
            _layer.DataReceived += (l, c, d, n, q, m, db) => delivered++;

            // Expected P(S) is 0; the peer sends 3.
            _layer.ProcessPacket(Data(lci, ps: 3, pr: 0));

            // FIXED 2026-09-06 (Phase B3), test rewritten deliberately. It used to assert that the
            // packet was delivered and acknowledged with P(R)=4 and no reset - the engine took
            // whatever P(S) arrived. X.25 4.4.1.3: a gap is a local procedure error; the circuit is
            // reset with diagnostic 1 (Annex E). As a DTE the cause is 0 (Table 5-8 note a).
            Assert.That(delivered, Is.EqualTo(0), "a packet out of sequence must not reach the application");
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(1));
            X25PacketInfo reset = _sink.LastInfo;
            Assert.That(reset.ResetCause, Is.EqualTo(X25Cause.DteOriginated));
            Assert.That(reset.DiagnosticCode, Is.EqualTo(X25Diagnostic.InvalidPS));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.ResetRequest));
            Assert.That(_layer.ProcedureErrors, Is.EqualTo(1));
        }

        [Test]
        public void R1_DuplicatePS_IsNotDeliveredAgainAndTheCircuitIsReset()
        {
            ushort lci = ConnectOutgoing();
            int delivered = 0;
            _layer.DataReceived += (l, c, d, n, q, m, db) => delivered++;

            _layer.ProcessPacket(Data(lci, ps: 0, pr: 0));
            _layer.ProcessPacket(Data(lci, ps: 0, pr: 0));   // duplicate

            // FIXED 2026-09-06 (Phase B3); used to assert two deliveries and no reset.
            Assert.That(delivered, Is.EqualTo(1));
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(1));
            Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(X25Diagnostic.InvalidPS));
        }

        [Test]
        public void R1_ResetCompletes_AndTheApplicationIsToldOnce()
        {
            ushort lci = ConnectOutgoing();
            int resets = 0;
            byte seenDiag = 0xFF;
            _layer.Reset += (l, c, cause, diag) => { resets++; seenDiag = diag; };

            _layer.ProcessPacket(Data(lci, ps: 3, pr: 0));
            Assert.That(resets, Is.EqualTo(0), "not told until the reset completes");

            _layer.ProcessPacket(X25PacketBuilder.CreateResetConfirmation(lci));

            Assert.That(resets, Is.EqualTo(1));
            Assert.That(seenDiag, Is.EqualTo(X25Diagnostic.InvalidPS));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.DataTransfer));
            Assert.That(_layer.Circuits[lci].RecvSeq, Is.EqualTo(0), "the window is zeroed in both directions (4.4.3)");
            Assert.That(_layer.Circuits[lci].SendSeq, Is.EqualTo(0));

            // And the circuit carries data again, from P(S)=0.
            _layer.ProcessPacket(Data(lci, ps: 0, pr: 0));
            Assert.That(_sink.LastInfo.PacketType, Is.EqualTo(X25PacketType.RR));
            Assert.That(_sink.LastInfo.PR, Is.EqualTo(1));
        }

        // ================================================================== R2  P(R) validation
        //
        // X.25 4.4.1.3: "the receipt of a P(R) ... that is not in the range [last P(R) received ..
        // P(S) of the next packet to send] ... is considered a local procedure error" - reset,
        // diagnostic 2 "Invalid P(R)".

        [Test]
        public void R2_ValidPR_OpensTheSendWindow()
        {
            ushort lci = ConnectOutgoing();   // default window 2

            Assert.That(_layer.SendData(lci, new byte[] { 1 }), Is.True);
            Assert.That(_layer.SendData(lci, new byte[] { 2 }), Is.True);
            Assert.That(_layer.SendData(lci, new byte[] { 3 }), Is.False, "window of 2 is full");

            _layer.ProcessPacket(X25PacketBuilder.CreateRR(lci, pr: 2));
            Assert.That(_layer.SendData(lci, new byte[] { 3 }), Is.True, "RR P(R)=2 acknowledged both");
        }

        [Test]
        public void R2_RRWithPROutsideTheWindow_ResetsTheCircuitWithDiagnostic2()
        {
            ushort lci = ConnectOutgoing();
            Assert.That(_layer.SendData(lci, new byte[] { 1 }), Is.True);   // P(S) is now 1

            // Only P(R) 0 or 1 is valid here. The peer sends 5.
            _layer.ProcessPacket(X25PacketBuilder.CreateRR(lci, pr: 5));

            // FIXED 2026-09-06 (Phase B3); used to assert AckedSeq == 5 and a circuit that could
            // never send again because (SendSeq - AckedSeq) & 7 looked like a full window.
            Assert.That(_layer.Circuits[lci].AckedSeq, Is.EqualTo(0), "an invalid P(R) must not be applied");
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(1));
            Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(X25Diagnostic.InvalidPR));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.ResetRequest));
        }

        [Test]
        public void R2_PROnTheWindowEdges_IsValid()
        {
            ushort lci = ConnectOutgoing();
            Assert.That(_layer.SendData(lci, new byte[] { 1 }), Is.True);
            Assert.That(_layer.SendData(lci, new byte[] { 2 }), Is.True);   // P(S) = 2, acked 0

            // P(R) equal to the last acknowledged value acknowledges nothing new and is valid.
            _layer.ProcessPacket(X25PacketBuilder.CreateRR(lci, pr: 0));
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(0));

            // P(R) equal to the next P(S) to send acknowledges everything and is valid.
            _layer.ProcessPacket(X25PacketBuilder.CreateRR(lci, pr: 2));
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(0));
            Assert.That(_layer.Circuits[lci].AckedSeq, Is.EqualTo(2));

            // One past that acknowledges a packet never sent.
            _layer.ProcessPacket(X25PacketBuilder.CreateRR(lci, pr: 3));
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(1));
        }

        [Test]
        public void R2_PRArithmeticWrapsAtModulo8()
        {
            ushort lci = ConnectOutgoing();   // window 2
            for (int round = 0; round < 6; round++)
            {
                Assert.That(_layer.SendData(lci, new byte[] { (byte)round }), Is.True);
                byte acked = (byte)((round + 1) & 0x07);
                _layer.ProcessPacket(X25PacketBuilder.CreateRR(lci, pr: acked));
            }

            // Sent P(S) 0..5, all acknowledged; SendSeq is 6, AckedSeq is 6.
            Assert.That(_layer.SendData(lci, new byte[] { 6 }), Is.True);
            Assert.That(_layer.SendData(lci, new byte[] { 7 }), Is.True);   // SendSeq wraps to 0
            Assert.That(_layer.Circuits[lci].SendSeq, Is.EqualTo(0));

            _layer.ProcessPacket(X25PacketBuilder.CreateRR(lci, pr: 0));   // acknowledges 6 and 7 across the wrap
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(0));
            Assert.That(_layer.Circuits[lci].AckedSeq, Is.EqualTo(0));
            Assert.That(_layer.SendData(lci, new byte[] { 8 }), Is.True, "window open again after the wrap");
        }

        [Test]
        public void R2_PiggybackedPROnData_AcknowledgesButDoesNotClearPeerBusy()
        {
            // X.25 4.4.1.6: the busy condition set by RNR is cleared by an RR (or a reset) - NOT by
            // the P(R) carried in a data packet. This is the rule that was broken in
            // ProtocolRuntime until 2026-09-05 and destroyed packets under load; the PacketLayer
            // engine has it right, and this pins it.
            ushort lci = ConnectOutgoing();
            _layer.SendData(lci, new byte[] { 1 });
            _layer.ProcessPacket(X25PacketBuilder.CreateRNR(lci, pr: 1));
            Assert.That(_layer.Circuits[lci].PeerBusy, Is.True);

            _layer.ProcessPacket(Data(lci, ps: 0, pr: 1));

            Assert.That(_layer.Circuits[lci].PeerBusy, Is.True, "a data packet's P(R) must not clear busy");
            Assert.That(_layer.SendData(lci, new byte[] { 2 }), Is.False);

            _layer.ProcessPacket(X25PacketBuilder.CreateRR(lci, pr: 1));
            Assert.That(_layer.Circuits[lci].PeerBusy, Is.False, "only an RR clears it");
            Assert.That(_layer.SendData(lci, new byte[] { 2 }), Is.True);
        }

        // ================================================================== R3  state gating
        //
        // X.25 Annex B (state tables): RR/RNR/REJ are only meaningful in the flow-control states
        // d1/d2/d3 of data transfer (p4). In p2 (DTE waiting) they are a procedure error.

        [Test]
        public void R3_RNRInCallRequestState_IsDroppedAndCounted()
        {
            ushort lci = _layer.PlaceCall(RemoteAddress);   // state p2, waiting for Call Connected
            _sink.Sent.Clear();

            _layer.ProcessPacket(X25PacketBuilder.CreateRNR(lci, pr: 0));

            // FIXED 2026-09-06 (Phase B3); used to assert PeerBusy == true on a circuit that was not
            // even connected. The Table D-1 reaction (Diagnostic / Clear, "packet type invalid for
            // state p2") is still to come with R17; until then the packet is dropped and counted.
            Assert.That(_layer.Circuits[lci].PeerBusy, Is.False);
            Assert.That(_layer.UnexpectedPackets, Is.EqualTo(1));
            Assert.That(_sink.Sent.Count, Is.EqualTo(0), "no reaction yet - that is R17");
        }

        [Test]
        public void R3_REJInDataTransfer_ResetsWithRejectNotSubscribed()
        {
            // Decided 2026-09-06 (audit D9, option A): this engine has no packet retransmission
            // facility, so a received REJ is a local procedure error, diagnostic 21 (Annex E,
            // "Reject not subscribed to"), and the engine never sends REJ itself.
            ushort lci = ConnectOutgoing();

            _layer.ProcessPacket(X25PacketBuilder.CreateREJ(lci, pr: 0));

            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(1));
            Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(X25Diagnostic.RejectNotSubscribed));
        }

        // ================================================================== R4  reset collision
        //
        // X.25 4.4.3.3 "Reset collision": "... the DCE will consider that the reset is completed.
        // The DCE will not expect a DTE reset confirmation packet and will not transfer a DCE
        // reset confirmation packet. This places the logical channel in the flow control ready
        // state (d1)."

        [Test]
        public void R4_ResetCollision_CompletesTheResetAtOnceWithNothingSent()
        {
            ushort lci = ConnectOutgoing();
            int resets = 0;
            _layer.Reset += (l, c, cause, diag) => resets++;

            _layer.ResetCall(lci, cause: 0, diagnostic: 0);
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.ResetRequest));
            _sink.Sent.Clear();

            _layer.ProcessPacket(X25PacketBuilder.CreateResetRequest(lci, resetCause: 0x05, diagnosticCode: 0x01));

            // FIXED 2026-09-06 (Phase B4); used to assert the colliding Reset was dropped and the
            // circuit stayed in ResetRequest until T22 (180 s). X.25 4.4.3.3: both sides consider
            // the reset complete, no confirmation crosses, the channel is in d1.
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.DataTransfer));
            Assert.That(resets, Is.EqualTo(1), "told once that the reset completed");
            Assert.That(_sink.Sent.Count, Is.EqualTo(0), "no Reset Confirmation on a collision");
            Assert.That(_layer.SendData(lci, new byte[] { 1 }), Is.True, "and the circuit carries data again");
            Assert.That(_sink.LastInfo.PS, Is.EqualTo(0));
        }

        // ================================================================== R5  clear collision
        //
        // X.25 4.1.9 "Clear collision": "... the DCE will consider that the clearing is completed.
        // The DCE will not expect a DTE clear confirmation packet and will not transfer a DCE
        // clear confirmation packet."

        [Test]
        public void R5_ClearCollision_FreesTheChannelWithNoConfirmationAndOneClearedEvent()
        {
            ushort lci = ConnectOutgoing();
            int cleared = 0;
            _layer.Cleared += (l, c, cause, diag) => cleared++;

            _layer.ClearCall(lci);
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.ClearRequest));
            _sink.Sent.Clear();

            _layer.ProcessPacket(X25PacketBuilder.CreateClearRequest(lci, clearingCause: 0, diagnosticCode: 0));

            // FIXED 2026-09-06 (Phase B4); used to assert a Clear Confirmation the peer did not
            // expect (X.25 4.1.9) and a Cleared event that looked like the peer's doing.
            Assert.That(_sink.CountOfType(X25PacketType.ClearConfirmation), Is.EqualTo(0));
            Assert.That(cleared, Is.EqualTo(1), "told once that the channel is free");
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.Ready));
        }

        [Test]
        public void R5_ClearConfirmation_CompletesOurClearAndTellsTheApplication()
        {
            ushort lci = ConnectOutgoing();
            int cleared = 0;
            _layer.Cleared += (l, c, cause, diag) => cleared++;

            _layer.ClearCall(lci, cause: 0, diagnostic: 0x0A);
            Assert.That(cleared, Is.EqualTo(0), "not until the peer confirms");

            _layer.ProcessPacket(X25PacketBuilder.CreateClearConfirmation(lci));

            // Cleared means "the channel is now free" whichever side started the clearing. Before
            // Phase B4 an application that cleared a call was never told when that completed.
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.Ready));
        }

        // ================================================================== R6  restart
        //
        // X.25 4.1.2 "Restart collision": no confirmation crosses. 4.1.1: a restart clears every
        // virtual call and resets every permanent virtual circuit - the application must learn
        // about each one. Table 11: T20 (180 s) retransmits an unanswered Restart Request.

        [Test]
        public void R6_IncomingRestart_ConfirmsFreesEveryCallAndTellsTheApplicationPerCall()
        {
            ushort a = ConnectOutgoing();
            ushort b = ConnectOutgoing();
            int cleared = 0;
            int restarts = 0;
            _layer.Cleared += (l, c, cause, diag) => cleared++;
            _layer.RestartOccurred += (l, cause, diag) => restarts++;

            _layer.ProcessPacket(X25PacketBuilder.CreateRestartRequest(restartCause: 0x07, diagnosticCode: 0));

            Assert.That(_sink.LastInfo.PacketType, Is.EqualTo(X25PacketType.RestartConfirmation));
            Assert.That(_layer.Circuits[a].State, Is.EqualTo(X25VcState.Ready));
            Assert.That(_layer.Circuits[b].State, Is.EqualTo(X25VcState.Ready));

            // FIXED 2026-09-06 (Phase B5); used to assert zero Cleared events. X.25 4.1: a restart
            // clears every virtual call - each holder is told, then the restart itself is reported.
            Assert.That(cleared, Is.EqualTo(2));
            Assert.That(restarts, Is.EqualTo(1));
        }

        [Test]
        public void R6_OurRestart_TellsTheApplicationPerCallToo()
        {
            ushort a = ConnectOutgoing();
            int cleared = 0;
            _layer.Cleared += (l, c, cause, diag) => cleared++;

            _layer.Restart();

            Assert.That(_layer.Circuits[a].State, Is.EqualTo(X25VcState.Ready));
            Assert.That(cleared, Is.EqualTo(1));
        }

        [Test]
        public void R6_RestartCollision_CompletesBothRestartsWithNoConfirmation()
        {
            int restarts = 0;
            _layer.RestartOccurred += (l, cause, diag) => restarts++;
            _layer.Restart();
            Assert.That(_layer.RestartOutstanding, Is.True);
            _sink.Sent.Clear();

            _layer.ProcessPacket(X25PacketBuilder.CreateRestartRequest(restartCause: 0, diagnosticCode: 0));

            // FIXED 2026-09-06 (Phase B4); used to assert a Restart Confirmation was sent. X.25
            // 4.1.2: on collision no confirmation crosses and both restarts are complete.
            Assert.That(_sink.CountOfType(X25PacketType.RestartConfirmation), Is.EqualTo(0));
            Assert.That(_layer.RestartOutstanding, Is.False);
            Assert.That(restarts, Is.EqualTo(1));
        }

        [Test]
        public void R6_RestartConfirmation_ClearsTheOutstandingRestart()
        {
            _layer.Restart();
            Assert.That(_layer.RestartOutstanding, Is.True);

            _layer.ProcessPacket(X25PacketBuilder.CreateRestartConfirmation());

            Assert.That(_layer.RestartOutstanding, Is.False);

            // An unsolicited one is dropped and counted.
            _layer.ProcessPacket(X25PacketBuilder.CreateRestartConfirmation());
            Assert.That(_layer.UnexpectedPackets, Is.EqualTo(1));
        }

        [Test]
        public void R6_UnansweredRestartRequest_IsRetransmittedOnT20ThenGivenUp()
        {
            int timedOut = 0;
            _layer.RestartTimedOut += l => timedOut++;
            _layer.Restart();
            Assert.That(_sink.CountOfType(X25PacketType.RestartRequest), Is.EqualTo(1));

            // FIXED 2026-09-06 (Phase B5); there used to be no T20 at all. X.25 Table D.2: T20 =
            // 180 s, "to retransmit the restart request"; note 1: after the retries, higher layers
            // decide. Five retransmissions, then RestartTimedOut.
            long now = Environment.TickCount64;
            for (int i = 1; i <= 5; i++)
            {
                _layer.Tick(now + i * 180_001L);
                Assert.That(_sink.CountOfType(X25PacketType.RestartRequest), Is.EqualTo(1 + i), "retransmission " + i);
                Assert.That(_layer.RestartOutstanding, Is.True);
            }

            _layer.Tick(now + 6 * 180_001L);
            Assert.That(_sink.CountOfType(X25PacketType.RestartRequest), Is.EqualTo(6), "no seventh attempt");
            Assert.That(_layer.RestartOutstanding, Is.False);
            Assert.That(timedOut, Is.EqualTo(1));
            Assert.That(_layer.RestartTimeouts, Is.EqualTo(1));
        }

        [Test]
        public void R6_ACallPlacedWhileOurRestartIsOutstanding_IsHeldUntilTheConfirmation()
        {
            // X.25 4.1.1: no call set-up packet may be sent in state r2. X25Client restarts in its
            // constructor and applications call straight away, so the call is held, not refused.
            _layer.Restart();
            _sink.Sent.Clear();

            ushort lci = _layer.PlaceCall(RemoteAddress);
            Assert.That(lci, Is.Not.EqualTo((ushort)0));
            Assert.That(_layer.Circuits[lci].CallRequestHeld, Is.True);
            Assert.That(_sink.CountOfType(X25PacketType.CallRequest), Is.EqualTo(0), "nothing on the wire in r2");

            _layer.ProcessPacket(X25PacketBuilder.CreateRestartConfirmation());

            Assert.That(_sink.CountOfType(X25PacketType.CallRequest), Is.EqualTo(1), "released by the confirmation");
            Assert.That(_layer.Circuits[lci].CallRequestHeld, Is.False);
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.CallRequest));
            Assert.That(_layer.Circuits[lci].TimerExpiryMs, Is.Not.EqualTo(0), "T21 runs from the send");
        }

        [Test]
        public void R6_AHeldCall_IsReportedClearedWhenTheRestartGivesUp()
        {
            _layer.Restart();
            ushort lci = _layer.PlaceCall(RemoteAddress);
            int cleared = 0;
            byte seenDiag = 0;
            _layer.Cleared += (l, c, cause, diag) => { cleared++; seenDiag = diag; };

            long now = Environment.TickCount64;
            for (int i = 1; i <= 6; i++)
            {
                _layer.Tick(now + i * 180_001L);
            }

            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(seenDiag, Is.EqualTo(X25Diagnostic.TimeExpiredRestart));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.Ready));
        }

        // ================================================================== R7  timers
        //
        // X.25 Table 11/X.25 (DTE time-limits): T21 = 200 s on Call Request -> the DTE clears the
        // call (Clear Request, diagnostic 49); T22 = 180 s on Reset Request -> retransmit, then
        // clear; T23 = 180 s on Clear Request -> retransmit the Clear Request.

        [Test]
        public void R7_T21Expiry_SendsAClearRequestAndTellsTheApplicationOnce()
        {
            ushort lci = _layer.PlaceCall(RemoteAddress);
            int cleared = 0;
            byte seenDiag = 0;
            _layer.Cleared += (l, c, cause, diag) => { cleared++; seenDiag = diag; };
            _sink.Sent.Clear();

            _layer.Tick(FarFuture);

            // FIXED 2026-09-06 (Phase B5); used to assert Ready with nothing on the wire, leaving
            // the peer holding the channel in p3. X.25 Table D.2, T21: "To transmit a clear request".
            // The application hears now (diagnostic 49, time expired for the call); the channel is
            // busy clearing until the peer confirms.
            Assert.That(_sink.CountOfType(X25PacketType.ClearRequest), Is.EqualTo(1));
            Assert.That(_sink.LastInfo.ClearingCause, Is.EqualTo(X25Cause.DteOriginated));
            Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(X25Diagnostic.TimeExpiredCall));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.ClearRequest));
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(seenDiag, Is.EqualTo(X25Diagnostic.TimeExpiredCall));
            Assert.That(_layer.CallTimeouts, Is.EqualTo(1));

            // The confirmation frees the channel without a second Cleared.
            _layer.ProcessPacket(X25PacketBuilder.CreateClearConfirmation(lci));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.Ready));
            Assert.That(cleared, Is.EqualTo(1), "told exactly once, however many steps the ending took");
        }

        [Test]
        public void R7_T23Expiry_RetransmitsTheClearRequestThenGivesUp()
        {
            ushort lci = ConnectOutgoing();
            int cleared = 0;
            _layer.Cleared += (l, c, cause, diag) => cleared++;
            _layer.ClearCall(lci, cause: 0, diagnostic: 0x0A);
            Assert.That(_sink.CountOfType(X25PacketType.ClearRequest), Is.EqualTo(1));

            // FIXED 2026-09-06 (Phase B5); used to give up after one attempt. Table D.2, T23: "To
            // retransmit the clear request"; note 2: after the retries the channel is out of order.
            long now = Environment.TickCount64;
            for (int i = 1; i <= 5; i++)
            {
                _layer.Tick(now + i * 180_001L);
                Assert.That(_sink.CountOfType(X25PacketType.ClearRequest), Is.EqualTo(1 + i), "retransmission " + i);
                Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(0x0A), "the same clear is repeated");
                Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.ClearRequest));
            }

            _layer.Tick(now + 6 * 180_001L);
            Assert.That(_sink.CountOfType(X25PacketType.ClearRequest), Is.EqualTo(6));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.Ready));
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(_layer.ClearTimeouts, Is.EqualTo(1));
        }

        [Test]
        public void R7_T22Expiry_RetransmitsTheResetRequestThenClearsTheCall()
        {
            ushort lci = ConnectOutgoing();
            int cleared = 0;
            byte seenDiag = 0;
            _layer.Cleared += (l, c, cause, diag) => { cleared++; seenDiag = diag; };
            _layer.ResetCall(lci);
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(1));

            // FIXED 2026-09-06 (Phase B5); used to flip back to DataTransfer as if the peer had
            // confirmed. Table D.2, T22: "For virtual calls, to retransmit the reset request or to
            // transmit a clear request" - retransmit five times, then clear (diagnostic 51).
            long now = Environment.TickCount64;
            for (int i = 1; i <= 5; i++)
            {
                _layer.Tick(now + i * 180_001L);
                Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(1 + i), "retransmission " + i);
                Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.ResetRequest));
            }

            _layer.Tick(now + 6 * 180_001L);
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(6));
            Assert.That(_sink.CountOfType(X25PacketType.ClearRequest), Is.EqualTo(1));
            Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(X25Diagnostic.TimeExpiredReset));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.ClearRequest));
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(seenDiag, Is.EqualTo(X25Diagnostic.TimeExpiredReset));
            Assert.That(_layer.ResetTimeouts, Is.EqualTo(1));
        }

        [Test]
        public void R7_AResetConfirmationDuringRetransmission_CompletesTheReset()
        {
            ushort lci = ConnectOutgoing();
            int resets = 0;
            _layer.Reset += (l, c, cause, diag) => resets++;
            _layer.ResetCall(lci);

            long now = Environment.TickCount64;
            _layer.Tick(now + 180_001L);   // one retransmission
            _layer.ProcessPacket(X25PacketBuilder.CreateResetConfirmation(lci));

            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.DataTransfer));
            Assert.That(resets, Is.EqualTo(1));

            _layer.Tick(now + 2 * 180_001L);   // the timer was cancelled
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(2));
        }

        // ================================================================== R8  call collision / LCI choice
        //
        // X.25 4.1.6 "Call collision": "The DCE will proceed with the call request and cancel the
        // incoming call." So at the DTE its own call stands and the incoming call is dropped.
        // Annex A note 6: "the DTE search algorithm is suggested to start with the highest
        // numbered" logical channel, the DCE with the lowest, precisely to keep collisions rare.

        [Test]
        public void R8_CallCollisionAtTheDte_OurCallStandsAndTheIncomingCallIsDropped()
        {
            ushort lci = _layer.PlaceCall(RemoteAddress);
            int incoming = 0;
            int cleared = 0;
            _layer.IncomingCall += (l, c, called, calling, cud) => incoming++;
            _layer.Cleared += (l, c, cause, diag) => cleared++;

            _layer.ProcessPacket(X25PacketBuilder.CreateCallRequest(lci, LocalAddress, "9999"));

            // FIXED 2026-09-06 (Phase B4); used to assert the incoming call overwrote ours. X.25
            // 4.1.6: "The DCE will proceed with the call request and cancel the incoming call" - at
            // the DTE, its own call is the one that proceeds.
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.CallRequest));
            Assert.That(incoming, Is.EqualTo(0));
            Assert.That(cleared, Is.EqualTo(0));

            _layer.ProcessPacket(X25PacketBuilder.CreateCallAccepted(lci));
            Assert.That(_layer.Circuits[lci].State, Is.EqualTo(X25VcState.DataTransfer));
        }

        [Test]
        public void R8_CallCollisionAtTheDce_ThePeersCallWinsAndOurApplicationIsTold()
        {
            RecordingSink sink = new RecordingSink();
            X25Layer dce = new X25Layer(sink, new X25Config { LocalAddress = LocalAddress, LciBase = 1, LciCount = 16, IsDte = false });
            ushort lci = dce.PlaceCall(RemoteAddress);
            int incoming = 0;
            int cleared = 0;
            dce.IncomingCall += (l, c, called, calling, cud) => incoming++;
            dce.Cleared += (l, c, cause, diag) => cleared++;

            dce.ProcessPacket(X25PacketBuilder.CreateCallRequest(lci, LocalAddress, "9999"));

            // X.25 4.1.6 from the DCE's side: the DTE's call request proceeds, the DCE's incoming
            // call (our outgoing one) is cancelled - and the application that placed it is told.
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(incoming, Is.EqualTo(1));
            Assert.That(dce.Circuits[lci].State, Is.EqualTo(X25VcState.CallIncoming));
        }

        [Test]
        public void R8_DteAllocatesFromTheTopOfTheRange_DceFromTheBottom()
        {
            // X.25 Annex A note 6: the DTE search starts with the highest numbered channel, the DCE
            // with the lowest, so that a DTE and a DCE calling at once do not collide. FIXED
            // 2026-09-06 (Phase B4); used to assert 1 then 2 for a DTE.
            ushort first = _layer.PlaceCall(RemoteAddress);
            ushort second = _layer.PlaceCall(RemoteAddress);
            Assert.That(first, Is.EqualTo((ushort)16));
            Assert.That(second, Is.EqualTo((ushort)15));

            RecordingSink sink = new RecordingSink();
            X25Layer dce = new X25Layer(sink, new X25Config { LocalAddress = LocalAddress, LciBase = 1, LciCount = 16, IsDte = false });
            Assert.That(dce.PlaceCall(RemoteAddress), Is.EqualTo((ushort)1));
            Assert.That(dce.PlaceCall(RemoteAddress), Is.EqualTo((ushort)2));
        }

        [Test]
        public void R8_AClearedChannelIsReused()
        {
            ushort lci = ConnectOutgoing();
            _layer.ProcessPacket(X25PacketBuilder.CreateClearRequest(lci, 0, 0));
            Assert.That(_layer.PlaceCall(RemoteAddress), Is.EqualTo(lci), "the highest channel is free again and is picked again");
        }

        // ================================================================== R9  negotiated sizes
        //
        // X.25 7.3.1.2: the window size facility in Call Connected is the value the call runs with.
        // RFC 1613 6.1: an XOT Call Request MUST carry the packet size and window size facilities.

        [Test]
        public void R9_PlaceCall_CarriesThePacketAndWindowRequest()
        {
            _layer.PlaceCall(RemoteAddress, LocalAddress);
            X25PacketInfo sent = _sink.LastInfo;

            Assert.That(sent.PacketType, Is.EqualTo(X25PacketType.CallRequest));
            // FIXED 2026-09-06 (Phase B7); the facility length used to be zero. RFC 1613 6.1 makes
            // the request a MUST over XOT; X.25 7.2.2.1 is how the peer learns what we want. The
            // configured defaults are 128 and 2: 42 07 07 43 02 02.
            Assert.That(BitConverter.ToString(sent.Facilities).Replace('-', ' '), Is.EqualTo("42 07 07 43 02 02"));
        }

        [Test]
        public void R9_CallConnectedWithASmallerWindow_IsApplied()
        {
            RecordingSink sink = new RecordingSink();
            X25Layer layer = new X25Layer(sink, new X25Config { LocalAddress = LocalAddress, LciBase = 1, LciCount = 16, DefaultWindowSize = 7 });
            ushort lci = layer.PlaceCall(RemoteAddress);   // asks for window 7

            // Call Connected per X.25 5.2.3.1.2: address block octet, facility length, window 3/3.
            // Table 6-2: with W(requested) >= 2 the indicated value may be anything from 2 up to the
            // request - 3 is valid. (The first draft of this test granted 1 against a request of 2,
            // which the table forbids, and the engine rightly cleared it.)
            byte[] callConnected = { 0x10, (byte)lci, 0x0F, 0x00, 0x03, 0x43, 0x03, 0x03 };
            layer.ProcessPacket(callConnected);
            sink.Sent.Clear();

            Assert.That(layer.Circuits[lci].State, Is.EqualTo(X25VcState.DataTransfer));

            // FIXED 2026-09-06 (Phase B7); the body used to be ignored and the request stood, so a
            // caller granted less than it asked sent into a window the peer had not authorised.
            Assert.That(layer.Circuits[lci].WindowSizeOut, Is.EqualTo(3));
            Assert.That(layer.Circuits[lci].WindowSizeIn, Is.EqualTo(3));
            Assert.That(layer.SendData(lci, new byte[] { 1 }), Is.True);
            Assert.That(layer.SendData(lci, new byte[] { 2 }), Is.True);
            Assert.That(layer.SendData(lci, new byte[] { 3 }), Is.True);
            Assert.That(layer.SendData(lci, new byte[] { 4 }), Is.False, "window of 3 is full");
        }

        [Test]
        public void R9_CallConnectedWithoutFacilities_MeansTheDefaults()
        {
            RecordingSink sink = new RecordingSink();
            X25Layer layer = new X25Layer(sink, new X25Config { LocalAddress = LocalAddress, LciBase = 1, LciCount = 16, DefaultWindowSize = 7, DefaultPacketSize = 1024 });
            ushort lci = layer.PlaceCall(RemoteAddress);   // asks for 1024 / 7

            layer.ProcessPacket(X25PacketBuilder.CreateCallAccepted(lci));   // basic format, three octets

            // 7.2.2.1: a value not indicated in the Call Connected is the default, whatever we asked.
            Assert.That(layer.Circuits[lci].WindowSizeOut, Is.EqualTo(2));
            Assert.That(layer.Circuits[lci].PacketSizeOut, Is.EqualTo(128));
        }

        [Test]
        public void R9_CallConnectedOutsideTheNegotiationRange_IsCleared()
        {
            ushort lci = _layer.PlaceCall(RemoteAddress);   // asks for window 2
            int cleared = 0;
            byte seenDiag = 0;
            _layer.Cleared += (l, c, cause, diag) => { cleared++; seenDiag = diag; };
            _sink.Sent.Clear();

            // Window 5 is neither between the default (2) and the request (2) nor equal to either.
            _layer.ProcessPacket(new byte[] { 0x10, (byte)lci, 0x0F, 0x00, 0x03, 0x43, 0x05, 0x05 });

            Assert.That(_sink.CountOfType(X25PacketType.ClearRequest), Is.EqualTo(1));
            Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(X25Diagnostic.FacilityParameterNotAllowed));
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(seenDiag, Is.EqualTo(X25Diagnostic.FacilityParameterNotAllowed));
        }

        [Test]
        public void R9_AcceptCall_AnswersWithTheValuesTheCallerAsked()
        {
            // Caller asks for 256 / 3 in both directions.
            byte[] fac = X25Facilities.ForRequest(3, 256).Encode();
            _layer.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, LocalAddress, RemoteAddress, fac));
            Assert.That(_layer.Circuits[5].PacketSizeOut, Is.EqualTo(256));
            Assert.That(_layer.Circuits[5].WindowSizeOut, Is.EqualTo(3));

            _layer.AcceptCall(5);

            X25PacketInfo accepted = _sink.LastInfo;
            Assert.That(accepted.PacketType, Is.EqualTo(X25PacketType.CallAccepted));
            Assert.That(BitConverter.ToString(accepted.Facilities).Replace('-', ' '), Is.EqualTo("42 08 08 43 03 03"));
            Assert.That(_sink.Last[3], Is.EqualTo(0x00), "5.2.3.1.2: the address octet precedes the facility length");
        }

        // ================================================================== R10  Call Request validation
        //
        // X.25 5.2.2.3-5.2.2.5: the facility length must fit the body; call user data is what
        // follows the facilities and is delivered to the called DTE.

        [Test]
        public void R10_CallRequestWhoseFacilityLengthExceedsTheBody_IsClearedAndNeverOffered()
        {
            int incoming = 0;
            _layer.IncomingCall += (l, c, called, calling, cud) => incoming++;

            // No addresses, facility length 0x32 = 50, three octets follow.
            _layer.ProcessPacket(new byte[] { 0x10, 0x05, 0x0B, 0x00, 0x32, 0x42, 0x07, 0x07 });

            // FIXED 2026-09-06 (Phase B7); it used to be offered as a normal call with the leftover
            // octets as user data. Annex E 69 "Invalid facility length".
            Assert.That(incoming, Is.EqualTo(0));
            Assert.That(_sink.CountOfType(X25PacketType.ClearRequest), Is.EqualTo(1));
            Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(X25Diagnostic.InvalidFacilityLength));
            Assert.That(_layer.Circuits[5].State, Is.EqualTo(X25VcState.ClearRequest));
        }

        [Test]
        public void R10_CallRequestWithAWindowOfZero_IsClearedAndNeverOffered()
        {
            int incoming = 0;
            _layer.IncomingCall += (l, c, called, calling, cud) => incoming++;

            _layer.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, LocalAddress, RemoteAddress, new byte[] { 0x43, 0x00, 0x00 }));

            Assert.That(incoming, Is.EqualTo(0));
            Assert.That(_sink.LastInfo.PacketType, Is.EqualTo(X25PacketType.ClearRequest));
            Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(X25Diagnostic.FacilityParameterNotAllowed));
        }

        [Test]
        public void R10_IncomingCallUserData_IsHandedToTheApplication()
        {
            byte[]? seenCud = null;
            _layer.IncomingCall += (l, c, called, calling, cud) => seenCud = cud;

            byte[] cud = { 0x01, 0x00, 0x00, 0x00 };   // the X.29 PAD protocol identifier
            _layer.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, LocalAddress, RemoteAddress, null, cud));

            // FIXED 2026-09-06 (Phase B7); the callback used to receive an empty array. The CUD is
            // where SINTRAN says which protocol a call wants.
            Assert.That(seenCud, Is.EqualTo(cud));
            Assert.That(_layer.Circuits[5].CallUserData, Is.EqualTo(cud));
        }

        // ================================================================== R12  state hygiene

        [Test]
        public void R12_AcceptingANewCallOnAReusedLci_StartsWithCleanFlowState()
        {
            // First call: peer goes busy, then the call is cleared by the peer.
            ConnectIncoming(5);
            _layer.ProcessPacket(X25PacketBuilder.CreateRNR(5, pr: 0));
            _layer.ProcessPacket(Data(5, ps: 0, pr: 0));
            Assert.That(_layer.Circuits[5].PeerBusy, Is.True);
            _layer.ProcessPacket(X25PacketBuilder.CreateClearRequest(5, 0, 0));
            Assert.That(_layer.Circuits[5].State, Is.EqualTo(X25VcState.Ready));

            // Second call on the same LCI must not inherit busy flags or sequence numbers.
            ConnectIncoming(5);
            X25Vc vc = _layer.Circuits[5];
            Assert.That(vc.PeerBusy, Is.False);
            Assert.That(vc.LocalBusy, Is.False);
            Assert.That(vc.SendSeq, Is.EqualTo(0));
            Assert.That(vc.RecvSeq, Is.EqualTo(0));
            Assert.That(vc.AckedSeq, Is.EqualTo(0));
            Assert.That(_layer.SendData(5, new byte[] { 1 }), Is.True);
        }

        // ================================================================== R13  clearing a PVC
        //
        // X.25 4.3: permanent virtual circuits are never cleared, only reset. The engine has an
        // X25VcKind.Pvc but no way to create one, so the only thing that can be pinned today is
        // that ClearCall on a channel the engine does not know is silent.

        [Test]
        public void R13_ClearCallOnAnUnknownLci_SendsNothing()
        {
            _layer.ClearCall(9);
            Assert.That(_sink.Sent.Count, Is.EqualTo(0));
        }

        // ================================================================== R14  send results
        //
        // FIXED 2026-09-06 (Phase B2) and these tests were rewritten deliberately. Until then
        // IX25PacketSink.SendPacket returned void, so the engine could not know whether the layer
        // below took its packet, reported success anyway and spent P(S) on a packet that never
        // left - the C audit's first root cause ("return values are produced but not consumed"),
        // designed in rather than merely ignored. The sink now answers Sent / Refused / LinkDown and
        // every state change that assumed a send is rolled back when it did not happen.

        /// <summary>A sink that refuses everything after a set number of packets, like a link whose transmit queue has filled.</summary>
        private sealed class RefusingSink : IX25PacketSink
        {
            public int AcceptFirst = int.MaxValue;
            public int Accepted;
            public int Refused;
            public readonly List<byte[]> Sent = new List<byte[]>();

            public X25SendResult SendPacket(ReadOnlySpan<byte> packet)
            {
                if (Accepted < AcceptFirst)
                {
                    Accepted++;
                    Sent.Add(packet.ToArray());
                    return X25SendResult.Sent;
                }

                Refused++;
                return X25SendResult.Refused;
            }
        }

        [Test]
        public void R14_WhenTheSinkRefusesAData_SendDataReportsFalseAndPSIsNotSpent()
        {
            RefusingSink sink = new RefusingSink();
            X25Layer layer = new X25Layer(sink, _config);
            ushort lci = layer.PlaceCall(RemoteAddress);
            layer.ProcessPacket(X25PacketBuilder.CreateCallAccepted(lci));
            sink.AcceptFirst = sink.Accepted;   // everything from here on is refused

            bool ok = layer.SendData(lci, new byte[] { 1 });

            // Used to be: true, and P(S) = 1 although nothing left, so the peer saw a gap on the next
            // packet and reset the circuit (4.4.1.3) - the same mechanism that lost packets in the C
            // switch (x25switch.c:2750).
            Assert.That(ok, Is.False);
            Assert.That(layer.Circuits[lci].SendSeq, Is.EqualTo(0), "P(S) must not move for a packet that did not leave");
            Assert.That(sink.Refused, Is.EqualTo(1));
            Assert.That(layer.SendFailures, Is.EqualTo(1));

            // When the link recovers, the same P(S) goes out - no hole.
            sink.AcceptFirst = int.MaxValue;
            Assert.That(layer.SendData(lci, new byte[] { 1 }), Is.True);
            Assert.That(X25PacketParser.ParsePacket(sink.Sent[sink.Sent.Count - 1]).PS, Is.EqualTo(0));
            Assert.That(layer.Circuits[lci].SendSeq, Is.EqualTo(1));
        }

        [Test]
        public void R14_WhenTheSinkRefusesTheCallRequest_NoCircuitIsCreated()
        {
            RefusingSink sink = new RefusingSink { AcceptFirst = 0 };
            X25Layer layer = new X25Layer(sink, _config);

            ushort lci = layer.PlaceCall(RemoteAddress);

            Assert.That(lci, Is.EqualTo((ushort)0));
            Assert.That(layer.Circuits.Count, Is.EqualTo(0), "a call whose request never left must not occupy a channel");
        }

        [Test]
        public void R14_WhenTheSinkRefusesTheCallAccepted_TheCallStaysIncoming()
        {
            RefusingSink sink = new RefusingSink();
            X25Layer layer = new X25Layer(sink, _config);
            layer.ProcessPacket(X25PacketBuilder.CreateCallRequest(5, LocalAddress, RemoteAddress));
            sink.AcceptFirst = sink.Accepted;

            Assert.That(layer.AcceptCall(5), Is.False);
            Assert.That(layer.Circuits[5].State, Is.EqualTo(X25VcState.CallIncoming));

            sink.AcceptFirst = int.MaxValue;
            Assert.That(layer.AcceptCall(5), Is.True);
            Assert.That(layer.Circuits[5].State, Is.EqualTo(X25VcState.DataTransfer));
        }

        [Test]
        public void R14_WhenTheSinkRefusesAnRnr_LocalBusyIsNotSet()
        {
            RefusingSink sink = new RefusingSink();
            X25Layer layer = new X25Layer(sink, _config);
            ushort lci = layer.PlaceCall(RemoteAddress);
            layer.ProcessPacket(X25PacketBuilder.CreateCallAccepted(lci));
            sink.AcceptFirst = sink.Accepted;

            Assert.That(layer.SetBusy(lci, true), Is.False);
            Assert.That(layer.Circuits[lci].LocalBusy, Is.False, "a busy condition the peer was never told about must not be recorded");
        }

        [Test]
        public void R14_ARefusedAcknowledgement_IsCountedButDataIsStillDelivered()
        {
            RefusingSink sink = new RefusingSink();
            X25Layer layer = new X25Layer(sink, _config);
            ushort lci = layer.PlaceCall(RemoteAddress);
            layer.ProcessPacket(X25PacketBuilder.CreateCallAccepted(lci));
            int delivered = 0;
            layer.DataReceived += (l, c, d, n, q, m, db) => delivered++;
            sink.AcceptFirst = sink.Accepted;

            layer.ProcessPacket(Data(lci, ps: 0, pr: 0));

            Assert.That(delivered, Is.EqualTo(1));
            Assert.That(layer.Circuits[lci].RecvSeq, Is.EqualTo(1), "P(R) moves: the data was accepted");
            Assert.That(layer.SendFailures, Is.EqualTo(1), "the RR that did not leave is counted");
        }

        // ================================================================== R15  received length
        //
        // X.25 4.3.3 / Annex E diag 39 "Packet too long": a data packet longer than the
        // negotiated maximum is a local procedure error (reset).

        [Test]
        public void R15_DataLongerThanTheNegotiatedPacketSize_IsNotDeliveredAndResetsTheCircuit()
        {
            ushort lci = ConnectOutgoing();   // 128
            int delivered = 0;
            _layer.DataReceived += (l, c, d, n, q, m, db) => delivered++;

            _layer.ProcessPacket(Data(lci, ps: 0, pr: 0, length: 300));

            // FIXED 2026-09-06 (Phase B7); the circuit had no packet size and 300 octets were
            // delivered and acknowledged. Annex E 39 "Packet too long".
            Assert.That(delivered, Is.EqualTo(0));
            Assert.That(_sink.CountOfType(X25PacketType.ResetRequest), Is.EqualTo(1));
            Assert.That(_sink.LastInfo.DiagnosticCode, Is.EqualTo(X25Diagnostic.PacketTooLong));
            Assert.That(_layer.Circuits[lci].RecvSeq, Is.EqualTo(0), "not acknowledged");
        }

        [Test]
        public void R15_ASendLongerThanTheNegotiatedPacketSize_IsRefused()
        {
            ushort lci = ConnectOutgoing();   // 128
            Assert.That(_layer.SendData(lci, new byte[128]), Is.True, "exactly the maximum is fine");
            Assert.That(_layer.SendData(lci, new byte[129]), Is.False, "one more is refused until M-bit segmentation (B11)");
            Assert.That(_layer.Circuits[lci].SendSeq, Is.EqualTo(1), "the refused send spent no P(S)");
        }

        // ================================================================== R16  interrupt
        //
        // X.25 4.4.2: an interrupt packet is confirmed with an interrupt confirmation and
        // delivered to the application; only one may be outstanding per direction.

        [Test]
        public void R16_IncomingInterrupt_IsNeitherConfirmedNorDelivered_WHICH_IS_WRONG()
        {
            ushort lci = ConnectOutgoing();
            int delivered = 0;
            _layer.DataReceived += (l, c, d, n, q, m, db) => delivered++;

            _layer.ProcessPacket(X25PacketBuilder.CreateInterrupt(lci, new byte[] { 0x01 }));

            // MEASURED 2026-09-06: ProcessPacket's default branch swallows it. The peer's T26 will
            // expire and it will reset the circuit (Table 11).
            Assert.That(_sink.CountOfType(X25PacketType.InterruptConfirmation), Is.EqualTo(0),
                "behaviour changed: an Interrupt Confirmation is the FIX for R16 - rewrite this test");
            Assert.That(delivered, Is.EqualTo(0));
        }

        // ================================================================== R17  protocol error reactions
        //
        // X.25 Annex D Table D-1: a packet on an unassigned logical channel is answered with a
        // Clear (DCE) or Diagnostic (DTE) carrying diagnostic 36 "Packet on unassigned logical
        // channel"; a non-restart packet on channel 0 gets diagnostic 41 (Table E.1).

        [Test]
        public void R17_DataOnAnUnassignedLci_IsDroppedWithoutAnyReaction_WHICH_IS_WRONG()
        {
            _layer.ProcessPacket(Data(9, ps: 0, pr: 0));

            Assert.That(_sink.Sent.Count, Is.EqualTo(0),
                "behaviour changed: a Diagnostic/Clear with diag 36 is the FIX for R17 - rewrite this test");
        }

        [Test]
        public void R17_DataOnLogicalChannelZero_IsDroppedWithoutAnyReaction_WHICH_IS_WRONG()
        {
            _layer.ProcessPacket(Data(0, ps: 0, pr: 0));

            Assert.That(_sink.Sent.Count, Is.EqualTo(0),
                "behaviour changed: a Diagnostic with diag 41 is the FIX for R17 - rewrite this test");
        }

        // ================================================================== R18  ranges
        //
        // X.25 5.1.2 / 5.1.3: the logical channel number is twelve bits, 0..4095.

        [Test]
        public void R18_ConfiguredRangeRunningPast4095_HandsOutImpossibleLcis_WHICH_IS_WRONG()
        {
            RecordingSink sink = new RecordingSink();
            X25Layer layer = new X25Layer(sink, new X25Config { LocalAddress = LocalAddress, LciBase = 4090, LciCount = 16 });

            ushort highest = 0;
            for (int i = 0; i < 16; i++)
            {
                ushort lci = layer.PlaceCall(RemoteAddress);
                if (lci > highest)
                {
                    highest = lci;
                }
            }

            // MEASURED 2026-09-06: X25Config is not validated and AllocateLci walks to base+count.
            // (And the builder's 3-bit group mask then turns every one of these into a different
            // LCN on the wire - vector pkt-clear-request-not-obtainable.)
            Assert.That(highest, Is.GreaterThan((ushort)4095),
                "behaviour changed: rejecting a range past 4095 at init is the FIX for R18 - rewrite this test");
        }

        // ================================================================== R19  DTE cause byte
        //
        // X.25 Table 5-7 note a: in a clear request from a DTE the clearing cause is 00000000, or
        // a value with bit 8 set (1xxxxxxx) when the network supports it. A DCE-style cause such as
        // 00001001 "Out of order" must never be sent by a DTE.

        [Test]
        public void R19_DteClearCallWithADceStyleCause_SendsItUnchanged_WHICH_IS_WRONG()
        {
            ushort lci = ConnectOutgoing();

            _layer.ClearCall(lci, cause: 0x09, diagnostic: 0);

            Assert.That(_sink.LastInfo.PacketType, Is.EqualTo(X25PacketType.ClearRequest));
            Assert.That(_sink.LastInfo.ClearingCause, Is.EqualTo(0x09),
                "behaviour changed: forcing a DTE cause to 0x00 is the FIX for R19 - rewrite this test");
        }
    }
}
