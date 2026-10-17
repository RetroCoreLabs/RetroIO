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
                var userGroups = new List<string>();

                for (int j = 0; j < user.Groups.Count; j++)
                {
                    var groupId = user.Groups[j];
                    for (int k = 0; k < groups.Length; k++)
                    {
                        if (groups[k].GroupId == groupId)
                        {
                            userGroups.Add(groups[k].GroupName);
                            break;
                        }
                    }
                }

                userDtos.Add(new UserDto
                {
                    UserId = user.UserId.ToString(),
                    Username = user.Username,
                    DisplayName = user.DisplayName,
                    Email = user.Email,
                    IsActive = user.IsActive,
                    IsAdmin = user.IsAdmin,
                    AllowTnfs = user.EffectiveAllowTnfs,
                    AllowEconet = user.EffectiveAllowEconet,
                    Groups = userGroups,
                    CreatedAt = user.CreatedAt,
                    LastLoginAt = user.LastLoginAt,
                    LastLoginTnfsAt = user.LastLoginTnfsAt,
                    LastLoginEconetAt = user.LastLoginEconetAt,
                    LastFailedLoginAt = user.LastFailedLoginAt
                });
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
                AllowTnfs = true,
                AllowEconet = false,
                CreatedAt = DateTime.UtcNow
            };

            await _idp.CreateUserAsync(newUser);
            await _idp.SetPasswordAsync(userId, request.Password);
            await _idp.SaveAsync();

            var userDto = new UserDto
            {
                UserId = newUser.UserId.ToString(),
                Username = newUser.Username,
                DisplayName = newUser.DisplayName,
                Email = newUser.Email,
                IsActive = newUser.IsActive,
                IsAdmin = newUser.IsAdmin,
                AllowTnfs = newUser.EffectiveAllowTnfs,
                AllowEconet = newUser.EffectiveAllowEconet,
                Groups = new List<string>(),
                CreatedAt = newUser.CreatedAt
            };

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

            await _idp.UpdateUserAsync(user);
            await _idp.SaveAsync();

            var userDto = new UserDto
            {
                UserId = user.UserId.ToString(),
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                IsActive = user.IsActive,
                IsAdmin = user.IsAdmin,
                AllowTnfs = user.EffectiveAllowTnfs,
                AllowEconet = user.EffectiveAllowEconet,
                Groups = new List<string>(),
                CreatedAt = user.CreatedAt,
                LastLoginAt = user.LastLoginAt,
                LastLoginTnfsAt = user.LastLoginTnfsAt,
                LastLoginEconetAt = user.LastLoginEconetAt,
                LastFailedLoginAt = user.LastFailedLoginAt
            };

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
}
