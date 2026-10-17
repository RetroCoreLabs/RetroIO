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
using RetroIO.Identity;

namespace RetroIO.Management.Api.Tests.Controllers;

[TestFixture]
public class GroupsControllerTests
{
    private Mock<IRetroIDP> _mockIdp = null!;
    private Mock<ILogger<GroupsController>> _mockLogger = null!;
    private GroupsController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockIdp = new Mock<IRetroIDP>();
        _mockLogger = new Mock<ILogger<GroupsController>>();
        _controller = new GroupsController(_mockIdp.Object, _mockLogger.Object);
    }

    [Test]
    public async Task GetGroups_ReturnsGroupList()
    {
        // Arrange
        var groups = new RetroGroup[]
        {
            new RetroGroup
            {
                GroupId = Guid.NewGuid(),
                GroupName = "TestGroup",
                CreatedAt = DateTime.UtcNow
            }
        };

        var users = new RetroUser[] { };

        _mockIdp.Setup(x => x.ListGroupsAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<RetroGroup[]>(groups));
        _mockIdp.Setup(x => x.ListUsersAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<RetroUser[]>(users));

        // Act
        var result = await _controller.GetGroups();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<GroupListResponse>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.Groups.Count, Is.EqualTo(1));
        Assert.That(response.Data.Groups[0].GroupName, Is.EqualTo("TestGroup"));
        Assert.That(response.Data.Groups[0].MemberCount, Is.EqualTo(0));
    }

    [Test]
    public async Task CreateGroup_ReturnsCreatedGroup()
    {
        // Arrange
        var request = new CreateGroupRequest { GroupName = "NewGroup" };
        _mockIdp.Setup(x => x.CreateGroupAsync(It.IsAny<RetroGroup>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<Guid>(Guid.NewGuid()));
        _mockIdp.Setup(x => x.SaveAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask());

        // Act
        var result = await _controller.CreateGroup(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<GroupDto>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.GroupName, Is.EqualTo("NewGroup"));
        Assert.That(response.Data.MemberCount, Is.EqualTo(0));
    }

    [Test]
    public async Task DeleteGroup_ValidId_ReturnsOk()
    {
        // Arrange
        var groupId = Guid.NewGuid().ToString();
        _mockIdp.Setup(x => x.DeleteGroupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        _mockIdp.Setup(x => x.SaveAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask());

        // Act
        var result = await _controller.DeleteGroup(groupId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<object>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
    }

    [Test]
    public async Task DeleteGroup_InvalidId_ReturnsBadRequest()
    {
        // Arrange
        var invalidGroupId = "not-a-guid";

        // Act
        var result = await _controller.DeleteGroup(invalidGroupId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task DeleteGroup_NotFound_ReturnsNotFound()
    {
        // Arrange
        var groupId = Guid.NewGuid().ToString();
        _mockIdp.Setup(x => x.DeleteGroupAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(false));

        // Act
        var result = await _controller.DeleteGroup(groupId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<NotFoundObjectResult>());
    }
}
