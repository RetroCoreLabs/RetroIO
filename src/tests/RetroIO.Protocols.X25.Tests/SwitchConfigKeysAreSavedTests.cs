//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Every setting the switch's configuration loader reads must also be written by <c>write memory</c>, or a saved
    /// configuration silently loses it on the next start. Twice a new key (<c>segment_size</c>, <c>charging_info</c>)
    /// needed a hand-made addition to the writer; this test makes the next one fail instead.
    /// </summary>
    /// <remarks>
    /// It reads the two source files: the loader names each key in an <c>ini.Get...(section, "key", ...)</c> call, and
    /// the writer must name the same key in a string. That is a check on the source, not on behaviour; the round-trip
    /// tests in <c>SwitchCliTests</c> check the values.
    /// </remarks>
    [TestFixture]
    public sealed class SwitchConfigKeysAreSavedTests
    {
        private static string SourceRoot()
        {
            DirectoryInfo? d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "RetroIO.Protocols.X25.Switch", "src", "SwitchConfigLoader.cs")))
            {
                d = d.Parent;
            }

            Assert.That(d, Is.Not.Null, "the source tree was not found above the test folder");
            return d!.FullName;
        }

        [Test]
        public void EveryKeyTheLoaderReads_IsWrittenByWriteMemory()
        {
            string root = SourceRoot();
            string loader = File.ReadAllText(Path.Combine(root, "RetroIO.Protocols.X25.Switch", "src", "SwitchConfigLoader.cs"));
            string writer = File.ReadAllText(Path.Combine(root, "RetroIO.Protocols.X25.Switch.Management", "src", "SwitchCli.cs"));

            MatchCollection keys = Regex.Matches(loader, "ini\\.Get\\w*\\([^,]+,\\s*\"([a-z0-9_]+)\"");
            Assert.That(keys.Count, Is.GreaterThan(30), "the loader's key pattern no longer matches; fix this test");

            string missing = string.Empty;
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i].Groups[1].Value;
                bool written = writer.Contains("\"" + key + " ") || writer.Contains("\"" + key + "\"")
                    || Regex.IsMatch(writer, "\\b" + key + " = ");
                if (!written && !missing.Contains(" " + key + " "))
                {
                    missing += " " + key + " ";
                }
            }

            Assert.That(missing.Trim(), Is.Empty, "read by the loader but never saved by write memory");
        }
    }
}
