//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Protocols.X25.Runtime.Layers;
using RetroIO.Link;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Tests for LinkManager - link creation, management, and routing
    /// </summary>
    [TestFixture]
    public class LinkManagerTests
    {
        private ILogger _logger = null!;
        private ILoggerFactory _loggerFactory = null!;
        private LinkManager _linkManager = null!;

        [SetUp]
        public void Setup()
        {
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = _loggerFactory.CreateLogger<LinkManagerTests>();
            _linkManager = new LinkManager(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            _linkManager?.Dispose();
            _loggerFactory?.Dispose();
        }

        #region Link Creation Tests

        [Test]
        public void CreateSerialLink_ValidConfig_ReturnsLink()
        {
            // Arrange
            var config = new SerialLinkConfig
            {
                Name = "Serial0",
                PortName = "COM99", // Non-existent port
                BaudRate = 115200,
                RemoteMachineId = "MACHINE1"
            };

            // Act
            var link = _linkManager.CreateSerialLink(config);

            // Assert
            Assert.That(link, Is.Not.Null);
            Assert.That(link.Name, Is.EqualTo("Serial0"));
            Assert.That(link.Type, Is.EqualTo(LinkType.Serial));
            Assert.That(link.RemoteMachineId, Is.EqualTo("MACHINE1"));
            Assert.That(_linkManager.LinkCount, Is.EqualTo(1));
        }

        [Test]
        public void CreateTcpLapbLink_ValidConfig_ReturnsLink()
        {
            // Arrange
            var config = new TcpLapbLinkConfig
            {
                Name = "TcpLapb0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 12345,
                RemoteMachineId = "MACHINE2"
            };

            // Act
            var link = _linkManager.CreateTcpLapbLink(config);

            // Assert
            Assert.That(link, Is.Not.Null);
            Assert.That(link.Name, Is.EqualTo("TcpLapb0"));
            Assert.That(link.Type, Is.EqualTo(LinkType.TcpLapb));
            Assert.That(link.RemoteMachineId, Is.EqualTo("MACHINE2"));
        }

        [Test]
        public void CreateXotLink_ValidConfig_ReturnsLink()
        {
            // Arrange
            var config = new XotLinkConfig
            {
                Name = "Xot0",
                Mode = TcpMode.Client,
                RemoteAddress = "127.0.0.1",
                Port = 1998,
                RemoteMachineId = "MACHINE3"
            };

            // Act
            var link = _linkManager.CreateXotLink(config);

            // Assert
            Assert.That(link, Is.Not.Null);
            Assert.That(link.Name, Is.EqualTo("Xot0"));
            Assert.That(link.Type, Is.EqualTo(LinkType.Xot));
            Assert.That(link.RemoteMachineId, Is.EqualTo("MACHINE3"));
        }

        [Test]
        public void CreateLink_DuplicateName_ThrowsException()
        {
            // Arrange
            var config = new XotLinkConfig
            {
                Name = "Link0",
                RemoteMachineId = "MACHINE1"
            };
            _linkManager.CreateXotLink(config);

            // Act & Assert
            var config2 = new XotLinkConfig
            {
                Name = "Link0",
                RemoteMachineId = "MACHINE2"
            };
            Assert.Throws<InvalidOperationException>(() => _linkManager.CreateXotLink(config2));
        }

        #endregion

        #region Link Retrieval Tests

        [Test]
        public void GetLink_ExistingLink_ReturnsLink()
        {
            // Arrange
            var config = new XotLinkConfig
            {
                Name = "TestLink",
                RemoteMachineId = "MACHINE1"
            };
            _linkManager.CreateXotLink(config);

            // Act
            var link = _linkManager.GetLink("TestLink");

            // Assert
            Assert.That(link, Is.Not.Null);
            Assert.That(link!.Name, Is.EqualTo("TestLink"));
        }

        [Test]
        public void GetLink_NonExistingLink_ReturnsNull()
        {
            // Act
            var link = _linkManager.GetLink("NonExistent");

            // Assert
            Assert.That(link, Is.Null);
        }

        [Test]
        public void GetLinkByMachineId_ExistingMachine_ReturnsLink()
        {
            // Arrange
            var config = new XotLinkConfig
            {
                Name = "TestLink",
                RemoteMachineId = "TARGET_MACHINE"
            };
            _linkManager.CreateXotLink(config);

            // Act
            var link = _linkManager.GetLinkByMachineId("TARGET_MACHINE");

            // Assert
            Assert.That(link, Is.Not.Null);
            Assert.That(link!.Name, Is.EqualTo("TestLink"));
        }

        [Test]
        public void GetAllLinks_MultipleLinks_ReturnsAll()
        {
            // Arrange
            _linkManager.CreateXotLink(new XotLinkConfig { Name = "Link1", RemoteMachineId = "M1" });
            _linkManager.CreateXotLink(new XotLinkConfig { Name = "Link2", RemoteMachineId = "M2" });
            _linkManager.CreateXotLink(new XotLinkConfig { Name = "Link3", RemoteMachineId = "M3" });

            // Act
            var links = _linkManager.GetAllLinks();

            // Assert
            Assert.That(links.Length, Is.EqualTo(3));
        }

        #endregion

        #region Link Removal Tests

        [Test]
        public void RemoveLink_ExistingLink_RemovesSuccessfully()
        {
            // Arrange
            _linkManager.CreateXotLink(new XotLinkConfig { Name = "ToRemove", RemoteMachineId = "M1" });
            Assert.That(_linkManager.LinkCount, Is.EqualTo(1));

            // Act
            bool removed = _linkManager.RemoveLink("ToRemove");

            // Assert
            Assert.That(removed, Is.True);
            Assert.That(_linkManager.LinkCount, Is.EqualTo(0));
            Assert.That(_linkManager.GetLink("ToRemove"), Is.Null);
        }

        [Test]
        public void RemoveLink_NonExistingLink_ReturnsFalse()
        {
            // Act
            bool removed = _linkManager.RemoveLink("NonExistent");

            // Assert
            Assert.That(removed, Is.False);
        }

        #endregion

        #region LAPB Interface Tests

        [Test]
        public void GetLapbState_LapbCapableLink_ReturnsState()
        {
            // Arrange
            var config = new TcpLapbLinkConfig
            {
                Name = "TcpLapb0",
                Mode = TcpMode.Server,
                Port = 23456,
                RemoteMachineId = "M1"
            };
            _linkManager.CreateTcpLapbLink(config);

            // Act
            var state = _linkManager.GetLapbState("TcpLapb0");

            // Assert
            Assert.That(state, Is.Not.Null);
            Assert.That(state, Is.EqualTo(LapbState.Disconnected));
        }

        [Test]
        public void GetLapbState_XotLink_ReturnsDisconnected()
        {
            // Arrange - RFC 1613 mode (default, no LAPB framing)
            _linkManager.CreateXotLink(new XotLinkConfig { Name = "Xot0", RemoteMachineId = "M1" });

            // Act
            var state = _linkManager.GetLapbState("Xot0");

            // Assert - XotLink now implements ILapbCapable, returns Disconnected in RFC 1613 mode
            Assert.That(state, Is.EqualTo(LapbState.Disconnected));
        }

        [Test]
        public void GetLapbStatistics_LapbCapableLink_ReturnsStatistics()
        {
            // Arrange
            var config = new TcpLapbLinkConfig
            {
                Name = "TcpLapb0",
                Mode = TcpMode.Server,
                Port = 23457,
                RemoteMachineId = "M1"
            };
            _linkManager.CreateTcpLapbLink(config);

            // Act
            var stats = _linkManager.GetLapbStatistics("TcpLapb0");

            // Assert
            Assert.That(stats, Is.Not.Null);
        }

        #endregion

        #region Statistics Tests

        [Test]
        public void GetStatistics_NoLinks_ReturnsZeros()
        {
            // Act
            var stats = _linkManager.GetStatistics();

            // Assert
            Assert.That(stats.TotalLinks, Is.EqualTo(0));
            Assert.That(stats.ActiveLinks, Is.EqualTo(0));
            Assert.That(stats.TotalBytesReceived, Is.EqualTo(0));
        }

        [Test]
        public void GetStatistics_WithLinks_ReturnsCounts()
        {
            // Arrange
            _linkManager.CreateXotLink(new XotLinkConfig { Name = "Link1", RemoteMachineId = "M1" });
            _linkManager.CreateXotLink(new XotLinkConfig { Name = "Link2", RemoteMachineId = "M2" });

            // Act
            var stats = _linkManager.GetStatistics();

            // Assert
            Assert.That(stats.TotalLinks, Is.EqualTo(2));
            Assert.That(stats.StoppedLinks, Is.EqualTo(2)); // Links not started
        }

        #endregion

        #region Disposal Tests

        [Test]
        public void Dispose_WithLinks_DisposesAll()
        {
            // Arrange
            _linkManager.CreateXotLink(new XotLinkConfig { Name = "Link1", RemoteMachineId = "M1" });
            _linkManager.CreateXotLink(new XotLinkConfig { Name = "Link2", RemoteMachineId = "M2" });

            // Act
            _linkManager.Dispose();

            // Assert - Should not throw
            Assert.That(_linkManager.LinkCount, Is.EqualTo(0));
        }

        #endregion
    }
}
