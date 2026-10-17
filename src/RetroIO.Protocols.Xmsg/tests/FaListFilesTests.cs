//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Xmsg.Fa;
using Xunit;
using static RetroIO.Protocols.Xmsg.TestSupport.TestHex;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// The remote LIST-FILES wire framing, checked against a captured listing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every vector here comes from
    /// <c>E:\Dev\Ronny\X25Emulator\pcap\claude-list-files-d100-system-2026-07-29.pcapng</c>, a
    /// <c>LIST-FILES d100(system).</c> issued on node 102 against node 100, which returned ten
    /// entries in ten consecutive round trips.
    /// </para>
    /// <para>
    /// These tests cover the FRAMING only. What is inside the 64-byte record is the SINTRAN object
    /// entry and is tested in <c>RetroIO.Protocols.Xmsg.Ndfs.Tests</c> against RetroIO.Storage.Ndfs, which owns that layout.
    /// </para>
    /// </remarks>
    public sealed class FaListFilesTests
    {
        /// <summary>
        /// The opaque directory/user block, identical in every captured request.
        /// </summary>
        private const string SpecBlockHex =
            "2853595354454d2927454d292e2853595354454d2927"
            + "3704b5dc27040000270548ff0000ffff000000000000000000000000000000000003"
            + "00000540b56b";

        /// <summary>
        /// Frame 321: the request for the FIRST entry, cursor 0xFFFF, serial 50.
        /// </summary>
        private const string Request321Hex =
            "92000c" + "920032" + "f20001" + "920078" + "f20002"
            + "8c8046" + "b03e" + "2853595354454d2927454d292e2853595354454d29273704b5dc27040000270548ff0000ffff00000000000000000000000000000000000300000540b56b"
            + "a20000" + "a2ffff"
            + "f200ff";

        /// <summary>
        /// The SINTRAN:DATA record as it arrived, used as an opaque payload here.
        /// </summary>
        private const string SintranRecordHex =
            "900053494e5452414e2700000000000000004441544100000000000700200000000000000000000ac1cd0de3c1cd0ef0c1cd0ef00000003f0001dfff000078da";

        /// <summary>
        /// Frame 327: the reply carrying SINTRAN:DATA.
        /// </summary>
        private const string Reply327Hex =
            "92000c" + "920032" + "f20002"
            + "8c4b" + "a20000" + "a20000" + "a20001"
            + "b040" + SintranRecordHex
            + "f200ff";

        /// <summary>
        /// A request built from the captured cursor and spec block is byte-identical to the request
        /// that was actually on the wire.
        /// </summary>
        /// <remarks>
        /// This is the test that makes a live replay safe: it proves the builder emits exactly the
        /// bytes node 100 already accepted, rather than something merely well-formed.
        /// </remarks>
        [Fact]
        public void BuiltRequest_IsByteIdenticalToTheCapturedRequest()
        {
            byte[] expected = FromHex(Request321Hex);

            byte[] actual = FaListFilesCodec.BuildRequest(
                serial: 50,
                cursor: FaListFilesCodec.FirstEntryCursor,
                specBlock: FromHex(SpecBlockHex));

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// The captured request round-trips back to its serial and cursor.
        /// </summary>
        [Fact]
        public void CapturedRequest_YieldsItsSerialAndCursor()
        {
            ushort serial;
            ushort cursor;

            Assert.True(FaListFilesCodec.TryReadRequest(FromHex(Request321Hex), out serial, out cursor));

            Assert.Equal(50, serial);
            Assert.Equal(FaListFilesCodec.FirstEntryCursor, cursor);
        }

        /// <summary>
        /// The captured reply yields its serial and the record unchanged.
        /// </summary>
        /// <remarks>
        /// The serial is what pairs a reply to its request - the capture showed it echoed on all ten
        /// round trips.
        /// </remarks>
        [Fact]
        public void CapturedReply_YieldsItsSerialAndTheRecordUnchanged()
        {
            ushort serial;
            byte[] record;

            Assert.True(FaListFilesCodec.TryReadReply(FromHex(Reply327Hex), out serial, out record));

            Assert.Equal(50, serial);
            Assert.Equal(FromHex(SintranRecordHex), record);
        }

        /// <summary>
        /// A reply built by our server reads back through our client with the record intact.
        /// </summary>
        /// <remarks>
        /// This is the offline client-against-server loop for the framing. It does NOT prove a real
        /// client would accept the reply - only a live run does.
        /// </remarks>
        [Fact]
        public void ServerBuiltReply_ReadsBackThroughTheClient()
        {
            byte[] original = FromHex(SintranRecordHex);

            byte[] body = FaListFilesCodec.BuildReply(serial: 1234, entryRecord: original);

            ushort serial;
            byte[] parsed;
            Assert.True(FaListFilesCodec.TryReadReply(body, out serial, out parsed));

            Assert.Equal(1234, serial);
            Assert.Equal(original, parsed);
        }

        /// <summary>
        /// A reply we build for the captured entry is byte-identical to the captured reply.
        /// </summary>
        /// <remarks>
        /// Stronger than the round trip above: it fixes the constructed length, the three leading
        /// A2 values and the end-of-list selector against bytes a real client accepted.
        /// </remarks>
        [Fact]
        public void BuiltReply_IsByteIdenticalToTheCapturedReply()
        {
            byte[] expected = FromHex(Reply327Hex);

            byte[] actual = FaListFilesCodec.BuildReply(
                serial: 50,
                entryRecord: FromHex(SintranRecordHex));

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// A record of the wrong size is refused rather than framed.
        /// </summary>
        [Fact]
        public void WrongSizedRecord_IsRefused()
        {
            byte[] tooShort = new byte[32];

            Assert.Throws<ArgumentException>(() => FaListFilesCodec.BuildReply(1, tooShort));
        }

        /// <summary>
        /// Walking the cursor over a directory visits every entry once, in order, the way the
        /// captured session did.
        /// </summary>
        /// <remarks>
        /// The captured cursors ran 0xFFFF, 0x0001, 0x0002 ... 0x0009 for ten entries: the first
        /// request uses the sentinel and thereafter the index of the entry wanted.
        /// </remarks>
        [Fact]
        public void CursorWalk_VisitsEveryEntryOnceInOrder()
        {
            // Four distinct records, told apart by a marker byte we can read back.
            byte[][] directory = new byte[4][];
            for (int i = 0; i < directory.Length; i++)
            {
                directory[i] = FromHex(SintranRecordHex);
                directory[i][1] = (byte)i;
            }

            byte[] seen = new byte[directory.Length];
            ushort cursor = FaListFilesCodec.FirstEntryCursor;
            for (int i = 0; i < directory.Length; i++)
            {
                int index = cursor == FaListFilesCodec.FirstEntryCursor ? 0 : cursor;

                byte[] reply = FaListFilesCodec.BuildReply((ushort)(100 + i), directory[index]);

                ushort serial;
                byte[] record;
                Assert.True(FaListFilesCodec.TryReadReply(reply, out serial, out record));
                Assert.Equal(100 + i, serial);

                seen[i] = record[1];
                cursor = (ushort)(index + 1);
            }

            Assert.Equal(new byte[] { 0, 1, 2, 3 }, seen);
        }
    }
}
