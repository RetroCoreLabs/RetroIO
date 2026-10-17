//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.ListRouting;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Diagnostic: dumps the XSGSY reply the node produces for the exact list-route request
    /// captured live from node 100, so it can be compared to a known-good captured response
    /// without hitting the live kernel.
    /// </summary>
    public sealed class XsgsyReplyDiagnosticTests
    {
        private readonly ITestOutputHelper _output;

        public XsgsyReplyDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void DumpReplyForLiveRequest()
        {
            // The exact 100->103 list-route request captured on the wire.
            byte[] reqInfo = Convert.FromHexString(
                "2113000E0067006400010100DD12210086C400670000006402A60100014B000401020067");
            XmsgFrame request = XmsgFrame.Parse(reqInfo);

            XmsgNode node = new XmsgNode(103, 0x00);
            node.AcknowledgeData = false;
            node.RoutingTable = new InMemoryRoutingTable(new[]
            {
                new RoutingTableEntry(103, XroutConnectionType.Local, 103, 0, 0),
            });

            XmsgFrame? reply = node.HandleFrame(request);

            _output.WriteLine("request : " + Convert.ToHexString(reqInfo));
            _output.WriteLine("reply   : " + (reply == null ? "null" : Convert.ToHexString(reply.ToArray())));
            // A known-good captured XSGSY response (102 exchange) for structural comparison:
            _output.WriteLine("good-ref: 2113000E0066006400F40100DC202100866000 6602A5006602A50100010000100102006402020004030200640402 0000".Replace(" ", ""));

            Assert.NotNull(reply);
        }
    }
}
