//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Protocols.X25.Runtime.Layers;
using RetroIO.Link;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;

namespace RetroIO.Protocols.X25.Tests
{

    /// <summary>
    /// End-to-end tests for TCP-LAPB links through ProtocolRuntime
    /// Tests RX path routing, multi-link independence, and full TX/RX flows
    /// </summary>
    [TestFixture]
    public class TcpLapbEndToEndTests
    {
        private ILogger _logger = null!;
        private ILoggerFactory _loggerFactory = null!;

        [SetUp]
        public void Setup()
        {
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = _loggerFactory.CreateLogger<TcpLapbEndToEndTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        #region Multi-Link Independence Tests

        [Test]
        public void MultipleLinks_IndependentLapbStates()
        {
            // Arrange - Two separate server links
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "Link1",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55600,
                RemoteMachineId = "100"
            });

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "Link2",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55601,
                RemoteMachineId = "101"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();
            runtime.StartAllLinks();
            Thread.Sleep(200);

            // Act - Get both links
            var link1 = runtime.GetLink("Link1") as ILapbCapable;
            var link2 = runtime.GetLink("Link2") as ILapbCapable;

            // Assert - Both should have independent LAPB states
            Assert.That(link1, Is.Not.Null);
            Assert.That(link2, Is.Not.Null);
            Assert.That(link1!.LapbState, Is.EqualTo(LapbState.Disconnected));
            Assert.That(link2!.LapbState, Is.EqualTo(LapbState.Disconnected));

            // These are independent - changing one shouldn't affect the other
            Assert.That(link1, Is.Not.SameAs(link2));
        }

        [Test]
        public void MultipleLinks_IndependentSequenceNumbers()
        {
            // Arrange - Two TCP-LAPB links
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "Link1",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55602,
                RemoteMachineId = "100"
            });

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "Link2",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55603,
                RemoteMachineId = "101"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();
            runtime.StartAllLinks();
            Thread.Sleep(200);

            // Act - Get sequence numbers from both links
            var link1 = runtime.GetLink("Link1") as TcpLapbLink;
            var link2 = runtime.GetLink("Link2") as TcpLapbLink;

            Assert.That(link1, Is.Not.Null);
            Assert.That(link2, Is.Not.Null);

            var (vs1, vr1, va1) = link1!.GetSequenceNumbers();
            var (vs2, vr2, va2) = link2!.GetSequenceNumbers();

            // Assert - Both should start at 0 independently
            Assert.That(vs1, Is.EqualTo(0));
            Assert.That(vs2, Is.EqualTo(0));
        }

        #endregion

        #region LCN-Link Association Tests

        [Test]
        public void LcnLinkAssociation_AfterAssociation_SendUsesCorrectLink()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "Link100",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55604,
                RemoteMachineId = "100"
            });

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "Link101",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55605,
                RemoteMachineId = "101"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act - Associate LCN 1 with Link100, LCN 2 with Link101
            runtime.AssociateLcnWithLink(1, "Link100");
            runtime.AssociateLcnWithLink(2, "Link101");

            // Assert
            Assert.That(runtime.GetLinkForLcn(1), Is.EqualTo("Link100"));
            Assert.That(runtime.GetLinkForLcn(2), Is.EqualTo("Link101"));
        }

        [Test]
        public void LcnLinkAssociation_AfterDisassociation_ReturnsNull()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            using var runtime = new ProtocolRuntime(_logger, config);

            runtime.AssociateLcnWithLink(1, "TestLink");
            Assert.That(runtime.GetLinkForLcn(1), Is.EqualTo("TestLink"));

            // Act
            runtime.DisassociateLcnFromLink(1);

            // Assert
            Assert.That(runtime.GetLinkForLcn(1), Is.Null);
        }

        [Test]
        public void GetLcnLinkAssociations_ReturnsReadOnlyCopy()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.AssociateLcnWithLink(1, "Link1");
            runtime.AssociateLcnWithLink(2, "Link2");

            // Act
            var associations = runtime.GetLcnLinkAssociations();

            // Assert - Should be a copy, modifications shouldn't affect runtime
            Assert.That(associations.Count, Is.EqualTo(2));
            Assert.That(associations, Is.InstanceOf<IReadOnlyDictionary<ushort, string>>());
        }

        #endregion

        #region Link Statistics Tests

        [Test]
        public void LinkStatistics_NewLink_HasZeroCounters()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "NewLink",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55606,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            var link = runtime.GetLink("NewLink");
            var stats = link!.GetStatistics();

            // Assert
            Assert.That(stats.BytesTransmitted, Is.EqualTo(0));
            Assert.That(stats.BytesReceived, Is.EqualTo(0));
            Assert.That(stats.FramesTransmitted, Is.EqualTo(0));
            Assert.That(stats.FramesReceived, Is.EqualTo(0));
        }

        [Test]
        public void LinkManagerStatistics_ReflectsAllLinks()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "Link1",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55607,
                RemoteMachineId = "100"
            });

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "Link2",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55608,
                RemoteMachineId = "101"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            runtime.StartLink("Link1");
            Thread.Sleep(100);

            var stats = runtime.GetLinkManagerStatistics();

            // Assert
            Assert.That(stats.TotalLinks, Is.EqualTo(2));
            Assert.That(stats.ActiveLinks, Is.EqualTo(1));
            Assert.That(stats.StoppedLinks, Is.EqualTo(1));
        }

        #endregion

        #region Protocol Mode Tests

        [Test]
        public void ProtocolMode_X25Only_IsPreserved()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "X25Link",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55609,
                RemoteMachineId = "100",
                ProtocolMode = ProtocolDetectionMode.X25Only
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            var link = runtime.GetLink("X25Link");

            // Assert
            Assert.That(link!.ProtocolMode.Mode, Is.EqualTo(ProtocolDetectionMode.X25Only));
        }

        [Test]
        public void ProtocolMode_SintranOnly_IsPreserved()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "SintranLink",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55610,
                RemoteMachineId = "100",
                ProtocolMode = ProtocolDetectionMode.SintranOnly
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            var link = runtime.GetLink("SintranLink");

            // Assert
            Assert.That(link!.ProtocolMode.Mode, Is.EqualTo(ProtocolDetectionMode.SintranOnly));
        }

        [Test]
        public void ProtocolMode_AutoDetect_IsPreserved()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "AutoLink",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55611,
                RemoteMachineId = "100",
                ProtocolMode = ProtocolDetectionMode.AutoDetect
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            var link = runtime.GetLink("AutoLink");

            // Assert
            Assert.That(link!.ProtocolMode.Mode, Is.EqualTo(ProtocolDetectionMode.AutoDetect));
        }

        #endregion

        #region Reconnection Tests

        [Test]
        public void Link_AfterStopAndStart_ReturnsToActive()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "RestartLink",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55612,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            runtime.StartLink("RestartLink");
            Thread.Sleep(100);
            Assert.That(runtime.GetLink("RestartLink")!.Status, Is.EqualTo(LinkStatus.Active));

            runtime.StopLink("RestartLink");
            Thread.Sleep(100);
            Assert.That(runtime.GetLink("RestartLink")!.Status, Is.EqualTo(LinkStatus.Stopped));

            runtime.StartLink("RestartLink");
            Thread.Sleep(100);

            // Assert
            Assert.That(runtime.GetLink("RestartLink")!.Status, Is.EqualTo(LinkStatus.Active));
        }

        [Test]
        public void Link_AfterRestart_LapbStateResets()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "ResetLink",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55613,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();
            runtime.StartLink("ResetLink");
            Thread.Sleep(100);

            var link = runtime.GetLink("ResetLink") as ILapbCapable;

            // Act - Stop and restart
            runtime.StopLink("ResetLink");
            Thread.Sleep(100);
            runtime.StartLink("ResetLink");
            Thread.Sleep(100);

            link = runtime.GetLink("ResetLink") as ILapbCapable;

            // Assert - LAPB should be disconnected after restart
            Assert.That(link!.LapbState, Is.EqualTo(LapbState.Disconnected));
        }

        #endregion
    }
}
