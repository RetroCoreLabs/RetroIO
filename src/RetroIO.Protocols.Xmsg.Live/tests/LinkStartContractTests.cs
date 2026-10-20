//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Ethernet;
using RetroIO.Protocols.Xmsg.Live.Seam;
using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Live.Tests
{
    /// <summary>
    /// Pins what <c>Start</c> returns for <see cref="EthernetLink"/> and <see cref="LapbLayerAdapter"/>,
    /// following the <see cref="ILink.Start"/> comment: true when the link was started, false when it
    /// was not Stopped or could not start. The other ILink classes (SerialLink, TcpLapbLink) already
    /// return false for a link that is not Stopped and set the status to Error on a failed start.
    /// </summary>
    public sealed class LinkStartContractTests
    {
        private const ushort LocalSystem = 19999;
        private const ushort PeerSystem = 100;

        /// <summary>
        /// A backend that never becomes active, like a raw NIC that failed to open.
        /// </summary>
        private sealed class DeadBackend : IEthernetBackend
        {
            public event EthernetPacketReceived? OnPacketReceived
            {
                add { }
                remove { }
            }

            public bool IsActive
            {
                get { return false; }
            }

            public string Description
            {
                get { return "dead"; }
            }

            public void Start()
            {
            }

            public void Stop()
            {
            }

            public void SendPacket(byte[] data, int offset, int length)
            {
            }

            public void Dispose()
            {
            }
        }

        [Fact]
        public void EthernetLink_Start_ReturnsTrueOnce_ThenFalseWhileNotStopped()
        {
            InProcessEthernetSegment segment = new InProcessEthernetSegment();
            using EthernetLink link = new EthernetLink("eth", LocalSystem, PeerSystem, segment.CreatePort("a"));

            Assert.True(link.Start());
            Assert.Equal(LinkStatus.Starting, link.Status);
            Assert.False(link.Start());
            Assert.Equal(LinkStatus.Starting, link.Status);
        }

        [Fact]
        public void EthernetLink_Start_WithABackendThatIsNotActive_ReturnsFalseAndSetsError()
        {
            using EthernetLink link = new EthernetLink("eth", LocalSystem, PeerSystem, new DeadBackend());

            Assert.False(link.Start());
            Assert.Equal(LinkStatus.Error, link.Status);
        }

        [Fact]
        public void LapbLayerAdapter_Start_ReturnsTrueOnce_ThenFalseWhileNotStopped()
        {
            LapbLayer layer = new LapbLayer(ownNode: 102);
            using LapbLayerAdapter adapter = new LapbLayerAdapter(
                "hdlc:test", new InMemoryDuplex(Array.Empty<byte>()), layer);

            Assert.True(adapter.Start());
            Assert.False(adapter.Start());
        }
    }
}
