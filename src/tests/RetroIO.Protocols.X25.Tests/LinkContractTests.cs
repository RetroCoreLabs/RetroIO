//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link;
using RetroIO.Link.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Pins two promises of <see cref="ILink"/>: <c>StatusChanged</c> fires only on a real
    /// transition, and <c>SendData</c> answers <c>false</c> when nothing could be sent.
    /// </summary>
    [TestFixture]
    public class LinkContractTests
    {
        private ILoggerFactory _factory = null!;
        private ILogger _logger = null!;

        [SetUp]
        public void Setup()
        {
            _factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            _logger = _factory.CreateLogger<LinkContractTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _factory?.Dispose();
        }

        // Stand-in transport that only reports open and closed; nothing is carried.
        private sealed class NullTransport : ILinkTransport
        {
            private bool _open;

            public string Name => "null";

            public LinkType LinkType => LinkType.TcpLapb;

            public bool IsOpen => _open;

            public event LinkTransportDataReceived? DataReceived
            {
                add { }
                remove { }
            }

            public event LinkTransportOpened? Opened;

            public event LinkTransportClosed? Closed;

            public bool Open()
            {
                _open = true;
                Opened?.Invoke(Name);
                return true;
            }

            public void Close()
            {
                _open = false;
                Closed?.Invoke(Name, "closed");
            }

            public void Write(ReadOnlySpan<byte> data)
            {
            }

            public void Dispose()
            {
            }
        }

        private static int FreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        // Calls the private SetStatus of a link twice with the same status and returns the number of
        // events seen for the second (repeated) call.
        private static int CountEventsForRepeatedStatus(ILink link)
        {
            MethodInfo setStatus = link.GetType().GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.That(setStatus, Is.Not.Null);

            setStatus.Invoke(link, new object[] { LinkStatus.Error, "first" });

            int seen = 0;
            link.StatusChanged += (_, _, _, _) => seen++;
            setStatus.Invoke(link, new object[] { LinkStatus.Error, "again" });
            return seen;
        }

        [Test]
        public void CodecLink_Stop_DoesNotRaiseAStoppedToStoppedEvent()
        {
            CodecLinkConfig config = new CodecLinkConfig { Name = "C", IsPrimary = false, LapbAutoConnect = false };
            using CodecLink link = new CodecLink(_logger, config, new NullTransport());
            List<string> transitions = new List<string>();
            link.StatusChanged += (_, oldStatus, newStatus, _) => transitions.Add(oldStatus + ">" + newStatus);

            link.Start();
            link.Stop();

            Assert.That(transitions, Does.Not.Contain("Stopped>Stopped"));
            Assert.That(transitions, Is.EqualTo(new[] { "Stopped>Starting", "Starting>Active", "Active>Stopped" }));
        }

        [Test]
        public void SerialLink_RepeatedStatus_RaisesNoEvent()
        {
            using SerialLink link = new SerialLink(_logger, new SerialLinkConfig { Name = "S" });
            Assert.That(CountEventsForRepeatedStatus(link), Is.EqualTo(0));
        }

        [Test]
        public void TcpLapbLink_RepeatedStatus_RaisesNoEvent()
        {
            using TcpLapbLink link = new TcpLapbLink(_logger, new TcpLapbLinkConfig { Name = "T" });
            Assert.That(CountEventsForRepeatedStatus(link), Is.EqualTo(0));
        }

        [Test]
        public void XotLink_RepeatedStatus_RaisesNoEvent()
        {
            using XotLink link = new XotLink(_logger, new XotLinkConfig { Name = "X" });
            Assert.That(CountEventsForRepeatedStatus(link), Is.EqualTo(0));
        }

        [Test]
        public void XotLink_Rfc1613_SendDataWithNoConnection_ReturnsFalse()
        {
            XotLinkConfig config = new XotLinkConfig
            {
                Name = "XotNoPeer",
                Mode = TcpMode.Server,
                Port = FreePort(),
                UseLapbFraming = false
            };
            using XotLink link = new XotLink(_logger, config);
            Assert.That(link.Start(), Is.True);
            Assert.That(link.Status, Is.EqualTo(LinkStatus.Active));

            Assert.That(link.SendData(new byte[] { 0x10, 0x01, 0x0B }), Is.False);
        }

        [Test]
        public void XotLink_Rfc1613_SendDataTheCodecRejects_ReturnsFalse()
        {
            XotLinkConfig config = new XotLinkConfig
            {
                Name = "XotBadLength",
                Mode = TcpMode.Server,
                Port = FreePort(),
                UseLapbFraming = false
            };
            using XotLink link = new XotLink(_logger, config);
            Assert.That(link.Start(), Is.True);

            using TcpClient peer = new TcpClient();
            peer.Connect(IPAddress.Loopback, config.Port);

            // Wait until the link has accepted the peer: a legal packet then goes out and arrives.
            byte[] legal = new byte[] { 0x10, 0x01, 0x0B };
            bool accepted = false;
            for (int i = 0; i < 100 && !accepted; i++)
            {
                link.SendData(legal);
                Thread.Sleep(50);
                accepted = peer.Available >= 7;
            }

            Assert.That(accepted, Is.True, "the link should have accepted the peer");
            Assert.That(link.SendData(legal), Is.True);

            // Two bytes is under the 3 byte minimum of an X.25 packet: the codec refuses it.
            Assert.That(link.SendData(new byte[] { 0x10, 0x01 }), Is.False);
        }
    }
}
