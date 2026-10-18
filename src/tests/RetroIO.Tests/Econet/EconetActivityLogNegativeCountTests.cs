//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// A negative <c>count</c> asked of <see cref="EconetActivityLog"/> gives an empty list, the same
/// as a count of 0.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EconetActivityLogNegativeCountTests
{
    [Test]
    public void GetRecentCommands_NegativeCount_ReturnsEmpty()
    {
        EconetActivityLog log = new EconetActivityLog(capacity: 3);
        log.RecordCommand(new EconetCommandEntry { Command = "Cmd" });

        Assert.That(log.GetRecentCommands(-1), Is.Empty);
    }

    [Test]
    public void GetRecentTransfers_NegativeCount_ReturnsEmpty()
    {
        EconetActivityLog log = new EconetActivityLog(capacity: 3);
        log.RecordFileTransfer(new EconetFileTransferEntry { FilePath = "A" });

        Assert.That(log.GetRecentTransfers(-1), Is.Empty);
    }
}
