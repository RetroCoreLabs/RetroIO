//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Management.Api.Tests.Controllers;

/// <summary>
/// Tests the uptime text and the file operation count that EconetController.GetStatistics reports.
/// The counter object counts its uptime from the moment it was created, so each test moves that
/// moment back by a chosen amount instead of waiting.
/// </summary>
[TestFixture]
public class EconetControllerUptimeTests
{
    // The start time is a private field that the constructor sets. A test sets it to a time in the past.
    private static EconetServerStatistics StatisticsThatStartedAgo(TimeSpan ago)
    {
        EconetServerStatistics stats = new EconetServerStatistics();
        FieldInfo? field = typeof(EconetServerStatistics).GetField("_startedAt", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, "EconetServerStatistics has no _startedAt field any more: update this helper");
        field!.SetValue(stats, DateTime.UtcNow - ago);
        return stats;
    }

    private static EconetStatisticsResponse Get(EconetServerStatistics stats)
    {
        EconetController controller = new EconetController(
            new Mock<ILogger<EconetController>>().Object, null, null, stats);
        OkObjectResult? ok = controller.GetStatistics().Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        ApiResponse<EconetStatisticsResponse>? response = ok!.Value as ApiResponse<EconetStatisticsResponse>;
        Assert.That(response, Is.Not.Null);
        return response!.Data!;
    }

    /// <summary>
    /// A server that has run for more than a day reports days, hours and minutes.
    /// </summary>
    [Test]
    public void Uptime_MoreThanADay_ReportsDaysHoursMinutes()
    {
        EconetServerStatistics stats = StatisticsThatStartedAgo(new TimeSpan(2, 3, 4, 20));

        Assert.That(Get(stats).Uptime, Is.EqualTo("2d 3h 4m"));
    }

    /// <summary>
    /// A server that has run for more than an hour but less than a day reports hours, minutes and seconds.
    /// </summary>
    [Test]
    public void Uptime_MoreThanAnHour_ReportsHoursMinutesSeconds()
    {
        EconetServerStatistics stats = StatisticsThatStartedAgo(new TimeSpan(0, 5, 6, 20));

        Assert.That(Get(stats).Uptime, Is.EqualTo("5h 6m 20s"));
    }

    /// <summary>
    /// A server that has run for more than a minute but less than an hour reports minutes and seconds.
    /// </summary>
    [Test]
    public void Uptime_MoreThanAMinute_ReportsMinutesSeconds()
    {
        EconetServerStatistics stats = StatisticsThatStartedAgo(new TimeSpan(0, 0, 7, 20));

        Assert.That(Get(stats).Uptime, Is.EqualTo("7m 20s"));
    }

    /// <summary>
    /// A server that has run for less than a minute reports seconds only.
    /// </summary>
    [Test]
    public void Uptime_LessThanAMinute_ReportsSecondsOnly()
    {
        EconetServerStatistics stats = StatisticsThatStartedAgo(new TimeSpan(0, 0, 0, 20));

        Assert.That(Get(stats).Uptime, Is.EqualTo("20s"));
    }

    /// <summary>
    /// The file operation count and the two byte counts reach the response unchanged.
    /// </summary>
    [Test]
    public void FileOperationsAndByteCounts_ReachTheResponse()
    {
        EconetServerStatistics stats = new EconetServerStatistics();
        stats.RecordRequest(1, 2, true);
        stats.RecordRequest(4, 8, true);
        stats.RecordRequest(16, 32, false);

        EconetStatisticsResponse data = Get(stats);

        Assert.That(data.FileOperations, Is.EqualTo(2));
        Assert.That(data.BytesReceived, Is.EqualTo(21));
        Assert.That(data.BytesSent, Is.EqualTo(42));
        Assert.That(data.BytesTransferred, Is.EqualTo(63));
    }
}
