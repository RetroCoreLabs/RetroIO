//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// A list of volumes.
/// </summary>
public class VolumeListResponse
{
    /// <summary>
    /// Gets or sets the volumes.
    /// </summary>
    public List<VolumeDto> Volumes { get; set; } = new();
    /// <summary>
    /// Gets or sets the number of volumes.
    /// </summary>
    public int Total { get; set; }
}

/// <summary>
/// One volume.
/// </summary>
public class VolumeDto
{
    /// <summary>
    /// Gets or sets the path of the volume.
    /// </summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the name of the volume.
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the type of the volume, as text.
    /// </summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the size in bytes.
    /// </summary>
    public long Size { get; set; }
    /// <summary>
    /// Gets or sets the size as display text.
    /// </summary>
    public string SizeFormatted { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets when the volume was last modified, or null when not known.
    /// </summary>
    public DateTime? LastModified { get; set; }
    /// <summary>
    /// Gets or sets whether the volume is a directory.
    /// </summary>
    public bool IsDirectory { get; set; }
}

/// <summary>
/// A request to list the contents of a path in a volume.
/// </summary>
public class VolumeContentsRequest
{
    /// <summary>
    /// Gets or sets the path to list.
    /// </summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// The contents of a path in a volume.
/// </summary>
public class VolumeContentsResponse
{
    /// <summary>
    /// Gets or sets the path that was listed.
    /// </summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the entries found.
    /// </summary>
    public List<VolumeEntryDto> Entries { get; set; } = new();
    /// <summary>
    /// Gets or sets the number of entries.
    /// </summary>
    public int Total { get; set; }
}

/// <summary>
/// One file or directory in a volume.
/// </summary>
public class VolumeEntryDto
{
    /// <summary>
    /// Gets or sets the entry name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the full path of the entry.
    /// </summary>
    public string FullPath { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets whether the entry is a directory.
    /// </summary>
    public bool IsDirectory { get; set; }
    /// <summary>
    /// Gets or sets the size in bytes.
    /// </summary>
    public long Size { get; set; }
    /// <summary>
    /// Gets or sets the size as display text.
    /// </summary>
    public string SizeFormatted { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets when the entry was last modified, or null when not known.
    /// </summary>
    public DateTime? LastModified { get; set; }
}
