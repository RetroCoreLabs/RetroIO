//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.Mvc;
using RetroIO.Management.Contracts;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Management.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class VolumesController : ControllerBase
{
    private readonly IFileSystemBackend _fileSystem;
    private readonly ILogger<VolumesController> _logger;
    private readonly string _volumesPath;

    public VolumesController(IFileSystemBackend fileSystem, ILogger<VolumesController> logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;

        // Get volumes path from RetroFS directory
        _volumesPath = GetVolumesPath();
    }

    /// <summary>
    /// List all volumes (disk images) in the volumes directory
    /// </summary>
    [HttpGet]
    public ActionResult<ApiResponse<VolumeListResponse>> GetVolumes()
    {
        try
        {
            if (!_fileSystem.DirectoryExists(_volumesPath))
            {
                Directory.CreateDirectory(_volumesPath);
            }

            var volumes = new List<VolumeDto>();
            var entries = _fileSystem.EnumerateFileSystemEntries(_volumesPath);

            foreach (var entry in entries)
            {
                if (_fileSystem.IsDirectory(entry))
                {
                    continue; // Skip directories
                }

                var fileInfo = _fileSystem.GetFileInfo(entry);
                if (fileInfo == null)
                {
                    continue;
                }

                var fileName = _fileSystem.GetFileName(entry);
                var fileType = GetFileType(fileName);

                volumes.Add(new VolumeDto
                {
                    Path = entry,
                    Name = fileName,
                    Type = fileType,
                    Size = fileInfo.Length,
                    SizeFormatted = FormatBytes(fileInfo.Length),
                    LastModified = fileInfo.LastWriteTime,
                    IsDirectory = false
                });
            }

            var response = new VolumeListResponse
            {
                Volumes = volumes,
                Total = volumes.Count
            };

            return Ok(ApiResponse<VolumeListResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing volumes");
            return StatusCode(500, ApiResponse<VolumeListResponse>.Fail("Failed to list volumes"));
        }
    }

    /// <summary>
    /// Get contents of a specific directory or volume
    /// </summary>
    [HttpPost("browse")]
    public ActionResult<ApiResponse<VolumeContentsResponse>> BrowseVolume([FromBody] VolumeContentsRequest request)
    {
        try
        {
            var path = request.Path;

            // Security: Ensure path is within volumes directory
            var fullPath = _fileSystem.GetFullPath(path);
            if (!fullPath.StartsWith(_volumesPath, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(ApiResponse<VolumeContentsResponse>.Fail("Invalid path", "INVALID_PATH"));
            }

            if (!_fileSystem.DirectoryExists(fullPath))
            {
                return NotFound(ApiResponse<VolumeContentsResponse>.Fail("Directory not found", "DIRECTORY_NOT_FOUND"));
            }

            var entries = new List<VolumeEntryDto>();
            var fileSystemEntries = _fileSystem.EnumerateFileSystemEntries(fullPath);

            foreach (var entry in fileSystemEntries)
            {
                var fileName = _fileSystem.GetFileName(entry);
                var isDirectory = _fileSystem.IsDirectory(entry);
                var fileInfo = _fileSystem.GetFileInfo(entry);

                entries.Add(new VolumeEntryDto
                {
                    Name = fileName,
                    FullPath = entry,
                    IsDirectory = isDirectory,
                    Size = fileInfo?.Length ?? 0,
                    SizeFormatted = FormatBytes(fileInfo?.Length ?? 0),
                    LastModified = fileInfo?.LastWriteTime
                });
            }

            var response = new VolumeContentsResponse
            {
                Path = fullPath,
                Entries = entries,
                Total = entries.Count
            };

            return Ok(ApiResponse<VolumeContentsResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error browsing volume {Path}", request.Path);
            return StatusCode(500, ApiResponse<VolumeContentsResponse>.Fail("Failed to browse volume"));
        }
    }

    /// <summary>
    /// Get volume information by path
    /// </summary>
    [HttpGet("info")]
    public ActionResult<ApiResponse<VolumeDto>> GetVolumeInfo([FromQuery] string path)
    {
        try
        {
            var fullPath = _fileSystem.GetFullPath(path);

            // Security: Ensure path is within volumes directory
            if (!fullPath.StartsWith(_volumesPath, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(ApiResponse<VolumeDto>.Fail("Invalid path", "INVALID_PATH"));
            }

            if (!_fileSystem.FileExists(fullPath))
            {
                return NotFound(ApiResponse<VolumeDto>.Fail("Volume not found", "VOLUME_NOT_FOUND"));
            }

            var fileInfo = _fileSystem.GetFileInfo(fullPath);
            if (fileInfo == null)
            {
                return NotFound(ApiResponse<VolumeDto>.Fail("Volume not found", "VOLUME_NOT_FOUND"));
            }

            var fileName = _fileSystem.GetFileName(fullPath);
            var volumeDto = new VolumeDto
            {
                Path = fullPath,
                Name = fileName,
                Type = GetFileType(fileName),
                Size = fileInfo.Length,
                SizeFormatted = FormatBytes(fileInfo.Length),
                LastModified = fileInfo.LastWriteTime,
                IsDirectory = false
            };

            return Ok(ApiResponse<VolumeDto>.Ok(volumeDto));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting volume info for {Path}", path);
            return StatusCode(500, ApiResponse<VolumeDto>.Fail("Failed to get volume info"));
        }
    }

    private static string GetVolumesPath()
    {
        string basePath;

        if (OperatingSystem.IsWindows())
        {
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            basePath = Path.Combine(documentsPath, "RetroFS", "volumes");
        }
        else
        {
            string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            if (string.IsNullOrEmpty(xdgDataHome))
            {
                xdgDataHome = Path.Combine(homeDir, ".local", "share");
            }

            basePath = Path.Combine(xdgDataHome, "retrofs", "volumes");
        }

        return basePath;
    }

    private static string GetFileType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".d64" => "D64 Disk Image (1541)",
            ".d71" => "D71 Disk Image (1571)",
            ".d81" => "D81 Disk Image (1581)",
            ".t64" => "T64 Tape Image",
            ".vdk" => "VDK Disk Image (CoCo)",
            ".dmk" => "DMK Disk Image (CoCo/Dragon)",
            ".ndfs" => "NDFS Disk Image",
            ".dsk" => "Generic Disk Image",
            ".img" => "Raw Disk Image",
            ".iso" => "ISO Disk Image",
            _ => "Unknown"
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}
