//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// A list of users.
/// </summary>
public class UserListResponse
{
    /// <summary>
    /// Gets or sets the users.
    /// </summary>
    public List<UserDto> Users { get; set; } = new();
    /// <summary>
    /// Gets or sets the number of users.
    /// </summary>
    public int Total { get; set; }
}

/// <summary>
/// One user account.
/// </summary>
public class UserDto
{
    /// <summary>
    /// Gets or sets the user identifier.
    /// </summary>
    public string UserId { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the e-mail address, or null when there is none.
    /// </summary>
    public string? Email { get; set; }
    /// <summary>
    /// Gets or sets whether the account is active.
    /// </summary>
    public bool IsActive { get; set; }
    /// <summary>
    /// Gets or sets whether the account is an administrator.
    /// </summary>
    public bool IsAdmin { get; set; }
    /// <summary>
    /// Gets or sets whether the user may use TNFS.
    /// </summary>
    public bool AllowTnfs { get; set; }
    /// <summary>
    /// Gets or sets whether the user may use Econet.
    /// </summary>
    public bool AllowEconet { get; set; }
    /// <summary>
    /// Gets or sets the names of the groups the user belongs to.
    /// </summary>
    public List<string> Groups { get; set; } = new();
    /// <summary>
    /// Gets or sets when the account was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
    /// <summary>
    /// Gets or sets the time of the last login, or null when there is none.
    /// </summary>
    public DateTime? LastLoginAt { get; set; }
    /// <summary>
    /// Gets or sets the time of the last TNFS login, or null when there is none.
    /// </summary>
    public DateTime? LastLoginTnfsAt { get; set; }
    /// <summary>
    /// Gets or sets the time of the last Econet login, or null when there is none.
    /// </summary>
    public DateTime? LastLoginEconetAt { get; set; }
    /// <summary>
    /// Gets or sets the time of the last failed login, or null when there is none.
    /// </summary>
    public DateTime? LastFailedLoginAt { get; set; }
}

/// <summary>
/// A request to create a user account.
/// </summary>
public class CreateUserRequest
{
    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the password.
    /// </summary>
    public string Password { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the display name, or null when there is none.
    /// </summary>
    public string? DisplayName { get; set; }
    /// <summary>
    /// Gets or sets the e-mail address, or null when there is none.
    /// </summary>
    public string? Email { get; set; }
    /// <summary>
    /// Gets or sets the names of the groups to add the user to.
    /// </summary>
    public List<string> Groups { get; set; } = new();
}

/// <summary>
/// A request to change a user account. A property left null is not changed.
/// </summary>
public class UpdateUserRequest
{
    /// <summary>
    /// Gets or sets the new display name, or null to keep the current one.
    /// </summary>
    public string? DisplayName { get; set; }
    /// <summary>
    /// Gets or sets the new e-mail address, or null to keep the current one.
    /// </summary>
    public string? Email { get; set; }
    /// <summary>
    /// Gets or sets whether the account is active, or null to keep the current value.
    /// </summary>
    public bool? IsActive { get; set; }
    /// <summary>
    /// Gets or sets whether the user may use TNFS, or null to keep the current value.
    /// </summary>
    public bool? AllowTnfs { get; set; }
    /// <summary>
    /// Gets or sets whether the user may use Econet, or null to keep the current value.
    /// </summary>
    public bool? AllowEconet { get; set; }
}

/// <summary>
/// A request to change a password.
/// </summary>
public class ChangePasswordRequest
{
    /// <summary>
    /// Gets or sets the new password.
    /// </summary>
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// A list of groups.
/// </summary>
public class GroupListResponse
{
    /// <summary>
    /// Gets or sets the groups.
    /// </summary>
    public List<GroupDto> Groups { get; set; } = new();
    /// <summary>
    /// Gets or sets the number of groups.
    /// </summary>
    public int Total { get; set; }
}

/// <summary>
/// One user group.
/// </summary>
public class GroupDto
{
    /// <summary>
    /// Gets or sets the group identifier.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the group name.
    /// </summary>
    public string GroupName { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the number of members.
    /// </summary>
    public int MemberCount { get; set; }
    /// <summary>
    /// Gets or sets when the group was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A request to create a group.
/// </summary>
public class CreateGroupRequest
{
    /// <summary>
    /// Gets or sets the group name.
    /// </summary>
    public string GroupName { get; set; } = string.Empty;
}
