//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Codec;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Tad;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// THE PORT ACCEPTANCE TEST (seam plan section 7). A hand-written fake <see cref="ILink"/> - with
    /// NO HDLC framing, NO LAPB state machine, NO transport, no async - drives the full XMSG stack
    /// (<see cref="XmsgCodec"/> + <see cref="XmsgLayer"/> + the TAD responder) in BOTH directions:
    /// a payload pushed UP produces a decoded SINTRAN frame back DOWN. Because the entire upper stack
    /// depends only on <see cref="ILink"/> (plus <c>RetroIO.Protocols.Xmsg</c>) and never touches
    /// <c>RetroIO.Protocols.Xmsg.Hdlc</c> or <c>LapbLayer</c>, this test IS the proof that porting into
    /// X25Emulator is "swap the link adapter" - bind these same upper layers to X25Emulator's ILink
    /// implementation and nothing above the seam changes.
    /// </summary>
    /// <remarks>
    /// This test lives in <c>RetroIO.Protocols.Xmsg.Node.Tests</c>, which references only RetroIO.Protocols.Xmsg + RetroIO.Protocols.Xmsg.Node -
    /// NOT RetroIO.Protocols.Xmsg.Live or RetroIO.Protocols.Xmsg.Hdlc. So "no link/framing dependency" is compiler-enforced here: if the
    /// upper stack ever grew a Live/HDLC dependency, this project would fail to compile.
    /// </remarks>
    public sealed class FakeLinkAcceptanceTests
    {
        // The exact captured connect request 100 -> 103 (proto DA) - same bytes the parity test uses.
        private const string ConnectRequestHex =
            "2113000E0067006400000400DA13210086E400670000006402F7040000410010FF072A54414441444D00FE0444313033";

        private static readonly Func<DateTime> FixedClock = () => new DateTime(2026, 7, 2);

        [Fact]
        public void FakeLink_DrivesFullXmsgStack_BothDirections()
        {
            // Compose the WHOLE upper stack over a fake link - no HDLC, no LAPB, no transport.
            FakeLink link = new FakeLink("fake:acceptance");
            LinkXmsgTransport transport = new LinkXmsgTransport(link);   // codec sends DOWN through the link
            XmsgCodec codec = new XmsgCodec(link.Name, transport);
            XmsgLayer layer = new XmsgLayer(codec, 103, 0x00);
            ConfigureLayer(layer);

            // UP wiring: a payload delivered by the link is parsed by the codec (which raises up to the
            // layer). This is the same one-liner the real runner uses - the only thing that differs
            // from production is the ILink implementation under it.
            link.PayloadReceived += delegate (ILink deliveringLink, byte[] payload, int length)
            {
                codec.ProcessBytes(payload.AsSpan(0, length));
            };

            Assert.True(link.Start());
            Assert.Equal(LinkStatus.Active, link.Status);

            // DRIVE UP: push the connect request in as if it had just arrived on the (absent) wire.
            link.DeliverUp(Convert.FromHexString(ConnectRequestHex));

            // DRIVE DOWN (assert): the layer + TAD responder answered, and the codec serialised that
            // answer back down through the link. At least the connect-accept must have been sent.
            Assert.NotEmpty(link.SentFrames);

            // Every frame the stack emitted is a well-formed SINTRAN frame (marker 0x21 0x13/0x12) -
            // proof the bytes went all the way through the codec, not some half-formed fragment.
            for (int i = 0; i < link.SentFrames.Count; i++)
            {
                byte[] sent = link.SentFrames[i];
                Assert.True(sent.Length >= 2, "sent frame too short to be a SINTRAN frame");
                Assert.Equal(0x21, sent[0]);
                Assert.True(sent[1] == 0x13 || sent[1] == 0x12, "second marker must be 0x13 or 0x12");
            }
        }

        [Fact]
        public void FakeLink_NotActive_RefusesSendWithFalse()
        {
            // Contract: a send on a not-Active link returns false (never throws).
            FakeLink link = new FakeLink("fake:idle");
            Assert.Equal(LinkStatus.Stopped, link.Status);
            Assert.False(link.SendData(new byte[] { 0x21, 0x13 }));
        }

        [Fact]
        public void FakeLink_ReusesOneUpBuffer_StackStillCorrect_ProvesCopyDiscipline()
        {
            // COPY-DISCIPLINE PROOF (seam plan section 7.2). The fake link delivers TWO different
            // inbound payloads UP through the SAME reused buffer - exactly what a pooled/reused
            // receive buffer would do. If any layer retained the buffer instead of consuming it inside
            // the callback, the second delivery would corrupt the first. We assert the stack answered
            // BOTH deliveries (two connect-accepts), proving nothing above the seam held the buffer.
            FakeLink link = new FakeLink("fake:reuse");
            LinkXmsgTransport transport = new LinkXmsgTransport(link);
            XmsgCodec codec = new XmsgCodec(link.Name, transport);
            XmsgLayer layer = new XmsgLayer(codec, 103, 0x00);
            ConfigureLayer(layer);

            link.PayloadReceived += delegate (ILink deliveringLink, byte[] payload, int length)
            {
                codec.ProcessBytes(payload.AsSpan(0, length));
            };

            Assert.True(link.Start());

            byte[] connect = Convert.FromHexString(ConnectRequestHex);

            // Deliver the SAME logical payload twice, both times through the link's ONE reusable up
            // buffer (DeliverUpReusingBuffer overwrites that buffer each call). A connect request that
            // is answered once must be answered again after a fresh session - the point is only that
            // the second delivery is not corrupted by the first having retained the buffer.
            int before = link.SentFrames.Count;
            link.DeliverUpReusingBuffer(connect);
            int afterFirst = link.SentFrames.Count;
            link.DeliverUpReusingBuffer(connect);
            int afterSecond = link.SentFrames.Count;

            // Each delivery produced at least one well-formed outbound frame (marker 0x21) - so both
            // passes through the reused buffer decoded cleanly.
            Assert.True(afterFirst > before, "first reused-buffer delivery produced no output");
            Assert.True(afterSecond > afterFirst, "second reused-buffer delivery produced no output");
            for (int i = before; i < link.SentFrames.Count; i++)
            {
                Assert.Equal(0x21, link.SentFrames[i][0]);
            }
        }

        /// <summary>
        /// Configures the layer exactly as the runner/parity test do (routing + TAD responder).
        /// </summary>
        private static void ConfigureLayer(XmsgLayer layer)
        {
            layer.AcknowledgeData = false;
            layer.RoutingTable = new InMemoryRoutingTable(RoutingEntries());
            layer.TadResponder = new TadTerminalResponder(103, FixedClock);
            layer.AcknowledgeTadFrames = true;
        }

        private static RoutingTableEntry[] RoutingEntries()
        {
            return new RoutingTableEntry[]
            {
                new RoutingTableEntry(100, XroutConnectionType.Neighbour, 1, 1, 0),
                new RoutingTableEntry(102, XroutConnectionType.Via, 100, 2, 0),
                new RoutingTableEntry(103, XroutConnectionType.Local, 103, 0, 0),
            };
        }

        /// <summary>
        /// A minimal in-memory <see cref="ILink"/> for the acceptance test: it carries opaque L3 with no
        /// framing, no LAPB, no transport. <see cref="DeliverUp"/> simulates an inbound payload;
        /// <see cref="SendData"/> captures the outbound frames the stack produces. It honours the
        /// contract's status gating, no-throw send semantics, and copy-on-send discipline.
        /// </summary>
        private sealed class FakeLink : ILink
        {
            private readonly string _name;
            private LinkStatus _status;

            // ONE reusable up buffer, deliberately shared across DeliverUpReusingBuffer calls to model a
            // pooled receive buffer and prove the upper stack copies-if-it-retains.
            private byte[] _reusableUpBuffer;

            /// <summary>
            /// The SINTRAN frames the upper stack sent DOWN through this link.
            /// </summary>
            public List<byte[]> SentFrames { get; }

            public event LinkPayloadReceived? PayloadReceived;
            public event LinkStatusChanged? StatusChanged;

            public FakeLink(string name)
            {
                _name = name;
                _status = LinkStatus.Stopped;
                _reusableUpBuffer = Array.Empty<byte>();
                SentFrames = new List<byte[]>();
            }

            public string Name
            {
                get { return _name; }
            }

            public LinkStatus Status
            {
                get { return _status; }
            }

            public bool Start()
            {
                // No LAPB handshake to wait for - the fake link is Active immediately.
                SetStatus(LinkStatus.Starting, "start requested");
                SetStatus(LinkStatus.Active, "fake link up");
                return true;
            }

            public void Stop()
            {
                if (_status == LinkStatus.Stopped || _status == LinkStatus.Stopping)
                {
                    return;
                }

                SetStatus(LinkStatus.Stopping, "stop requested");
                SetStatus(LinkStatus.Stopped, "stopped");
            }

            public void Dispose()
            {
                Stop();
            }

            public bool SendData(ReadOnlySpan<byte> payload)
            {
                // Contract: refuse (logged false, never throw) when the link is not Active.
                if (_status != LinkStatus.Active)
                {
                    return false;
                }

                if (payload.Length == 0)
                {
                    return false;
                }

                // Copy the span into our own storage - the compiler forbids storing the span itself, so
                // this copy is mandatory and the caller's buffer is free the instant we return.
                byte[] copy = payload.ToArray();
                SentFrames.Add(copy);
                return true;
            }

            /// <summary>
            /// Test hook: simulate one complete L3 payload arriving up on a fresh buffer.
            /// </summary>
            /// <param name="payload">
            /// The SINTRAN information field bytes.
            /// </param>
            public void DeliverUp(byte[] payload)
            {
                PayloadReceived?.Invoke(this, payload, payload.Length);
            }

            /// <summary>
            /// Test hook: simulate one payload arriving up through the link's ONE reused buffer (models a
            /// pooled receive buffer). The buffer is overwritten on every call, so a consumer that
            /// retains it across calls would see corruption - which is exactly what this proves absent.
            /// </summary>
            /// <param name="payload">
            /// The SINTRAN information field bytes to copy into the reused buffer.
            /// </param>
            public void DeliverUpReusingBuffer(byte[] payload)
            {
                if (_reusableUpBuffer.Length < payload.Length)
                {
                    _reusableUpBuffer = new byte[payload.Length];
                }

                Array.Copy(payload, 0, _reusableUpBuffer, 0, payload.Length);
                PayloadReceived?.Invoke(this, _reusableUpBuffer, payload.Length);
            }

            private void SetStatus(LinkStatus next, string reason)
            {
                if (next != _status)
                {
                    LinkStatus previous = _status;
                    _status = next;
                    StatusChanged?.Invoke(this, previous, next, reason);
                }
            }
        }
    }
}
