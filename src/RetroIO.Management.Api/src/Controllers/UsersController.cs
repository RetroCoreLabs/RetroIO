//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Identity;
using RetroIO.Management.Contracts;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// Management API controller for users.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IRetroIDP _idp;
    private readonly ILogger<UsersController> _logger;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="idp">
    /// The identity provider that holds the users and groups.
    /// </param>
    /// <param name="logger">
    /// The logger for error messages.
    /// </param>
    public UsersController(IRetroIDP idp, ILogger<UsersController> logger)
    {
        _idp = idp;
        _logger = logger;
    }

    /// <summary>
    /// List all users
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<UserListResponse>>> GetUsers()
    {
        try
        {
            var users = await _idp.ListUsersAsync();
            var groups = await _idp.ListGroupsAsync();

            var userDtos = new List<UserDto>();
            for (int i = 0; i < users.Length; i++)
            {
                var user = users[i];
                userDtos.Add(ToDto(user, groups));
            }

            var response = new UserListResponse
            {
                Users = userDtos,
                Total = userDtos.Count
            };

            return Ok(ApiResponse<UserListResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing users");
            return StatusCode(500, ApiResponse<UserListResponse>.Fail("Internal server error"));
        }
    }

    /// <summary>
    /// Create a new user
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<UserDto>>> CreateUser([FromBody] CreateUserRequest request)
    {
        try
        {
            RetroGroup[] allGroups = await _idp.ListGroupsAsync();
            string? groupProblem = ParseGroupIds(request.GroupIds, allGroups, out List<Guid> groupIds);
            if (groupProblem != null)
            {
                return BadRequest(ApiResponse<UserDto>.Fail(groupProblem, "UNKNOWN_GROUP"));
            }

            var userId = Guid.NewGuid();
            var newUser = new RetroUser
            {
                UserId = userId,
                Username = request.Username,
                DisplayName = request.DisplayName ?? request.Username,
                Email = request.Email,
                Groups = new List<Guid>(),
                IsActive = true,
                IsAdmin = false,
                // The create form's boxes (RetroAdmin) were never sent until 2026-11-10; when a
                // caller still sends nothing, the old defaults stand.
                AllowTnfs = request.AllowTnfs ?? true,
                AllowEconet = request.AllowEconet ?? false,
                CreatedAt = DateTime.UtcNow
            };

            await _idp.CreateUserAsync(newUser);
            await _idp.SetPasswordAsync(userId, request.Password);

            // Through the identity store, so the group's own member list is kept in step with the user's.
            for (int i = 0; i < groupIds.Count; i++)
            {
                await _idp.AddUserToGroupAsync(userId, groupIds[i]);
            }

            await _idp.SaveAsync();
            newUser = await _idp.GetUserAsync(userId) ?? newUser;

            UserDto userDto = ToDto(newUser, allGroups);

            return Ok(ApiResponse<UserDto>.Ok(userDto, "User created successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user");
            return StatusCode(500, ApiResponse<UserDto>.Fail("Failed to create user"));
        }
    }

    /// <summary>
    /// Delete a user
    /// </summary>
    [HttpDelete("{userId}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteUser(string userId)
    {
        try
        {
            var guid = Guid.Parse(userId);
            var success = await _idp.DeleteUserAsync(guid);

            if (!success)
            {
                return NotFound(ApiResponse<object>.Fail("User not found", "USER_NOT_FOUND"));
            }

            await _idp.SaveAsync();

            return Ok(ApiResponse<object>.Ok(null, "User deleted successfully"));
        }
        catch (FormatException)
        {
            return BadRequest(ApiResponse<object>.Fail("Invalid user ID format", "INVALID_FORMAT"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user {UserId}", userId);
            return StatusCode(500, ApiResponse<object>.Fail("Failed to delete user"));
        }
    }

    /// <summary>
    /// Update user settings
    /// </summary>
    [HttpPut("{userId}")]
    public async Task<ActionResult<ApiResponse<UserDto>>> UpdateUser(string userId, [FromBody] UpdateUserRequest request)
    {
        try
        {
            var guid = Guid.Parse(userId);
            var user = await _idp.GetUserAsync(guid);
            if (user == null)
                return NotFound(ApiResponse<UserDto>.Fail("User not found", "USER_NOT_FOUND"));

            if (request.DisplayName != null)
                user.DisplayName = request.DisplayName;
            if (request.Email != null)
                user.Email = request.Email;
            if (request.IsActive.HasValue)
                user.IsActive = request.IsActive.Value;
            if (request.AllowTnfs.HasValue)
                user.AllowTnfs = request.AllowTnfs.Value;
            if (request.AllowEconet.HasValue)
                user.AllowEconet = request.AllowEconet.Value;

            RetroGroup[] allGroups = await _idp.ListGroupsAsync();
            List<Guid> wanted = new List<Guid>();
            if (request.GroupIds != null)
            {
                string? groupProblem = ParseGroupIds(request.GroupIds, allGroups, out wanted);
                if (groupProblem != null)
                {
                    return BadRequest(ApiResponse<UserDto>.Fail(groupProblem, "UNKNOWN_GROUP"));
                }
            }

            await _idp.UpdateUserAsync(user);

            if (request.GroupIds != null)
            {
                // Out of every group not wanted, then into every wanted one (adding twice is harmless).
                Guid[] current = user.Groups.ToArray();
                for (int i = 0; i < current.Length; i++)
                {
                    if (!wanted.Contains(current[i]))
                    {
                        await _idp.RemoveUserFromGroupAsync(guid, current[i]);
                    }
                }

                for (int i = 0; i < wanted.Count; i++)
                {
                    await _idp.AddUserToGroupAsync(guid, wanted[i]);
                }
            }

            await _idp.SaveAsync();
            user = await _idp.GetUserAsync(guid) ?? user;

            UserDto userDto = ToDto(user, allGroups);

            return Ok(ApiResponse<UserDto>.Ok(userDto, "User updated successfully"));
        }
        catch (FormatException)
        {
            return BadRequest(ApiResponse<UserDto>.Fail("Invalid user ID format", "INVALID_FORMAT"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user {UserId}", userId);
            return StatusCode(500, ApiResponse<UserDto>.Fail("Failed to update user"));
        }
    }

    /// <summary>
    /// Change user password
    /// </summary>
    [HttpPut("{userId}/password")]
    public async Task<ActionResult<ApiResponse<object>>> ChangePassword(string userId, [FromBody] ChangePasswordRequest request)
    {
        try
        {
            var guid = Guid.Parse(userId);
            var success = await _idp.SetPasswordAsync(guid, request.NewPassword);

            if (!success)
            {
                return NotFound(ApiResponse<object>.Fail("User not found", "USER_NOT_FOUND"));
            }

            await _idp.SaveAsync();

            return Ok(ApiResponse<object>.Ok(null, "Password changed successfully"));
        }
        catch (FormatException)
        {
            return BadRequest(ApiResponse<object>.Fail("Invalid user ID format", "INVALID_FORMAT"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password for user {UserId}", userId);
            return StatusCode(500, ApiResponse<object>.Fail("Failed to change password"));
        }
    }

    /// <summary>
    /// Reads a list of group ids and checks each one names a group that exists.
    /// </summary>
    /// <param name="ids">The ids as sent.</param>
    /// <param name="groups">Every group.</param>
    /// <param name="parsed">Receives the ids, without repeats.</param>
    /// <returns>Null when all are good, else what is wrong.</returns>
    private static string? ParseGroupIds(List<string>? ids, RetroGroup[] groups, out List<Guid> parsed)
    {
        parsed = new List<Guid>();
        if (ids == null)
        {
            return null;
        }

        for (int i = 0; i < ids.Count; i++)
        {
            if (!Guid.TryParse(ids[i], out Guid id))
            {
                return "\"" + ids[i] + "\" is not a group id.";
            }

            bool found = false;
            for (int k = 0; k < groups.Length; k++)
            {
                if (groups[k].GroupId == id)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return "There is no group with id " + ids[i] + ".";
            }

            if (!parsed.Contains(id))
            {
                parsed.Add(id);
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the user as the API shows it, with group names and ids.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="groups">Every group, to look the names up in.</param>
    /// <returns>The DTO.</returns>
    /// <remarks>
    /// One builder for list, create and update. Before 2026-11-10 create and update each built their own and sent
    /// an empty group list back.
    /// </remarks>
    private static UserDto ToDto(RetroUser user, RetroGroup[] groups)
    {
        List<string> names = new List<string>();
        List<string> ids = new List<string>();
        for (int j = 0; j < user.Groups.Count; j++)
        {
            Guid groupId = user.Groups[j];
            for (int k = 0; k < groups.Length; k++)
            {
                if (groups[k].GroupId == groupId)
                {
                    names.Add(groups[k].GroupName);
                    ids.Add(groupId.ToString());
                    break;
                }
            }
        }

        return new UserDto
        {
            UserId = user.UserId.ToString(),
            Username = user.Username,
            DisplayName = user.DisplayName,
            Email = user.Email,
            IsActive = user.IsActive,
            IsAdmin = user.IsAdmin,
            AllowTnfs = user.EffectiveAllowTnfs,
            AllowEconet = user.EffectiveAllowEconet,
            Groups = names,
            GroupIds = ids,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            LastLoginTnfsAt = user.LastLoginTnfsAt,
            LastLoginEconetAt = user.LastLoginEconetAt,
            LastFailedLoginAt = user.LastFailedLoginAt
        };
    }
}
