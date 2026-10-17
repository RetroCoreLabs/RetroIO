//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Client;

/// <summary>
/// One catalogue entry returned by an Econet examine operation.
/// </summary>
public sealed class AunDirectoryEntry
{
  /// <summary>
  /// Gets the name of the entry.
  /// </summary>
  public string Name { get; init; } = string.Empty;
  /// <summary>
  /// Gets a value indicating whether the entry is a directory.
  /// </summary>
  public bool IsDirectory { get; init; }
  /// <summary>
  /// Gets the size of the entry in bytes.
  /// </summary>
  public long Size { get; init; }
}
