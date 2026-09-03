using System;
using System.Threading;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Link;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Protocols.X25.Runtime.Layers;

namespace RetroIO.Protocols.X25.Tests
{
using X25 = RetroIO.Protocols.X25.Protocols.X25;

    /// <summary>
    /// Integration tests for ProtocolRuntime link management
    /// Tests InitializeLinksFromConfig and the new multi-link API
    /// </summary>
    [TestFixture]
    public class ProtocolRuntimeLinkTests
    {
        private ILoggerFactory _loggerFactory = null!;
        private ILogger _logger = null!;

        [SetUp]
        public void Setup()
        {
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
            _logger = _loggerFactory.CreateLogger<ProtocolRuntimeLinkTests>();
        }

        [TearDown]
        public void TearDown()
        {
            _loggerFactory?.Dispose();
        }

        #region InitializeLinksFromConfig Tests

        [Test]
        public void InitializeLinksFromConfig_EmptyConfig_CreatesNoLinks()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            using var runtime = new ProtocolRuntime(_logger, config);

            // Act
            runtime.InitializeLinksFromConfig();

            // Assert
            Assert.That(runtime.GetAllLinks().Length, Is.EqualTo(0));
        }

        [Test]
        public void InitializeLinksFromConfig_DisabledInterfaces_SkipsDisabled()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                Enabled = false,  // Disabled
                PortName = "COM99",
                RemoteMachineId = "100"
            });

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb0",
                Enabled = false,  // Disabled
                Port = 12345,
                RemoteMachineId = "200"
            });

            using var runtime = new ProtocolRuntime(_logger, config);

            // Act
            runtime.InitializeLinksFromConfig();

            // Assert
            Assert.That(runtime.GetAllLinks().Length, Is.EqualTo(0));
        }

        [Test]
        public void InitializeLinksFromConfig_TcpLapbServer_CreatesLink()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb0",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54400,
                RemoteMachineId = "100",
                LapbWindowSize = 7,
                LapbT1TimeoutMs = 3000
            });

            using var runtime = new ProtocolRuntime(_logger, config);

            // Act
            runtime.InitializeLinksFromConfig();

            // Assert
            var links = runtime.GetAllLinks();
            Assert.That(links.Length, Is.EqualTo(1));
            Assert.That(links[0].Name, Is.EqualTo("TcpLapb0"));
            Assert.That(links[0].Type, Is.EqualTo(LinkType.TcpLapb));
            Assert.That(links[0].RemoteMachineId, Is.EqualTo("100"));
        }

        [Test]
        public void InitializeLinksFromConfig_XotServer_CreatesLink()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0",
                Enabled = true,
                Mode = XotMode.Server,
                LocalPort = 54401,
                RemoteMachineId = "300"
            });

            using var runtime = new ProtocolRuntime(_logger, config);

            // Act
            runtime.InitializeLinksFromConfig();

            // Assert
            var links = runtime.GetAllLinks();
            Assert.That(links.Length, Is.EqualTo(1));
            Assert.That(links[0].Name, Is.EqualTo("XOT0"));
            Assert.That(links[0].Type, Is.EqualTo(LinkType.Xot));
            Assert.That(links[0].RemoteMachineId, Is.EqualTo("300"));
        }

        [Test]
        public void InitializeLinksFromConfig_MultipleInterfaces_CreatesAll()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb0",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54402,
                RemoteMachineId = "100"
            });

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb1",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54403,
                RemoteMachineId = "101"
            });

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0",
                Enabled = true,
                Mode = XotMode.Server,
                LocalPort = 54404,
                RemoteMachineId = "200"
            });

            using var runtime = new ProtocolRuntime(_logger, config);

            // Act
            runtime.InitializeLinksFromConfig();

            // Assert
            var links = runtime.GetAllLinks();
            Assert.That(links.Length, Is.EqualTo(3));
        }

        #endregion

        #region Link Retrieval Tests

        [Test]
        public void GetLink_AfterInitialize_ReturnsCorrectLink()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TestLink",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54405,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            var link = runtime.GetLink("TestLink");

            // Assert
            Assert.That(link, Is.Not.Null);
            Assert.That(link!.Name, Is.EqualTo("TestLink"));
        }

        [Test]
        public void GetLinkByMachineId_AfterInitialize_ReturnsCorrectLink()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "LinkToMachine100",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54406,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            var link = runtime.GetLinkByMachineId("100");

            // Assert
            Assert.That(link, Is.Not.Null);
            Assert.That(link!.Name, Is.EqualTo("LinkToMachine100"));
        }

        #endregion

        #region Link Control Tests

        [Test]
        public void StartLink_TcpLapbServer_StartsSuccessfully()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb0",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54407,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            bool started = runtime.StartLink("TcpLapb0");
            Thread.Sleep(100); // Allow time to start

            // Assert
            Assert.That(started, Is.True);
            var link = runtime.GetLink("TcpLapb0");
            Assert.That(link!.Status, Is.EqualTo(LinkStatus.Active));
        }

        [Test]
        public void StopLink_RunningLink_StopsSuccessfully()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb0",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54408,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();
            runtime.StartLink("TcpLapb0");
            Thread.Sleep(100);

            // Act
            runtime.StopLink("TcpLapb0");
            Thread.Sleep(100);

            // Assert
            var link = runtime.GetLink("TcpLapb0");
            Assert.That(link!.Status, Is.EqualTo(LinkStatus.Stopped));
        }

        [Test]
        public void StartAllLinks_MultipleLinks_StartsAll()
        {
            // Arrange - Use only TcpLapb links to avoid conflict with old XOT transport system
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb0",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55509,
                RemoteMachineId = "100"
            });

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb1",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 55510,
                RemoteMachineId = "101"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            runtime.StartAllLinks();
            Thread.Sleep(300);

            // Assert
            var links = runtime.GetAllLinks();
            Assert.That(links.Length, Is.EqualTo(2));
            for (int i = 0; i < links.Length; i++)
            {
                Assert.That(links[i].Status, Is.EqualTo(LinkStatus.Active),
                    $"Link {links[i].Name} should be active");
            }
        }

        #endregion

        #region LAPB Interface Tests

        [Test]
        public void TcpLapbLink_IsLapbCapable()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb0",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54411,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            var link = runtime.GetLink("TcpLapb0");

            // Assert
            Assert.That(link, Is.InstanceOf<ILapbCapable>());
            var lapbLink = (ILapbCapable)link!;
            Assert.That(lapbLink.LapbState, Is.EqualTo(LapbState.Disconnected));
        }

        [Test]
        public void XotLink_IsLapbCapable()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0",
                Enabled = true,
                Mode = XotMode.Server,
                LocalPort = 54412,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            var link = runtime.GetLink("XOT0");

            // Assert - XotLink now implements ILapbCapable for both RFC 1613 and Cisco modes
            Assert.That(link, Is.InstanceOf<ILapbCapable>());
        }

        #endregion

        #region Statistics Tests

        [Test]
        public void GetLinkManagerStatistics_ReturnsCorrectCounts()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "TcpLapb0",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54413,
                RemoteMachineId = "100"
            });

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0",
                Enabled = true,
                Mode = XotMode.Server,
                LocalPort = 54414,
                RemoteMachineId = "200"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Start one link
            runtime.StartLink("TcpLapb0");
            Thread.Sleep(100);

            // Act
            var stats = runtime.GetLinkManagerStatistics();

            // Assert
            Assert.That(stats.TotalLinks, Is.EqualTo(2));
            Assert.That(stats.ActiveLinks, Is.EqualTo(1));
            Assert.That(stats.StoppedLinks, Is.EqualTo(1));
        }

        #endregion

        #region Protocol Mode Tests

        [Test]
        public void InitializeLinksFromConfig_ProtocolMode_IsPreserved()
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
                Port = 54415,
                RemoteMachineId = "100",
                ProtocolMode = ProtocolDetectionMode.SintranOnly
            });

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "AutoDetectLink",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54416,
                RemoteMachineId = "101",
                ProtocolMode = ProtocolDetectionMode.AutoDetect
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();

            // Act
            var sintranLink = runtime.GetLink("SintranLink");
            var autoDetectLink = runtime.GetLink("AutoDetectLink");

            // Assert
            Assert.That(sintranLink!.ProtocolMode.Mode, Is.EqualTo(ProtocolDetectionMode.SintranOnly));
            Assert.That(autoDetectLink!.ProtocolMode.Mode, Is.EqualTo(ProtocolDetectionMode.AutoDetect));
        }

        #endregion

        #region Cleanup Tests

        [Test]
        public void RemoveLink_ExistingLink_RemovesSuccessfully()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            config.Routing.TcpLapbInterfaces.Add(new TcpLapbInterfaceConfig
            {
                Name = "ToRemove",
                Enabled = true,
                Mode = TcpMode.Server,
                Port = 54417,
                RemoteMachineId = "100"
            });

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.InitializeLinksFromConfig();
            Assert.That(runtime.GetAllLinks().Length, Is.EqualTo(1));

            // Act
            bool removed = runtime.RemoveLink("ToRemove");

            // Assert
            Assert.That(removed, Is.True);
            Assert.That(runtime.GetAllLinks().Length, Is.EqualTo(0));
            Assert.That(runtime.GetLink("ToRemove"), Is.Null);
        }

        #endregion

        #region RX Path Integration Tests

        [Test]
        public void AssociateLcnWithLink_NewLcn_AddsAssociation()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            using var runtime = new ProtocolRuntime(_logger, config);

            // Act
            runtime.AssociateLcnWithLink(1, "TestLink");

            // Assert
            Assert.That(runtime.GetLinkForLcn(1), Is.EqualTo("TestLink"));
        }

        [Test]
        public void AssociateLcnWithLink_ExistingLcn_UpdatesAssociation()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.AssociateLcnWithLink(1, "OldLink");

            // Act
            runtime.AssociateLcnWithLink(1, "NewLink");

            // Assert
            Assert.That(runtime.GetLinkForLcn(1), Is.EqualTo("NewLink"));
        }

        [Test]
        public void DisassociateLcnFromLink_ExistingLcn_RemovesAssociation()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            using var runtime = new ProtocolRuntime(_logger, config);
            runtime.AssociateLcnWithLink(1, "TestLink");

            // Act
            runtime.DisassociateLcnFromLink(1);

            // Assert
            Assert.That(runtime.GetLinkForLcn(1), Is.Null);
        }

        [Test]
        public void DisassociateLcnFromLink_NonExistentLcn_DoesNotThrow()
        {
            // Arrange
            var config = new RuntimeConfig
            {
                X25 = new X25Config { LocalMachineId = "200" },
                TestMode = true
            };

            using var runtime = new ProtocolRuntime(_logger, config);

            // Act & Assert - Should not throw
            Assert.DoesNotThrow(() => runtime.DisassociateLcnFromLink(999));
        }

        [Test]
        public void GetLcnLinkAssociations_MultipleAssociations_ReturnsAll()
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
            runtime.AssociateLcnWithLink(3, "Link1");

            // Act
            var associations = runtime.GetLcnLinkAssociations();

            // Assert
            Assert.That(associations.Count, Is.EqualTo(3));
            Assert.That(associations[1], Is.EqualTo("Link1"));
            Assert.That(associations[2], Is.EqualTo("Link2"));
            Assert.That(associations[3], Is.EqualTo("Link1"));
        }

        #endregion
    }
}
