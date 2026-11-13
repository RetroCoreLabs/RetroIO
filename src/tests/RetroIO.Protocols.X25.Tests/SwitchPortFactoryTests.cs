//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The one place that turns a configured transport into a port class, and the loader rules that go with it.
    /// </summary>
    [TestFixture]
    public class SwitchPortFactoryTests
    {
        private static SwitchConfig Load(string text)
        {
            return SwitchConfigLoader.Load(IniConfig.Parse(new List<string>(text.Split((char)10))));
        }

        /// <summary>
        /// Each transport the build can run gives the class that runs it.
        /// </summary>
        [TestCase(PortTransport.Xot, typeof(XotSwitchPort))]
        [TestCase(PortTransport.HdlcTcp, typeof(HdlcTcpSwitchPort))]
        [TestCase(PortTransport.HdlcPpp, typeof(HdlcTcpSwitchPort))]
        [TestCase(PortTransport.HdlcSerial, typeof(HdlcSerialSwitchPort))]
        public void ATransportThatCanRun_GivesTheClassThatRunsIt(PortTransport transport, Type expected)
        {
            using IDisposable? port = SwitchPortFactory.Create(new PortConfig { Id = 2, Transport = transport, Mode = PortMode.Dce, Host = "127.0.0.1", Port = 0, SerialDevice = "COM-FACTORY-TEST" }) as IDisposable;

            Assert.That(port, Is.InstanceOf(expected));
        }

        /// <summary>
        /// A transport value that is not one of the known ones is refused, naming the port. This test stays whatever
        /// transports are added later.
        /// </summary>
        [Test]
        public void ATransportValueNobodyKnows_IsRefused_NamingThePort_AndIsNotRunAsXot()
        {
            PortConfig config = new PortConfig { Id = 7, Type = PortType.Trunk, Transport = (PortTransport)99 };

            NotSupportedException ex = Assert.Throws<NotSupportedException>(() => SwitchPortFactory.Create(config))!;

            Assert.That(ex.Message, Does.Contain("Port 7"));
            Assert.That(ex.Message, Does.Contain("99"));
            Assert.That(ex.Message, Does.Contain("not supported"));
        }

        /// <summary>
        /// The configuration names of the transports are the ones the C emulator uses.
        /// </summary>
        [TestCase(PortTransport.Xot, "xot")]
        [TestCase(PortTransport.HdlcTcp, "hdlc-tcp")]
        [TestCase(PortTransport.HdlcPpp, "hdlc-ppp")]
        [TestCase(PortTransport.HdlcSerial, "hdlc-serial")]
        public void TheTransportNames_AreTheConfigurationNames(PortTransport transport, string name)
        {
            Assert.That(SwitchPortFactory.Describe(transport), Is.EqualTo(name));
        }

        /// <summary>
        /// The loader refuses a transport name it does not know, where it used to fall back to XOT.
        /// </summary>
        [Test]
        public void TheLoader_RefusesATransportNameItDoesNotKnow()
        {
            string ini = "[switch]\ndnic = 2340\n\n[port.0]\ntype = trunk\ntransport = hdlc-udp\nmode = dte\nhost = 127.0.0.1\n";

            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => Load(ini))!;

            Assert.That(ex.Message, Does.Contain("hdlc-udp"));
            Assert.That(ex.Message, Does.Contain("port.0"));
        }

        /// <summary>
        /// The trunk keys of the C emulator's configuration (and the T2 of the LAPB options) are read.
        /// </summary>
        [Test]
        public void TheLoader_ReadsTheSerialAndLapbKeys()
        {
            string ini = "[switch]\ndnic = 2340\n\n[port.0]\ntype = trunk\ntransport = hdlc-serial\nmode = dte\n" +
                "device = COM3\nbaud = 19200\ndatabits = 7\nparity = even\nstopbits = 2\nflow = none\n" +
                "lapb_t1 = 2500\nlapb_t2 = 200\nlapb_t3 = 20000\nlapb_n2 = 6\nlapb_k = 4\n";

            PortConfig port = Load(ini).Ports[0];

            Assert.That(port.Transport, Is.EqualTo(PortTransport.HdlcSerial));
            Assert.That(port.SerialDevice, Is.EqualTo("COM3"));
            Assert.That(port.SerialBaud, Is.EqualTo(19200u));
            Assert.That(port.SerialDataBits, Is.EqualTo(7));
            Assert.That(port.SerialParity, Is.EqualTo("even"));
            Assert.That(port.SerialStopBits, Is.EqualTo(2));
            Assert.That(port.SerialFlow, Is.EqualTo("none"));
            Assert.That(port.LapbT1Ms, Is.EqualTo(2500));
            Assert.That(port.LapbT2Ms, Is.EqualTo(200));
            Assert.That(port.LapbT3Ms, Is.EqualTo(20000));
            Assert.That(port.LapbN2, Is.EqualTo(6));
            Assert.That(port.LapbK, Is.EqualTo(4));
        }

        /// <summary>
        /// Missing keys keep the defaults of the C emulator's documentation.
        /// </summary>
        [Test]
        public void TheLoader_KeepsTheDefaultsWhenTheKeysAreMissing()
        {
            PortConfig port = Load("[switch]\ndnic = 2340\n\n[port.0]\ntype = trunk\ntransport = hdlc-tcp\nmode = dte\nhost = 127.0.0.1\n").Ports[0];

            Assert.That(port.SerialDataBits, Is.EqualTo(8));
            Assert.That(port.SerialParity, Is.EqualTo("none"));
            Assert.That(port.SerialStopBits, Is.EqualTo(1));
            Assert.That(port.SerialFlow, Is.EqualTo("rtscts"));
            Assert.That(port.LapbT1Ms, Is.EqualTo(0), "zero means the default of 3000");
            Assert.That(port.LapbK, Is.EqualTo(0), "zero means the default of 7");
        }
    }
}
