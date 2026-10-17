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
using RetroIO.Protocols.Xmsg.Qform;
using RetroIO.Protocols.Xmsg.Servers.Fa;
using RetroIO.Protocols.Xmsg.Sintran;
using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Covers a client WRITING a file to us: the request that arms it, the data messages that
    /// follow, the reply that completes it, and the set-length that states the file's true size.
    /// </summary>
    /// <remarks>
    /// <para><b>A write is a read backwards</b></para>
    /// Measured in wire order in <c>DOC\FA-READ-WRITE-WIRE-PROTOCOL-2026-08-04.md</c> section 3:
    ///  - READ: request, then the reply, THEN the data messages from the server.
    ///  - WRITE: request, then the data messages from the client, THEN the reply.
    /// So the reply is the COMPLETION in both cases. These tests exist mostly to hold that ordering
    /// down, because it is the one thing about a write that is not simply the read mirrored.
    /// <para><b>Content arrives as fragment pairs</b></para>
    /// A data message is 1032 bytes and does not fit in one frame. The client sends a first fragment
    /// and a continuation; <c>SintranFragmentReassembler</c> rejoins them before the server sees
    /// anything. These tests drive whole messages, because the rejoining is covered on its own in
    /// <c>SintranFragmentReassemblerTests</c>.
    /// <para><b>What is NOT claimed</b></para>
    /// No real client has written a file to us. The shapes come from the capture; that a SINTRAN
    /// machine accepts our side of them is unverified.
    /// </remarks>
    public sealed class FaWriteFileWiringTests : IDisposable
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// The temporary folder the server serves during a test.
        /// </summary>
        private readonly string _folder;

        /// <summary>
        /// The file written by these tests. Quoted on the wire, which is how a SINTRAN caller asks
        /// for a file to be created.
        /// </summary>
        private const string WriteFileName = "WRTEST1.OUT";

        /// <summary>
        /// Where a file-access message body starts inside a serialised datagram.
        /// </summary>
        private const int BodyOffset = SintranHeader.Size + XmsgSubHeader.Size;

        /// <summary>
        /// Creates the temporary folder and the file to be written into.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaWriteFileWiringTests(ITestOutputHelper output)
        {
            _output = output;
            _folder = Path.Combine(Path.GetTempPath(), "fa-write-wiring-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);

            // The store opens an existing file; creating one from the wire is a separate operation
            // that is still uncaptured, so the file is put there first.
            File.WriteAllBytes(Path.Combine(_folder, WriteFileName), Array.Empty<byte>());
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
        /// The request is acknowledged but NOT replied to - the reply waits for the content.
        /// </summary>
        /// <remarks>
        /// This is the assertion that pins the ordering. If a write ever starts replying to its own
        /// request, this fails, and the client would be told the write finished before a single byte
        /// of it had arrived.
        /// </remarks>
        [Fact]
        public void TheRequestIsAcknowledgedButNotYetReplied()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            OpenForWrite(host, sequence: 1, flags1: 0x0002);

            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                BuildWriteRequest(sequence: 2, position: 0), 0x0004));

            // The acknowledgement alone.
            Assert.Single(frames);

            byte[] body = BodyOf(frames[0]);
            Assert.Equal(8, body.Length);
            Assert.Equal(
                (ushort)FaMessageType.ShortAck,
                (ushort)((body[0] << 8) | body[1]));
        }

        /// <summary>
        /// A whole write lands in the file, and only the last block draws the reply.
        /// </summary>
        [Fact]
        public void TheContentLandsAndTheLastBlockDrawsTheReply()
        {
            byte[] content = MakeContent(FaFileDataCodec.ReadLength);

            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            OpenForWrite(host, sequence: 1, flags1: 0x0002);
            host.Route(FaTestClient.BuildSessionFrame(BuildWriteRequest(2, 0), 0x0004));

            // First block: acknowledged, no reply - the write is not finished.
            IReadOnlyList<XmsgFrame> first = host.Route(FaTestClient.BuildSessionFrame(
                BuildDataMessage(counter: 0x04, isLast: false, content, 0), 0x0006));
            Assert.Single(first);

            // Second block: acknowledged AND replied to.
            IReadOnlyList<XmsgFrame> second = host.Route(FaTestClient.BuildSessionFrame(
                BuildDataMessage(counter: 0x85, isLast: true, content, FaFileDataCodec.BlockLength), 0x0008));
            Assert.Equal(2, second.Count);

            byte[] replyBody = BodyOf(second[1]);
            FaOperation operation;
            ushort sequence;
            Assert.True(FaExchangeCodec.TryReadOperation(replyBody, out operation, out sequence));
            Assert.Equal(FaOperation.WriteFile, operation);
            Assert.Equal(2, sequence);

            // Close before reading from disk - the store holds the file open, and closing is what a
            // real client does next anyway.
            CloseTheFile(host, sequence: 3, flags1: 0x000A);

            // And the bytes are on disk, both blocks of them.
            byte[] written = File.ReadAllBytes(Path.Combine(_folder, WriteFileName));
            _output.WriteLine("file is now " + written.Length + " byte(s)");

            Assert.Equal(FaFileDataCodec.ReadLength, written.Length);
            Assert.Equal(content, written);
        }

        /// <summary>
        /// The set-length request truncates the padded file to its true size.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A write ships WHOLE 2048-byte blocks, so without this the file would be rounded up to a
        /// block boundary.
        /// </para>
        /// <para><b>The value is the LAST BYTE'S INDEX, so the file is one longer</b></para>
        /// This test used to send 1500 and expect 1500, which was our own reading and had never been
        /// measured. Live on 2026-08-06 D100 copied a file its own FILE-STATISTICS calls 12690 bytes
        /// and sent <c>SetEndOfFile 12689</c>; taking that literally stored 12689 bytes, identical
        /// to the source but with the last byte gone. NDIX's client applies the same +1/-1 and calls
        /// it a kludge factor, and the capture's OPEN reply says <c>45F1</c> where its
        /// <c>SetEndOfFile</c> says <c>45F0</c>. See <c>FaServer.SetEndOfFile</c>.
        /// </remarks>
        [Fact]
        public void SetEndOfFileTruncatesThePaddingAway()
        {
            const int LastByteIndex = 1499;
            const int TrueLength = LastByteIndex + 1;
            byte[] content = MakeContent(FaFileDataCodec.ReadLength);

            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            OpenForWrite(host, sequence: 1, flags1: 0x0002);
            host.Route(FaTestClient.BuildSessionFrame(BuildWriteRequest(2, 0), 0x0004));
            host.Route(FaTestClient.BuildSessionFrame(BuildDataMessage(0x04, false, content, 0), 0x0006));
            host.Route(FaTestClient.BuildSessionFrame(
                BuildDataMessage(0x85, true, content, FaFileDataCodec.BlockLength), 0x0008));

            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                BuildSetEndOfFile(sequence: 3, byteLength: LastByteIndex), 0x000A));

            Assert.Equal(2, frames.Count);

            // The set-length comes BEFORE the close, exactly as the captured write does.
            CloseTheFile(host, sequence: 4, flags1: 0x000C);

            byte[] written = File.ReadAllBytes(Path.Combine(_folder, WriteFileName));
            _output.WriteLine("after SetEndOfFile the file is " + written.Length + " byte(s)");

            Assert.Equal(TrueLength, written.Length);

            // The bytes that survive are the ones that were written, not zeros.
            for (int i = 0; i < TrueLength; i++)
            {
                Assert.Equal(content[i], written[i]);
            }
        }

        /// <summary>
        /// A write onto a file opened for READING is refused rather than carried out.
        /// </summary>
        [Fact]
        public void AWriteOntoAReadOnlyOpenIsRefused()
        {
            XmsgServerHost host = BuildHost();
            host.Route(FaTestClient.BuildConnectLetter());
            OpenForRead(host, sequence: 1, flags1: 0x0002);

            IReadOnlyList<XmsgFrame> frames = host.Route(FaTestClient.BuildSessionFrame(
                BuildWriteRequest(sequence: 2, position: 0), 0x0004));

            // The refusal is a REPLY, so both frames go out - not the lone acknowledgement an armed
            // write produces.
            Assert.Equal(2, frames.Count);

            byte[] body = BodyOf(frames[1]);
            int at = FaExchangeCodec.QformOffset + 6;
            Assert.Equal((byte)QformTagByte.Selector, body[at]);
            Assert.Equal(0x01, body[at + 2]);
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
        /// Opens the test file for writing.
        /// </summary>
        /// <param name="host">
        /// The host to route through.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence of the request.
        /// </param>
        private static void OpenForWrite(XmsgServerHost host, ushort sequence, ushort flags1)
        {
            host.Route(FaTestClient.BuildSessionFrame(BuildOpenRequest(sequence, true), flags1));
        }

        /// <summary>
        /// Opens the test file for reading.
        /// </summary>
        /// <param name="host">
        /// The host to route through.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence of the request.
        /// </param>
        private static void OpenForRead(XmsgServerHost host, ushort sequence, ushort flags1)
        {
            host.Route(FaTestClient.BuildSessionFrame(BuildOpenRequest(sequence, false), flags1));
        }

        /// <summary>
        /// Closes the file the conversation has open, which releases it on disk.
        /// </summary>
        /// <param name="host">
        /// The host to route through.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence of the request.
        /// </param>
        /// <remarks>
        /// A close carries no file number and no parameters - the open file belongs to the
        /// conversation.
        /// </remarks>
        private static void CloseTheFile(XmsgServerHost host, ushort sequence, ushort flags1)
        {
            byte[] fields = new byte[3];
            QformWriter writer = new QformWriter(fields);
            writer.WriteEndOfList();
            writer.EnsureComplete("Close request");

            host.Route(FaTestClient.BuildSessionFrame(
                FaTestClient.BuildRequestEnvelope(FaOperation.CloseFile, sequence, fields), flags1));
        }

        /// <summary>
        /// Builds an open request for the test file.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="forWrite">
        /// Whether to ask for write access, which rides under selector 3.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        private static byte[] BuildOpenRequest(ushort sequence, bool forWrite)
        {
            int nameFieldLength = WriteFileName.Length + 2;
            byte[] nameField = new byte[nameFieldLength];
            SintranName.Write(nameField, WriteFileName);

            byte[] fields = new byte[3 + 2 + nameFieldLength + (forWrite ? 3 + 3 : 0) + 3];
            QformWriter writer = new QformWriter(fields);

            writer.WriteSelector(FaOpenFileCodec.PrimarySelector);
            writer.WriteByteString(nameField);

            if (forWrite)
            {
                writer.WriteSelector(FaOpenFileCodec.SecondarySelector);
                writer.WriteInteger(FaOpenFileCodec.AccessModeWrite);
            }

            writer.WriteEndOfList();
            writer.EnsureComplete("Open request");

            return FaTestClient.BuildRequestEnvelope(FaOperation.OpenFile, sequence, fields);
        }

        /// <summary>
        /// Builds a write request for one block-pair at a position.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="position">
        /// The position, counted in units of <c>FaFileDataCodec.ReadLength</c>.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        /// <remarks>
        /// The captured write request is the READ request with the operation changed from
        /// <c>0x0008</c> to <c>0x0009</c> and nothing else, which is what this builds.
        /// </remarks>
        private static byte[] BuildWriteRequest(ushort sequence, uint position)
        {
            byte[] fields = new byte[3 + 5 + 3];
            QformWriter writer = new QformWriter(fields);

            writer.WriteSelector(FaFileDataCodec.PositionSelector);
            writer.WriteTypedInteger32(position);
            writer.WriteEndOfList();
            writer.EnsureComplete("Write request");

            return FaTestClient.BuildRequestEnvelope(FaOperation.WriteFile, sequence, fields);
        }

        /// <summary>
        /// Builds one data message carrying a block of the content.
        /// </summary>
        /// <param name="counter">
        /// The session-header counter, with bit 7 set on the last message of a delivery.
        /// </param>
        /// <param name="isLast">
        /// Whether this is the last message of the delivery, which chooses the token.
        /// </param>
        /// <param name="content">
        /// The whole content being written.
        /// </param>
        /// <param name="offset">
        /// Where in <paramref name="content"/> this block starts.
        /// </param>
        /// <returns>
        /// The 1032-byte message body.
        /// </returns>
        private static byte[] BuildDataMessage(byte counter, bool isLast, byte[] content, int offset)
        {
            ushort token = isLast
                ? FaFileDataCodec.LastDataMessageToken
                : FaExchangeCodec.SessionTokenAsker;

            return FaFileDataCodec.BuildDataMessage(
                FaTestClient.ClientConversation,
                counter,
                token,
                new ReadOnlySpan<byte>(content, offset, FaFileDataCodec.BlockLength));
        }

        /// <summary>
        /// Builds the request that states the file's true byte length.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="byteLength">
        /// The length to set.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        /// <remarks>
        /// The captured shape is
        /// <c>F2 0001 92 003B F2 0002 8C 80 05 A4 length F2 00FF</c>. The three bytes
        /// <c>8C 80 05</c> are reproduced because they are what the wire carries; what they mean is
        /// UNKNOWN and the reader deliberately does not depend on them.
        /// </remarks>
        private static byte[] BuildSetEndOfFile(ushort sequence, uint byteLength)
        {
            byte[] fields = new byte[3 + 3 + 3 + 3 + 5 + 3];
            QformWriter writer = new QformWriter(fields);

            writer.WriteSelector(FaListFilesCodec.FunctionSelector);
            writer.WriteInteger((ushort)FaSpecialFunction.SetEndOfFile);
            writer.WriteSelector(FaListFilesCodec.PayloadSelector);
            writer.WriteConstructedEscaped(5);
            writer.WriteTypedInteger32(byteLength);
            writer.WriteEndOfList();
            writer.EnsureComplete("SetEndOfFile request");

            return FaTestClient.BuildRequestEnvelope(FaOperation.SiiiSpecial, sequence, fields);
        }

        /// <summary>
        /// Makes content whose every byte is distinguishable from its neighbours.
        /// </summary>
        /// <param name="length">
        /// How many bytes to make.
        /// </param>
        /// <returns>
        /// The content.
        /// </returns>
        private static byte[] MakeContent(int length)
        {
            byte[] content = new byte[length];
            for (int i = 0; i < length; i++)
            {
                content[i] = (byte)((i * 11) + 3);
            }

            return content;
        }

        /// <summary>
        /// Extracts the file-access message body from a frame.
        /// </summary>
        /// <param name="frame">
        /// The frame.
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
