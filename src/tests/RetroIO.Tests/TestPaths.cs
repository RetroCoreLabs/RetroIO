//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.IO;
using System.Runtime.InteropServices;

namespace RetroIO.Tests
{
    /// <summary>
    /// Converts Windows paths to work in both Windows and WSL.
    /// Test files keep their Windows paths everywhere — just wrap
    /// them with TestPaths.Resolve() and it works on both platforms.
    /// </summary>
    internal static class TestPaths
    {
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// Takes a Windows path like "C:\Users\ronny\foo" or "E:\Dev\bar"
        /// and returns a path that works in the current environment.
        /// On Windows: returns the input unchanged.
        /// On WSL/Linux: converts to /mnt/c/Users/ronny/foo or /mnt/e/Dev/bar.
        /// </summary>
        public static string Resolve(string windowsPath)
        {
            if (IsWindows)
                return windowsPath;

            // Already a Unix path — return as-is
            if (windowsPath.Length > 0 && windowsPath[0] == '/')
                return windowsPath;

            // Convert  C:\foo\bar  →  /mnt/c/foo/bar
            //          E:\Dev\x    →  /mnt/e/Dev/x
            if (windowsPath.Length >= 2 && windowsPath[1] == ':')
            {
                char drive = char.ToLower(windowsPath[0]);
                string rest = windowsPath.Substring(2).Replace('\\', '/');
                return $"/mnt/{drive}{rest}";
            }

            // Relative path with backslashes — just flip slashes
            return windowsPath.Replace('\\', '/');
        }

        /// <summary>
        /// Resolves a directory path. Same as Resolve but
        /// named for clarity at call sites that expect a directory.
        /// </summary>
        public static string ResolveDir(string windowsPath)
        {
            return Resolve(windowsPath);
        }
    }
}
