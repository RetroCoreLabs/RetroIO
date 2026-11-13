//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The history size in the configuration (HistoryCapacity) is the size the engine keeps. It was read
    /// and saved but never used: the engine always kept 100 packets.
    /// </summary>
    [TestFixture]
    public class HistoryCapacityConfigTests
    {
        /// <summary>
        /// A runtime made from a configuration with HistoryCapacity 37 keeps 37 packets, and a
        /// configuration without the setting keeps the default of 100.
        /// </summary>
        [TestCase(37, 37)]
        [TestCase(500, 500)]
        [TestCase(100, 100)]
        public void TheConfiguredHistoryCapacity_IsTheOneTheEngineUses(int configured, int expected)
        {
            RuntimeConfig config = new RuntimeConfig();
            config.X25.LocalMachineId = "200";
            config.HistoryCapacity = configured;
            using (ProtocolRuntime runtime = new ProtocolRuntime(NullLogger.Instance, config))
            {
                Assert.That(runtime.HistoryCapacity, Is.EqualTo(expected));
            }
        }
    }
}
