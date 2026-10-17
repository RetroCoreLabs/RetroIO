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
using RetroIO.Protocols.X25.Nmc;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Live end-to-end test of in-band NMC seed-and-walk discovery against the running C# demo network.
    /// It rides real X.25: the engine connects to Switch A's access port, calls each switch's management
    /// address, logs in, and walks the topology A -> B -> C.
    /// </summary>
    /// <remarks>
    /// Gated on the <c>NMC_DEMO</c> environment variable so it only runs when the demo is up
    /// (<c>./demo/demo.ps1 start</c>).
    /// </remarks>
    [TestFixture]
    public class NmcDiscoveryLiveTests
    {
        [Test]
        public void SeedWalk_DiscoversAllThreeCSharpSwitches()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NMC_DEMO")))
            {
                Assert.Ignore("Start the C# demo (./demo/demo.ps1 start) and set NMC_DEMO=1 to run this test.");
            }

            NmcDiscoveryOptions options = new NmcDiscoveryOptions
            {
                Host = "127.0.0.1",
                Port = 2200,
                LocalAddress = "99990002",
                Username = "admin",
                Password = "admin",
                MaxDepth = 5
            };

            HashSet<string> reachableDnics = new HashSet<string>(StringComparer.Ordinal);
            NmcNode? switchA = null;
            object gate = new object();

            using NmcDiscovery discovery = new NmcDiscovery(options);
            discovery.NodeChanged += node =>
            {
                lock (gate)
                {
                    if (node.Reachable && node.Info != null && node.Info.Dnic.Length > 0)
                    {
                        reachableDnics.Add(node.Info.Dnic);
                        if (node.Info.Dnic == "2350")
                        {
                            switchA = node;
                        }
                    }
                }
            };

            Assert.That(discovery.Start("23500000"), Is.True, "the engine should open the X.25 client");

            // Wait up to 20s for the walk to reach all three C# switches.
            bool all = SpinUntil(() =>
            {
                lock (gate)
                {
                    return reachableDnics.Contains("2350")
                        && reachableDnics.Contains("2351")
                        && reachableDnics.Contains("2352");
                }
            }, 20000);

            string got;
            lock (gate)
            {
                got = string.Join(",", reachableDnics);
            }

            Assert.That(all, Is.True, $"seed+walk should discover DNICs 2350, 2351, 2352; got: {got}");

            // Switch A should report Switch-B as a neighbour with its management address (walk edge).
            Assert.That(switchA, Is.Not.Null);
            bool hasBNeighbour = false;
            lock (gate)
            {
                for (int i = 0; i < switchA!.Neighbours.Count; i++)
                {
                    if (switchA.Neighbours[i].Name == "Switch-B"
                        && switchA.Neighbours[i].MgmtAddr == "23510000")
                    {
                        hasBNeighbour = true;
                    }
                }
            }
            Assert.That(hasBNeighbour, Is.True, "Switch A's topology should list Switch-B with mgmt 23510000");
        }

        private static bool SpinUntil(Func<bool> condition, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(100);
            }
            return condition();
        }
    }
}
