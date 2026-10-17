//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;

using RetroIO.Protocols.Xmsg.Fa;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Builds the datagrams a COSMOS client sends to <c>*FA-SERVER</c>: the XSLET connect letter and
    /// the file-access requests that follow it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is its own class</b></para>
    /// Two test files drive the same server - the wiring tests and the byte-for-byte listing
    /// regression - and the request shapes are the thing under test in both. A second copy of a
    /// request builder is a second chance to fix one and not the other, which is how a regression net
    /// quietly stops covering the code it was written for.
    /// <para><b>What is fixed here on purpose</b></para>
    /// The node numbers, ports and conversation number are constants rather than parameters. A golden
    /// byte comparison needs them to be, and no test has yet had a reason to vary them.
    /// </remarks>
    public static class FaTestClient
    {
        /// <summary>
        /// Our own node number: the node running <c>*FA-SERVER</c>.
        /// </summary>
        public const ushort ServerNode = 19999;

        /// <summary>
        /// The calling client's node number.
        /// </summary>
        public const ushort ClientNode = 100;

        /// <summary>
        /// The client's port.
        /// </summary>
        public const ushort ClientPort = 0x02F7;

        /// <summary>
        /// The conversation number the client stamps on every request.
        /// </summary>
        public const ushort ClientConversation = 0x0048;

        /// <summary>
        /// The word the connect letter carries in its extras, which the confirmation must echo.
        /// </summary>
        /// <remarks>
        /// <c>0x0004</c> on purpose, not the usual <c>0x0002</c>:
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c> has one connect with each,
        /// and only the odd one out can catch a server that emits a constant.
        /// </remarks>
        public const ushort LetterEchoWord = 0x0004;

        /// <summary>
        /// The bytes a real connect letter carries past its declared XROUT length.
        /// </summary>
        /// <remarks>
        /// Copied from <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c> line 419, with
        /// the echoed word left as that letter's <c>0x0004</c>. What the rest means is UNKNOWN.
        /// </remarks>
        private static readonly byte[] LetterExtras =
        {
            0x07, 0xE2, 0x00, 0x00,
            (byte)(LetterEchoWord >> 8), (byte)LetterEchoWord,
            0x64, 0x00, 0xA2, 0x00, 0xFF, 0x00,
        };

        /// <summary>
        /// The session-header byte a client's first request carries.
        /// </summary>
        /// <remarks>
        /// The value the real client opens a conversation with; see
        /// <c>FaExchangeCodec.SessionTokenFirst</c> for the word that follows it.
        /// </remarks>
        private const byte FirstSessionHeaderByte = 0x80;

        // KNOWN SIMPLIFICATION: this client sends the same session-header byte on every request,
        // where the real one counts - capture-list-files.txt runs 80, 81, 82 unbroken up to B5.
        // It is harmless now: the server takes its reply byte from its OWN counter (see
        // FaServerConversation.NextMessageCounter) and no longer echoes this one. It was NOT
        // harmless while the server echoed - a constant here made every golden reply carry 0x80,
        // so the goldens looked stable while agreeing with nothing on the wire.
        //
        // Not fixed here because FaTestClient is static: an incrementing field would have to be
        // static too, and would then leak between tests and make them order-dependent. Give the
        // client per-conversation state before making it count.

        /// <summary>
        /// Builds the XSLET connect letter naming <c>*FA-SERVER</c>, in the shape the capture shows:
        /// the server name as string parameter 1 and the system name as string parameter 2.
        /// </summary>
        /// <returns>
        /// The letter frame, addressed to XROUT's port 0.
        /// </returns>
        public static XmsgFrame BuildConnectLetter()
        {
            List<byte> payload = new List<byte>(32);
            AppendString(payload, 0xFF, Servers.Fa.FaServer.ServerName);
            AppendString(payload, 0xFE, "D19999");

            // The BODY of a letter starts at absolute 28 with the 4-byte XROUT header - serial,
            // service, big-endian length - then the parameter blocks. XmcsmService.XsletLetter
            // 0x04000041 decomposes exactly into XMCSM 0x0400 and body word 0x0041, i.e. serial 0
            // and service 0x41 = XSLET. The captured *FA-SERVER letter has the same shape with a
            // non-zero serial: 1B41 0012 FF0A 2A46412D534552564552 ...
            byte[] parameters = payload.ToArray();
            List<byte> body = new List<byte>(parameters.Length + XroutMessage.HeaderSize + LetterExtras.Length);

            // Through a local: the compiler folds the enum constant before truncating it, so a
            // direct cast is a compile-time overflow.
            uint xsletLetter = (uint)XmcsmService.XsletLetter;
            body.Add((byte)(xsletLetter >> 8));                         // serial
            body.Add((byte)xsletLetter);                                // service 0x41 = XSLET
            body.Add((byte)(parameters.Length >> 8));
            body.Add((byte)parameters.Length);
            for (int i = 0; i < parameters.Length; i++)
            {
                body.Add(parameters[i]);
            }

            // The real letter carries bytes PAST its declared length; the confirmation echoes the
            // third word of them. Captured shape: 07E2 0000 <word> 6400 A200 FF00.
            for (int i = 0; i < LetterExtras.Length; i++)
            {
                body.Add(LetterExtras[i]);
            }

            return BuildFrame(
                destinationPort: 0x0000,
                xmcsm: (ushort)(xsletLetter >> 16),
                body: body.ToArray(),
                flags1: 0x0001);
        }

        /// <summary>
        /// Builds a session datagram carrying a file-access message body to the server's port.
        /// </summary>
        /// <param name="body">
        /// The file-access message body.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence.
        /// </param>
        /// <returns>
        /// The datagram.
        /// </returns>
        public static XmsgFrame BuildSessionFrame(byte[] body, ushort flags1)
        {
            // XMCSM on a file-access session frame is UNKNOWN. TerminalData's class word is used here
            // only as "something that is not an XSLET letter"; the server echoes whatever it receives
            // and never reads it. The message body goes straight at absolute 28, with NOTHING in
            // front of it - the captured D102 reply is 07D2 0002 0042 6400 at exactly that offset.
            return BuildFrame(
                destinationPort: Servers.Fa.FaServer.FaServerWirePort,
                xmcsm: (ushort)(((uint)XmcsmService.TerminalData) >> 16),
                body: body,
                flags1: flags1);
        }

        /// <summary>
        /// Builds a complete file-access request body: the eight-byte envelope, the operation and
        /// sequence pair, then the given QFORM fields.
        /// </summary>
        /// <param name="operation">
        /// The operation to ask for.
        /// </param>
        /// <param name="sequence">
        /// The exchange sequence.
        /// </param>
        /// <param name="fields">
        /// The QFORM fields after the pair.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        public static byte[] BuildRequestEnvelope(FaOperation operation, ushort sequence, byte[] fields)
        {
            byte[] body = new byte[FaExchangeCodec.QformOffset + 6 + fields.Length];

            // Through a local: casting the enum constant straight to a byte is a compile-time
            // overflow, because the compiler folds it before the truncation.
            ushort messageType = (ushort)FaMessageType.Request;
            body[0] = (byte)(messageType >> 8);
            body[1] = (byte)messageType;
            body[2] = (byte)(ClientConversation >> 8);
            body[3] = (byte)ClientConversation;
            body[4] = FirstSessionHeaderByte;
            body[5] = 0x00;
            body[6] = (byte)(FaExchangeCodec.SessionTokenFirst >> 8);
            body[7] = (byte)FaExchangeCodec.SessionTokenFirst;

            int at = FaExchangeCodec.QformOffset;
            body[at++] = (byte)RetroIO.Protocols.Xmsg.Qform.QformTagByte.Integer;
            body[at++] = (byte)((ushort)operation >> 8);
            body[at++] = (byte)(ushort)operation;
            body[at++] = (byte)RetroIO.Protocols.Xmsg.Qform.QformTagByte.Integer;
            body[at++] = (byte)(sequence >> 8);
            body[at++] = (byte)sequence;

            for (int i = 0; i < fields.Length; i++)
            {
                body[at + i] = fields[i];
            }

            return body;
        }

        /// <summary>
        /// Builds a directory-enquiry request body asking for one file entry.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence, which the walk also uses as its serial.
        /// </param>
        /// <param name="cursor">
        /// The entry cursor - <c>FaListFilesCodec.FirstEntryCursor</c> starts the walk, anything else
        /// means "next".
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        /// <remarks>
        /// The 62-byte directory/user block is opaque and is sent as zeros. The server does not read
        /// it - one Windows folder is the whole world it serves - so a captured block would add
        /// nothing but the false impression that it is being interpreted.
        /// </remarks>
        public static byte[] BuildListingRequest(ushort sequence, ushort cursor)
        {
            return BuildListingRequest(sequence, cursor, new byte[FaListFilesCodec.SpecBlockLength]);
        }

        /// <summary>
        /// Builds a directory-enquiry request carrying a given spec block.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence, which the walk also uses as its serial.
        /// </param>
        /// <param name="cursor">
        /// The entry cursor.
        /// </param>
        /// <param name="specBlock">
        /// The <c>FaListFilesCodec.SpecBlockLength</c>-byte directory and user block.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        public static byte[] BuildListingRequest(ushort sequence, ushort cursor, byte[] specBlock)
        {
            byte[] qform = FaListFilesCodec.BuildRequest(sequence, cursor, specBlock);

            return BuildRequestEnvelope(
                FaListFilesCodec.OperationDirectoryEnquiry, sequence, StripOperationAndSequence(qform));
        }

        /// <summary>
        /// Builds a spec block naming a user and a file, in the shape the captures carry.
        /// </summary>
        /// <param name="user">
        /// The user name, written in brackets at the front.
        /// </param>
        /// <param name="fileSpecification">
        /// The filespec, or an empty string for a whole-directory listing.
        /// </param>
        /// <returns>
        /// The block, zero filled to its full length.
        /// </returns>
        /// <remarks>
        /// From <c>claude-file-stat-102-to-100-2026-07-29.pcapng</c>, whose block reads
        /// <c>(SYSTEM)SINTRAN:DATA'SINTRAN:DATA'</c> and then zeros. The filespec appears TWICE, each
        /// copy ended by the SINTRAN terminator; why is UNKNOWN, and it is reproduced rather than
        /// explained. A whole-directory listing puts the terminator straight after the bracket.
        /// </remarks>
        public static byte[] BuildSpecBlock(string user, string fileSpecification)
        {
            byte[] block = new byte[FaListFilesCodec.SpecBlockLength];
            int at = 0;

            at = Append(block, at, "(" + user + ")");
            at = Append(block, at, fileSpecification);
            at = Append(block, at, "'");
            at = Append(block, at, fileSpecification);
            Append(block, at, "'");

            return block;
        }

        /// <summary>
        /// Writes ASCII into a block and reports where the next write should start.
        /// </summary>
        /// <param name="block">
        /// The block being filled.
        /// </param>
        /// <param name="at">
        /// Where to write.
        /// </param>
        /// <param name="text">
        /// The text to write. Anything past the end of the block is dropped.
        /// </param>
        /// <returns>
        /// The offset just past what was written.
        /// </returns>
        private static int Append(byte[] block, int at, string text)
        {
            for (int i = 0; i < text.Length && at < block.Length; i++)
            {
                block[at++] = (byte)text[i];
            }

            return at;
        }

        /// <summary>
        /// Builds the short request a client uses to ask for the directory or the user entry.
        /// </summary>
        /// <param name="sequence">
        /// The exchange sequence, which is also the serial.
        /// </param>
        /// <param name="function">
        /// Which entry to ask for.
        /// </param>
        /// <returns>
        /// The message body.
        /// </returns>
        public static byte[] BuildFunctionRequest(ushort sequence, FaSpecialFunction function)
        {
            byte[] qform = FaListFilesCodec.BuildShortRequest(sequence, function);

            return BuildRequestEnvelope(
                FaListFilesCodec.OperationDirectoryEnquiry, sequence, StripOperationAndSequence(qform));
        }

        /// <summary>
        /// Drops the leading operation and sequence pair from a codec-built body.
        /// </summary>
        /// <param name="qform">
        /// The whole body as the codec writes it.
        /// </param>
        /// <returns>
        /// The body from the third tagged field onwards.
        /// </returns>
        /// <remarks>
        /// The codecs emit the WHOLE body including the operation and sequence pair, and
        /// <see cref="BuildRequestEnvelope"/> writes that pair itself, so the first six bytes would
        /// otherwise be written twice.
        /// </remarks>
        private static byte[] StripOperationAndSequence(byte[] qform)
        {
            byte[] fields = new byte[qform.Length - 6];
            for (int i = 0; i < fields.Length; i++)
            {
                fields[i] = qform[i + 6];
            }

            return fields;
        }

        /// <summary>
        /// Appends a tagged string parameter (tag, length, ASCII) to a letter payload.
        /// </summary>
        /// <param name="payload">
        /// The payload being built.
        /// </param>
        /// <param name="tag">
        /// The parameter tag byte.
        /// </param>
        /// <param name="text">
        /// The string value.
        /// </param>
        private static void AppendString(List<byte> payload, byte tag, string text)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(text);
            payload.Add(tag);
            payload.Add((byte)ascii.Length);
            for (int i = 0; i < ascii.Length; i++)
            {
                payload.Add(ascii[i]);
            }
        }

        /// <summary>
        /// Builds a datagram from the test client to the server node.
        /// </summary>
        /// <param name="destinationPort">
        /// The port on the server's side.
        /// </param>
        /// <param name="xmcsm">
        /// XMCSM, the one word at wire 26-27.
        /// </param>
        /// <param name="body">
        /// The message body, which starts at wire offset 28.
        /// </param>
        /// <param name="flags1">
        /// The datagram sequence.
        /// </param>
        /// <returns>
        /// The datagram.
        /// </returns>
        private static XmsgFrame BuildFrame(ushort destinationPort, ushort xmcsm, byte[] body, ushort flags1)
        {
            XmsgFrame frame = new XmsgFrame();
            frame.Header.Marker1 = SintranHeader.Marker1Value;
            frame.Header.Marker2 = SintranHeader.Marker2Normal;
            frame.Header.PacketType = 0x00;
            frame.Header.Subtype = SintranPacketSubtype.Data;
            frame.Header.DestinationNode = ServerNode;
            frame.Header.SourceNode = ClientNode;
            frame.Header.Flags1 = flags1;

            // Flags2 equals the 16-bit XMCSM on 1449 of 1449 captured data frames.
            frame.Header.Flags2 = xmcsm;

            // Header word 6 is the ones-complement checksum over words 0-5, carved from the kernel
            // and verified on 3595 of 3595 captured frames. A test client that fabricates it is
            // sending a corrupt header, so it is DERIVED here like a real sender derives it.
            frame.Header.Checksum = Packet.XmsgEnvelope.ComputeHeaderChecksum(
                (ushort)((frame.Header.Marker1 << 8) | frame.Header.Marker2),
                (ushort)((frame.Header.PacketType << 8) | (byte)frame.Header.Subtype),
                frame.Header.DestinationNode,
                frame.Header.SourceNode,
                frame.Header.Flags1,
                frame.Header.Flags2);

            XmsgSubHeader sub = new XmsgSubHeader();
            sub.FrameFlags = (byte)XmsgFrameFlags.DataA;
            sub.Role = 0x00;
            sub.DestinationSystem = ServerNode;
            sub.DestinationPort = destinationPort;
            sub.SourceSystem = ClientNode;
            sub.SourcePort = ClientPort;
            sub.Xmcsm = xmcsm;

            frame.SubHeader = sub;
            frame.TrailingBytes = body;
            return frame;
        }
    }
}
