//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text.Json;

using RetroIO.Protocols.Xmsg.Node.Tad;
using RetroIO.Protocols.Xmsg.TestSupport;
using RetroIO.Protocols.Xmsg.Packet;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Asks whether a TAD-built frame carries a REAL header word 6.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// Word 6 is a ones-complement checksum over words 0-5, carved from the kernel and confirmed on
    /// 3595/3595 captured frames. A fabricated one kills D100 with <c>XMSG ERROR CODE 24</c>, and
    /// getting that wrong on the file-access path cost most of a day.
    /// <para>
    /// <c>TadSession.AssembleDataFrame</c> does not compute it. It sets
    /// <c>Header.ProtocolId</c> and <c>Header.Counter</c> from <see cref="TadFrameContext"/>, and
    /// those two properties are compatibility views over the checksum's HIGH and LOW bytes. So a TAD
    /// frame's word 6 is whatever the caller put in the context.
    /// </para>
    /// <para><b>What this test is for</b></para>
    /// To say plainly which it is, rather than leaving it as a suspicion in a comment. If the
    /// assertion below holds, TAD computes a real checksum somewhere and there is nothing to fix. If
    /// it fails, TAD ships a fabricated word 6 and works today only because nodes 100 and 103 are
    /// small numbers - exactly how the same defect hid on the file-access path until node 19999.
    /// </remarks>
    public sealed class TadHeaderChecksumTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public TadHeaderChecksumTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// A frame built by <see cref="TadSession"/> carries the carved checksum in word 6.
        /// </summary>
        /// <remarks>
        /// The context deliberately carries a <c>ProtocolId</c> and <c>Counter</c> that are NOT the
        /// checksum, which is what a caller does today. Large node numbers are used because that is
        /// where a fabricated value and a real one diverge: with both nodes under 256 the sum's high
        /// half contributes nothing and the two can agree by accident.
        /// </remarks>
        [Fact]
        public void ATadBuiltFrameCarriesTheCarvedHeaderChecksum()
        {
            TadSession session = new TadSession(TadSessionRole.Server);

            TadFrameContext context = new TadFrameContext
            {
                DestinationNode = 19999,
                SourceNode = 103,
                DatagramSequence = 0x0042,
                FrameClass = 0x000E,
                ProtocolId = SintranProtocolId.Tad,
                Counter = 0x55,
                FrameFlags = 0x82,
                Role = 0x84,
                DestinationSystem = 100,
                DestinationPort = 0x044D,
                SourceSystem = 103,
                SourcePort = 0x0211,
                ControlService = 0x00080000,
            };

            XmsgFrame frame = session.BuildControlFrame(context, TadOp.Bdat, new byte[] { 0x41, 0x42 });

            SintranHeader header = frame.Header!;
            ushort carved = XmsgEnvelope.ComputeHeaderChecksum(
                (ushort)((header.Marker1 << 8) | header.Marker2),
                (ushort)((header.PacketType << 8) | (byte)header.Subtype),
                header.DestinationNode,
                header.SourceNode,
                header.Flags1,
                header.Flags2);

            _output.WriteLine($"word 6 on the frame : 0x{header.Checksum:X4}");
            _output.WriteLine($"carved checksum     : 0x{carved:X4}");

            Assert.Equal(carved, header.Checksum);
        }

        /// <summary>
        /// Every frame the RESPONDER builds carries a correct word 6, including for node 19999.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this is separate from the test above</b></para>
        /// <para>
        /// That one checks a hand-built context. This one checks the frames
        /// <see cref="TadTerminalResponder"/> actually puts on the wire, which is where word 6 is
        /// computed by <c>ComputeCounter</c> and <c>DeriveChannel</c> - members whose own
        /// documentation says they are SUPERSEDED and "reproduce the wire on the traffic they were
        /// fitted to and NOT in general".
        /// </para>
        /// <para>
        /// The traffic they were fitted to was nodes 100, 102 and 103 - all under 256. We answer as
        /// 19999, whose header words are nothing like the fitted set, so "works in general" is
        /// exactly the property that cannot be assumed. A wrong word 6 is a corrupt header checksum,
        /// and the peer drops the frame in silence - which looks identical to the reply never being
        /// sent.
        /// </para>
        /// <para>
        /// The connect below is the real one D100 sent us on 2026-08-17, taken off the wire.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryResponderFrameCarriesACorrectChecksumForOurNodeNumber()
        {
            // Captured live: D100 -> 19999, the *TADADM connect naming D19999.
            byte[] connectBytes = Convert.FromHexString(
                "2113000E4E1F0064000004008C5B210086E44E1F0000006406B1040000410012FF072A54414441444D00FE06443139393939");

            TadTerminalResponder responder =
                new TadTerminalResponder(19999, () => new DateTime(2026, 8, 17));

            IReadOnlyList<XmsgFrame> frames = responder.OnConnect(XmsgFrame.Parse(connectBytes));
            Assert.NotEmpty(frames);

            for (int i = 0; i < frames.Count; i++)
            {
                SintranHeader header = frames[i].Header!;
                ushort carved = XmsgEnvelope.ComputeHeaderChecksum(
                    (ushort)((header.Marker1 << 8) | header.Marker2),
                    (ushort)((header.PacketType << 8) | (byte)header.Subtype),
                    header.DestinationNode,
                    header.SourceNode,
                    header.Flags1,
                    header.Flags2);

                _output.WriteLine(
                    $"frame {i}: word 6 = 0x{header.Checksum:X4}, carved = 0x{carved:X4}");

                Assert.True(
                    carved == header.Checksum,
                    $"Responder frame {i} carries word 6 0x{header.Checksum:X4} but the carved "
                    + $"checksum over its header is 0x{carved:X4}. A wrong word 6 is a corrupt "
                    + "header and the peer drops the frame in silence.");
            }
        }

        /// <summary>
        /// Every TAD opcode in the registry has the value the enum gives it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Lives in this project rather than beside the other registry checks because
        /// <see cref="TadOp"/> is here, and reaching across for it would make the protocol tests
        /// depend on the node layer.
        /// </para>
        /// <para>
        /// The registry marks some opcodes MEASURED and others INFERRED - seen on the wire, versus
        /// only read from ND's sources. This test does not judge that distinction; it holds the
        /// VALUES to the enum so the file and the code cannot drift apart.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryTadOpcodeInTheRegistryMatchesTheEnum()
        {
            using JsonDocument doc = ProtocolRegistry.Load("tad-wire.json");
            JsonElement values = doc.RootElement.GetProperty("operations").GetProperty("values");

            int compared = 0;
            foreach (JsonElement op in values.EnumerateArray())
            {
                string name = op.GetProperty("name").GetString()!;
                int declared = (int)ProtocolRegistry.ParseHex(op.GetProperty("value").GetString()!);

                Assert.True(
                    Enum.TryParse(name, out TadOp parsed),
                    "The registry names a TAD opcode the code does not have: " + name);
                Assert.Equal(declared, (int)parsed);
                compared++;
            }

            _output.WriteLine($"{compared} TAD opcodes agree with the enum");
            Assert.True(compared >= 14, "The registry should describe every opcode the enum has.");
        }

        /// <summary>
        /// The TAD registry holds every status claim to the evidence rule.
        /// </summary>
        /// <remarks>
        /// Stated per registry file on purpose: a second registry that quietly skipped the check
        /// would defeat the point of having the rule at all.
        /// </remarks>
        [Fact]
        public void TheTadRegistryNamesItsEvidence()
        {
            using JsonDocument doc = ProtocolRegistry.Load("tad-wire.json");
            IReadOnlyList<string> offenders = ProtocolRegistry.FindClaimsWithoutEvidence(doc.RootElement);

            if (offenders.Count > 0)
            {
                Assert.Fail(
                    "These carry a status with no evidence behind it:" + Environment.NewLine
                    + string.Join(Environment.NewLine, offenders)
                    + Environment.NewLine
                    + "A status without evidence is an opinion.");
            }
        }
    }
}