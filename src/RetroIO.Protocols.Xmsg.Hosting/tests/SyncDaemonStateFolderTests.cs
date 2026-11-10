//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Hosting.Tests
{
    /// <summary>
    /// The sync daemon keeps its ledger and reads its password list in the state folder it is
    /// given, and reports through the log it is given.
    /// </summary>
    /// <remarks>
    /// In the runner both files sat beside the executable. A service's executable folder is not
    /// where its data belongs, and is often not writable at all.
    /// </remarks>
    public sealed class SyncDaemonStateFolderTests
    {
        [Fact]
        public void TheLedgerIsInTheStateFolder_AndTheLinesReachTheLog()
        {
            using TempFolder state = new TempFolder();
            using TempFolder watched = new TempFolder();
            List<string> lines = new List<string>();

            using TempFolder nodeState = new TempFolder();
            XmsgHostedNode node = XmsgNodeBuilder.Build(
                new NullLink(), 19999,
                new RoutingTableEntry[] { new RoutingTableEntry(100, XroutConnectionType.Neighbour, 1, 1, 0) },
                new XmsgNodeOptions { StateDirectory = nodeState.Path });

            SyncDaemon daemon = new SyncDaemon(
                watched.Path, "D100", "SYSTEM", node.Host, 100, () => false,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), null, state.Path, lines.Add);

            string ledger = XmsgStateFiles.PathOf(state.Path, XmsgStateFiles.SyncLedger);
            Assert.Contains(lines, line => line.Contains(ledger, StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(daemon);
        }

        [Fact]
        public void AMissingStateFolderArgumentIsRefused()
        {
            using TempFolder watched = new TempFolder();
            using TempFolder nodeState = new TempFolder();
            XmsgHostedNode node = XmsgNodeBuilder.Build(
                new NullLink(), 19999,
                new RoutingTableEntry[] { new RoutingTableEntry(100, XroutConnectionType.Neighbour, 1, 1, 0) },
                new XmsgNodeOptions { StateDirectory = nodeState.Path });

            Assert.Throws<ArgumentNullException>(() => new SyncDaemon(
                watched.Path, "D100", "SYSTEM", node.Host, 100, () => false,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), null, null!, null));
        }

        /// <summary>
        /// A link that is never up and never delivers anything.
        /// </summary>
        private sealed class NullLink : ILink
        {
            public event LinkPayloadReceived? PayloadReceived
            {
                add { }
                remove { }
            }

            public event LinkStatusChanged? StatusChanged
            {
                add { }
                remove { }
            }

            public string Name
            {
                get { return "fake:null"; }
            }

            public LinkStatus Status
            {
                get { return LinkStatus.Stopped; }
            }

            public bool Start()
            {
                return false;
            }

            public void Stop()
            {
            }

            public void Dispose()
            {
            }

            public bool SendData(ReadOnlySpan<byte> payload)
            {
                return false;
            }
        }
    }
}
