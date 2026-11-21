//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;

namespace RetroIO.Storage.Local.Tests
{
    /// <summary>
    /// Runs cpmtools in WSL as an independent check on CP/M images in the CP/M tests.
    /// </summary>
    internal static class Cpmtools
    {
        // Runs a command line in WSL in the given Windows folder; the test is ignored when WSL or
        // cpmtools is not installed.
        public static string Run(string folder, string commands)
        {
            string wslFolder = "/mnt/" + char.ToLowerInvariant(folder[0]) + folder.Substring(2).Replace('\\', '/');
            System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo("wsl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("sh");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("command -v cpmcp >/dev/null || exit 99; cd '" + wslFolder + "' && " + commands);
            System.Diagnostics.Process? process;
            try
            {
                process = System.Diagnostics.Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Ignore("WSL is not installed; this check needs cpmtools in WSL.");
                return string.Empty;
            }

            using System.Diagnostics.Process running = process!;
            string output = running.StandardOutput.ReadToEnd() + running.StandardError.ReadToEnd();
            running.WaitForExit();
            if (running.ExitCode == 99)
            {
                Assert.Ignore("cpmtools is not installed in WSL (apt install cpmtools).");
            }

            Assert.That(running.ExitCode, Is.EqualTo(0), "cpmtools failed: " + output);
            return output;
        }
    }
}
