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
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Ndfs;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Servers.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The file server serves one folder per SINTRAN user, takes the user from the reserve, and
    /// never shows a <c>.xat</c> metadata file (Ronny, 2026-11-22).
    /// </summary>
    public sealed class FaPerUserFolderTests : IDisposable
    {
        /// <summary>
        /// The FA body of the reserve D102 sent for <c>LIST-FILES d19999(ronny).</c>, from the hub
        /// capture of 2026-11-22, byte for byte from the message type to the end-of-list selector.
        /// The asker is <c>BAK02  SYSTEM</c> and the user slot holds <c>RONNY''</c>.
        /// </summary>
        private const string CapturedRonnyReserve =
            "07F000C280000001"
            + "920002920001F20001A207D0F200028C06920001920001F20003BD42414B3032202053595354454D"
            + "F200048C38B010524F4E4E592727000000000000000000E180"
            + "B01000000000000000000000000000000000B01000000000000000000000000000000000F200FF";

        private readonly ITestOutputHelper _output;
        private readonly string _root;

        /// <summary>
        /// Makes a root with two users, SYSTEM and RONNY, each with one file.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaPerUserFolderTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "fa-per-user-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "SYSTEM"));
            Directory.CreateDirectory(Path.Combine(_root, "Ronny"));
            File.WriteAllText(Path.Combine(_root, "SYSTEM", "SYSFILE.SYMB"), "system");
            File.WriteAllText(Path.Combine(_root, "Ronny", "LETTER.SYMB"), "dear ronny");
        }

        /// <summary>
        /// Removes the root.
        /// </summary>
        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Fact]
        public void TheCapturedReserveNamesRonny_TheNameEndsAtTheFirstApostrophe()
        {
            byte[] body = Convert.FromHexString(CapturedRonnyReserve);

            Assert.True(FaWriteRequests.TryReadReserveUser(
                new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset),
                out string user, out ushort password));

            Assert.Equal("RONNY", user);
            Assert.Equal(0, password);
        }

        [Fact]
        public void TheBuiltReserveReadsBack_WithItsPasswordWord()
        {
            byte[] fields = FaWriteRequests.ReserveFileEntry("BAK04", "SYSTEM", "SECRET", 0x6D2A);
            byte[] body = FaTestClient.BuildRequestEnvelope(FaOperation.ReserveFileEntry, 1, fields);

            Assert.True(FaWriteRequests.TryReadReserveUser(
                new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset),
                out string user, out ushort password));

            Assert.Equal("SECRET", user);
            Assert.Equal(0x6D2A, password);
        }

        [Fact]
        public void AUserIsAFolder_MatchedWithoutRegardToCase()
        {
            FaUserFolders folders = new FaUserFolders(_root);

            IFaFileStore? ronny = folders.ForUser("RONNY");
            Assert.NotNull(ronny);
            Assert.Single(ronny!.ListFiles());
            Assert.Equal("LETTER", ronny.ListFiles()[0].Name.Name);

            Assert.Same(ronny, folders.ForUser("ronny"));
            Assert.Null(folders.ForUser("NOUSR"));

            IReadOnlyList<string> users = folders.ListUsers();
            Assert.Equal(2, users.Count);
        }

        [Theory]
        [InlineData("..")]
        [InlineData("A/B")]
        [InlineData("A\\B")]
        [InlineData("")]
        [InlineData("SIXTEEN-LETTERSX")]
        public void ANameThatCannotBeAUser_IsRefusedNotCleaned(string name)
        {
            Directory.CreateDirectory(Path.Combine(_root, "A"));
            FaUserFolders folders = new FaUserFolders(_root);

            Assert.False(FaUserFolders.IsValidUserName(name));
            Assert.Null(folders.ForUser(name));
        }

        [Fact]
        public void AMetadataFile_IsNeverListedNorOpened()
        {
            File.WriteAllText(Path.Combine(_root, "Ronny", "LETTER.SYMB.xat"), "{}");
            File.WriteAllText(Path.Combine(_root, "Ronny", "OLD.xat.bad"), "x");
            FaUserFolders folders = new FaUserFolders(_root);
            IFaFileStore ronny = folders.ForUser("RONNY")!;

            Assert.Single(ronny.ListFiles());
            Assert.NotEqual(FaStoreStatus.Ok, ronny.Open("OLD:XAT", false, out _));
        }

        [Fact]
        public void TheListingWalksTheReservedUsersFolder_AndAnswersThatUser()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            host.Route(FaTestClient.BuildReserve("RONNY"));

            IReadOnlyList<XmsgFrame> walk = host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildListingRequest(1, FaListFilesCodec.FirstEntryCursor), 0x0002));
            string all = Hex(walk);
            Assert.Contains(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("LETTER")), all);
            Assert.DoesNotContain(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("SYSFILE")), all);

            IReadOnlyList<XmsgFrame> userEntry = host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildFunctionRequest(2, FaSpecialFunction.UserEntry), 0x0004));
            Assert.Contains(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("RONNY'")), Hex(userEntry));
        }

        [Fact]
        public void AnUnknownUser_IsRefusedWithError39_OnTheOpen()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            host.Route(FaTestClient.BuildReserve("NOUSR"));

            IReadOnlyList<XmsgFrame> open = host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildRequestEnvelope(FaOperation.OpenFile, 1, FaReadRequests.OpenFile("LETTER:SYMB")), 0x0002));

            // F2 0001 A2 0027: selector 1 carrying SINTRAN error 39, as D100 answered (NOUSR)A:T.
            Assert.Contains("F20001A20027", Hex(open));
        }

        [Fact]
        public void AFileNameNamingAnotherUser_UsesThatUsersFolder()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            host.Route(FaTestClient.BuildReserve("SYSTEM"));

            IReadOnlyList<XmsgFrame> other = host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildRequestEnvelope(FaOperation.OpenFile, 1, FaReadRequests.OpenFile("(RONNY)LETTER:SYMB")), 0x0002));
            Assert.DoesNotContain("F20001A2", Hex(other));

            IReadOnlyList<XmsgFrame> missing = host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildRequestEnvelope(FaOperation.OpenFile, 2, FaReadRequests.OpenFile("(NOUSR)A:T")), 0x0004));
            Assert.Contains("F20001A20027", Hex(missing));
        }

        /// <summary>
        /// The FA body D102 sent for its second listing on one conversation, frame 90 of
        /// <c>DOC/captures/2026-11-22-fa-two-listings-change-user-d102.pcap</c>: operation 4 naming
        /// <c>SYSTEM</c>.
        /// </summary>
        private const string CapturedChangeToSystem =
            "07F000828500D77E"
            + "920004920006F200018C38B01053595354454D27000000000000000000E180"
            + "B01000000000000000000000000000000000B01000000000000000000000000000000000F200FF";

        [Fact]
        public void TheCapturedChangeOfUser_MovesTheConversation_SoTheSecondListingShowsTheNewUser()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            host.Route(FaTestClient.BuildReserve("RONNY"));

            IReadOnlyList<XmsgFrame> change = host.Route(FaTestClient.BuildSessionFrame(
                Convert.FromHexString(CapturedChangeToSystem), 0x0002));
            Assert.DoesNotContain("F20001A2", Hex(change));

            IReadOnlyList<XmsgFrame> walk = host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildListingRequest(7, FaListFilesCodec.FirstEntryCursor), 0x0004));
            string all = Hex(walk);
            Assert.Contains(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("SYSFILE")), all);
            Assert.DoesNotContain(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("LETTER")), all);
        }

        private XmsgServerHost BuildHost()
        {
            FaServer server = new FaServer(new FaUserFolders(_root));
            server.Log += line => _output.WriteLine(line);
            XmsgServerHost host = new XmsgServerHost(FaTestClient.ServerNode);
            host.Register(server);
            return host;
        }

        private static string Hex(IReadOnlyList<XmsgFrame> frames)
        {
            System.Text.StringBuilder all = new System.Text.StringBuilder();
            for (int i = 0; i < frames.Count; i++)
            {
                all.Append(Convert.ToHexString(frames[i].ToArray()));
            }

            return all.ToString();
        }
    }
}
