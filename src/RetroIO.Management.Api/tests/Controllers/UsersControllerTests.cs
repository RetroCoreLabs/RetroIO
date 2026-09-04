//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;
using RetroIO.Identity;

namespace RetroIO.Management.Api.Tests.Controllers;

[TestFixture]
public class UsersControllerTests
{
    private Mock<IRetroIDP> _mockIdp = null!;
    private Mock<ILogger<UsersController>> _mockLogger = null!;
    private UsersController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockIdp = new Mock<IRetroIDP>();
        _mockLogger = new Mock<ILogger<UsersController>>();
        _controller = new UsersController(_mockIdp.Object, _mockLogger.Object);
    }

    [Test]
    public async Task GetUsers_ReturnsUserList()
    {
        // Arrange
        var users = new RetroUser[]
        {
            new RetroUser
            {
                UserId = Guid.NewGuid(),
                Username = "testuser",
                DisplayName = "Test User",
                Email = "test@example.com",
                IsActive = true,
                IsAdmin = false,
                Groups = new List<Guid>(),
                CreatedAt = DateTime.UtcNow
            }
        };

        var groups = new RetroGroup[] { };

        _mockIdp.Setup(x => x.ListUsersAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<RetroUser[]>(users));
        _mockIdp.Setup(x => x.ListGroupsAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask<RetroGroup[]>(groups));

        // Act
        var result = await _controller.GetUsers();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<UserListResponse>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.Users.Count, Is.EqualTo(1));
        Assert.That(response.Data.Users[0].Username, Is.EqualTo("testuser"));
    }

    [Test]
    public async Task CreateUser_ReturnsCreatedUser()
    {
        // Arrange
        var request = new CreateUserRequest
        {
            Username = "newuser",
            Password = "password123",
            DisplayName = "New User",
            Email = "newuser@example.com"
        };

        _mockIdp.Setup(x => x.CreateUserAsync(It.IsAny<RetroUser>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<Guid>(Guid.NewGuid()));
        _mockIdp.Setup(x => x.SetPasswordAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        _mockIdp.Setup(x => x.SaveAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask());

        // Act
        var result = await _controller.CreateUser(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<UserDto>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Data!.Username, Is.EqualTo("newuser"));
    }

    [Test]
    public async Task DeleteUser_ValidId_ReturnsOk()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        _mockIdp.Setup(x => x.DeleteUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        _mockIdp.Setup(x => x.SaveAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask());

        // Act
        var result = await _controller.DeleteUser(userId);

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
    public async Task DeleteUser_InvalidId_ReturnsBadRequest()
    {
        // Arrange
        var invalidUserId = "not-a-guid";

        // Act
        var result = await _controller.DeleteUser(invalidUserId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task DeleteUser_NotFound_ReturnsNotFound()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        _mockIdp.Setup(x => x.DeleteUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(false));

        // Act
        var result = await _controller.DeleteUser(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task ChangePassword_ValidRequest_ReturnsOk()
    {
        // Arrange
        var userId = Guid.NewGuid().ToString();
        var request = new ChangePasswordRequest { NewPassword = "newpassword123" };
        _mockIdp.Setup(x => x.SetPasswordAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        _mockIdp.Setup(x => x.SaveAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask());

        // Act
        var result = await _controller.ChangePassword(userId, request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Result, Is.InstanceOf<OkObjectResult>());
        var okResult = result.Result as OkObjectResult;
        Assert.That(okResult, Is.Not.Null);
        var response = okResult!.Value as ApiResponse<object>;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
    }
}
