//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Management.Api.Tests.Controllers;

/// <summary>
/// Tests that EconetController.GetStatistics reports the real counters of the Econet server,
/// and zeros only when no Econet server runs in the process.
/// </summary>
[TestFixture]
public class EconetControllerStatisticsTests
{
    private static EconetStatisticsResponse Get(EconetController controller)
    {
        OkObjectResult? ok = controller.GetStatistics().Result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        ApiResponse<EconetStatisticsResponse>? response = ok!.Value as ApiResponse<EconetStatisticsResponse>;
        Assert.That(response, Is.Not.Null);
        return response!.Data!;
    }

    [Test]
    public void NoEconetServerInTheProcess_ReportsZeros()
    {
        EconetController controller = new EconetController(new Mock<ILogger<EconetController>>().Object);

        EconetStatisticsResponse data = Get(controller);

        Assert.That(data.TotalRequests, Is.EqualTo(0));
        Assert.That(data.BytesTransferred, Is.EqualTo(0));
        Assert.That(data.Uptime, Is.EqualTo("0s"));
    }

    [Test]
    public void WithStatistics_ReportsTheRealCounters()
    {
        EconetServerStatistics stats = new EconetServerStatistics();
        stats.RecordRequest(10, 100, false);
        stats.RecordRequest(20, 200, true);
        stats.RecordError();

        EconetController controller = new EconetController(
            new Mock<ILogger<EconetController>>().Object, null, null, stats);

        EconetStatisticsResponse data = Get(controller);

        Assert.That(data.TotalRequests, Is.EqualTo(2));
        Assert.That(data.BytesReceived, Is.EqualTo(30));
        Assert.That(data.BytesSent, Is.EqualTo(300));
        Assert.That(data.BytesTransferred, Is.EqualTo(330));
        Assert.That(data.Errors, Is.EqualTo(1));
    }
}
