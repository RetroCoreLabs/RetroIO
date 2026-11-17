//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Transport;
using RetroIO.Protocols.X25.Transport;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Who opens a configured serial line. The runtime opens every configured serial line itself
    /// when it starts, and each line is opened by exactly one part of the program: a line whose
    /// <c>X25Link</c> options are complete is run by the serial transport, so the runtime opens it;
    /// any other line belongs to the link manager (<c>InitializeLinksFromConfig</c>), so the runtime
    /// leaves it alone.
    /// </summary>
    /// <remarks>
    /// Before 2026-11-17 nothing in the program opened the port: only tests called
    /// <c>PhysicalLayer.InitializeSerialPorts</c> and <c>PhysicalLayer.Start</c>, so a serial
    /// transport configured through <c>RuntimeConfig</c> ended in Error ("interface not active").
    /// The line here is a mock port of the physical layer (test mode); no hardware is used.
    /// </remarks>
    [TestFixture]
    public class SerialPortOwnershipTests
    {
        private const string InterfaceName = "ser0";

        private ProtocolRuntime? _runtime;

        [TearDown]
        public void TearDown()
        {
            _runtime?.Dispose();
            _runtime = null;
        }

        private static RuntimeConfig ConfigWithOneSerialLine(SerialX25LinkOptions? options)
        {
            SerialInterfaceConfig serial = new SerialInterfaceConfig
            {
                Name = InterfaceName,
                PortName = "MOCK1",
                RemoteMachineId = "REMOTE1",
                X25Link = options,
            };

            RuntimeConfig config = new RuntimeConfig { TestMode = true };
            config.X25.LocalMachineId = "200";
            config.Routing.Enabled = true;
            config.Routing.SerialInterfaces.Add(serial);
            return config;
        }

        [Test]
        public void ASerialLineWithX25LinkOptions_IsOpenedByTheRuntime_AndItsTransportIsActiveAfterStart()
        {
            // The same options the serial incoming call tests use: a DCE that waits for the peer.
            _runtime = new ProtocolRuntime(NullLogger.Instance, ConfigWithOneSerialLine(new SerialX25LinkOptions(SerialX25LinkRole.Dce, false)));

            SerialTransport transport = (SerialTransport)_runtime.TransportInterfaces[InterfaceName];

            Assert.That(transport.Status, Is.EqualTo(TransportStatus.Active),
                "the serial transport must be active after start, not in Error with \"interface not active\"");
            Assert.That(_runtime.PhysicalLayer.IsSerialInterfaceActive(InterfaceName), Is.True,
                "the runtime must open the serial line itself when it starts");
        }

        [Test]
        public void ASerialLineTheLinkManagerOpens_IsNotOpenedASecondTimeByTheRuntime()
        {
            // No X25Link options: this line belongs to the link manager (see InitializeLinksFromConfig).
            _runtime = new ProtocolRuntime(NullLogger.Instance, ConfigWithOneSerialLine(null));

            Assert.That(_runtime.PhysicalLayer.IsSerialInterfaceActive(InterfaceName), Is.False,
                "the runtime must not open a line the link manager opens");

            _runtime.InitializeLinksFromConfig();

            Assert.That(_runtime.GetLink(InterfaceName), Is.Not.Null, "the link manager owns this line");
            Assert.That(_runtime.PhysicalLayer.IsSerialInterfaceActive(InterfaceName), Is.False,
                "still opened by one part of the program only");
        }

        [Test]
        public void ASerialLineTheRuntimeOpens_GetsNoLinkFromTheLinkManager()
        {
            _runtime = new ProtocolRuntime(NullLogger.Instance, ConfigWithOneSerialLine(new SerialX25LinkOptions(SerialX25LinkRole.Dce, false)));

            _runtime.InitializeLinksFromConfig();

            Assert.That(_runtime.PhysicalLayer.IsSerialInterfaceActive(InterfaceName), Is.True);
            Assert.That(_runtime.GetLink(InterfaceName), Is.Null, "the link manager must not open the same line again");
        }
    }
}
