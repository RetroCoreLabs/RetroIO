//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Management.Api.Tests.Controllers;

/// <summary>
/// Regression tests for TnfsController's GetStatistics/GetSessions endpoints, which previously
/// always returned hardcoded zero/empty placeholder data (see Stub-Gap-Audit.md). Covers both:
/// real data flowing through when SessionManager/TnfsServerStatistics are available (the
/// same-process hosting scenario), and the honest null-dependency fallback (today's actual
/// deployment, where Management API and the TNFS server run as separate processes).
/// </summary>
[TestFixture]
public class TnfsControllerTests
{
    private Mock<ILogger<TnfsController>> _mockLogger = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<TnfsController>>();
    }

    [Test]
    public void GetStatistics_NoDependencies_ReturnsAllZeros_NotError()
    {
        var controller = new TnfsController(_mockLogger.Object);

        var result = controller.GetStatistics();

        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<TnfsStatisticsResponse>;
        Assert.That(response!.Data!.ActiveConnections, Is.EqualTo(0));
        Assert.That(response.Data.TotalRequests, Is.EqualTo(0));
        Assert.That(response.Data.Uptime, Is.EqualTo("0s"));
    }

    [Test]
    public void GetStatistics_WithRealStatistics_ReportsActualCounters()
    {
        var stats = new TnfsServerStatistics();
        stats.RecordRequest(2048);
        stats.RecordRequest(1024);
        stats.RecordFileTransfer();

        var controller = new TnfsController(_mockLogger.Object, sessionManager: null, statistics: stats);

        var result = controller.GetStatistics();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as ApiResponse<TnfsStatisticsResponse>;

        Assert.That(response!.Data!.TotalRequests, Is.EqualTo(2));
        Assert.That(response.Data.BytesTransferred, Is.EqualTo(3072));
        Assert.That(response.Data.FilesTransferred, Is.EqualTo(1));
        // NOTE: Uptime legitimately still formats as "0s" here — a real TnfsServerStatistics
        // constructed microseconds ago has a genuinely sub-second uptime, so "0s" is correct
        // output, not a sign the null-dependency path was hit. TotalRequests/BytesTransferred/
        // FilesTransferred above are what actually distinguish real data from the placeholder.
        Assert.That(response.Data.Uptime, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void GetStatistics_WithRealSessionManager_ReportsActiveConnections()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        var sessionManager = new SessionManager(
            loggerFactory.CreateLogger<SessionManager>(), TimeSpan.FromMinutes(10), maxSessionsPerIP: 8);
        sessionManager.CreateSession(IPAddress.Parse("10.0.0.1"), "/root");

        var controller = new TnfsController(_mockLogger.Object, sessionManager);

        var result = controller.GetStatistics();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as ApiResponse<TnfsStatisticsResponse>;

        Assert.That(response!.Data!.ActiveConnections, Is.EqualTo(1));
    }

    [Test]
    public void GetSessions_NoSessionManager_ReturnsEmptyList_NotError()
    {
        var controller = new TnfsController(_mockLogger.Object);

        var result = controller.GetSessions();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as ApiResponse<TnfsSessionsResponse>;

        Assert.That(response!.Data!.Sessions, Is.Empty);
        Assert.That(response.Data.Total, Is.EqualTo(0));
    }

    [Test]
    public void GetSessions_WithRealSessionManager_ReportsRealSessionDetails()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        var sessionManager = new SessionManager(
            loggerFactory.CreateLogger<SessionManager>(), TimeSpan.FromMinutes(10), maxSessionsPerIP: 8);
        var session = sessionManager.CreateSession(
            IPAddress.Parse("192.168.1.50"), "/root", username: "ALICE");

        var controller = new TnfsController(_mockLogger.Object, sessionManager);

        var result = controller.GetSessions();
        var okResult = result.Result as OkObjectResult;
        var response = okResult!.Value as ApiResponse<TnfsSessionsResponse>;

        Assert.That(response!.Data!.Total, Is.EqualTo(1));
        var info = response.Data.Sessions[0];
        Assert.That(info.SessionId, Is.EqualTo($"0x{session.SessionId:X4}"));
        Assert.That(info.ClientIP, Is.EqualTo("192.168.1.50"));
        Assert.That(info.Username, Is.EqualTo("ALICE"));
        Assert.That(info.Transport, Is.EqualTo("UDP"), "TcpConnectionId defaults to 0 for a session created without one, which means UDP");
        Assert.That(info.OpenFiles, Is.EqualTo(0));
        Assert.That(info.OpenDirectories, Is.EqualTo(0));
    }
}
