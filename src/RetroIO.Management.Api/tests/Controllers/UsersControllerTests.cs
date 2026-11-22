//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using RetroIO.Identity;
using RetroIO.Management.Api.Controllers;
using RetroIO.Management.Contracts;

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

    /// <summary>
    /// The allow-TNFS and allow-Econet choices on the create form reach the new user. Until
    /// 2026-11-10 the request had no such fields and every new user got TNFS yes, Econet no.
    /// </summary>
    [Test]
    public async Task CreateUser_KeepsTheProtocolChoices()
    {
        RetroUser? made = null;
        _mockIdp.Setup(x => x.CreateUserAsync(It.IsAny<RetroUser>(), It.IsAny<CancellationToken>()))
            .Callback<RetroUser, CancellationToken>((u, _) => made = u)
            .Returns(new ValueTask<Guid>(Guid.NewGuid()));
        _mockIdp.Setup(x => x.SetPasswordAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        _mockIdp.Setup(x => x.SaveAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask());

        await _controller.CreateUser(new CreateUserRequest
        {
            Username = "beeb",
            Password = "password123",
            AllowTnfs = false,
            AllowEconet = true,
        });

        Assert.That(made, Is.Not.Null);
        Assert.That(made!.AllowTnfs, Is.False);
        Assert.That(made.AllowEconet, Is.True);
    }

    /// <summary>
    /// Dial-up is off for a new user unless the admin ticks it, and a fixed address is kept
    /// (Ronny, 2026-11-22: only ticked users dial in; pool, or a fixed address when set).
    /// </summary>
    [Test]
    public async Task CreateUser_DialUpIsOffUnlessTicked_AndKeepsTheFixedAddress()
    {
        List<RetroUser> made = new List<RetroUser>();
        _mockIdp.Setup(x => x.CreateUserAsync(It.IsAny<RetroUser>(), It.IsAny<CancellationToken>()))
            .Callback<RetroUser, CancellationToken>((u, _) => made.Add(u))
            .Returns(new ValueTask<Guid>(Guid.NewGuid()));
        _mockIdp.Setup(x => x.SetPasswordAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<bool>(true));
        _mockIdp.Setup(x => x.SaveAsync(It.IsAny<CancellationToken>())).Returns(new ValueTask());

        await _controller.CreateUser(new CreateUserRequest { Username = "plain", Password = "password123" });
        await _controller.CreateUser(new CreateUserRequest
        {
            Username = "caller",
            Password = "password123",
            AllowDialUp = true,
            DialUpAddress = "10.42.0.20",
        });

        Assert.That(made.Count, Is.EqualTo(2));
        Assert.That(made[0].EffectiveAllowDialUp, Is.False);
        Assert.That(made[0].DialUpAddress, Is.Null);
        Assert.That(made[1].EffectiveAllowDialUp, Is.True);
        Assert.That(made[1].DialUpAddress, Is.EqualTo("10.42.0.20"));
        Assert.That(RetroUserProtocolAuth.CanUseProtocol(made[0], RetroFsProtocol.DialUp), Is.False);
        Assert.That(RetroUserProtocolAuth.CanUseProtocol(made[1], RetroFsProtocol.DialUp), Is.True);
    }

    /// <summary>
    /// A fixed dial-up address that is not a full dotted IPv4 address is refused, and the user is
    /// not created.
    /// </summary>
    [TestCase("10.42.0")]
    [TestCase("10.42.0.256")]
    [TestCase("10.42.0.20.1")]
    [TestCase("10.42..20")]
    [TestCase("10.42.0.x")]
    [TestCase("10.42.0.0020")]
    public async Task CreateUser_ABadDialUpAddress_IsRefused(string address)
    {
        var result = await _controller.CreateUser(new CreateUserRequest
        {
            Username = "caller",
            Password = "password123",
            DialUpAddress = address,
        });

        Assert.That(result.Result, Is.InstanceOf<BadRequestObjectResult>());
        _mockIdp.Verify(x => x.CreateUserAsync(It.IsAny<RetroUser>(), It.IsAny<CancellationToken>()), Times.Never);
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
