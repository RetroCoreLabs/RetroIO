//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime;
using RetroIO.Protocols.X25.Runtime.Models;
using Xunit;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The statistics snapshot must show the calls there are now, not the calls there were at the last update.
    /// </summary>
    /// <remarks>
    /// Found on the RetroX25 build runner (2026-11-20): <c>PadWindow_ReceivingABurst_FollowsTheOutput</c> cleared its
    /// call, got CLR CONF, and still read one active virtual circuit 10 seconds later. The count was copied into the
    /// snapshot only by an update, and the "every 5 seconds" update timer is a one-shot TimerManager timer, so it ran
    /// once. On a fast machine some other event (a link status change) happened to update it after the clear.
    /// </remarks>
    public class RuntimeStatisticsLiveCountTests
    {
        [Fact]
        public void ActiveVirtualCircuits_FollowsABindingAndItsRemoval_AtOnce()
        {
            using ProtocolRuntime runtime = new ProtocolRuntime(NullLogger<ProtocolRuntime>.Instance, new RuntimeConfig());
            PadClient pad = new PadClient(NullLogger<ProtocolRuntime>.Instance, runtime);

            Assert.Equal(0, runtime.GetStatistics().X25.ActiveVirtualCircuits);

            runtime.BindLcnToModule(5, pad);
            Assert.Equal(1, runtime.GetStatistics().X25.ActiveVirtualCircuits);
            Assert.Equal(1, runtime.GetStatistics().Runtime.ActiveLcnBindings);

            runtime.UnbindModule(5);
            Assert.Equal(0, runtime.GetStatistics().X25.ActiveVirtualCircuits);
            Assert.Equal(0, runtime.GetStatistics().Runtime.ActiveLcnBindings);
        }

        [Fact]
        public void TotalVirtualCircuits_CountsEveryCall_AndKeepsTheCountAfterTheClear()
        {
            using ProtocolRuntime runtime = new ProtocolRuntime(NullLogger<ProtocolRuntime>.Instance, new RuntimeConfig());
            PadClient pad = new PadClient(NullLogger<ProtocolRuntime>.Instance, runtime);

            runtime.BindLcnToModule(5, pad);
            runtime.UnbindModule(5);
            runtime.BindLcnToModule(6, pad);
            runtime.UnbindModule(6);

            Assert.Equal(2, runtime.GetStatistics().X25.TotalVirtualCircuits);
        }
    }
}
