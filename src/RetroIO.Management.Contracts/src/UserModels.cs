//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

public class UserListResponse
{
    public List<UserDto> Users { get; set; } = new();
    public int Total { get; set; }
}

public class UserDto
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool IsActive { get; set; }
    public bool IsAdmin { get; set; }
    public bool AllowTnfs { get; set; }
    public bool AllowEconet { get; set; }
    public List<string> Groups { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? LastLoginTnfsAt { get; set; }
    public DateTime? LastLoginEconetAt { get; set; }
    public DateTime? LastFailedLoginAt { get; set; }
}

public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public List<string> Groups { get; set; } = new();
}

public class UpdateUserRequest
{
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public bool? IsActive { get; set; }
    public bool? AllowTnfs { get; set; }
    public bool? AllowEconet { get; set; }
}

public class ChangePasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}

public class GroupListResponse
{
    public List<GroupDto> Groups { get; set; } = new();
    public int Total { get; set; }
}

public class GroupDto
{
    public string GroupId { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateGroupRequest
{
    public string GroupName { get; set; } = string.Empty;
}
