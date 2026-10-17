//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using NUnit.Framework;
using RetroIO.Protocols.X25.Switch;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Loads the actual demo switch configuration files and validates the routing and cross-environment
    /// trunk they define, so a typo in the shipped INI is caught by the test suite.
    /// </summary>
    [TestFixture]
    public class DemoConfigTests
    {
        private static string DemoDir()
        {
            // Tests run from Tests/bin/Debug/net10.0; the demo folder is four levels up at the repo root.
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            return Path.Combine(root, "demo");
        }

        [Test]
        public void SwitchA_LoadsAndRoutesLocalVsCrossEnv()
        {
            string path = Path.Combine(DemoDir(), "switch-a.ini");
            Assert.That(File.Exists(path), Is.True, $"demo config not found at {path}");

            SwitchConfig cfg = SwitchConfigLoader.LoadFile(path);

            Assert.That(cfg.Dnic, Is.EqualTo("2350"));
            Assert.That(cfg.MonitorPort, Is.EqualTo((ushort)4001));

            // A WSL DNIC (234x) must route out the cross-environment trunk; a C# DNIC (235x) must stay
            // internal (default route toward Switch B).
            Assert.That(cfg.Routing.TryLookup("23420001", 0, out int wslPort), Is.True);
            Assert.That(cfg.Routing.TryLookup("23520001", 0, out int localPort), Is.True);
            Assert.That(wslPort, Is.Not.EqualTo(localPort), "234x and 235x must route to different ports");

            // The cross-environment trunk port must be a listening (DCE) port bound on 2210.
            bool hasCrossEnvTrunk = false;
            for (int i = 0; i < cfg.Ports.Count; i++)
            {
                if (cfg.Ports[i].Port == 2210 && cfg.Ports[i].Mode == PortMode.Dce)
                {
                    hasCrossEnvTrunk = true;
                }
            }
            Assert.That(hasCrossEnvTrunk, Is.True, "Switch A must listen on 2210 for the WSL trunk");
        }

        [Test]
        public void AllDemoSwitches_LoadWithMonitorPorts()
        {
            string[] files = { "switch-a.ini", "switch-b.ini", "switch-c.ini" };
            ushort[] monitorPorts = { 4001, 4002, 4003 };
            string[] dnics = { "2350", "2351", "2352" };

            for (int i = 0; i < files.Length; i++)
            {
                string path = Path.Combine(DemoDir(), files[i]);
                Assert.That(File.Exists(path), Is.True, $"demo config not found at {path}");

                SwitchConfig cfg = SwitchConfigLoader.LoadFile(path);
                Assert.That(cfg.Dnic, Is.EqualTo(dnics[i]), files[i]);
                Assert.That(cfg.MonitorPort, Is.EqualTo(monitorPorts[i]), files[i]);
                Assert.That(cfg.Ports.Count, Is.GreaterThan(0), files[i]);
            }
        }
    }
}
