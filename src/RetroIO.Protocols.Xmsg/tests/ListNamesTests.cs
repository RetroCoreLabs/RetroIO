//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.ListNames;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Verifies the XROUT <c>XSGNI</c> LIST-NAMES client and server against a complete real walk.
    /// </summary>
    /// <remarks>
    /// Captured 2026-11-10 on the COSMOS Ethernet hub (TCP 5010): D102 ran XMSG-COMMAND LIST-NAMES
    /// against "XROUT system? d100". Every datagram below is copied from that capture, from SINTRAN
    /// byte 0 (the 11-byte hub link header is not included), in wire order: request, reply,
    /// request, reply ... Every one of them was also ACKed on the wire; the ACKs are not listed.
    /// </remarks>
    public sealed class ListNamesTests
    {
        /// <summary>
        /// The whole walk: 14 requests (D102 to D100) and 14 replies, interleaved.
        /// </summary>
        internal static readonly string[] CapturedWalk =
        {
            "2113000e0064006600120100dd022100868400640000006606b201000145000401020000",
            "2113000e0066006400110100dd0321008660006606b2006606b201000100000c010400640001fe0444313030",
            "2113000e0064006600130100dd012100868400640000006606b2010001450006010400640002",
            "2113000e0066006400120100dd0221008660006606b2006606b20100010000100104006401dafe082a584d2d4649444f",
            "2113000e0064006600140100dd002100868400640000006606b20100014500060104006401db",
            "2113000e0066006400130100dd0121008660006606b2006606b2010001000012010400640227fe062a584654524103020001",
            "2113000e0064006600150100dcff2100868400640000006606b2010001450006010400640228",
            "2113000e0066006400140100dd0021008660006606b2006606b20100010000120104006402d0fe092a584d2d454e4e533000",
            "2113000e0064006600160100dcfe2100868400640000006606b20100014500060104006402d1",
            "2113000e0066006400150100dcff21008660006606b2006606b201000100000e0104006403acfe062a434f53504f",
            "2113000e0064006600170100dcfd2100868400640000006606b20100014500060104006403ad",
            "2113000e0066006400160100dcfe21008660006606b2006606b2010001000010010400640401fe072a54414441444d00",
            "2113000e0064006600180100dcfc2100868400640000006606b2010001450006010400640402",
            "2113000e0066006400170100dcfd21008660006606b2006606b20100010000140104006404cefe072a46412d4653410003020002",
            "2113000e0064006600190100dcfb2100868400640000006606b20100014500060104006404cf",
            "2113000e0066006400180100dcfc21008660006606b2006606b201000100001601040064053ffe092a46412d4653412d490003020000",
            "2113000e00640066001a0100dcfa2100868400640000006606b2010001450006010400640540",
            "2113000e0066006400190100dcfb21008660006606b2006606b201000100000c010400650001fe0444313031",
            "2113000e00640066001b0100dcf92100868400640000006606b2010001450006010400650002",
            "2113000e00660064001a0100dcfa21008660006606b2006606b201000100000c010400660001fe0444313032",
            "2113000e00640066001c0100dcf82100868400640000006606b2010001450006010400660002",
            "2113000e00660064001b0100dcf921008660006606b2006606b201000100000c010400670001fe0444313033",
            "2113000e00640066001d0100dcf72100868400640000006606b2010001450006010400670002",
            "2113000e00660064001c0100dcf821008660006606b2006606b201000100000c010400c80001fe0444323030",
            "2113000e00640066001e0100dcf62100868400640000006606b2010001450006010400c80002",
            "2113000e00660064001d0100dcf721008660006606b2006606b201000100000e01044e1f0001fe06443139393939",
            "2113000e00640066001f0100dcf52100868400640000006606b201000145000601044e1f0002",
            "2113000e00660064001e0100dcf621008660006606b2006606b201000100000401020000",
        };

        // The asker in the capture: node/system 102, port word 0x06B2, asking system 100.
        private const ushort AskerNode = 102;
        private const ushort AnswerNode = 100;
        private const ushort AskerPort = 0x06B2;

        /// <summary>
        /// D100's name table as the walk reported it, in magic order.
        /// </summary>
        /// <returns>
        /// The 13 rows.
        /// </returns>
        internal static List<NameTableEntry> D100Table()
        {
            List<NameTableEntry> rows = new List<NameTableEntry>();
            rows.Add(new NameTableEntry(0x00640001u, "D100"));
            rows.Add(new NameTableEntry(0x006401DAu, "*XM-FIDO"));
            rows.Add(new NameTableEntry(0x00640227u, "*XFTRA", 1));
            rows.Add(new NameTableEntry(0x006402D0u, "*XM-ENNS0"));
            rows.Add(new NameTableEntry(0x006403ACu, "*COSPO"));
            rows.Add(new NameTableEntry(0x00640401u, "*TADADM"));
            rows.Add(new NameTableEntry(0x006404CEu, "*FA-FSA", 2));
            rows.Add(new NameTableEntry(0x0064053Fu, "*FA-FSA-I", 0));
            rows.Add(new NameTableEntry(0x00650001u, "D101"));
            rows.Add(new NameTableEntry(0x00660001u, "D102"));
            rows.Add(new NameTableEntry(0x00670001u, "D103"));
            rows.Add(new NameTableEntry(0x00C80001u, "D200"));
            rows.Add(new NameTableEntry(0x4E1F0001u, "D19999"));
            return rows;
        }

        /// <summary>
        /// Every captured request is rebuilt byte for byte by the client, Flags 1 included.
        /// </summary>
        [Fact]
        public void Client_rebuilds_every_captured_request()
        {
            ListNamesClient client = new ListNamesClient();
            uint fromMagic = 0;
            List<NameTableEntry> table = D100Table();

            for (int i = 0; i < CapturedWalk.Length; i += 2)
            {
                byte[] expected = Convert.FromHexString(CapturedWalk[i]);
                ushort flags1 = (ushort)((expected[8] << 8) | expected[9]);

                byte[] actual = client.BuildRequest(
                    fromMagic, AnswerNode, AskerNode, targetSystem: 100, sourceSystem: 102,
                    sourcePort: AskerPort, flags1: flags1);

                Assert.Equal(expected, actual);

                int row = i / 2;
                fromMagic = row < table.Count ? table[row].Magic + 1 : 0;
            }
        }

        /// <summary>
        /// Every captured reply is decoded into the row the walk printed, and the last one is the
        /// end of the table.
        /// </summary>
        [Fact]
        public void Client_decodes_every_captured_reply()
        {
            ListNamesClient client = new ListNamesClient();
            List<NameTableEntry> table = D100Table();

            for (int i = 1; i < CapturedWalk.Length; i += 2)
            {
                XmsgFrame reply = XmsgFrame.Parse(Convert.FromHexString(CapturedWalk[i]));
                Assert.True(client.TryParseResponse(reply, out bool end, out NameTableEntry entry));

                int row = i / 2;
                if (row == table.Count)
                {
                    Assert.True(end);
                    continue;
                }

                Assert.False(end);
                Assert.Equal(table[row].Magic, entry.Magic);
                Assert.Equal(table[row].Name, entry.Name);
                Assert.Equal(table[row].HasFreeConnections, entry.HasFreeConnections);
                Assert.Equal(table[row].FreeConnections, entry.FreeConnections);
            }
        }

        /// <summary>
        /// The body encoders reproduce the captured bodies from wire offset 28, the odd-name pad
        /// and the 16-bit first parameter included.
        /// </summary>
        [Fact]
        public void Wire_bodies_match_the_capture()
        {
            Assert.Equal(Convert.FromHexString("0145000401020000"), XsgniWire.BuildRequestBody(1, 0));
            Assert.Equal(Convert.FromHexString("01450006010400640402"), XsgniWire.BuildRequestBody(1, 0x00640402u));

            // An even name with no free count.
            Assert.Equal(
                Convert.FromHexString("0100000c010400640001fe0444313030"),
                XsgniWire.BuildReplyBody(1, new NameTableEntry(0x00640001u, "D100")));

            // An odd name that ends the body: one pad byte, counted in the length, name length still 9.
            Assert.Equal(
                Convert.FromHexString("010000120104006402d0fe092a584d2d454e4e533000"),
                XsgniWire.BuildReplyBody(1, new NameTableEntry(0x006402D0u, "*XM-ENNS0")));

            // An odd name followed by the free count: the pad sits between them.
            Assert.Equal(
                Convert.FromHexString("0100001601040064053ffe092a46412d4653412d490003020000"),
                XsgniWire.BuildReplyBody(1, new NameTableEntry(0x0064053Fu, "*FA-FSA-I", 0)));

            // The end of the table.
            Assert.Equal(Convert.FromHexString("0100000401020000"), XsgniWire.BuildReplyBody(1, null));

            Assert.True(XsgniWire.TryReadRequestBody(Convert.FromHexString("0145000401020000"), out byte serial, out uint first));
            Assert.Equal((byte)1, serial);
            Assert.Equal(0u, first);
            Assert.True(XsgniWire.TryReadRequestBody(Convert.FromHexString("01450006010400640402"), out serial, out uint next));
            Assert.Equal(0x00640402u, next);
        }

        /// <summary>
        /// The server answers every captured request from D100's table with the captured reply,
        /// byte for byte, given the reply's Flags 1.
        /// </summary>
        /// <remarks>
        /// The Flags 1 is passed in because it is the ANSWERING node's own counter, which the
        /// request does not carry: D100 answered 0x0012 with 0x0011, 0x0013 with 0x0012, and so on.
        /// Everything else - the node swap, the asker's endpoint in both sub-header slots, the
        /// serial echo, the body and the header checksum - comes from the server.
        /// </remarks>
        [Fact]
        public void Server_answers_every_captured_request_with_the_captured_reply()
        {
            InMemoryNameTable table = new InMemoryNameTable(D100Table());
            ListNamesServer server = new ListNamesServer();

            for (int i = 0; i < CapturedWalk.Length; i += 2)
            {
                XmsgFrame request = XmsgFrame.Parse(Convert.FromHexString(CapturedWalk[i]));
                byte[] expected = Convert.FromHexString(CapturedWalk[i + 1]);
                ushort replyFlags1 = (ushort)((expected[8] << 8) | expected[9]);
                ushort requestFlags1 = request.Header.Flags1;

                // The capture's own proof that the reply does not copy the request's number.
                Assert.NotEqual(requestFlags1, replyFlags1);

                byte[] actual = server.Handle(request, table, replyFlags1);
                Assert.Equal(expected, actual);
            }
        }

        /// <summary>
        /// A repeated request (a retransmit) is answered with the same row.
        /// </summary>
        [Fact]
        public void Server_answers_a_repeated_request_the_same()
        {
            InMemoryNameTable table = new InMemoryNameTable(D100Table());
            ListNamesServer server = new ListNamesServer();
            XmsgFrame request = XmsgFrame.Parse(Convert.FromHexString(CapturedWalk[4]));

            byte[] first = server.Handle(request, table, 0x0013);
            byte[] again = server.Handle(request, table, 0x0013);

            Assert.Equal(Convert.FromHexString(CapturedWalk[5]), first);
            Assert.Equal(first, again);
        }

        /// <summary>
        /// Past the last row the server sends the captured end-of-table reply.
        /// </summary>
        [Fact]
        public void Server_sends_the_end_form_past_the_last_row()
        {
            InMemoryNameTable table = new InMemoryNameTable(D100Table());
            ListNamesServer server = new ListNamesServer();
            XmsgFrame request = XmsgFrame.Parse(Convert.FromHexString(CapturedWalk[26]));

            byte[] reply = server.Handle(request, table, 0x001E);

            Assert.Equal(Convert.FromHexString(CapturedWalk[27]), reply);
        }

        /// <summary>
        /// A request with role byte <c>0xC4</c> - the form D100 itself sends to a gateway - is
        /// answered exactly like the <c>0x84</c> one.
        /// </summary>
        [Fact]
        public void Server_accepts_role_C4()
        {
            byte[] bytes = Convert.FromHexString(CapturedWalk[0]);
            Assert.Equal(0x84, bytes[17]);
            bytes[17] = 0xC4;

            InMemoryNameTable table = new InMemoryNameTable(D100Table());
            ListNamesServer server = new ListNamesServer();

            byte[] reply = server.Handle(XmsgFrame.Parse(bytes), table, 0x0011);

            Assert.Equal(Convert.FromHexString(CapturedWalk[1]), reply);
        }

        /// <summary>
        /// The client walks a scripted server end to end and gets all 13 rows.
        /// </summary>
        [Fact]
        public void Client_walks_a_scripted_server_end_to_end()
        {
            ScriptedExchange exchange = new ScriptedExchange(new InMemoryNameTable(D100Table()), 0x0012, 0x0011);
            ListNamesClient client = new ListNamesClient();

            ListNamesWalkResult result = client.Walk(
                exchange, AnswerNode, AskerNode, targetSystem: 100, sourceSystem: 102,
                sourcePort: AskerPort, timeout: TimeSpan.FromSeconds(1));

            Assert.Equal(ListNamesWalkStatus.Complete, result.Status);
            List<NameTableEntry> expected = D100Table();
            Assert.Equal(expected.Count, result.Entries.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Magic, result.Entries[i].Magic);
                Assert.Equal(expected[i].Name, result.Entries[i].Name);
                Assert.Equal(expected[i].FreeConnections, result.Entries[i].FreeConnections);
            }

            Assert.True(result.Entries[0].IsSystemName);
            Assert.Equal(3, result.Entries[1].PortNumber);

            // Every datagram both sides sent equals the capture, in order.
            Assert.Equal(CapturedWalk.Length, exchange.Wire.Count);
            for (int i = 0; i < CapturedWalk.Length; i++)
            {
                Assert.Equal(Convert.FromHexString(CapturedWalk[i]), exchange.Wire[i]);
            }
        }

        /// <summary>
        /// A reply that never comes ends the walk as timed out, keeping the rows received before.
        /// </summary>
        [Fact]
        public void Client_walk_times_out_and_keeps_the_rows_so_far()
        {
            ScriptedExchange exchange = new ScriptedExchange(new InMemoryNameTable(D100Table()), 0x0012, 0x0011);
            exchange.AnswerOnly = 3;
            ListNamesClient client = new ListNamesClient();

            ListNamesWalkResult result = client.Walk(
                exchange, AnswerNode, AskerNode, targetSystem: 100, sourceSystem: 102,
                sourcePort: AskerPort, timeout: TimeSpan.FromMilliseconds(50));

            Assert.Equal(ListNamesWalkStatus.TimedOut, result.Status);
            Assert.Equal(3, result.Entries.Count);
            Assert.Equal("*XFTRA", result.Entries[2].Name);
            Assert.Equal(TimeSpan.FromMilliseconds(50), exchange.LastTimeout);
        }

        /// <summary>
        /// Answers requests with a <see cref="ListNamesServer"/>, as D100 did, and records the wire.
        /// </summary>
        private sealed class ScriptedExchange : IListNamesExchange
        {
            private readonly INameTable _table;
            private readonly ListNamesServer _server = new ListNamesServer();
            private ushort _askerFlags1;
            private ushort _answerFlags1;
            private int _answered;

            internal ScriptedExchange(INameTable table, ushort askerFlags1, ushort answerFlags1)
            {
                _table = table;
                _askerFlags1 = askerFlags1;
                _answerFlags1 = answerFlags1;
            }

            internal List<byte[]> Wire { get; } = new List<byte[]>();

            internal int AnswerOnly { get; set; } = int.MaxValue;

            internal TimeSpan LastTimeout { get; private set; }

            public ushort NextFlags1()
            {
                ushort value = _askerFlags1;
                _askerFlags1++;
                return value;
            }

            public bool TryExchange(byte[] request, TimeSpan timeout, out XmsgFrame? reply)
            {
                LastTimeout = timeout;
                Wire.Add(request);
                if (_answered >= AnswerOnly)
                {
                    reply = null;
                    return false;
                }

                byte[] answer = _server.Handle(XmsgFrame.Parse(request), _table, _answerFlags1);
                _answerFlags1++;
                _answered++;
                Wire.Add(answer);
                reply = XmsgFrame.Parse(answer);
                return true;
            }
        }
    }
}
