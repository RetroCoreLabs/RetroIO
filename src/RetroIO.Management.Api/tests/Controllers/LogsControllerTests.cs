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
using RetroIO.Management.Api.Services;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Tests.Controllers;

[TestFixture]
public class LogsControllerTests
{
    private InMemoryLogProvider _logProvider = null!;
    private Mock<ILogger<LogsController>> _mockLogger = null!;
    private LogsController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _logProvider = new InMemoryLogProvider(maxEntries: 100);
        _mockLogger = new Mock<ILogger<LogsController>>();
        _controller = new LogsController(_logProvider, _mockLogger.Object);

        // Add some test log entries
        var logger = _logProvider.CreateLogger("TestCategory");
        logger.LogInformation("Test log entry 1");
        logger.LogWarning("Test log entry 2");
        logger.LogError("Test log entry 3");
    }

    [TearDown]
    public void TearDown()
    {
        _logProvider.Dispose();
    }

    [Test]
    public void GetLogs_ReturnsLogEntries()
    {
        // Act
        var result = _controller.GetLogs(limit: 10);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<LogListResponse>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.Entries.Count, Is.GreaterThan(0));
    }

    [Test]
    public void FilterLogs_ByLevel_ReturnsFilteredEntries()
    {
        // Arrange
        var request = new LogFilterRequest
        {
            Level = "Error",
            Limit = 100
        };

        // Act
        var result = _controller.FilterLogs(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<LogListResponse>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);

        // Check all entries are Error level
        for (int i = 0; i < response.Data!.Entries.Count; i++)
        {
            Assert.That(response.Data.Entries[i].Level, Is.EqualTo("Error"));
        }
    }

    [Test]
    public void FilterLogs_ByCategory_ReturnsFilteredEntries()
    {
        // Arrange
        var request = new LogFilterRequest
        {
            Category = "TestCategory",
            Limit = 100
        };

        // Act
        var result = _controller.FilterLogs(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<LogListResponse>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);

        // Check all entries contain the category
        for (int i = 0; i < response.Data!.Entries.Count; i++)
        {
            Assert.That(response.Data.Entries[i].Category.Contains("TestCategory"), Is.True);
        }
    }

    [Test]
    public void ClearLogs_RemovesAllEntries()
    {
        // Act
        var clearResult = _controller.ClearLogs();
        var getResult = _controller.GetLogs(limit: 100);

        // Assert
        Assert.That(clearResult, Is.Not.Null);
        Assert.That(clearResult.Result, Is.InstanceOf<OkObjectResult>());

        Assert.That(getResult, Is.Not.Null);
        Assert.That(getResult.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = getResult.Result as OkObjectResult;
        var response = okResult!.Value as ApiResponse<LogListResponse>;
        Assert.That(response!.Data!.Entries.Count, Is.EqualTo(0));
    }
}
