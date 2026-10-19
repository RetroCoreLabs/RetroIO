//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

namespace RetroIO.Storage.Providers;

/// <summary>
/// Resolves host-style paths the way <c>IFileSystemBackend.GetFullPath</c> promises: one kind of
/// separator, no repeated separators, no <c>.</c> or <c>..</c> parts, no trailing separator.
/// </summary>
/// <remarks>
/// The result always uses forward slashes. A leading drive such as <c>C:</c> is kept and counts as
/// part of the root. A path that starts with a separator is rooted and stays rooted. A path that
/// does not is relative and stays relative; nothing is added in front of it.
/// </remarks>
internal static class HostPathResolver
{
    /// <summary>
    /// Resolves a path.
    /// </summary>
    /// <param name="path">
    /// The path to resolve. Backslashes count as slashes.
    /// </param>
    /// <param name="mountRoot">
    /// A directory that the result must not leave, or <c>null</c> for none. It only matters when
    /// <paramref name="path"/> starts inside this directory: such a path may not climb out of it.
    /// </param>
    /// <returns>
    /// The resolved path.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="path"/> is null.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when a <c>..</c> part would climb above the start of the path, or out of
    /// <paramref name="mountRoot"/>.
    /// </exception>
    public static string Resolve(string path, string? mountRoot = null)
    {
        if (path == null)
            throw new ArgumentNullException(nameof(path));

        string[]? rootSegments = null;
        string rootDrive = string.Empty;
        if (!string.IsNullOrEmpty(mountRoot))
        {
            SplitRoot(mountRoot.Replace('\\', '/'), out rootDrive, out _, out string rootRest);
            rootSegments = SplitSegments(rootRest);
        }

        SplitRoot(path.Replace('\\', '/'), out string drive, out bool rooted, out string rest);
        string[] parts = rest.Split('/');

        List<string> stack = new List<string>(parts.Length);
        bool insideMountRoot = false;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || part == ".")
                continue;

            if (part == "..")
            {
                if (stack.Count == 0)
                    throw new UnauthorizedAccessException($"The path '{path}' climbs above its root.");
                if (insideMountRoot && stack.Count <= rootSegments!.Length)
                    throw new UnauthorizedAccessException($"The path '{path}' climbs out of '{mountRoot}'.");

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(part);

            // Note when the path has just walked into the mount root, segment by segment.
            if (rootSegments != null && stack.Count == rootSegments.Length
                && string.Equals(drive, rootDrive, StringComparison.OrdinalIgnoreCase)
                && StartsWith(stack, rootSegments))
            {
                insideMountRoot = true;
            }
        }

        string joined = string.Join("/", stack);
        if (rooted)
            return drive + "/" + joined;

        return drive + joined;
    }

    // Splits "C:/x" into drive "C:", rooted true, rest "x". The drive is empty when there is none.
    private static void SplitRoot(string path, out string drive, out bool rooted, out string rest)
    {
        drive = string.Empty;
        rest = path;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            drive = path.Substring(0, 2);
            rest = path.Substring(2);
        }

        rooted = rest.StartsWith('/');
    }

    private static string[] SplitSegments(string path)
    {
        string[] parts = path.Split('/');
        List<string> kept = new List<string>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != 0 && parts[i] != ".")
                kept.Add(parts[i]);
        }
        return kept.ToArray();
    }

    private static bool StartsWith(List<string> stack, string[] prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
        {
            if (!string.Equals(stack[i], prefix[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
