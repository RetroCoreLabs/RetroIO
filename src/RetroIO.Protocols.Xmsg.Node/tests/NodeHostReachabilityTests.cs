//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Reachability through the FULL runner composition - <see cref="XmsgNodeHost"/> with a
    /// <see cref="XmsgServerHost"/> and a sequence store attached - rather than through a bare
    /// <c>XmsgLayer</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>XmsgLayerTests.Reachability_EmitsByteExactReply</c> already proves the LAYER answers a
    /// subtype-0x19 request. It does NOT prove the runner does, because the runner composes the
    /// layer through <see cref="XmsgNodeHost"/>, which additionally sets a server host. Reachability
    /// calls <c>ServerHost.ResetSequence</c> on the way through, so the server host sits directly on
    /// the reachability path even though reachability is not a Data frame and never reaches a server.
    /// </para>
    /// <para>
    /// This test exists because the live D19999 node was observed LOGGING an inbound
    /// ReachabilityRequest from D100 and D100 then aborting with
    /// <c>NO ANSWER FROM REMOTE SYSTEM</c>. That symptom accuses everything between the codec and
    /// the link, and the bare-layer test could not tell the two compositions apart.
    /// </para>
    /// </remarks>
    public sealed class NodeHostReachabilityTests
    {
        // The captured reachability request 100 -> 102, retargeted at our node number below. The
        // node number is the only thing that changes; the shape is the captured one.
        private const string ReachabilityRequest100To102 = "2113001900660064FFFF0001DE08";

        [Fact]
        public void NodeHost_WithServerHost_AnswersReachabilityRequest()
        {
            RecordingLink link = new RecordingLink("fake:reach");
            XmsgNodeHost host = BuildHost(link, nodeNumber: 102);

            Assert.True(link.Start());
            link.DeliverUp(Convert.FromHexString(ReachabilityRequest100To102));

            // The whole point: a reply must leave the node. "Received but unanswered" hangs the
            // calling SINTRAN terminal - ESC will not abort it - so silence here is the live bug.
            Assert.NotEmpty(link.SentFrames);
            Assert.Equal(
                Convert.FromHexString("2113001300640066FFFF0001DE0E"),
                link.SentFrames[0]);
        }

        /// <summary>
        /// The live node is 19999, not 102. A five-digit node number does not fit the one-byte
        /// assumptions that crept into some of the older fixtures, so it is asserted separately.
        /// </summary>
        [Fact]
        public void NodeHost_AtNode19999_AnswersReachabilityRequest()
        {
            // Request 100 -> 19999. 19999 = 0x4E1F.
            // CORRECTED 2026-08-04. This fixture used to end DE08 - the two bytes copied straight
            // off the node-102 capture - but offsets 12-13 are header word 6, a checksum over the
            // OTHER SIX WORDS, and those words include the node numbers. For 19999 the value is
            // 0x904F: the ones-complement sum with end-around carry of
            // 2113 0019 4E1F 0064 FFFF 0001 is 0x6FB0, and ~0x6FB0 = 0x904F.
            // CONFIRMED ON THE WIRE the same day: with our node live at 19999, D100's own
            // reachability request read 211300194E1F0064FFFF0001904F - byte for byte this string.
            string request = "211300194E1F0064FFFF0001904F";

            RecordingLink link = new RecordingLink("fake:reach19999");
            XmsgNodeHost host = BuildHost(link, nodeNumber: 19999);

            Assert.True(link.Start());
            link.DeliverUp(Convert.FromHexString(request));

            Assert.NotEmpty(link.SentFrames);

            // Reply must swap direction: 19999 -> 100, subtype 0x13, and word 6 = 0x9055.
            // The old "+6 on the trailing counter" rule survives as a CONSEQUENCE: a reply differs
            // from its request only in the subtype (0x19 -> 0x13, six less) and in swapped node
            // numbers, which cancel in a sum, so a sum six smaller complements six larger.
            // 0x904F + 6 = 0x9055, exactly as 0xDE08 + 6 = 0xDE0E on the node-102 pair.
            Assert.Equal(
                Convert.FromHexString("211300130064" + "4E1F" + "FFFF00019055"),
                link.SentFrames[0]);
        }

        private static XmsgNodeHost BuildHost(ILink link, ushort nodeNumber)
        {
            return new XmsgNodeHost(
                link,
                nodeNumber,
                RoutingEntries(),
                new InMemoryResponderSequenceStore());
        }

        private static RoutingTableEntry[] RoutingEntries()
        {
            return new RoutingTableEntry[]
            {
                new RoutingTableEntry(100, XroutConnectionType.Neighbour, 1, 1, 0),
            };
        }

        /// <summary>
        /// A minimal in-memory sequence store, so the test does not touch the filesystem.
        /// </summary>
        private sealed class InMemoryResponderSequenceStore : IResponderSequenceStore
        {
            private readonly Dictionary<ushort, ushort> _sequences = new Dictionary<ushort, ushort>();

            public ushort LoadNextFlags1(ushort remoteNode)
            {
                ushort value;
                return _sequences.TryGetValue(remoteNode, out value) ? value : (ushort)0;
            }

            public void SaveNextFlags1(ushort remoteNode, ushort nextFlags1)
            {
                _sequences[remoteNode] = nextFlags1;
            }
        }

        /// <summary>
        /// An <see cref="ILink"/> that records what the stack sends DOWN and lets a test push a
        /// payload UP, with no framing or transport of any kind.
        /// </summary>
        private sealed class RecordingLink : ILink
        {
            private readonly string _name;
            private LinkStatus _status;

            public List<byte[]> SentFrames { get; }

            public event LinkPayloadReceived? PayloadReceived;
            public event LinkStatusChanged? StatusChanged;

            public RecordingLink(string name)
            {
                _name = name;
                _status = LinkStatus.Stopped;
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
                if (_status != LinkStatus.Active || payload.Length == 0)
                {
                    return false;
                }

                SentFrames.Add(payload.ToArray());
                return true;
            }

            public void DeliverUp(byte[] payload)
            {
                PayloadReceived?.Invoke(this, payload, payload.Length);
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
