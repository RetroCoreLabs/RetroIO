//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Switch;
using RetroIO.Protocols.X25.Switch.Ports;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The <c>tcp = listen | dial</c> setting of an <c>hdlc-tcp</c> port: which end opens the TCP connection,
    /// kept apart from the X.25 role.
    /// </summary>
    /// <remarks>
    /// The TCP connection is only the emulated cable. Until 2026-11-16 the X.25 role decided it (a DCE port
    /// listened, a DTE port dialled), so a DTE port could not face an emulator that dials too. That was the case
    /// with the SINTRAN K X.25 option on RetroCore: its HDLC card dials the switch, and its link only accepts
    /// replies addressed the DTE way.
    /// </remarks>
    [TestFixture]
    public class SwitchTcpRoleTests
    {
        private static SwitchConfig Load(string tcpLine)
        {
            List<string> lines = new List<string> { "[port.0]", "type = trunk", "transport = hdlc-tcp", "mode = dte" };
            if (tcpLine.Length > 0)
            {
                lines.Add(tcpLine);
            }

            return SwitchConfigLoader.Load(IniConfig.Parse(lines));
        }

        [Test]
        public void WithoutTheSetting_TheRoleDecides_DceListens_DteDials()
        {
            PortConfig dte = Load(string.Empty).Ports[0];
            Assert.That(dte.TcpRole, Is.EqualTo(PortTcpRole.FromMode));
            Assert.That(dte.TcpListens, Is.False, "a DTE dials, as before the setting existed");

            PortConfig dce = new PortConfig { Mode = PortMode.Dce };
            Assert.That(dce.TcpListens, Is.True, "a DCE listens, as before the setting existed");
        }

        [Test]
        public void ListenAndDial_AreRead_AndOverrideTheRole()
        {
            PortConfig listening = Load("tcp = listen").Ports[0];
            Assert.That(listening.Mode, Is.EqualTo(PortMode.Dte), "the X.25 role is untouched");
            Assert.That(listening.TcpRole, Is.EqualTo(PortTcpRole.Listen));
            Assert.That(listening.TcpListens, Is.True);

            PortConfig dialling = new PortConfig { Mode = PortMode.Dce, TcpRole = PortTcpRole.Dial };
            Assert.That(dialling.TcpListens, Is.False);
        }

        [Test]
        public void AValueThatIsNotListenOrDial_IsRefused_NamingThePort()
        {
            InvalidDataException? ex = Assert.Throws<InvalidDataException>(() => Load("tcp = lisen"));
            Assert.That(ex!.Message, Does.Contain("[port.0]"));
            Assert.That(ex.Message, Does.Contain("lisen"));
        }

        /// <summary>
        /// A DTE port that listens and a DCE port that dials it: the link comes up exactly as when the DTE dials.
        /// The X.25 role, not the cable, decides who sends the SABM (the DTE) and which addresses are used.
        /// </summary>
        [Test]
        public void ADtePortThatListens_AndADcePortThatDials_BringTheLinkUp()
        {
            TrunkListLogger dteLog = new TrunkListLogger();
            TrunkListLogger dceLog = new TrunkListLogger();

            PortConfig dteConfig = TrunkPair.Config(1, PortMode.Dte, "127.0.0.1", 0);
            dteConfig.TcpRole = PortTcpRole.Listen;
            using HdlcTcpSwitchPort dte = new HdlcTcpSwitchPort(dteConfig, dteLog);
            Assert.That(dte.Start(), Is.True, "DTE did not start: " + dteLog.All());
            Assert.That(dte.BoundPort, Is.Not.EqualTo(0), "a listening DTE port binds a port");

            PortConfig dceConfig = TrunkPair.Config(0, PortMode.Dce, "127.0.0.1", dte.BoundPort);
            dceConfig.TcpRole = PortTcpRole.Dial;
            using HdlcTcpSwitchPort dce = new HdlcTcpSwitchPort(dceConfig, dceLog);
            Assert.That(dce.Start(), Is.True, "DCE did not start: " + dceLog.All());

            bool up = SpinWait.SpinUntil(() => dte.GetLinkSnapshot().LapbState == "DataTransfer"
                && dce.GetLinkSnapshot().LapbState == "DataTransfer", 10000);
            Assert.That(up, Is.True, "the link did not come up. DTE log:\n" + dteLog.All() + "\nDCE log:\n" + dceLog.All());
            Assert.That(dte.GetLinkSnapshot().LinkUps, Is.EqualTo(1));
            Assert.That(dce.GetLinkSnapshot().LinkUps, Is.EqualTo(1));
            Assert.That(dce.BoundPort, Is.EqualTo(0), "a dialling DCE port does not listen");
        }
    }
}
