//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Protocols.Econet.Client;

/// <summary>
/// One catalogue entry returned by an Econet examine operation.
/// </summary>
public sealed class AunDirectoryEntry
{
  public string Name { get; init; } = string.Empty;
  public bool IsDirectory { get; init; }
  public long Size { get; init; }
}
