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

/// <summary>
/// Group membership set through the users API, against the real JSON identity store in a temp folder.
/// </summary>
[TestFixture]
public class UserGroupMembershipTests
{
    private string _folder = null!;
    private JsonRetroIDP _idp = null!;
    private UsersController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "retroio-groups-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _idp = new JsonRetroIDP(Path.Combine(_folder, "users.json"), Path.Combine(_folder, "groups.json"));
        _controller = new UsersController(_idp, new Mock<ILogger<UsersController>>().Object);
    }

    [TearDown]
    public void TearDown()
    {
        _idp.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    /// <summary>
    /// A user created with a group id is in that group, on both sides; an update can take them out again.
    /// </summary>
    [Test]
    public async Task CreateWithGroupId_PutsTheUserInTheGroup_AndUpdateTakesThemOut()
    {
        Guid groupId = await _idp.CreateGroupAsync(new RetroGroup { GroupId = Guid.NewGuid(), GroupName = "beebs" });

        ActionResult<ApiResponse<UserDto>> created = await _controller.CreateUser(new CreateUserRequest
        {
            Username = "acorn",
            Password = "password123",
            GroupIds = new List<string> { groupId.ToString() },
        });

        UserDto user = Data(created);
        Assert.That(user.Groups, Is.EqualTo(new[] { "beebs" }));
        Assert.That(user.GroupIds, Is.EqualTo(new[] { groupId.ToString() }));
        RetroGroup? group = await _idp.GetGroupAsync(groupId);
        Assert.That(group!.Members, Does.Contain(Guid.Parse(user.UserId)), "the group's own member list is kept in step");

        UserDto updated = Data(await _controller.UpdateUser(user.UserId, new UpdateUserRequest { GroupIds = new List<string>() }));
        Assert.That(updated.Groups, Is.Empty);
        Assert.That((await _idp.GetGroupAsync(groupId))!.Members, Is.Empty);
    }

    /// <summary>
    /// An id that names no group refuses the create, and no user is made.
    /// </summary>
    [Test]
    public async Task CreateWithUnknownGroupId_IsRefused()
    {
        ActionResult<ApiResponse<UserDto>> created = await _controller.CreateUser(new CreateUserRequest
        {
            Username = "acorn",
            Password = "password123",
            GroupIds = new List<string> { Guid.NewGuid().ToString() },
        });

        Assert.That(created.Result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(await _idp.ListUsersAsync(), Is.Empty);
    }

    /// <summary>
    /// Leaving GroupIds out of an update keeps the membership.
    /// </summary>
    [Test]
    public async Task UpdateWithoutGroupIds_KeepsTheGroups()
    {
        Guid groupId = await _idp.CreateGroupAsync(new RetroGroup { GroupId = Guid.NewGuid(), GroupName = "beebs" });
        UserDto user = Data(await _controller.CreateUser(new CreateUserRequest
        {
            Username = "acorn",
            Password = "password123",
            GroupIds = new List<string> { groupId.ToString() },
        }));

        UserDto updated = Data(await _controller.UpdateUser(user.UserId, new UpdateUserRequest { DisplayName = "Acorn" }));
        Assert.That(updated.Groups, Is.EqualTo(new[] { "beebs" }));
        Assert.That(updated.DisplayName, Is.EqualTo("Acorn"));
    }

    private static T Data<T>(ActionResult<ApiResponse<T>> result)
    {
        return ((ApiResponse<T>)((OkObjectResult)result.Result!).Value!).Data!;
    }
}
