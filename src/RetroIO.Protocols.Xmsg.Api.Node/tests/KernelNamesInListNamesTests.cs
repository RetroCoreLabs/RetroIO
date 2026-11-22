//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Api.Node;
using RetroIO.Protocols.Xmsg.ListNames;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Services;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Node.Tests
{
    /// <summary>
    /// Names claimed by tasks behind the kernel bridge show in the node's LIST-NAMES table, at the
    /// magic number they were claimed with (RetroIO BUGS.md, kernel bridge, part 3, 2026-11-10).
    /// </summary>
    public sealed class KernelNamesInListNamesTests
    {
        /// <summary>
        /// A plain name has no seat count; a name with seats reports how many are free.
        /// </summary>
        [Fact]
        public void ClaimedNames_AreListed_WithTheirMagicAndSeats()
        {
            XmsgKernel kernel = new XmsgKernel(102, 0x1111, null);
            XmsgKernelServer bridge = new XmsgKernelServer(kernel, "*DEMO", 4);
            XroutDirectory directory = new XroutDirectory();
            bridge.Directory = directory;

            XmsgMagicNumber plain = new XmsgMagicNumber(0x006604C2u);
            XmsgMagicNumber seats = new XmsgMagicNumber(0x006605D3u);
            Assert.Equal(XroutError.XRSOK, directory.RegisterName("CHATSV", kernel, plain));
            Assert.Equal(XroutError.XRSOK, directory.RegisterConnectionPort("*CHAT-ROOM", kernel, seats, 5, true));

            XmsgServerHost host = new XmsgServerHost(102);
            host.Register(bridge);
            HostedNameTable table = new HostedNameTable(102, new List<RoutingTableEntry>(), host);

            Assert.True(table.TryLookupFrom(0x006604C2u, out NameTableEntry first));
            Assert.Equal("CHATSV", first.Name);
            Assert.Equal(0x006604C2u, first.Magic);
            Assert.False(first.HasFreeConnections);

            Assert.True(table.TryLookupFrom(0x006605D3u, out NameTableEntry second));
            Assert.Equal("*CHAT-ROOM", second.Name);
            Assert.True(second.HasFreeConnections);
            Assert.Equal((ushort)5, second.FreeConnections);
        }

        /// <summary>
        /// A bridge with no name table adds nothing.
        /// </summary>
        [Fact]
        public void NoDirectory_AddsNoRows()
        {
            XmsgKernel kernel = new XmsgKernel(102, 0x1111, null);
            XmsgKernelServer bridge = new XmsgKernelServer(kernel, "*DEMO", 4);
            List<NameTableEntry> rows = new List<NameTableEntry>();

            bridge.AddNameRows(rows);

            Assert.Empty(rows);
        }
    }
}
