//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Live;
using RetroIO.Protocols.Xmsg.Live.Seam;   // LapbLayerAdapter (concrete ILink over HDLC/LAPB, staying half)
using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Phase 3 gate for the link seam: an incoming SINTRAN information field round-trips through
    /// <see cref="LapbLayerAdapter"/> - it surfaces UP as <see cref="ILink.PayloadReceived"/>, and a
    /// <see cref="ILink.SendSintranFrame"/> in response goes DOWN and appears on the wire as a LAPB
    /// I-frame carrying those exact bytes - all over an in-memory duplex, deterministically.
    /// </summary>
    public sealed class LinkSeamTests
    {
        // A SINTRAN reachability request used as the round-tripped payload (14 bytes, marker 0x21).
        private static readonly byte[] SintranInfo =
            Convert.FromHexString("2113001900660064FFFF0001DE08");

        [Fact]
        public async Task Payload_RoundTripsUpThenDownThroughAdapter()
        {
            // Inbound wire: the peer (node 100) SABM, then its first I-frame carrying SintranInfo.
            List<byte> inbound = new List<byte>();
            inbound.AddRange(HdlcEncoder.Encode(new byte[] { 0x01, 0x3F, 0x00, 0x64 }));   // peer SABM
            // The peer's UA answering OUR SABM (Initiate below sends one). Spec 3.1: a station
            // "MUST answer any received SABM with UA", and the link is UP only when BOTH directions
            // have completed the exchange. Without this the fixture models a peer that violates a
            // MUST, and the adapter correctly refuses to report the link usable - see
            // LapbLinkUpGatingTests for why that gating exists.
            inbound.AddRange(HdlcEncoder.Encode(new byte[] { 0x01, 0x73, 0x00, 0x64 }));   // peer UA
            byte[] iframeBody = BuildIFrameBody(sendSeq: 0, receiveSeq: 0, SintranInfo);
            inbound.AddRange(HdlcEncoder.Encode(iframeBody));                               // peer I-frame

            InMemoryDuplex duplex = new InMemoryDuplex(inbound.ToArray());
            LapbLayer link = new LapbLayer(ownNode: 102);
            LapbLayerAdapter adapter = new LapbLayerAdapter("hdlc:test", duplex, link);

            List<byte[]> received = new List<byte[]>();
            List<LinkStatus> statuses = new List<LinkStatus>();
            adapter.PayloadReceived += delegate (ILink link, byte[] payload, int length)
            {
                Assert.Equal("hdlc:test", link.Name);         // sender-first: the ILink instance
                byte[] copy = new byte[length];               // contract: copy within the callback if retained
                Array.Copy(payload, 0, copy, 0, length);
                received.Add(copy);
                // Respond DOWN: echo the payload back as an opaque L3 frame (the codec/layer's job later).
                adapter.SendData(payload.AsSpan(0, length));
            };
            adapter.StatusChanged += delegate (ILink link, LinkStatus oldStatus, LinkStatus newStatus, string reason)
            {
                statuses.Add(newStatus);
            };

            adapter.Initiate();
            await adapter.RunWithoutTimersAsync(CancellationToken.None);

            // UP: the exact SINTRAN information field was delivered.
            Assert.Single(received);
            Assert.Equal(SintranInfo, received[0]);

            // Status went Active when the LAPB link connected.
            Assert.Contains(LinkStatus.Active, statuses);

            // DOWN: among the frames written to the wire there is a data I-frame whose info field is
            // exactly the echoed SINTRAN bytes - proof the seam carried the reply out to the wire.
            byte[] written = duplex.GetWrittenBytes();
            Assert.True(ContainsIFrameWithInfo(written, SintranInfo),
                "expected an outbound LAPB I-frame carrying the echoed SINTRAN payload");
        }

        [Fact]
        public void NotActiveLink_RefusesSendWithFalse()
        {
            // Contract: SendData on a not-yet-started (not Active) link returns a logged false, never
            // throws. The single opaque send replaced the old per-protocol sends and their binding gate.
            LapbLayer link = new LapbLayer(ownNode: 102);
            LapbLayerAdapter adapter = new LapbLayerAdapter(
                "hdlc:test", new InMemoryDuplex(Array.Empty<byte>()), link);

            Assert.Equal(LinkStatus.Stopped, adapter.Status);
            Assert.False(adapter.SendData(new byte[] { 0x21, 0x13 }));
        }

        /// <summary>
        /// Builds a LAPB data I-frame body: addr 0x09, control from N(S)/N(R), then info.
        /// </summary>
        private static byte[] BuildIFrameBody(int sendSeq, int receiveSeq, byte[] info)
        {
            byte control = (byte)((receiveSeq << 5) | (sendSeq << 1));   // bit0 = 0 -> I-frame
            byte[] body = new byte[2 + info.Length];
            body[0] = 0x09;             // data address
            body[1] = control;
            Array.Copy(info, 0, body, 2, info.Length);
            return body;
        }

        /// <summary>
        /// Deframes an HDLC byte stream and returns true when any FCS-valid data I-frame carries an
        /// info field equal to <paramref name="expectedInfo"/>.
        /// </summary>
        private static bool ContainsIFrameWithInfo(byte[] wire, byte[] expectedInfo)
        {
            IReadOnlyList<byte[]> frames = HdlcDeframer.SplitFrames(wire);
            for (int i = 0; i < frames.Count; i++)
            {
                byte[] frameBytes = frames[i];
                if (!Fcs16.IsValid(frameBytes))
                {
                    continue;
                }

                LapbFrame frame = LapbFrame.FromCapture(frameBytes);
                if (frame.Kind != LapbFrameKind.Information)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                if (info.Length != expectedInfo.Length)
                {
                    continue;
                }

                bool equal = true;
                for (int j = 0; j < info.Length; j++)
                {
                    if (info[j] != expectedInfo[j])
                    {
                        equal = false;
                        break;
                    }
                }

                if (equal)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
