//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Assert what the RetroFS Econet activity monitor recorded for each client request.
/// </summary>
internal static class EconetActivityAssertions
{
    public static int CommandCount(EconetActivityLog log) =>
        log.GetRecentCommands(10_000).Count;

    public static int TransferCount(EconetActivityLog log) =>
        log.GetRecentTransfers(10_000).Count;

    public static IReadOnlyList<EconetCommandEntry> NewCommandsSince(EconetActivityLog log, int previousCount)
    {
        IReadOnlyList<EconetCommandEntry> all = log.GetRecentCommands(10_000);
        int added = all.Count - previousCount;
        Assert.That(added, Is.GreaterThanOrEqualTo(0), () =>
            $"Activity log count shrank unexpectedly.\n{new EconetTestMonitor(log).FormatTrace()}");

        EconetCommandEntry[] slice = new EconetCommandEntry[added];
        for (int i = 0; i < added; i++)
            slice[i] = all[i];

        return slice;
    }

    public static IReadOnlyList<EconetCommandEntry> NewCommandsSince(EconetTestMonitor monitor, int previousCount) =>
        NewCommandsSince(monitor.Log, previousCount);

    public static void AssertOneNewCommand(
        EconetTestMonitor monitor,
        int previousCount,
        string expectedCommand,
        bool expectedSuccess,
        string expectedResult)
    {
        IReadOnlyList<EconetCommandEntry> added = NewCommandsSince(monitor, previousCount);
        Assert.That(added.Count, Is.EqualTo(1), () =>
            $"Expected exactly one new monitor entry.\n{monitor.FormatTraceSinceMark()}");
        AssertCommand(added[0], expectedCommand, expectedSuccess, expectedResult, monitor);
    }

    public static void AssertOneNewCommand(
        EconetActivityLog log,
        int previousCount,
        string expectedCommand,
        bool expectedSuccess,
        string expectedResult) =>
        AssertOneNewCommand(new EconetTestMonitor(log), previousCount, expectedCommand, expectedSuccess, expectedResult);

    public static void AssertCommand(
        EconetCommandEntry entry,
        string expectedCommand,
        bool expectedSuccess,
        string expectedResult,
        EconetTestMonitor? monitor = null)
    {
        string trace = monitor?.FormatTrace() ?? string.Empty;
        Assert.That(entry.Command, Is.EqualTo(expectedCommand), $"Monitor command label.\n{trace}");
        Assert.That(entry.Success, Is.EqualTo(expectedSuccess), $"Monitor success for {entry.Command}.\n{trace}");
        Assert.That(entry.Result, Does.StartWith(expectedResult), $"Monitor result for {entry.Command}.\n{trace}");
    }

    public static void AssertOneNewTransfer(
        EconetTestMonitor monitor,
        int previousTransferCount,
        EconetFileTransferDirection direction,
        string pathContains,
        long bytes,
        string status = "OK")
    {
        IReadOnlyList<EconetFileTransferEntry> all = monitor.RecentTransfers;
        int added = all.Count - previousTransferCount;
        Assert.That(added, Is.EqualTo(1), () =>
            $"Expected one file transfer record.\n{monitor.FormatTrace()}");

        EconetFileTransferEntry entry = all[0];
        Assert.That(entry.Direction, Is.EqualTo(direction), monitor.FormatTrace());
        Assert.That(entry.FilePath, Does.Contain(pathContains), monitor.FormatTrace());
        Assert.That(entry.Bytes, Is.EqualTo(bytes), monitor.FormatTrace());
        Assert.That(entry.Status, Is.EqualTo(status), monitor.FormatTrace());
        Assert.That(entry.Success, Is.True, monitor.FormatTrace());
    }
}
