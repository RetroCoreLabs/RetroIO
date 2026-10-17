//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Ndfs;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Packet;
using RetroIO.Protocols.Xmsg.Servers.Fa;
using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Pins the COSMOS directory listing to the exact bytes that a real SINTRAN III machine accepted,
    /// so that a later change to the frame layer cannot break it quietly.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a golden byte test, when the wiring tests already pass</b></para>
    /// On 2026-08-05 D100 listed our <c>*FA-SERVER</c> and printed the files - the first time a real
    /// client ever accepted this path. Every behavioural test in <see cref="FaServerWiringTests"/>
    /// passed BEFORE that worked too, because they check meanings rather than bytes: the record's
    /// name, the walk position, the refusal code. What actually made the difference was a mixture of
    /// tag bytes, field widths, word alignment and frame sequencing that no meaning-level assertion
    /// touches.
    /// <para><b>What this file therefore does</b></para>
    /// It drives one whole listing - connect, first file, directory, user, second file, end of walk -
    /// and compares every outgoing frame byte for byte against a recorded run. The comparison is
    /// deliberately dumb. It cannot tell a deliberate improvement from a regression, and it is not
    /// meant to: it is meant to make either one impossible to do by accident.
    /// <para><b>When a golden legitimately changes</b></para>
    /// Update <see cref="ExpectedFrames"/> and say in the commit message what changed on the wire and
    /// why. A silent golden update is the one thing that makes this file worthless.
    /// <para><b>Determinism</b></para>
    /// The record carries the file's length and modification time, so the served files are written
    /// with fixed content and a fixed timestamp. Everything else - names, walk order, ports, the
    /// connection counter - is already fixed by the server or by <see cref="FaTestClient"/>.
    /// </remarks>
    public sealed class FaListingRegressionTests : IDisposable
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// The temporary folder the server serves during a test.
        /// </summary>
        private readonly string _folder;

        /// <summary>
        /// The modification time stamped on every served file.
        /// </summary>
        /// <remarks>
        /// Fixed so the packed ND date in the record is fixed. The year is deliberately a present-day
        /// one, so the 64-year fold in <c>FaFolderEntry.ToListingDate</c> is exercised rather than
        /// side-stepped - that fold is what stopped every record carrying a zero creation date, which
        /// no real record in the capture ever does.
        /// </remarks>
        private static readonly DateTime FixedModified =
            new DateTime(2026, 8, 5, 11, 22, 33, DateTimeKind.Utc);

        /// <summary>
        /// The first served file: name, then contents.
        /// </summary>
        private const string FirstFileName = "ALPHA.SYMB";

        /// <summary>
        /// The contents of the first served file.
        /// </summary>
        private const string FirstFileText = "one";

        /// <summary>
        /// The second served file.
        /// </summary>
        private const string SecondFileName = "BETA.DATA";

        /// <summary>
        /// The contents of the second served file.
        /// </summary>
        private const string SecondFileText = "two two";

        /// <summary>
        /// Where a file-access message body starts inside a serialised datagram.
        /// </summary>
        private const int BodyOffset = SintranHeader.Size + XmsgSubHeader.Size;

        /// <summary>
        /// The largest information field the LAPB layer will accept on receive.
        /// </summary>
        /// <remarks>
        /// <c>LapbLayer.MaxInformationLength</c>. A frame longer than this is answered with an FRMR
        /// rather than delivered, which is the whole reason the fragment subtypes
        /// <c>0x0A</c> / <c>0x0C</c> exist. Repeated here rather than referenced because
        /// <c>RetroIO.Protocols.Xmsg.Live</c> is not on this test project's reference list.
        /// </remarks>
        private const int MaxInformationLength = 312;

        /// <summary>
        /// The recorded bytes of every frame the server emits during one whole listing, in order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Recorded 2026-08-05 from the code that D100 accepted, then RE-RECORDED the same day when
        /// header word 6 was corrected to the carved checksum.
        /// </para>
        /// <para>
        /// That update is exactly the deliberate kind the class remarks call for, and its diff is
        /// worth knowing: <b>only bytes 12-13 of each frame moved</b> - <c>DE53</c> became
        /// <c>9053</c>, <c>DDF7</c> became <c>8FF7</c>, and so on down the list. Every other byte on
        /// every frame is unchanged, which is the evidence that the fix touched word 6 and nothing
        /// else.
        /// </para>
        /// </remarks>
        // RE-RECORDED 2026-08-06, deliberately. Four bytes moved and nothing else: byte 32 of each
        // REPLY frame, the session-header byte, from a constant 0x80 to 0x80, 0x81, 0x82, 0x83, 0x84.
        //
        // The server used to ECHO that byte from the request. FaTestClient sends the same 0x80 on
        // every request, so every reply here carried 0x80 and these goldens recorded it - stable, and
        // agreeing with nothing on the wire. The real server keeps ONE counter across every message
        // it sends; capture-read.txt shows a read running reply 8A, data 0B, data 8C. See
        // FaServerConversation.NextMessageCounter.
        //
        // Live effect of the old model: D100 took the first ReadFile of a COPY-FILE, received both
        // data messages and then stopped instead of asking for position 1.
        // RE-RECORDED 2026-08-08, deliberately, and this one was a LIVE BUG rather than a tidy-up.
        // Ten bytes moved: the conversation word in every reply and short acknowledgement, 0002 to
        // 0004. Nothing else - the header checksum covers only words 0 to 5, so a body change does
        // not touch it.
        //
        // The old value was the constant FaExchangeCodec.ResponderConversation. A real server puts
        // the word it ECHOED IN ITS CONNECTION CONFIRMATION on everything it sends afterwards, and
        // the confirmation on the very first line here echoes 0004 - so this golden had our server
        // contradicting itself, echoing 0004 and then answering 0002.
        //
        // Measured across four real ND-to-ND conversations in DOC/captures/ND-TO-ND-2026-08-08:
        //   confirm 07D2 0006 0004 -> replies 07F0 0006
        //   confirm 07D2 0008 003F -> replies 07F0 0008
        //   confirm 07D2 000C 0040 -> replies 07F0 000C
        //   confirm 07D2 0002 0004 -> replies 07F0 0002
        //
        // Live effect of the old constant: the client took the connection, sent its first request,
        // took our acknowledgement and our reply, then stopped dead with no error - five separate
        // runs on 2026-08-08. The one run that completed is the one whose letter happened to carry
        // 0002, where the constant was accidentally right. That coincidence is why this survived,
        // and why FaTestClient's letter carrying 0004 makes the test disagree now.
        private static readonly string[] ExpectedFrames =
        {
            // The connection confirmation: 07D2, the echoed letter word 0004, our connection
            // counter 0042, then the client's system byte 64.
            "2113000E00644E1F00000008905321008284006402F74E1F0211000807D2000400426400",

            // First file: the acknowledgement, then the 64-byte object entry for ALPHA:SYMB.
            "2113000E00644E1F00010008905221009600006402F74E1F0211000807A200040100922A",
            "2113000E00644E1F000200628FF721009600006402F74E1F0211006207F000048000908192000C920001F200028C4BA20000A20000A20001B0409000414C504841270000000000000000000053594D420000000004F7000800000000000000000000320AB5A100000000320AB5A10000000100000002400078DAF200FF00",

            // The directory entry: a 42-byte record naming PACK-ONE, under the same B0 tag as a
            // file record and told apart only by its length.
            "2113000E00644E1F00030008905021009600006402F74E1F0211000807A200040200922A",
            "2113000E00644E1F00040046901121009600006402F74E1F0211004607F000048100908192000C920002F200028C2FB02AD00002400000054000005041434B2D4F4E452700000000000000400048FC400048FE000048240000347EA20001F200FF00",

            // The user entry: a 16-byte name and no record at all.
            "2113000E00644E1F00050008904E21009600006402F74E1F0211000807A200040300922A",
            "2113000E00644E1F00060028902D21009600006402F74E1F0211002807F000048200908192000C920003F200028C12B01053595354454D27000000000000000000F200FF",

            // Second file: BETA:DATA, at walk ordinal 1.
            "2113000E00644E1F00070008904C21009600006402F74E1F0211000807A200040400922A",
            "2113000E00644E1F000800628FF121009600006402F74E1F0211006207F000048300908192000C920004F200028C4BA20000A20001A20001B040900042455441270000000000000000000000444154410001000104F7000800000000000100000000320AB5A100000000320AB5A10000000100000006400078DAF200FF00",

            // Past the end of the walk: selector 1 with SINTRAN error 0x00C5 = 197, "no more
            // files". A refusal is the presence of selector 1, not a flag inside it.
            "2113000E00644E1F00090008904A21009600006402F74E1F0211000807A200040500922A",
            "2113000E00644E1F000A0018903921009600006402F74E1F0211001807F000048400908192000C920005F20001A200C5F200FF00",
        };

        /// <summary>
        /// Creates the temporary folder and the two files served during a test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaListingRegressionTests(ITestOutputHelper output)
        {
            _output = output;
            _folder = Path.Combine(Path.GetTempPath(), "fa-listing-golden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);

            WriteServedFile(FirstFileName, FirstFileText);
            WriteServedFile(SecondFileName, SecondFileText);
        }

        /// <summary>
        /// Removes the temporary folder.
        /// </summary>
        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }

        /// <summary>
        /// The whole listing conversation is byte for byte what it was when a real client accepted it.
        /// </summary>
        [Fact]
        public void TheListingConversationIsByteForByteUnchanged()
        {
            IReadOnlyList<XmsgFrame> frames = RunListingConversation();

            _output.WriteLine("--- frames emitted: " + frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                _output.WriteLine("            \"" + Convert.ToHexString(frames[i].ToArray()) + "\",");
            }

            Assert.Equal(ExpectedFrames.Length, frames.Count);

            for (int i = 0; i < frames.Count; i++)
            {
                Assert.Equal(ExpectedFrames[i], Convert.ToHexString(frames[i].ToArray()));
            }
        }

        /// <summary>
        /// Every frame of a listing is an ordinary, unfragmented data frame that fits in one LAPB
        /// information field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the guard that matters when fragmentation is added for reading file CONTENTS.
        /// Serving a file's bytes needs the two fragment subtypes, and those travel through the same
        /// send path a listing does. If fragmentation ever leaks into a reply short enough not to
        /// need it, this fails here rather than on the wire.
        /// </para>
        /// <para>
        /// The four properties checked are all measured facts about the traffic D100 accepted:
        /// subtype <c>0x0E</c>, a sub-header present so the body starts at 28, Flags2 equal to this
        /// frame's own body length, and an EVEN body length (all 480 captured file-access data frames
        /// carry one).
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryFrameOfAListingIsOneUnfragmentedDataFrame()
        {
            IReadOnlyList<XmsgFrame> frames = RunListingConversation();
            Assert.NotEmpty(frames);

            for (int i = 0; i < frames.Count; i++)
            {
                XmsgFrame frame = frames[i];
                byte[] wire = frame.ToArray();

                Assert.Equal(SintranPacketSubtype.Data, frame.Header.Subtype);
                Assert.NotNull(frame.SubHeader);

                int bodyLength = wire.Length - BodyOffset;
                Assert.True(bodyLength > 0, "frame " + i + " carries no body");
                Assert.Equal(0, bodyLength % 2);
                Assert.Equal(bodyLength, frame.Header.Flags2);

                Assert.True(
                    wire.Length <= MaxInformationLength,
                    "frame " + i + " is " + wire.Length + " bytes, past the " + MaxInformationLength
                    + "-byte LAPB limit, so it would need fragmenting and a listing never should");
            }
        }

        /// <summary>
        /// Every frame the server emits carries the carved header checksum in word 6.
        /// </summary>
        /// <remarks>
        /// <para><b>CORRECTED 2026-08-05 - this test used to assert the opposite</b></para>
        /// It recorded a measured DEFECT: <c>XmsgServerHost</c> filled bytes 12-13 from the
        /// superseded seed / counter / channel arithmetic, and against node 19999 the value was
        /// high by exactly <c>0x4E00</c> on every frame:
        /// <code>
        /// wire 0xDE53   carved 0x9053
        /// wire 0xDDF7   carved 0x8FF7
        /// </code>
        /// The low byte was right, because the seed is learned from a received frame's real word 6
        /// and the arithmetic reproduces one by construction. The high byte was anchored at a
        /// constant <c>0xDE</c> - the complement of a sum whose node numbers are small. Every
        /// capture the model was fitted to ran between nodes 100, 102 and 103, all under 256; ours
        /// is 19999 = <c>0x4E1F</c>, so the gap was the SOURCE NODE's high byte.
        /// <para><b>The host now derives it, so this asserts agreement instead</b></para>
        /// Word 6 is a ones-complement checksum over words 0-5, carved from the kernel and verified
        /// on 3595 of 3595 captured frames across every subtype. There is one definition of it and
        /// this test requires every emitted frame to match it - including the fragment subtypes,
        /// which the same corpus covers 226 times each.
        /// <para>
        /// <see cref="ExpectedFrames"/> was re-recorded in the same commit, which is why those
        /// bytes changed. That is the deliberate golden update the class remarks call for.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryEmittedFrameCarriesTheCarvedHeaderChecksum()
        {
            IReadOnlyList<XmsgFrame> frames = RunListingConversation();
            Assert.NotEmpty(frames);

            for (int i = 0; i < frames.Count; i++)
            {
                SintranHeader header = frames[i].Header;
                ushort carved = XmsgEnvelope.ComputeHeaderChecksum(
                    (ushort)((header.Marker1 << 8) | header.Marker2),
                    (ushort)((header.PacketType << 8) | (byte)header.Subtype),
                    header.DestinationNode,
                    header.SourceNode,
                    header.Flags1,
                    header.Flags2);

                _output.WriteLine(
                    "frame " + i + ": word 6 = 0x" + header.Checksum.ToString("X4")
                    + ", carved = 0x" + carved.ToString("X4"));

                Assert.Equal(carved, header.Checksum);
            }
        }

        /// <summary>
        /// Drives one complete directory listing and returns every frame the server emitted, in order.
        /// </summary>
        /// <returns>
        /// The emitted frames.
        /// </returns>
        /// <remarks>
        /// The step order is the one D100 uses: the connect letter, the first file, the directory
        /// entry, the user entry, the next file, and one request past the end of the walk. The
        /// client's Flags 1 rises by two per exchange because each of ours is answered by an
        /// acknowledgement of its own.
        /// </remarks>
        private IReadOnlyList<XmsgFrame> RunListingConversation()
        {
            FaServer server = new FaServer(new FolderFileStore(_folder));
            server.Log += line => _output.WriteLine(line);

            XmsgServerHost host = new XmsgServerHost(FaTestClient.ServerNode);
            host.Register(server);

            List<XmsgFrame> emitted = new List<XmsgFrame>(16);

            Collect(emitted, host.Route(FaTestClient.BuildConnectLetter()));

            // 0xFFFF starts the walk and asks for the first file.
            Collect(emitted, host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildListingRequest(1, FaListFilesCodec.FirstEntryCursor), 0x0002)));

            // The two header entries a SINTRAN listing prints as "(PACK-ONE:SYSTEM)". Neither moves
            // the walk on - which is the defect that stopped D100 listing anything at all.
            Collect(emitted, host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildFunctionRequest(2, FaSpecialFunction.DirectoryEntry), 0x0004)));
            Collect(emitted, host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildFunctionRequest(3, FaSpecialFunction.UserEntry), 0x0006)));

            // The second file, then one request past the end.
            Collect(emitted, host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildListingRequest(4, 0), 0x0008)));
            Collect(emitted, host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildListingRequest(5, 0), 0x000A)));

            return emitted;
        }

        /// <summary>
        /// Appends one routing step's frames to the running list.
        /// </summary>
        /// <param name="emitted">
        /// The list being built.
        /// </param>
        /// <param name="frames">
        /// What the host produced for one inbound datagram.
        /// </param>
        private static void Collect(List<XmsgFrame> emitted, IReadOnlyList<XmsgFrame> frames)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                emitted.Add(frames[i]);
            }
        }

        /// <summary>
        /// Writes one served file with fixed contents and a fixed modification time.
        /// </summary>
        /// <param name="name">
        /// The file name inside the served folder.
        /// </param>
        /// <param name="text">
        /// The file's contents.
        /// </param>
        private void WriteServedFile(string name, string text)
        {
            string path = Path.Combine(_folder, name);
            File.WriteAllText(path, text);

            // The record carries the modification time, so a real "now" would make the golden bytes
            // change every run.
            File.SetLastWriteTimeUtc(path, FixedModified);
        }
    }
}
