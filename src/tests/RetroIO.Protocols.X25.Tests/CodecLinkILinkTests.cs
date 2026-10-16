using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using RetroIO.Link;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for CodecLink (the transport-agnostic SLIP/HDLC LAPB link) over an in-memory transport.
    /// </summary>
    /// <remarks>
    /// Uses a fake loopback ILinkTransport so the full ILink path is exercised without a real TCP socket or
    /// serial port. Because CodecLink is transport-agnostic, the same behavior applies to SLIP or HDLC
    /// framing over TCP or serial. Both framing methods are covered here; the two directions establish LAPB
    /// automatically and carry X.25 and SINTRAN traffic with protocol auto-detection.
    /// </remarks>
    [TestFixture]
    public class CodecLinkILinkTests
    {
        // In-memory transport that delivers each write straight to its peer's receive event.
        private sealed class FakeLinkTransport : ILinkTransport
        {
            public FakeLinkTransport? Peer;
            private bool _open;

            public FakeLinkTransport(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public LinkType LinkType => LinkType.TcpLapb;

            public bool IsOpen => _open;

            public event LinkTransportDataReceived? DataReceived;

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
                Peer?.Deliver(data);
            }

            public void Dispose()
            {
            }

            private void Deliver(ReadOnlySpan<byte> data)
            {
                DataReceived?.Invoke(Name, data);
            }
        }

        private ILoggerFactory _factory = null!;
        private ILogger _logger = null!;

        [SetUp]
        public void Setup()
        {
            _factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            _logger = _factory.CreateLogger<CodecLinkILinkTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _factory?.Dispose();
        }

        [TestCase(FramingType.Slip)]
        [TestCase(FramingType.Hdlc)]
        public void CodecLink_OverLoopback_EstablishesAndCarriesX25AndSintran(FramingType framing)
        {
            FakeLinkTransport transportA = new FakeLinkTransport("A");
            FakeLinkTransport transportB = new FakeLinkTransport("B");
            transportA.Peer = transportB;
            transportB.Peer = transportA;

            CodecLinkConfig configA = new CodecLinkConfig
            {
                Name = "A",
                Framing = framing,
                IsPrimary = true,
                LapbAutoConnect = true,
                ProtocolMode = new ProtocolModeConfig { Mode = ProtocolDetectionMode.AutoDetect }
            };
            CodecLinkConfig configB = new CodecLinkConfig
            {
                Name = "B",
                Framing = framing,
                IsPrimary = false,
                LapbAutoConnect = false,
                ProtocolMode = new ProtocolModeConfig { Mode = ProtocolDetectionMode.AutoDetect }
            };

            using CodecLink linkA = new CodecLink(_logger, configA, transportA);
            using CodecLink linkB = new CodecLink(_logger, configB, transportB);

            // The link is protocol-agnostic: both X.25 and SINTRAN arrive as opaque payloads.
            List<byte[]> payloadsOnB = new List<byte[]>();
            linkB.PayloadReceived += (link, payload, length) => payloadsOnB.Add(payload);

            // Bring the secondary up first, then the primary (which auto-establishes LAPB over the loopback).
            linkB.Start();
            linkA.Start();

            Assert.That(linkA.Status, Is.EqualTo(LinkStatus.Active), "primary link should be active");

            // X.25 packet: GFI/LCN/PTI header, delivered opaque.
            byte[] x25 = new byte[] { 0x10, 0x01, 0x0B, 0xAA, 0xBB };
            Assert.That(linkA.SendData(x25), Is.True, "LAPB should be connected");
            Assert.That(payloadsOnB.Count, Is.EqualTo(1), "B should receive one payload");
            Assert.That(payloadsOnB[0], Is.EqualTo(x25));

            // SINTRAN frame: 0x21 0x13 markers with TAD protocol id 0xDD; same opaque link path.
            byte[] sintran = new byte[] { 0x21, 0x13, 0xDD, 0x01, 0x02, 0x03 };
            Assert.That(linkA.SendData(sintran), Is.True);
            Assert.That(payloadsOnB.Count, Is.EqualTo(2), "B should receive a second payload");
            Assert.That(payloadsOnB[1], Is.EqualTo(sintran));
        }

        [Test]
        public void LinkManager_CreateCodecLink_RegistersAndWorks()
        {
            FakeLinkTransport transportA = new FakeLinkTransport("A");
            FakeLinkTransport transportB = new FakeLinkTransport("B");
            transportA.Peer = transportB;
            transportB.Peer = transportA;

            using LinkManager manager = new LinkManager(_logger);

            CodecLinkConfig configA = new CodecLinkConfig
            {
                Name = "A",
                Framing = FramingType.Slip,
                IsPrimary = true,
                LapbAutoConnect = true,
                ProtocolMode = new ProtocolModeConfig { Mode = ProtocolDetectionMode.AutoDetect }
            };
            CodecLinkConfig configB = new CodecLinkConfig
            {
                Name = "B",
                Framing = FramingType.Slip,
                IsPrimary = false,
                LapbAutoConnect = false,
                ProtocolMode = new ProtocolModeConfig { Mode = ProtocolDetectionMode.AutoDetect }
            };

            CodecLink linkA = manager.CreateCodecLink(configA, transportA);
            CodecLink linkB = manager.CreateCodecLink(configB, transportB);

            Assert.That(manager.GetLink("A"), Is.SameAs(linkA), "LinkManager should register the link by name");
            Assert.That(manager.LinkCount, Is.EqualTo(2));

            List<byte[]> x25OnB = new List<byte[]>();
            linkB.PayloadReceived += (link, payload, length) => x25OnB.Add(payload);

            linkB.Start();
            linkA.Start();

            byte[] packet = new byte[] { 0x10, 0x01, 0x0B, 0x42 };
            Assert.That(linkA.SendData(packet), Is.True);
            Assert.That(x25OnB.Count, Is.EqualTo(1));
            Assert.That(x25OnB[0], Is.EqualTo(packet));
        }
    }
}
