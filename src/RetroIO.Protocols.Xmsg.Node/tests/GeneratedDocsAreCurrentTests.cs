using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using RetroIO.Protocols.Xmsg.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The generated markdown was produced from the registries as they stand now.
    /// </summary>
    /// <remarks>
    /// <para><b>The last manual step, removed</b></para>
    /// <para>
    /// Three checks already run under <c>dotnet test</c>: the catalog matches the folder, the enums
    /// are described in full, and the values match the code. The fourth - that the readable markdown
    /// still matches the JSON - lived only in <c>generate.py --check</c>, which somebody has to
    /// remember to run. A check that depends on being remembered is the one that stops happening.
    /// </para>
    /// <para><b>Why a hash rather than running the generator</b></para>
    /// <para>
    /// Invoking python from a test would make the suite fail wherever python is absent or a
    /// different version, which turns a documentation check into a flaky build. Instead the
    /// generator stamps each page with the SHA-256 of the registry it read; this recomputes that
    /// hash and compares. No interpreter, no re-rendering, and it cannot pass by accident - any edit
    /// to the JSON changes the hash.
    /// </para>
    /// <para>
    /// It does NOT verify the page's prose is correct, only that it was generated from these exact
    /// bytes. Correctness of the rendering is the generator's job and is visible on the page.
    /// </para>
    /// </remarks>
    public sealed class GeneratedDocsAreCurrentTests
    {
        private static readonly Regex Stamp =
            new Regex(@"<!--\s*source-sha256:\s*(?<hash>[0-9a-f]{64})\s*-->", RegexOptions.Compiled);

        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the fixture.
        /// </summary>
        /// <param name="output">
        /// xunit's output sink.
        /// </param>
        public GeneratedDocsAreCurrentTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Every generated page carries the hash of the registry it came from, and it still matches.
        /// </summary>
        [Fact]
        public void EveryGeneratedPageMatchesItsRegistry()
        {
            string folder = ProtocolRegistry.FolderPath();
            using JsonDocument catalog = ProtocolRegistry.Load("catalog.json");

            List<(string Page, string Source)> pairs = new List<(string, string)>();
            foreach (JsonElement entry in catalog.RootElement.GetProperty("registries").EnumerateArray())
            {
                pairs.Add((entry.GetProperty("markdown").GetString()!,
                           entry.GetProperty("file").GetString()!));
            }

            // The index is generated from the catalog itself.
            pairs.Add(("PROTOCOLS.md", "catalog.json"));

            List<string> stale = new List<string>();
            foreach ((string page, string source) in pairs)
            {
                string pagePath = Path.Combine(folder, page);
                if (!File.Exists(pagePath))
                {
                    stale.Add(page + " has not been generated at all");
                    continue;
                }

                Match match = Stamp.Match(File.ReadAllText(pagePath));
                if (!match.Success)
                {
                    stale.Add(page + " carries no source stamp - it was hand-written or the "
                        + "generator changed without updating this check");
                    continue;
                }

                string expected = HashOf(Path.Combine(folder, source));
                string found = match.Groups["hash"].Value;
                if (!string.Equals(expected, found, StringComparison.Ordinal))
                {
                    stale.Add(page + " was generated from an older " + source
                        + " (page says " + found.Substring(0, 12)
                        + ", the file is now " + expected.Substring(0, 12) + ")");
                }
            }

            _output.WriteLine($"{pairs.Count} generated page(s) checked against their registries");

            if (stale.Count > 0)
            {
                Assert.Fail(
                    "The generated documentation no longer matches the registries:" + Environment.NewLine
                    + "  " + string.Join(Environment.NewLine + "  ", stale) + Environment.NewLine
                    + "Run: python DOC/protocols/generate.py");
            }
        }

        /// <summary>
        /// Hashes a file exactly as the generator does.
        /// </summary>
        /// <param name="path">
        /// The file to hash.
        /// </param>
        /// <returns>
        /// The lower-case hex SHA-256 of its bytes.
        /// </returns>
        private static string HashOf(string path)
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(File.ReadAllBytes(path));

            StringBuilder text = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                text.Append(hash[i].ToString("x2"));
            }

            return text.ToString();
        }
    }
}
