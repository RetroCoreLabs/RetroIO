//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Storage.Core;
using RetroIO.Storage.Utilities;

namespace RetroIO.Management.Api.Controllers;

/// <summary>
/// The mounted volumes list, mounts.json in the data folder, which Econet discs name by id.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class MountsController : ControllerBase
{
    private readonly string _mountsPath;

    /// <summary>
    /// Creates the controller.
    /// </summary>
    /// <param name="retroFsPath">
    /// The data folder holding mounts.json; null (what DI does) means the real one. Tests pass a temp folder.
    /// </param>
    public MountsController(string? retroFsPath = null)
    {
        _mountsPath = Path.Combine(retroFsPath ?? PathHelper.GetRetroFSPath(), "mounts.json");
    }

    /// <summary>
    /// Gets every mounted volume. Passwords are never sent back.
    /// </summary>
    /// <returns>
    /// The volumes.
    /// </returns>
    [HttpGet]
    public ActionResult<ApiResponse<List<MountDto>>> GetMounts()
    {
        return Ok(ApiResponse<List<MountDto>>.Ok(ToDtos(Load())));
    }

    /// <summary>
    /// Replaces the whole list. A volume keeps its stored password unless a new one (or an empty one, to remove it)
    /// is given. Read at start: a change applies after RetroServer restarts.
    /// </summary>
    /// <param name="mounts">
    /// Every volume.
    /// </param>
    /// <returns>
    /// The list as saved, or why it was refused.
    /// </returns>
    [HttpPut]
    public ActionResult<ApiResponse<List<MountDto>>> PutMounts([FromBody] List<MountDto> mounts)
    {
        if (mounts == null)
        {
            return BadRequest(ApiResponse<List<MountDto>>.Fail("No list was sent."));
        }

        for (int i = 0; i < mounts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(mounts[i].VolumeId) || string.IsNullOrWhiteSpace(mounts[i].ProviderType)
                || string.IsNullOrWhiteSpace(mounts[i].SourceUri))
            {
                return BadRequest(ApiResponse<List<MountDto>>.Fail("Every volume needs an id, a storage kind and an address."));
            }

            for (int j = 0; j < i; j++)
            {
                if (string.Equals(mounts[j].VolumeId.Trim(), mounts[i].VolumeId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(ApiResponse<List<MountDto>>.Fail("The volume id '" + mounts[i].VolumeId + "' is used twice."));
                }
            }
        }

        UnifiedMountConfig stored = Load();
        UnifiedMountConfig config = new UnifiedMountConfig { Version = stored.Version };
        for (int i = 0; i < mounts.Count; i++)
        {
            MountDto m = mounts[i];
            string id = m.VolumeId.Trim();
            string? password = m.NewPassword == null
                ? StoredPassword(stored, id)
                : (m.NewPassword.Length == 0 ? null : m.NewPassword);

            config.Mounts.Add(new UnifiedMountEntry
            {
                VolumeId = id,
                ProviderType = m.ProviderType.Trim(),
                SourceUri = m.SourceUri.Trim(),
                IsReadOnly = m.IsReadOnly,
                FileSystemType = string.IsNullOrWhiteSpace(m.FileSystemType) ? null : m.FileSystemType.Trim(),
                Username = string.IsNullOrWhiteSpace(m.Username) ? null : m.Username.Trim(),
                Password = password,
            });
        }

        UnifiedMountConfigStore.Save(config, _mountsPath);
        return Ok(ApiResponse<List<MountDto>>.Ok(ToDtos(config)));
    }

    private UnifiedMountConfig Load()
    {
        return System.IO.File.Exists(_mountsPath) ? UnifiedMountConfigStore.Load(_mountsPath) : new UnifiedMountConfig();
    }

    private static string? StoredPassword(UnifiedMountConfig stored, string id)
    {
        for (int i = 0; i < stored.Mounts.Count; i++)
        {
            if (string.Equals(stored.Mounts[i].VolumeId, id, StringComparison.OrdinalIgnoreCase))
            {
                return stored.Mounts[i].Password;
            }
        }

        return null;
    }

    private static List<MountDto> ToDtos(UnifiedMountConfig config)
    {
        List<MountDto> result = new List<MountDto>();
        for (int i = 0; i < config.Mounts.Count; i++)
        {
            UnifiedMountEntry m = config.Mounts[i];
            result.Add(new MountDto
            {
                VolumeId = m.VolumeId,
                ProviderType = m.ProviderType,
                SourceUri = m.SourceUri,
                IsReadOnly = m.IsReadOnly,
                FileSystemType = m.FileSystemType,
                Username = m.Username,
                PasswordSet = !string.IsNullOrEmpty(m.Password),
            });
        }

        return result;
    }
}
