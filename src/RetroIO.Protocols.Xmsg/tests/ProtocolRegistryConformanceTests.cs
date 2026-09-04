using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Packet;
using RetroIO.Protocols.Xmsg.Qform;
using RetroIO.Protocols.Xmsg.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// The protocol registry and the code say the same thing.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this test is the whole point of the registry</b></para>
    /// <para>
    /// A JSON file describing the wire is worth nothing on its own - it becomes another document
    /// that drifts, which is the problem it was written to solve. This test is what makes it
    /// authoritative: change the layout in C# without changing <c>DOC/protocols/sintran-wire.json</c>
    /// and the build fails, and vice versa.
    /// </para>
    /// <para><b>What it checks, and what it deliberately does not</b></para>
    /// <para>
    /// It checks the things a machine can check: offsets, widths, masks and named values. It cannot
    /// check that a MEASURED status is honest - that is what the evidence pointer is for, and a
    /// human reading it. What it CAN do is refuse a MEASURED claim with no evidence behind it, which
    /// is the cheapest guard against "we think" quietly becoming "it is".
    /// </para>
    /// </remarks>
    public sealed class ProtocolRegistryConformanceTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the fixture.
        /// </summary>
        /// <param name="output">
        /// xunit's output sink.
        /// </param>
        public ProtocolRegistryConformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }


        /// <summary>
        /// The header size and word count in the registry match the code.
        /// </summary>
        [Fact]
        public void TheHeaderShapeMatchesTheCode()
        {
            using JsonDocument doc = ProtocolRegistry.Load("sintran-wire.json");
            JsonElement header = doc.RootElement
                .GetProperty("structures").GetProperty("sintran_header");

            Assert.Equal(SintranHeader.Size, header.GetProperty("size_bytes").GetInt32());
            Assert.Equal(SintranHeader.Size / 2, header.GetProperty("words").GetInt32());
        }

        /// <summary>
        /// Every field's byte offset is its word number times two, and they are in order.
        /// </summary>
        /// <remarks>
        /// Cheap, but it catches the copy-paste that leaves two fields claiming one offset - which
        /// is exactly the kind of quiet wrongness a prose table hides.
        /// </remarks>
        [Fact]
        public void EveryFieldOffsetAgreesWithItsWordNumber()
        {
            using JsonDocument doc = ProtocolRegistry.Load("sintran-wire.json");
            JsonElement fields = doc.RootElement
                .GetProperty("structures").GetProperty("sintran_header").GetProperty("fields");

            int expectedWord = 0;
            foreach (JsonElement field in fields.EnumerateArray())
            {
                int word = field.GetProperty("word").GetInt32();
                int offset = field.GetProperty("byte_offset").GetInt32();

                Assert.Equal(expectedWord, word);
                Assert.Equal(word * 2, offset);
                expectedWord++;
            }

            Assert.Equal(SintranHeader.Size / 2, expectedWord);
        }

        /// <summary>
        /// The checksum rule in the registry is the one the code implements.
        /// </summary>
        /// <remarks>
        /// Word 6 is the field that was re-derived wrongly, so it gets its own check: the registry
        /// states the rule, and the rule is exercised against the code on a real captured header.
        /// </remarks>
        [Fact]
        public void TheChecksumRuleHoldsOnARealCapturedHeader()
        {
            using JsonDocument doc = ProtocolRegistry.Load("sintran-wire.json");
            JsonElement checksumField = doc.RootElement
                .GetProperty("structures").GetProperty("sintran_header")
                .GetProperty("fields")[6];

            Assert.Equal("checksum", checksumField.GetProperty("name").GetString());
            Assert.Equal("MEASURED", checksumField.GetProperty("status").GetString());

            // D100's accept to 102, off the wire: words 0..5 then the checksum it carried.
            ushort computed = XmsgEnvelope.ComputeHeaderChecksum(
                0x2113, 0x000E, 0x0066, 0x0064, 0x0021, 0x0400);

            _output.WriteLine($"rule     : {checksumField.GetProperty("rule").GetString()}");
            _output.WriteLine($"computed : 0x{computed:X4}, on the wire 0xD9F3");

            Assert.Equal((ushort)0xD9F3, computed);
        }

        /// <summary>
        /// Every bit named in the registry has the mask the code gives it.
        /// </summary>
        /// <remarks>
        /// The reason the registry exists in JSON rather than as prose or XML comments: a bitfield
        /// is where prose fails, and a per-bit mask is something a test can hold the code to.
        /// </remarks>
        [Fact]
        public void EveryFrameFlagBitHasTheMaskTheCodeGivesIt()
        {
            using JsonDocument doc = ProtocolRegistry.Load("sintran-wire.json");
            JsonElement bits = doc.RootElement
                .GetProperty("bitfields").GetProperty("xmsg_frame_flags").GetProperty("bits");

            Dictionary<string, int> fromCode = new Dictionary<string, int>
            {
                { "Marker01", (int)XmsgFrameFlags.Marker01 },
                { "Letter", (int)XmsgFrameFlags.Letter },
                { "DataPhase", (int)XmsgFrameFlags.DataPhase },
                { "SystemMode", (int)XmsgFrameFlags.SystemMode },
            };

            int seen = 0;
            foreach (JsonElement bit in bits.EnumerateArray())
            {
                string name = bit.GetProperty("name").GetString()!;
                int position = bit.GetProperty("bit").GetInt32();
                int mask = (int)ProtocolRegistry.ParseHex(bit.GetProperty("mask").GetString()!);

                Assert.Equal(1 << position, mask);
                Assert.True(fromCode.ContainsKey(name), "The registry names a bit the code does not have: " + name);
                Assert.Equal(fromCode[name], mask);
                seen++;
            }

            Assert.Equal(fromCode.Count, seen);
        }

        /// <summary>
        /// The named combinations are exactly the OR of the bits they list.
        /// </summary>
        /// <remarks>
        /// Frames are built from whole captured combos rather than composed bit by bit, precisely
        /// because two of the per-bit rules are UNKNOWN. This keeps the recorded combos honest about
        /// which bits they contain.
        /// </remarks>
        [Fact]
        public void EveryComboIsTheOrOfItsNamedBits()
        {
            using JsonDocument doc = ProtocolRegistry.Load("sintran-wire.json");
            JsonElement flags = doc.RootElement
                .GetProperty("bitfields").GetProperty("xmsg_frame_flags");

            Dictionary<string, int> masks = new Dictionary<string, int>();
            foreach (JsonElement bit in flags.GetProperty("bits").EnumerateArray())
            {
                masks[bit.GetProperty("name").GetString()!] = (int)ProtocolRegistry.ParseHex(bit.GetProperty("mask").GetString()!);
            }

            foreach (JsonElement combo in flags.GetProperty("combos").EnumerateArray())
            {
                int expected = 0;
                foreach (JsonElement member in combo.GetProperty("bits").EnumerateArray())
                {
                    expected |= masks[member.GetString()!];
                }

                int declared = (int)ProtocolRegistry.ParseHex(combo.GetProperty("value").GetString()!);
                Assert.Equal(expected, declared);
            }

            // And the combos the code exposes carry the same values.
            Assert.Equal(0x86, (int)XmsgFrameFlags.Setup);
            Assert.Equal(0x82, (int)XmsgFrameFlags.ControlBare);
        }

        /// <summary>
        /// A MEASURED claim must name its evidence.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The one rule that stops the registry rotting into the documents it replaces. MEASURED
        /// means somebody watched it happen; without a pointer to WHERE, it is just confidence, and
        /// confidence is what turned a fitted model into a "fact" that survived for months.
        /// </para>
        /// <para>
        /// UNKNOWN and INFERRED rows are held to the same rule - an INFERRED claim has to say what
        /// it was inferred from, and an UNKNOWN one has to say how we know we do not know.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryStatusClaimNamesItsEvidence()
        {
            using JsonDocument doc = ProtocolRegistry.Load("sintran-wire.json");
            IReadOnlyList<string> offenders = ProtocolRegistry.FindClaimsWithoutEvidence(doc.RootElement);

            if (offenders.Count > 0)
            {
                Assert.Fail(
                    "These carry a status with no evidence behind it:" + Environment.NewLine
                    + string.Join(Environment.NewLine, offenders)
                    + Environment.NewLine
                    + "A status without evidence is an opinion. Name the capture, the manual page or the carve.");
            }
        }



        /// <summary>
        /// The control-service words in the registry match the enum.
        /// </summary>
        /// <remarks>
        /// These identify what kind of exchange a frame belongs to, and one of them - XsletLetter -
        /// is written into every connect accept. A wrong value here would be invisible in review and
        /// obvious only on a machine.
        /// </remarks>
        [Fact]
        public void EveryControlServiceMatchesTheEnum()
        {
            using JsonDocument doc = ProtocolRegistry.Load("tad-wire.json");
            JsonElement values = doc.RootElement.GetProperty("control_services").GetProperty("values");

            foreach (JsonElement service in values.EnumerateArray())
            {
                string name = service.GetProperty("name").GetString()!;
                uint declared = (uint)ProtocolRegistry.ParseHex(service.GetProperty("value").GetString()!);

                Assert.True(
                    Enum.TryParse(name, out XmcsmService parsed),
                    "The registry names a control service the code does not have: " + name);
                Assert.Equal(declared, (uint)parsed);
            }
        }

        /// <summary>
        /// The TAD registry holds every status claim to the same evidence rule.
        /// </summary>
        /// <remarks>
        /// The rule is per-registry rather than per-file by accident of how they are loaded, so it
        /// is stated again here: a second registry that quietly skipped the check would defeat the
        /// point of having one.
        /// </remarks>
        [Fact]
        public void TheTadRegistryAlsoNamesItsEvidence()
        {
            using JsonDocument doc = ProtocolRegistry.Load("tad-wire.json");
            IReadOnlyList<string> offenders = ProtocolRegistry.FindClaimsWithoutEvidence(doc.RootElement);

            if (offenders.Count > 0)
            {
                Assert.Fail(
                    "These carry a status with no evidence behind it:" + Environment.NewLine
                    + string.Join(Environment.NewLine, offenders));
            }
        }


        /// <summary>
        /// Every FA operation, message type and QFORM class in the registry matches the code.
        /// </summary>
        /// <remarks>
        /// FA is the protocol behind the three tasks that are actually proved against a machine -
        /// the pull, the create and the sync daemon - so most of its registry is MEASURED. That is
        /// exactly why it needs holding to the code: a value that is right today and quietly edited
        /// tomorrow would break a path we have evidence for.
        /// </remarks>
        [Fact]
        public void EveryFaValueMatchesTheCode()
        {
            using JsonDocument doc = ProtocolRegistry.Load("fa-qform.json");

            int compared = 0;

            foreach (JsonElement op in doc.RootElement.GetProperty("operations").GetProperty("values").EnumerateArray())
            {
                string name = op.GetProperty("name").GetString()!;
                int declared = (int)ProtocolRegistry.ParseHex(op.GetProperty("value").GetString()!);
                Assert.True(Enum.TryParse(name, out FaOperation parsedOp),
                    "The registry names an FA operation the code does not have: " + name);
                Assert.Equal(declared, (int)parsedOp);
                compared++;
            }

            foreach (JsonElement type in doc.RootElement.GetProperty("message_types").EnumerateArray())
            {
                string name = type.GetProperty("name").GetString()!;
                int declared = (int)ProtocolRegistry.ParseHex(type.GetProperty("value").GetString()!);
                Assert.True(Enum.TryParse(name, out FaMessageType parsedType),
                    "The registry names an FA message type the code does not have: " + name);
                Assert.Equal(declared, (int)parsedType);
                compared++;
            }

            foreach (JsonElement cls in doc.RootElement.GetProperty("qform").GetProperty("classes").EnumerateArray())
            {
                string name = cls.GetProperty("name").GetString()!;
                int declared = cls.GetProperty("value").GetInt32();
                Assert.True(Enum.TryParse(name, out QformClass parsedClass),
                    "The registry names a QFORM class the code does not have: " + name);
                Assert.Equal(declared, (int)parsedClass);
                compared++;
            }

            _output.WriteLine($"{compared} FA/QFORM values agree with the code");
            Assert.True(compared >= 25);
        }

        /// <summary>
        /// The FA prefix offsets and transfer lengths in the registry match the codecs.
        /// </summary>
        /// <remarks>
        /// The eight-byte prefix and the 1032-byte data message are what tell a data message from a
        /// QFORM one. Getting either wrong makes the server parse file content as tagged fields,
        /// which is how a transfer fails in a way that looks like a protocol fault.
        /// </remarks>
        [Fact]
        public void TheFaPrefixAndTransferLengthsMatchTheCodecs()
        {
            using JsonDocument doc = ProtocolRegistry.Load("fa-qform.json");

            JsonElement prefix = doc.RootElement.GetProperty("message_prefix");
            Dictionary<string, int> offsets = new Dictionary<string, int>
            {
                { "message_type", FaExchangeCodec.MessageTypeOffset },
                { "conversation", FaExchangeCodec.ConversationOffset },
                { "session_header", FaExchangeCodec.SessionHeaderOffset },
                { "qform_start", FaExchangeCodec.QformOffset },
            };

            foreach (JsonElement field in prefix.GetProperty("fields").EnumerateArray())
            {
                string name = field.GetProperty("name").GetString()!;
                Assert.True(offsets.ContainsKey(name), "Unexpected prefix field in the registry: " + name);
                Assert.Equal(offsets[name], field.GetProperty("byte_offset").GetInt32());
            }

            Assert.Equal(FaExchangeCodec.MinimumBodyLength, prefix.GetProperty("minimum_body_length").GetInt32());

            JsonElement transfer = doc.RootElement.GetProperty("data_transfer");
            Assert.Equal(FaFileDataCodec.BlockLength, transfer.GetProperty("block_length").GetInt32());
            Assert.Equal(FaFileDataCodec.BlocksPerRead, transfer.GetProperty("blocks_per_read").GetInt32());
            Assert.Equal(FaFileDataCodec.DataMessageLength, transfer.GetProperty("data_message_length").GetInt32());
        }

        /// <summary>
        /// The FA registry holds every status claim to the evidence rule.
        /// </summary>
        [Fact]
        public void TheFaRegistryNamesItsEvidence()
        {
            using JsonDocument doc = ProtocolRegistry.Load("fa-qform.json");
            IReadOnlyList<string> offenders = ProtocolRegistry.FindClaimsWithoutEvidence(doc.RootElement);

            if (offenders.Count > 0)
            {
                Assert.Fail(
                    "These carry a status with no evidence behind it:" + Environment.NewLine
                    + string.Join(Environment.NewLine, offenders));
            }
        }

        /// <summary>
        /// Every XROUT service, connection type and error in the registry matches the code.
        /// </summary>
        /// <remarks>
        /// Service codes are DECIMAL in the registry and OCTAL in the ND manuals - the manual's
        /// XSRME=106 is 70. That is a silent error waiting to happen, so the values are held to the
        /// enum rather than to anybody's reading of the table.
        /// </remarks>
        [Fact]
        public void EveryXroutValueMatchesTheCode()
        {
            using JsonDocument doc = ProtocolRegistry.Load("xrout-services.json");

            int compared = 0;

            foreach (JsonElement service in doc.RootElement.GetProperty("services").GetProperty("values").EnumerateArray())
            {
                string name = service.GetProperty("name").GetString()!;
                int declared = service.GetProperty("value").GetInt32();
                Assert.True(Enum.TryParse(name, out XroutService parsed),
                    "The registry names an XROUT service the code does not have: " + name);
                Assert.Equal(declared, (int)parsed);
                compared++;
            }

            foreach (JsonElement type in doc.RootElement.GetProperty("connection_types").GetProperty("values").EnumerateArray())
            {
                string name = type.GetProperty("name").GetString()!;
                int declared = type.GetProperty("value").GetInt32();
                Assert.True(Enum.TryParse(name, out XroutConnectionType parsedType),
                    "The registry names a connection type the code does not have: " + name);
                Assert.Equal(declared, (int)parsedType);
                compared++;
            }

            foreach (JsonElement error in doc.RootElement.GetProperty("errors").GetProperty("values").EnumerateArray())
            {
                string name = error.GetProperty("name").GetString()!;
                int declared = error.GetProperty("value").GetInt32();
                Assert.True(Enum.TryParse(name, out XroutError parsedError),
                    "The registry names an XROUT error the code does not have: " + name);
                Assert.Equal(declared, (int)parsedError);
                compared++;
            }

            _output.WriteLine($"{compared} XROUT values agree with the code");
            Assert.True(compared >= 25);
        }

        /// <summary>
        /// Every service the registry calls an alias really does share its partner's value.
        /// </summary>
        /// <remarks>
        /// <para>
        /// XSDMC and XSDSY are one code with two names, and so are XSGMC and XSGSY. That is the same
        /// shape as XFHIP and XFRRO sharing a bit, which caused a real defect when prose lost it -
        /// so the registry states the alias and this test proves the claim rather than trusting it.
        /// </para>
        /// <para>
        /// A wrong alias claim would be worse than none: it would license treating two DIFFERENT
        /// services as interchangeable.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryClaimedAliasSharesItsPartnersValue()
        {
            using JsonDocument doc = ProtocolRegistry.Load("xrout-services.json");
            JsonElement values = doc.RootElement.GetProperty("services").GetProperty("values");

            Dictionary<string, int> byName = new Dictionary<string, int>();
            foreach (JsonElement service in values.EnumerateArray())
            {
                byName[service.GetProperty("name").GetString()!] = service.GetProperty("value").GetInt32();
            }

            int aliases = 0;
            foreach (JsonElement service in values.EnumerateArray())
            {
                if (!service.TryGetProperty("alias_of", out JsonElement aliasOf))
                {
                    continue;
                }

                string name = service.GetProperty("name").GetString()!;
                string partner = aliasOf.GetString()!;

                Assert.True(byName.ContainsKey(partner),
                    name + " claims to alias " + partner + ", which the registry does not list");
                Assert.True(byName[name] == byName[partner],
                    name + " claims to alias " + partner + " but they hold different values: "
                    + byName[name] + " and " + byName[partner]);
                aliases++;
            }

            _output.WriteLine($"{aliases} alias claims verified");
            Assert.True(aliases >= 4, "XSDMC/XSDSY and XSGMC/XSGSY should all be marked.");
        }

        /// <summary>
        /// The XROUT registry holds every status claim to the evidence rule.
        /// </summary>
        [Fact]
        public void TheXroutRegistryNamesItsEvidence()
        {
            using JsonDocument doc = ProtocolRegistry.Load("xrout-services.json");
            IReadOnlyList<string> offenders = ProtocolRegistry.FindClaimsWithoutEvidence(doc.RootElement);

            if (offenders.Count > 0)
            {
                Assert.Fail(
                    "These carry a status with no evidence behind it:" + Environment.NewLine
                    + string.Join(Environment.NewLine, offenders));
            }
        }
    }
}