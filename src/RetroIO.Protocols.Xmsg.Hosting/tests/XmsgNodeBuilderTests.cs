//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using RetroIO.Protocols.Xmsg.Ethernet;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Live;
using RetroIO.Protocols.Xmsg.Live.Seam;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Servers.Fa;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Hosting.Tests
{
    /// <summary>
    /// <see cref="XmsgNodeBuilder"/> and <see cref="XmsgNodeShutdown"/>: the node the runner and a
    /// hosted service both build.
    /// </summary>
    public sealed class XmsgNodeBuilderTests
    {
        // Captured reachability request 100 -> 102 and our captured reply 102 -> 100. The
        // simplest exchange that proves the built node answers on its link.
        private const string Request = "2113001900660064FFFF0001DE08";
        private const string Reply = "2113001300640066FFFF0001DE0E";

        [Fact]
        public void TheBuiltNodeAnswersOnItsLink()
        {
            using TempFolder state = new TempFolder();
            RecordingLink link = new RecordingLink();

            XmsgHostedNode node = XmsgNodeBuilder.Build(link, 102, Routes(), new XmsgNodeOptions { StateDirectory = state.Path });
            link.DeliverUp(Convert.FromHexString(Request));

            Assert.Single(link.Sent);
            Assert.Equal(Convert.FromHexString(Reply), link.Sent[0]);
            Assert.Null(node.FileServer);
        }

        [Fact]
        public void TheTerminalServerIsAlwaysRegistered_AndTheFileServerOnlyWhenGiven()
        {
            using TempFolder state = new TempFolder();
            using TempFolder served = new TempFolder();

            XmsgHostedNode withoutFiles = XmsgNodeBuilder.Build(
                new RecordingLink(), 102, Routes(), new XmsgNodeOptions { StateDirectory = state.Path });
            Assert.Equal(new[] { "*TADADM" }, Names(withoutFiles));

            FaServer? files = XmsgNodeBuilder.CreateFileServer(
                new FileServerConfig { Root = served.Path }, state.Path, state.Path, null);
            XmsgHostedNode withFiles = XmsgNodeBuilder.Build(
                new RecordingLink(), 102, Routes(), new XmsgNodeOptions { StateDirectory = state.Path, FileServer = files });
            Assert.Equal(new[] { "*TADADM", FaServer.ServerName }, Names(withFiles));
        }

        [Fact]
        public void StateFilesAreNamedInTheStateFolder_NotBesideTheExecutable()
        {
            using TempFolder state = new TempFolder();
            List<string> lines = new List<string>();

            XmsgNodeBuilder.Build(
                new RecordingLink(), 102, Routes(),
                new XmsgNodeOptions { StateDirectory = state.Path, Log = lines.Add });

            string sequence = XmsgStateFiles.PathOf(state.Path, XmsgStateFiles.Sequence);
            string seed = XmsgStateFiles.PathOf(state.Path, XmsgStateFiles.LinkSeed);
            Assert.Contains(lines, line => line.Contains(sequence, StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains(seed, StringComparison.Ordinal));
        }

        [Fact]
        public void TheInboundQueueSettingReachesTheHost()
        {
            using TempFolder state = new TempFolder();
            RecordingLink link = new RecordingLink();

            XmsgHostedNode node = XmsgNodeBuilder.Build(
                link, 102, Routes(),
                new XmsgNodeOptions { StateDirectory = state.Path, DeliverInboundOnPump = true });
            link.DeliverUp(Convert.FromHexString(Request));

            Assert.Empty(link.Sent);
            node.Host.Pump();
            Assert.Single(link.Sent);
        }

        [Fact]
        public void AMissingStateFolderIsRefused()
        {
            Assert.Throws<ArgumentNullException>(
                () => XmsgNodeBuilder.Build(new RecordingLink(), 102, Routes(), new XmsgNodeOptions()));
        }

        [Fact]
        public void NoRootMeansNoFileServer()
        {
            using TempFolder state = new TempFolder();
            List<string> lines = new List<string>();

            Assert.Null(XmsgNodeBuilder.CreateFileServer(null, state.Path, state.Path, lines.Add));
            Assert.Null(XmsgNodeBuilder.CreateFileServer(new FileServerConfig(), state.Path, state.Path, lines.Add));
            Assert.All(lines, line => Assert.Contains("file server OFF", line));
        }

        [Fact]
        public void ADisabledFileServerIsNotBuilt()
        {
            using TempFolder state = new TempFolder();

            FaServer? server = XmsgNodeBuilder.CreateFileServer(
                new FileServerConfig { Root = state.Path, Enabled = false }, state.Path, state.Path, null);

            Assert.Null(server);
        }

        [Fact]
        public void ARelativeRootIsTakenFromTheBaseFolder_AndTheConnectionFileGoesToTheStateFolder()
        {
            using TempFolder baseFolder = new TempFolder();
            using TempFolder state = new TempFolder();

            FaServer? server = XmsgNodeBuilder.CreateFileServer(
                new FileServerConfig { Root = "served" }, baseFolder.Path, state.Path, null);

            Assert.NotNull(server);
            Assert.True(Directory.Exists(Path.Combine(baseFolder.Path, "served")));
            Assert.True(File.Exists(XmsgStateFiles.PathOf(state.Path, XmsgStateFiles.FaConnection)));
            Assert.False(File.Exists(XmsgStateFiles.PathOf(baseFolder.Path, XmsgStateFiles.FaConnection)));
        }

        [Fact]
        public void ShutdownWithNoSessionsOpen_SendsNothing()
        {
            using TempFolder state = new TempFolder();
            RecordingLink link = new RecordingLink();
            XmsgHostedNode node = XmsgNodeBuilder.Build(link, 102, Routes(), new XmsgNodeOptions { StateDirectory = state.Path });

            Assert.Equal(0, XmsgNodeShutdown.LogOutTadSessions(node, null));
            Assert.Empty(link.Sent);
        }

        [Fact]
        public void ALapbLinkThatNeverCameUpIsNotSentADisc()
        {
            LapbLayer lapb = new LapbLayer(102);

            Assert.False(XmsgNodeShutdown.DisconnectLapb(lapb, null));
        }

        [Fact]
        public async Task AnEthernetLinkWithNothingParked_IsReadyToCloseAtOnce()
        {
            InProcessEthernetSegment segment = new InProcessEthernetSegment();
            using EthernetLink link = new EthernetLink("eth", 19999, 100, segment.CreatePort("a"));

            Assert.Equal(0, link.QueuedDatagrams);
            Assert.True(await XmsgNodeShutdown.WaitForEthernetQueueAsync(link, TimeSpan.FromSeconds(2), null));
        }

        private static RoutingTableEntry[] Routes()
        {
            return new RoutingTableEntry[]
            {
                new RoutingTableEntry(100, XroutConnectionType.Neighbour, 1, 1, 0),
            };
        }

        private static string[] Names(XmsgHostedNode node)
        {
            IReadOnlyList<XmsgServerInfo> servers = node.Host.ServerHost.DescribeServers();
            string[] names = new string[servers.Count];
            for (int i = 0; i < servers.Count; i++)
            {
                names[i] = servers[i].Name;
            }

            return names;
        }

        /// <summary>
        /// An Active link that records what the node sends and lets a test push a payload up.
        /// </summary>
        private sealed class RecordingLink : ILink
        {
            public event LinkPayloadReceived? PayloadReceived;

            public event LinkStatusChanged? StatusChanged;

            public string Name
            {
                get { return "fake:builder"; }
            }

            public LinkStatus Status { get; private set; } = LinkStatus.Active;

            public List<byte[]> Sent { get; } = new List<byte[]>();

            public bool Start()
            {
                Status = LinkStatus.Active;
                return true;
            }

            public void Stop()
            {
                LinkStatus previous = Status;
                Status = LinkStatus.Stopped;
                StatusChanged?.Invoke(this, previous, Status, "stopped");
            }

            public void Dispose()
            {
                Stop();
            }

            public bool SendData(ReadOnlySpan<byte> payload)
            {
                Sent.Add(payload.ToArray());
                return true;
            }

            public void DeliverUp(byte[] payload)
            {
                PayloadReceived?.Invoke(this, payload, payload.Length);
            }
        }
    }
}
