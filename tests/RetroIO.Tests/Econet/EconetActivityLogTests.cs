//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class EconetActivityLogTests
{
    [Test]
    public void RingBuffer_KeepsMostRecentCommands()
    {
        EconetActivityLog log = new EconetActivityLog(capacity: 3);

        for (int i = 0; i < 5; i++)
        {
            log.RecordCommand(new EconetCommandEntry
            {
                TimestampUtc = DateTime.UtcNow,
                ClientNet = 1,
                ClientStation = (byte)(10 + i),
                Command = $"Cmd{i}",
            });
        }

        IReadOnlyList<EconetCommandEntry> recent = log.GetRecentCommands(3);
        Assert.That(recent.Count, Is.EqualTo(3));
        Assert.That(recent[0].Command, Is.EqualTo("Cmd4"));
        Assert.That(recent[1].Command, Is.EqualTo("Cmd3"));
        Assert.That(recent[2].Command, Is.EqualTo("Cmd2"));
    }

    [Test]
    public void RingBuffer_KeepsMostRecentTransfers()
    {
        EconetActivityLog log = new EconetActivityLog(capacity: 2);

        log.RecordFileTransfer(new EconetFileTransferEntry
        {
            FilePath = "A",
            Direction = EconetFileTransferDirection.Send,
            Bytes = 100,
        });
        log.RecordFileTransfer(new EconetFileTransferEntry
        {
            FilePath = "B",
            Direction = EconetFileTransferDirection.Receive,
            Bytes = 200,
        });
        log.RecordFileTransfer(new EconetFileTransferEntry
        {
            FilePath = "C",
            Direction = EconetFileTransferDirection.Send,
            Bytes = 300,
        });

        IReadOnlyList<EconetFileTransferEntry> recent = log.GetRecentTransfers(2);
        Assert.That(recent.Count, Is.EqualTo(2));
        Assert.That(recent[0].FilePath, Is.EqualTo("C"));
        Assert.That(recent[1].FilePath, Is.EqualTo("B"));
    }

    [Test]
    public void ActivityRecorded_FiresOnCommand()
    {
        EconetActivityLog log = new EconetActivityLog();
        int events = 0;
        log.ActivityRecorded += (_, _) => events++;

        log.RecordCommand(new EconetCommandEntry { Command = "Test" });
        Assert.That(events, Is.EqualTo(1));
    }
}
