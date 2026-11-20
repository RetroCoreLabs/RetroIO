//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace RetroIO.Tests
{
    /// <summary>
    /// Fails when a file in the repository names a path on somebody's disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The repository is public, and a drive-letter path or a home folder is right on exactly one
    /// machine. In November 2026 about 150 of them were found in code comments, test data lookups and
    /// scripts - one of them on the published API site - with nothing checking. Test images are found
    /// through <see cref="TestData"/> instead; sources are cited by name, not by where they sit.
    /// </para>
    /// <para>
    /// The only files allowed to hold a drive letter are listed in <see cref="Allowed"/>, each with
    /// the reason. Add to that list only when the drive letter itself is what the code is about.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class NoMachinePathsTests
    {
        // A drive letter followed by a slash or backslash and a name, not glued to a word before it
        // (so "Commands:\r\n" in a C# string is not a drive). Also a home folder on Linux or WSL.
        private static readonly Regex MachinePath = new Regex(
            @"(?<![A-Za-z0-9_])[A-Za-z]:(?:\\\\|\\|/)[A-Za-z]"
            + @"|/home/[A-Za-z0-9_.-]+/"
            + @"|/mnt/[a-z]/[A-Za-z]",
            RegexOptions.Compiled);

        // Repository-relative paths (forward slashes) that may hold a drive letter, and why.
        private static readonly Dictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/tests/RetroIO.Tests/VFS/GetFullPathTests.cs"] =
                "tests that the path normaliser handles a Windows drive letter; the drive letter is the input under test",
            ["src/tests/RetroIO.Tests/NoMachinePathsTests.cs"] =
                "this file: the pattern and its comments",
        };

        // Text files worth reading. Anything else (images, disk images, binaries) is skipped.
        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".md", ".ps1", ".py", ".sh", ".bat", ".json", ".yml", ".yaml", ".xml",
            ".csproj", ".props", ".targets", ".slnx", ".txt", ".html", ".js", ".css", ".config",
        };

        [Test]
        public void NoFileNamesAPathOnAParticularMachine()
        {
            string root = FindRepositoryRoot();
            StringBuilder found = new StringBuilder();
            int count = 0;
            string[] tracked = TrackedFiles(root);
            for (int i = 0; i < tracked.Length; i++)
            {
                Check(root, tracked[i], found, ref count);
            }

            Assert.That(count, Is.EqualTo(0),
                count + " line(s) name a path on a particular machine. Cite the source by name, find test data "
                + "through TestData, or use a variable such as %TEMP% or ~:" + Environment.NewLine + found);
        }

        // Reads one tracked file and records every line that names a machine path.
        private static void Check(string root, string relative, StringBuilder found, ref int count)
        {
            if (!TextExtensions.Contains(Path.GetExtension(relative)) || Allowed.ContainsKey(relative))
            {
                return;
            }

            string full = Path.Combine(root, relative);
            if (!File.Exists(full))
            {
                // Deleted in the working tree but not yet committed.
                return;
            }

            string[] lines = File.ReadAllLines(full);
            for (int n = 0; n < lines.Length; n++)
            {
                if (MachinePath.IsMatch(lines[n]))
                {
                    count++;
                    found.Append("  ").Append(relative).Append(':').Append(n + 1).Append(": ")
                         .AppendLine(lines[n].Trim());
                }
            }
        }

        // The files git tracks, plus new files not yet added that are not ignored. Ignored files
        // (build output, generated API pages, a person's own test-env.local.bat) are not the
        // repository's content, so they are not read.
        private static string[] TrackedFiles(string root)
        {
            System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo("git", "ls-files --cached --others --exclude-standard")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using System.Diagnostics.Process? git = System.Diagnostics.Process.Start(start);
            if (git == null)
            {
                Assert.Fail("Could not start git to list the repository's files.");
                return Array.Empty<string>();
            }

            string output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            Assert.That(git.ExitCode, Is.EqualTo(0), "git ls-files failed");
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        // The repository root is the first folder above the test binary that holds .git.
        private static string FindRepositoryRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            Assert.Fail("No .git folder above " + AppContext.BaseDirectory + "; this test reads the repository's own files.");
            return string.Empty;
        }
    }
}
