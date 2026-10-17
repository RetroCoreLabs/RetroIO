//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using RetroIO.Protocols.Xmsg.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The catalog and the folder agree about which protocols exist.
    /// </summary>
    /// <remarks>
    /// <para><b>The hole this closes</b></para>
    /// <para>
    /// Every other check reads a list of registries. If that list is written in the test, adding a
    /// fifth protocol and forgetting to add it there means the new registry is never checked at
    /// all - it can contradict the code freely, and every test stays green. The list being wrong is
    /// invisible precisely because the list is what decides what gets looked at.
    /// </para>
    /// <para>
    /// So the list lives in <c>catalog.json</c>, everything reads it, and this test holds the
    /// catalog to the folder in both directions: a file not listed fails, and a listing with no
    /// file fails.
    /// </para>
    /// </remarks>
    public sealed class RegistryCatalogTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the fixture.
        /// </summary>
        /// <param name="output">
        /// xunit's output sink.
        /// </param>
        public RegistryCatalogTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Every registry in the folder is catalogued, and every catalogue entry exists.
        /// </summary>
        [Fact]
        public void TheCatalogAndTheFolderAgree()
        {
            using JsonDocument catalog = ProtocolRegistry.Load("catalog.json");
            string folder = ProtocolRegistry.FolderPath();

            HashSet<string> listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement entry in catalog.RootElement.GetProperty("registries").EnumerateArray())
            {
                string file = entry.GetProperty("file").GetString()!;
                Assert.True(
                    listed.Add(file),
                    "The catalog lists " + file + " twice.");
                Assert.True(
                    File.Exists(Path.Combine(folder, file)),
                    "The catalog lists " + file + ", which does not exist. Either it was deleted "
                    + "without removing the entry, or the entry is a typo.");
            }

            HashSet<string> ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement file in catalog.RootElement
                .GetProperty("not_registries").GetProperty("files").EnumerateArray())
            {
                ignore.Add(file.GetString()!);
            }

            List<string> uncatalogued = new List<string>();
            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                string name = Path.GetFileName(path);
                if (!listed.Contains(name) && !ignore.Contains(name))
                {
                    uncatalogued.Add(name);
                }
            }

            if (uncatalogued.Count > 0)
            {
                Assert.Fail(
                    "These registries are not in catalog.json, so NOTHING checks them:"
                    + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", uncatalogued)
                    + Environment.NewLine
                    + "Add them to the catalog, or list them under not_registries with the folder's "
                    + "other non-registry files.");
            }

            _output.WriteLine($"{listed.Count} registries catalogued, folder agrees");
            Assert.True(listed.Count >= 4);
        }

        /// <summary>
        /// Each catalogue entry says what the protocol is, and where its generated page lives.
        /// </summary>
        /// <remarks>
        /// A catalog of file names would be an index. What makes it useful to somebody arriving
        /// cold is the one-line description, so it is required rather than optional - and the read
        /// order has to be unique, because "read this first" means nothing if two things claim it.
        /// </remarks>
        [Fact]
        public void EveryEntryDescribesItselfAndHasAPlaceInTheOrder()
        {
            using JsonDocument catalog = ProtocolRegistry.Load("catalog.json");
            string folder = ProtocolRegistry.FolderPath();

            HashSet<int> orders = new HashSet<int>();
            foreach (JsonElement entry in catalog.RootElement.GetProperty("registries").EnumerateArray())
            {
                string file = entry.GetProperty("file").GetString()!;

                Assert.True(entry.TryGetProperty("title", out JsonElement title)
                    && !string.IsNullOrWhiteSpace(title.GetString()),
                    file + " has no title.");

                Assert.True(entry.TryGetProperty("one_line", out JsonElement line)
                    && !string.IsNullOrWhiteSpace(line.GetString()),
                    file + " has no one_line. Somebody arriving cold needs to know what the "
                    + "protocol IS before they open it.");

                Assert.True(entry.TryGetProperty("markdown", out JsonElement md),
                    file + " does not say where its generated page lives.");
                Assert.True(File.Exists(Path.Combine(folder, md.GetString()!)),
                    file + " points at " + md.GetString() + ", which has not been generated. "
                    + "Run generate.py.");

                int order = entry.GetProperty("read_order").GetInt32();
                Assert.True(orders.Add(order),
                    file + " claims read_order " + order + ", which another registry already has.");
            }
        }
    }
}
