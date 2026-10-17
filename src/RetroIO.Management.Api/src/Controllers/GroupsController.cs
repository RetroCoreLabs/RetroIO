//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Identity;

namespace RetroIO.Management.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class GroupsController : ControllerBase
{
    private readonly IRetroIDP _idp;
    private readonly ILogger<GroupsController> _logger;

    public GroupsController(IRetroIDP idp, ILogger<GroupsController> logger)
    {
        _idp = idp;
        _logger = logger;
    }

    /// <summary>
    /// List all groups
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<GroupListResponse>>> GetGroups()
    {
        try
        {
            var groups = await _idp.ListGroupsAsync();
            var users = await _idp.ListUsersAsync();

            var groupDtos = new List<GroupDto>();
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                int memberCount = 0;

                for (int j = 0; j < users.Length; j++)
                {
                    if (users[j].Groups.Contains(group.GroupId))
                    {
                        memberCount++;
                    }
                }

                groupDtos.Add(new GroupDto
                {
                    GroupId = group.GroupId.ToString(),
                    GroupName = group.GroupName,
                    MemberCount = memberCount,
                    CreatedAt = group.CreatedAt
                });
            }

            var response = new GroupListResponse
            {
                Groups = groupDtos,
                Total = groupDtos.Count
            };

            return Ok(ApiResponse<GroupListResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing groups");
            return StatusCode(500, ApiResponse<GroupListResponse>.Fail("Internal server error"));
        }
    }

    /// <summary>
    /// Create a new group
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<GroupDto>>> CreateGroup([FromBody] CreateGroupRequest request)
    {
        try
        {
            var newGroup = new RetroGroup
            {
                GroupId = Guid.NewGuid(),
                GroupName = request.GroupName,
                CreatedAt = DateTime.UtcNow
            };

            await _idp.CreateGroupAsync(newGroup);
            await _idp.SaveAsync();

            var groupDto = new GroupDto
            {
                GroupId = newGroup.GroupId.ToString(),
                GroupName = newGroup.GroupName,
                MemberCount = 0,
                CreatedAt = newGroup.CreatedAt
            };

            return Ok(ApiResponse<GroupDto>.Ok(groupDto, "Group created successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating group");
            return StatusCode(500, ApiResponse<GroupDto>.Fail("Failed to create group"));
        }
    }

    /// <summary>
    /// Delete a group
    /// </summary>
    [HttpDelete("{groupId}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteGroup(string groupId)
    {
        try
        {
            var guid = Guid.Parse(groupId);
            var success = await _idp.DeleteGroupAsync(guid);

            if (!success)
            {
                return NotFound(ApiResponse<object>.Fail("Group not found", "GROUP_NOT_FOUND"));
            }

            await _idp.SaveAsync();

            return Ok(ApiResponse<object>.Ok(null, "Group deleted successfully"));
        }
        catch (FormatException)
        {
            return BadRequest(ApiResponse<object>.Fail("Invalid group ID format", "INVALID_FORMAT"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting group {GroupId}", groupId);
            return StatusCode(500, ApiResponse<object>.Fail("Failed to delete group"));
        }
    }
}
