//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.ListNames;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Servers.Tad;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// A node built by <see cref="XmsgNodeHost"/> answers LIST-NAMES with no extra set-up: its own
    /// system, the systems it routes to and its registered servers, then the end form.
    /// </summary>
    /// <remarks>
    /// The requests have the captured shape of D102 walking D100's table on 2026-11-10 (XSGNI to
    /// port 0, from port <c>0x06B2</c>, each asking from the previous reply's magic plus one).
    /// </remarks>
    public sealed class HostedNameTableTests
    {
        private const ushort OwnNode = 19999;
        private const ushort Asker = 102;
        private const ushort AskerPort = 0x06B2;

        private static readonly Func<DateTime> FixedClock = () => new DateTime(2026, 11, 10);

        private static RoutingTableEntry[] Routes()
        {
            return new RoutingTableEntry[]
            {
                new RoutingTableEntry(OwnNode, XroutConnectionType.Local, OwnNode, 0, 0),
                new RoutingTableEntry(100, XroutConnectionType.Neighbour, 1, 1, 0),
                new RoutingTableEntry(102, XroutConnectionType.Via, 100, 2, 0),
            };
        }

        /// <summary>
        /// Walks the whole table through the link, the way SINTRAN's LIST-NAMES does, and checks
        /// every row, its order, and the end form.
        /// </summary>
        [Fact]
        public void Hosted_node_answers_a_list_names_walk_with_its_own_rows()
        {
            FakeLink link = new FakeLink("fake:names");
            TadServer tad = new TadServer(FixedClock);
            XmsgNodeHost host = new XmsgNodeHost(link, OwnNode, Routes(), new NullResponderSequenceStore(), tad);

            List<NameTableEntry> rows = Walk(link, out int requests);

            Assert.Equal(5, requests);
            Assert.Equal(4, rows.Count);
            AssertRow(rows[0], 0x00640001u, "D100");
            AssertRow(rows[1], 0x00660001u, "D102");
            AssertRow(rows[2], ((uint)OwnNode << 16) | 0x0001u, "D19999");
            AssertRow(rows[3], ((uint)OwnNode << 16) | tad.WirePort, tad.Name);
            Assert.False(rows[3].HasFreeConnections);
            Assert.Same(host.NameTable, host.Layer.NameTable);
        }

        /// <summary>
        /// A changed own system name is what the walk returns.
        /// </summary>
        [Fact]
        public void Own_system_name_can_be_changed()
        {
            FakeLink link = new FakeLink("fake:names2");
            XmsgNodeHost host = new XmsgNodeHost(link, OwnNode, Routes(), new NullResponderSequenceStore());
            host.NameTable.OwnSystemName = "RETRO";

            List<NameTableEntry> rows = Walk(link, out _);

            Assert.Equal(3, rows.Count);
            AssertRow(rows[2], ((uint)OwnNode << 16) | 0x0001u, "RETRO");
        }

        /// <summary>
        /// Sends XSGNI requests up the link until the end form comes back, collecting the rows.
        /// </summary>
        private static List<NameTableEntry> Walk(FakeLink link, out int requests)
        {
            ListNamesClient client = new ListNamesClient();
            List<NameTableEntry> rows = new List<NameTableEntry>();
            uint from = 0;
            ushort flags1 = 0x0012;
            requests = 0;

            while (requests < 20)
            {
                int before = link.Sent.Count;
                link.RaisePayload(client.BuildRequest(
                    fromMagic: from,
                    destinationNode: OwnNode,
                    sourceNode: Asker,
                    targetSystem: OwnNode,
                    sourceSystem: Asker,
                    sourcePort: AskerPort,
                    flags1: flags1));
                requests++;
                flags1++;

                // The ACK, then the reply.
                Assert.Equal(before + 2, link.Sent.Count);
                XmsgFrame ack = XmsgFrame.Parse(link.Sent[before]);
                Assert.Equal(SintranPacketSubtype.Ack, ack.Header!.Subtype);

                XmsgFrame reply = XmsgFrame.Parse(link.Sent[before + 1]);
                Assert.True(client.TryParseResponse(reply, out bool end, out NameTableEntry row));
                if (end)
                {
                    return rows;
                }

                rows.Add(row);
                from = row.Magic + 1;
            }

            Assert.Fail("the walk never reached the end form");
            return rows;
        }

        private static void AssertRow(NameTableEntry row, uint magic, string name)
        {
            Assert.Equal(magic, row.Magic);
            Assert.Equal(name, row.Name);
        }
    }
}
