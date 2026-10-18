//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Node.Seam;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The test double must keep the same promise as <see cref="ILink"/>: <c>StatusChanged</c> is
    /// raised on a real transition only.
    /// </summary>
    public sealed class FakeLinkStatusTests
    {
        [Fact]
        public void Stop_OnAnAlreadyStoppedLink_RaisesNoSecondEvent()
        {
            FakeLink link = new FakeLink("L");
            List<string> transitions = new List<string>();
            link.StatusChanged += (_, oldStatus, newStatus, _) => transitions.Add(oldStatus + ">" + newStatus);

            link.Stop();
            link.Stop();

            Assert.Equal(new[] { "Active>Stopped" }, transitions);
        }
    }
}
