//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Node.Tad;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Verifies the <see cref="FileResponderSequenceStore"/> load/save round-trip and the ND-100
    /// sequence rule (XMSG-PROTOCOL.md section 18.8): the responder CONTINUES its per-link outgoing Flags1
    /// from the persisted store across connects - it is NOT reset per connect. It zeroes ONLY on
    /// first-ever contact or a peer ReachabilityRequest (the peer's XMSG restarted). Hard-resetting to
    /// 0x0000 against a climbed peer lands behind its expected-from-us and the accept is silently dropped;
    /// the historical fatal-24B crash was an ECHO of the asker's Flags1 (a wrong value), not continuation.
    /// </summary>
    public sealed class ResponderSequenceStoreTests
    {
        // A simple in-memory store for the responder test (no disk).
        private sealed class MemoryStore : IResponderSequenceStore
        {
            private readonly Dictionary<ushort, ushort> _map = new Dictionary<ushort, ushort>();

            public ushort LoadNextFlags1(ushort remoteNode)
            {
                return _map.TryGetValue(remoteNode, out ushort v) ? v : (ushort)0x0000;
            }

            public void SaveNextFlags1(ushort remoteNode, ushort nextFlags1)
            {
                _map[remoteNode] = nextFlags1;
            }
        }

        private const string ConnectHex =
            "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033";

        // The EXACT bytes of 100's session-2 reconnect connect from the multiple-connect capture:
        // 100 -> 102, Flags1 0x0016, class 0x0400, channel D9 (epoch 1), counter 0xFE, XMCSM 0x04000041.
        private const string D9ReconnectConnectHex =
            "2113000E0066006400160400D9FE210086E40066000000640290040000410010FF072A54414441444D00FE0444313032";

        /// <summary>
        /// A restart reads back a value at or AHEAD of what was saved - never behind - and the live
        /// value inside one process stays exact.
        /// </summary>
        /// <remarks>
        /// <para><b>This test used to demand an exact round-trip. That contract was wrong.</b></para>
        /// <para>
        /// MEASURED 2026-08-11 against a real D100: a datagram whose Flags 1 is AHEAD of the peer's
        /// expectation draws a XENSE (<c>0xFFDE</c>) naming the offending number, which can be
        /// recovered from. One that is BEHIND is dropped in SILENCE - no acknowledgement, no error -
        /// and the conversation simply stops with nothing to diagnose. So when the stored number
        /// cannot be exact it must err HIGH, and the file is written with
        /// <see cref="FileResponderSequenceStore.LookaheadFrames"/> added precisely so that a
        /// process killed between writes resumes ahead rather than short.
        /// </para>
        /// <para>
        /// Asserting equality here would forbid the very behaviour that keeps a restarted node
        /// talking, so the assertion is a RANGE.
        /// </para>
        /// </remarks>
        [Fact]
        public void FileStore_ResumesAtOrAheadOfWhatWasSaved()
        {
            string path = Path.Combine(Path.GetTempPath(), "xmsg-seq-test-" + Guid.NewGuid().ToString("N") + ".state");
            try
            {
                // First "process": store node 100 -> next Flags1 0x0007.
                FileResponderSequenceStore a = new FileResponderSequenceStore(path);
                Assert.Equal(0x0000, a.LoadNextFlags1(100));   // first-ever contact starts at 0
                a.SaveNextFlags1(100, 0x0007);

                // Within the SAME process the live value is exact - the lookahead is a property of
                // the file, not of the counter.
                Assert.Equal(0x0007, a.LoadNextFlags1(100));

                // Second "process" (new instance = a restart): at or ahead, never behind.
                FileResponderSequenceStore b = new FileResponderSequenceStore(path);
                ushort resumed = b.LoadNextFlags1(100);

                Assert.True(
                    resumed >= 0x0007,
                    "a restart must never resume BEHIND what was saved - behind is dropped in "
                    + "silence. Resumed at 0x" + resumed.ToString("X4"));
                Assert.True(
                    resumed <= 0x0007 + FileResponderSequenceStore.LookaheadFrames,
                    "the lookahead is bounded; resuming further ahead than "
                    + FileResponderSequenceStore.LookaheadFrames + " wastes numbers. Resumed at 0x"
                    + resumed.ToString("X4"));

                Assert.Equal(0x0000, b.LoadNextFlags1(102));   // an unseen node is still fresh
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void Responder_ContinuesFromStoredSequence()
        {
            // The accept CONTINUES our persisted per-link outgoing Flags1 (GOD-LLM S6a MEASUREMENT:
            // continuation is right; its values are capture-legal at any epoch/channel; "echo crashes" was
            // disproven). ConnectHex is a 100-103 connect (seed 0x13), so the wrap boundary is F1 0x14;
            // 0x0004 is a normal non-boundary value and must be used verbatim.
            MemoryStore store = new MemoryStore();
            store.SaveNextFlags1(100, 0x0004);   // continued value from prior sessions on this link

            TadTerminalResponder responder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2), store);
            System.Collections.Generic.IReadOnlyList<XmsgFrame> frames =
                responder.OnConnect(XmsgFrame.Parse(Convert.FromHexString(ConnectHex)));

            byte[] accept = frames[0].ToArray();
            // The accept's Flags1 (header offset 8-9) equals the stored value, NOT 0x0000.
            Assert.Equal(0x0004, (accept[8] << 8) | accept[9]);
        }

        [Fact]
        public void Responder_ContinuesAcrossWrapBoundary_NoResetNoGuard()
        {
            // S7 capture answer (multiple-connect-100-to102-and-then-reboot): the responder's counter is a
            // single free-running per-direction value that CONTINUES across connects and is NEVER reset per
            // connect nor guarded away from the Counter-0xFF boundary. Real 102 continued its session-2
            // letters at 0x0016/0x0017 straight past the boundary at 0x0015. So when the store holds the
            // boundary value itself, the accept MUST carry it unchanged - the earlier fall-back-to-0x0000
            // was the wrong fix (it produced the reset-to-0 stall). For ConnectHex (seed 0x13) the boundary
            // is F1 0x14.
            MemoryStore store = new MemoryStore();
            store.SaveNextFlags1(100, 0x0014);

            TadTerminalResponder responder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2), store);
            System.Collections.Generic.IReadOnlyList<XmsgFrame> frames =
                responder.OnConnect(XmsgFrame.Parse(Convert.FromHexString(ConnectHex)));

            byte[] accept = frames[0].ToArray();
            // Continued, NOT reset: the accept carries the stored 0x0014.
            Assert.Equal(0x0014, (accept[8] << 8) | accept[9]);
        }

        [Fact]
        public void Responder_ContinuesWhenPortAssignWouldHitBoundary_NoReset()
        {
            // The connect handshake emits TWO class-0x0400 letters: the accept at V and the port-assign at
            // V+1. Under continuation NEITHER is special-cased: a store of 0x0013 (accept 0x13, port-assign
            // 0x14 = the boundary for seed 0x13) still emits the accept at its continued 0x0013. The port
            // assign at the boundary is legal (continuation) and, per the S7 capture, never the crash shape.
            MemoryStore store = new MemoryStore();
            store.SaveNextFlags1(100, 0x0013);

            TadTerminalResponder responder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2), store);
            System.Collections.Generic.IReadOnlyList<XmsgFrame> frames =
                responder.OnConnect(XmsgFrame.Parse(Convert.FromHexString(ConnectHex)));

            byte[] accept = frames[0].ToArray();
            // Continued, NOT reset: the accept carries the stored 0x0013.
            Assert.Equal(0x0013, (accept[8] << 8) | accept[9]);
        }

        [Fact]
        public void Responder_AckChannel_IsDeAnchor_ForEpoch1D9Reconnect()
        {
            // CAPTURE-CORRECTED (multiple-connect session 2): a reconnect whose connect rides D9 (epoch 1)
            // still ACKs on the 0xDE anchor, NOT D9+4=DD. The old connect+4 rule produced a DD ACK during
            // the handshake that crashed 100 at PERF_CONNCT. Feeding 100's actual session-2 connect bytes
            // (D9) must leave the responder ACKing on DE.
            MemoryStore store = new MemoryStore();
            TadTerminalResponder responder = new TadTerminalResponder(102, () => new DateTime(2026, 7, 2), store);

            responder.OnConnect(XmsgFrame.Parse(Convert.FromHexString(D9ReconnectConnectHex)));

            Assert.Equal((SintranProtocolId)0xDE, responder.AckChannel);
        }

        [Fact]
        public void ReachabilityRequest_ResetsSequenceForThatNode()
        {
            // 100's XMSG restart is signalled by a ReachabilityRequest; it zeroes 100's expected-from-
            // us. A stale stored value must be reset to 0x0000 so our next session is in step.
            MemoryStore store = new MemoryStore();
            store.SaveNextFlags1(100, 0x0002);   // stale value from a prior (dropped) run

            string? logged = null;
            XmsgNode node = new XmsgNode(102, 0x00);
            TadTerminalResponder responder = new TadTerminalResponder(102, () => new DateTime(2026, 7, 2), store);
            responder.Log = line => logged = line;
            node.TadResponder = responder;

            // ReachabilityRequest 100 -> 102 (subtype 0x19, source node 0x0064 = 100).
            node.HandleFrames(XmsgFrame.Parse(Convert.FromHexString("2113001900660064FFFF0001DE08")));

            Assert.Equal(0x0000, store.LoadNextFlags1(100));
            // The reset is loudly logged (greppable "RESET") so a later reconnect issue is easy to trace.
            Assert.NotNull(logged);
            Assert.Contains("RESET", logged);
        }

        [Fact]
        public void Responder_FirstContact_StartsAtZero()
        {
            // No stored value -> a fresh contact starts at 0x0000 (matches a fresh peer).
            TadTerminalResponder responder = new TadTerminalResponder(103, () => new DateTime(2026, 7, 2), new MemoryStore());
            System.Collections.Generic.IReadOnlyList<XmsgFrame> frames =
                responder.OnConnect(XmsgFrame.Parse(Convert.FromHexString(ConnectHex)));

            byte[] accept = frames[0].ToArray();
            Assert.Equal(0x0000, (accept[8] << 8) | accept[9]);
        }

        [Fact]
        public void AnsweringAPeerThatIsAheadDoesNotDragOurOwnCountForward()
        {
            // THE DEFECT THAT KILLED THE LIVE PUSH, 2026-08-10. Echoing used to ratchet our own
            // outgoing counter up to the echoed number plus one. That is the "one sequence shared
            // by both machines" model, and it is wrong: the two counters are independent, so
            // answering a peer that is AHEAD of us must not move ours at all.
            //
            // The numbers here are the real ones off the wire
            // (DOC/captures/ND-TO-ND-WRITE-2026-08-10/): our next was 0x0022 and D100 was running
            // eight ahead at 0x0028. The ratchet threw us to 0x0029, D100 was still waiting for
            // 0x0022, and it answered with XENSE (-34) and then tore the link down.
            //
            // Why no earlier test caught it: while both sides are in step the ratchet is a no-op,
            // and every offline test drove a peer that was in step. It only shows when the counts
            // DIFFER, which is why this test forces them apart.
            // A host will not address a node until an inbound frame has taught it the link, so the
            // client's own letter seeds it - the same way the live push does. That fixture is node
            // 19999 talking to node 100, which is exactly the pairing this defect was found on.
            XmsgServerHost host = new XmsgServerHost(FaTestClient.ServerNode);
            host.Route(FaTestClient.BuildConnectLetter());

            // Spend numbers up to 0x0021 so our next origination is 0x0022. The values are read
            // back from the frames rather than assumed, so seeding above can cost any number of
            // them without breaking the test.
            ushort ourNext = 0x0000;
            while (ourNext <= 0x0021)
            {
                XmsgFrame spent = host.BuildBodyDatagram(
                    remoteNode: FaTestClient.ClientNode, clientSystem: FaTestClient.ClientNode, clientPort: 0x05B9, sourcePort: 0x0212,
                    xmcsm: 0x8284, frameFlags: 0x00, role: 0x00,
                    body: new byte[] { 0x07, 0xF0 },
                    answeredFlags1: XmsgAnsweredFlags1.None);
                byte[] spentBytes = spent.ToArray();
                ourNext = (ushort)(((spentBytes[8] << 8) | spentBytes[9]) + 1);
            }

            // Answer a frame of the peer's that is well ahead of us. This is the ECHO path.
            XmsgFrame echo = host.BuildBodyDatagram(
                remoteNode: FaTestClient.ClientNode, clientSystem: FaTestClient.ClientNode, clientPort: 0x05B9, sourcePort: 0x0212,
                xmcsm: 0x8284, frameFlags: 0x00, role: 0x00,
                body: new byte[] { 0x07, 0xA2 },
                answeredFlags1: 0x0028);
            byte[] echoBytes = echo.ToArray();
            Assert.Equal(0x0028, (echoBytes[8] << 8) | echoBytes[9]);   // it echoes, as it must

            // The next thing we ORIGINATE must still be OUR number, 0x0022 - not the peer's 0x0029.
            XmsgFrame originated = host.BuildBodyDatagram(
                remoteNode: FaTestClient.ClientNode, clientSystem: FaTestClient.ClientNode, clientPort: 0x05B9, sourcePort: 0x0212,
                xmcsm: 0x8284, frameFlags: 0x00, role: 0x00,
                body: new byte[] { 0x07, 0xF0 },
                answeredFlags1: XmsgAnsweredFlags1.None);
            byte[] originatedBytes = originated.ToArray();
            Assert.Equal(0x0022, (originatedBytes[8] << 8) | originatedBytes[9]);
        }
    }
}
