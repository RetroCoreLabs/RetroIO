//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

public class VolumeListResponse
{
    public List<VolumeDto> Volumes { get; set; } = new();
    public int Total { get; set; }
}

public class VolumeDto
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long Size { get; set; }
    public string SizeFormatted { get; set; } = string.Empty;
    public DateTime? LastModified { get; set; }
    public bool IsDirectory { get; set; }
}

public class VolumeContentsRequest
{
    public string Path { get; set; } = string.Empty;
}

public class VolumeContentsResponse
{
    public string Path { get; set; } = string.Empty;
    public List<VolumeEntryDto> Entries { get; set; } = new();
    public int Total { get; set; }
}

public class VolumeEntryDto
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public string SizeFormatted { get; set; } = string.Empty;
    public DateTime? LastModified { get; set; }
}
