//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

using RetroIO.Protocols.Xmsg.Ndfs;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Qform;
using RetroIO.Protocols.Xmsg.Servers.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Covers a listing that names ONE file - what <c>FILE-STATISTICS</c> sends - as distinct from
    /// <c>LIST-FILES</c>, which names none.
    /// </summary>
    /// <remarks>
    /// <para><b>What was wrong before 2026-08-05</b></para>
    /// The 62-byte spec block in the request was ignored completely, so a <c>FILE-STATISTICS</c> of
    /// one file walked the whole folder and reported every file in it.
    /// <para><b>How the block was read</b></para>
    /// By comparing a capture that names a file against two that do not - see
    /// <c>FaSpecBlockCrossUserTests.TheFileStatBlockNamesAFileWhereAPlainListingDoesNot</c>. Only the
    /// filespec is decoded; the rest of the block is still opaque and is not read.
    /// </remarks>
    public sealed class FaNamedFileListingTests : IDisposable
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// The temporary folder the server serves during a test.
        /// </summary>
        private readonly string _folder;

        /// <summary>
        /// Where a file-access message body starts inside a serialised datagram.
        /// </summary>
        private const int BodyOffset = SintranHeader.Size + XmsgSubHeader.Size;

        /// <summary>
        /// Creates the temporary folder and three files to pick from.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaNamedFileListingTests(ITestOutputHelper output)
        {
            _output = output;
            _folder = Path.Combine(Path.GetTempPath(), "fa-named-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);

            File.WriteAllText(Path.Combine(_folder, "ALPHA.SYMB"), "one");
            File.WriteAllText(Path.Combine(_folder, "BETA.DATA"), "two");
            File.WriteAllText(Path.Combine(_folder, "GAMMA.TEXT"), "three");
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
        /// A request naming one file returns that file and then the end of the directory.
        /// </summary>
        /// <remarks>
        /// The second half matters as much as the first: before this, the walk carried on into the
        /// rest of the folder. A named listing that returns the right file and then two more is
        /// still wrong.
        /// </remarks>
        [Fact]
        public void ARequestNamingOneFileReturnsOnlyThatFile()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());

            byte[] block = FaTestClient.BuildSpecBlock(FaServer.UserName, "BETA:DATA");

            byte[] first = BodyOf(ReplyTo(host, 1, FaListFilesCodec.FirstEntryCursor, block, 0x0002));
            Assert.Equal("BETA", NameOfEntry(first));

            // ...and the walk is over, rather than carrying on into the rest of the folder.
            byte[] second = BodyOf(ReplyTo(host, 2, 0, block, 0x0004));
            Assert.Equal(FaServerStatus.EndOfDirectory, StatusOf(second));
        }

        /// <summary>
        /// A request naming no file still walks the whole folder.
        /// </summary>
        /// <remarks>
        /// This is the <c>LIST-FILES</c> case, and the one a real machine has accepted. An empty
        /// filespec - the terminator straight after the user's closing bracket - must keep meaning
        /// "everything", or reading the block at all would have broken the only path that works.
        /// </remarks>
        [Fact]
        public void ARequestNamingNoFileStillWalksTheWholeFolder()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());

            byte[] block = FaTestClient.BuildSpecBlock(FaServer.UserName, string.Empty);

            Assert.Equal("ALPHA", NameOfEntry(BodyOf(ReplyTo(host, 1, FaListFilesCodec.FirstEntryCursor, block, 0x0002))));
            Assert.Equal("BETA", NameOfEntry(BodyOf(ReplyTo(host, 2, 0, block, 0x0004))));
            Assert.Equal("GAMMA", NameOfEntry(BodyOf(ReplyTo(host, 3, 0, block, 0x0006))));
        }

        /// <summary>
        /// Naming a file the folder does not hold is answered with the end of the directory, not
        /// with the wrong file.
        /// </summary>
        [Fact]
        public void NamingAFileWeDoNotHaveEndsTheWalkRatherThanSubstituting()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());

            byte[] block = FaTestClient.BuildSpecBlock(FaServer.UserName, "NOSUCH:FILE");

            byte[] body = BodyOf(ReplyTo(host, 1, FaListFilesCodec.FirstEntryCursor, block, 0x0002));
            Assert.Equal(FaServerStatus.EndOfDirectory, StatusOf(body));
        }

        /// <summary>
        /// A short file name does not match every specification the client could send.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect this stands for</b></para>
        /// The first version of the match asked whether the FILE's name appeared anywhere IN the
        /// specification. A file called <c>A</c> is a substring of nearly every specification there
        /// is, so it came back for all of them. The three files the other tests use are long enough
        /// that none of them collided, which is exactly why the bug would have survived.
        /// </remarks>
        [Fact]
        public void AShortFileNameDoesNotMatchEverySpecification()
        {
            File.WriteAllText(Path.Combine(_folder, "A.SYMB"), "short");

            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());

            byte[] block = FaTestClient.BuildSpecBlock(FaServer.UserName, "BETA:DATA");

            byte[] body = BodyOf(ReplyTo(host, 1, FaListFilesCodec.FirstEntryCursor, block, 0x0002));

            // BETA, and only BETA - "A" is a substring of "BETA:DATA" but it is not what was asked
            // for.
            Assert.Equal("BETA", NameOfEntry(body));
            Assert.Equal(
                FaServerStatus.EndOfDirectory,
                StatusOf(BodyOf(ReplyTo(host, 2, 0, block, 0x0004))));
        }

        /// <summary>
        /// A named type has to agree, while naming no type accepts any.
        /// </summary>
        [Fact]
        public void TheTypeIsComparedOnlyWhenTheClientGivesOne()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());

            // The right name and the WRONG type finds nothing...
            byte[] wrongType = FaTestClient.BuildSpecBlock(FaServer.UserName, "BETA:SYMB");
            Assert.Equal(
                FaServerStatus.EndOfDirectory,
                StatusOf(BodyOf(ReplyTo(host, 1, FaListFilesCodec.FirstEntryCursor, wrongType, 0x0002))));

            // ...and the name alone finds the file whatever its type.
            byte[] noType = FaTestClient.BuildSpecBlock(FaServer.UserName, "BETA");
            Assert.Equal(
                "BETA",
                NameOfEntry(BodyOf(ReplyTo(host, 2, FaListFilesCodec.FirstEntryCursor, noType, 0x0004))));
        }

        /// <summary>
        /// The captured spec blocks read back as the file names they carry.
        /// </summary>
        /// <remarks>
        /// The bytes are the real ones from the captures, printable characters shown in the class
        /// remarks. This is the test that would fail if the block layout were re-read differently.
        /// </remarks>
        [Theory]
        [InlineData("(SYSTEM)SINTRAN:DATA'SINTRAN:DATA'", "SINTRAN:DATA")]
        [InlineData("(SYSTEM)'EM).(SYSTEM)'7", "")]
        [InlineData("(SECRET)'ET(SECRET)).(SECRET)'", "")]
        public void TheCapturedBlocksReadBackAsTheirFileNames(string blockText, string expected)
        {
            byte[] block = new byte[FaListFilesCodec.SpecBlockLength];
            for (int i = 0; i < blockText.Length && i < block.Length; i++)
            {
                block[i] = (byte)blockText[i];
            }

            byte[] request = FaTestClient.BuildListingRequest(1, FaListFilesCodec.FirstEntryCursor, block);

            string named;
            Assert.True(FaListFilesCodec.TryReadRequestedFileName(
                new ReadOnlySpan<byte>(request, FaExchangeCodec.QformOffset, request.Length - FaExchangeCodec.QformOffset),
                out named));

            _output.WriteLine("'" + blockText + "' -> '" + named + "'");
            Assert.Equal(expected, named);
        }

        /// <summary>
        /// Builds the host with the file server registered over the test folder.
        /// </summary>
        /// <returns>
        /// The host, which is also the transport the server replies through.
        /// </returns>
        private XmsgServerHost BuildHost()
        {
            FaServer server = new FaServer(new FolderFileStore(_folder));
            server.Log += line => _output.WriteLine(line);

            XmsgServerHost host = new XmsgServerHost(FaTestClient.ServerNode);
            host.Register(server);
            return host;
        }

        /// <summary>
        /// Sends one listing request and returns the reply frame.
        /// </summary>
        /// <param name="host">
        /// The host to route through.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="cursor">
        /// The entry cursor.
        /// </param>
        /// <param name="specBlock">
        /// The spec block to carry.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence of the request.
        /// </param>
        /// <returns>
        /// The reply, which is the second of the two frames a request produces.
        /// </returns>
        private static XmsgFrame ReplyTo(
            XmsgServerHost host, ushort sequence, ushort cursor, byte[] specBlock, ushort flags1)
        {
            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildListingRequest(sequence, cursor, specBlock), flags1));

            Assert.Equal(2, frames.Count);
            return frames[1];
        }

        /// <summary>
        /// Reads the object name out of a directory-entry reply body.
        /// </summary>
        /// <param name="body">
        /// The reply body, starting at the message type.
        /// </param>
        /// <returns>
        /// The entry's object name.
        /// </returns>
        private static string NameOfEntry(byte[] body)
        {
            ushort serial;
            byte[] record;
            Assert.True(FaListFilesCodec.TryReadReply(
                new ReadOnlySpan<byte>(body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset),
                out serial, out record));

            RetroIO.Storage.Ndfs.Elements.ObjectEntry? entry = FaDirectoryListing.ParseRecord(record);
            Assert.NotNull(entry);
            return entry!.ObjectName;
        }

        /// <summary>
        /// Reads the refusal status out of a reply body.
        /// </summary>
        /// <param name="body">
        /// The reply's message body.
        /// </param>
        /// <returns>
        /// The status the server reported.
        /// </returns>
        private static FaServerStatus StatusOf(byte[] body)
        {
            int at = FaExchangeCodec.QformOffset + 6;
            Assert.True(body.Length >= at + 9, "the reply is too short to carry a status field");

            Assert.Equal((byte)QformTagByte.Selector, body[at]);
            Assert.Equal(0x01, body[at + 2]);
            Assert.Equal((byte)QformTagByte.TypedInteger, body[at + 3]);
            return (FaServerStatus)((body[at + 4] << 8) | body[at + 5]);
        }

        /// <summary>
        /// Extracts the file-access message body from a reply frame.
        /// </summary>
        /// <param name="frame">
        /// The reply.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        private static byte[] BodyOf(XmsgFrame frame)
        {
            byte[] all = frame.ToArray();
            byte[] body = new byte[all.Length - BodyOffset];
            for (int i = 0; i < body.Length; i++)
            {
                body[i] = all[i + BodyOffset];
            }

            return body;
        }
    }
}
