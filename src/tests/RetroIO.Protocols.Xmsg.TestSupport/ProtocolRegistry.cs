//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RetroIO.Protocols.Xmsg.TestSupport
{
    /// <summary>
    /// Loads the machine-readable protocol registry from <c>DOC/protocols</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives here rather than in one test project because the registry spans layers: the header and
    /// bitfields are checked against <c>RetroIO.Protocols.Xmsg</c>, the TAD opcodes against <c>RetroIO.Protocols.Xmsg.Node</c>.
    /// Copying a loader into each would be the first step towards two loaders that disagree about
    /// where the registry is.
    /// </para>
    /// <para>
    /// See <c>DOC/protocols/README.md</c> for what the registry is and the rules that keep it
    /// honest.
    /// </para>
    /// </remarks>
    public static class ProtocolRegistry
    {
        /// <summary>
        /// Loads one registry file.
        /// </summary>
        /// <param name="fileName">
        /// The file name inside <c>DOC/protocols</c>, for example <c>sintran-wire.json</c>.
        /// </param>
        /// <returns>
        /// The parsed document. The caller owns it and should dispose it.
        /// </returns>
        /// <exception cref="FileNotFoundException">
        /// Thrown when the file cannot be found above the test binary.
        /// </exception>
        public static JsonDocument Load(string fileName)
        {
            // Walk up from the test binary until the repository folder appears. A fixed relative
            // depth breaks the moment a test project moves.
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "DOC", "protocols", fileName);
                if (File.Exists(candidate))
                {
                    return JsonDocument.Parse(File.ReadAllText(candidate));
                }

                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                "DOC/protocols/" + fileName + " was not found above " + AppContext.BaseDirectory
                + ". The registry is required: it is the statement of what the wire IS.");
        }


        /// <summary>
        /// The folder the registries live in.
        /// </summary>
        /// <returns>
        /// The absolute path to <c>DOC/protocols</c>.
        /// </returns>
        public static string FolderPath()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "DOC", "protocols");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "DOC/protocols was not found above " + AppContext.BaseDirectory);
        }

        /// <summary>
        /// The registry files, in the order the catalog says to read them.
        /// </summary>
        /// <returns>
        /// One file name per registry.
        /// </returns>
        /// <remarks>
        /// Read from <c>catalog.json</c> rather than written out in a test. A hard-coded list is a
        /// blind spot exactly where it hurts: a registry missing from it is never checked, and
        /// nothing reports that, because the list is what decides what gets looked at.
        /// </remarks>
        public static IReadOnlyList<string> CataloguedRegistries()
        {
            using JsonDocument catalog = Load("catalog.json");
            List<(int, string)> found = new List<(int, string)>();
            foreach (JsonElement entry in catalog.RootElement.GetProperty("registries").EnumerateArray())
            {
                found.Add((entry.GetProperty("read_order").GetInt32(),
                           entry.GetProperty("file").GetString()!));
            }

            found.Sort();
            List<string> names = new List<string>();
            for (int i = 0; i < found.Count; i++)
            {
                names.Add(found[i].Item2);
            }

            return names;
        }

        /// <summary>
        /// Finds every claim carrying a status with no evidence behind it.
        /// </summary>
        /// <param name="element">
        /// The node to walk, usually a document root.
        /// </param>
        /// <returns>
        /// The paths of the offending objects, empty when every claim names its evidence.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The one rule that stops a registry rotting into the documents it replaced. MEASURED means
        /// somebody watched it happen; without a pointer to WHERE, it is confidence rather than
        /// evidence - and confidence is how a fitted model became a "fact" that survived for months.
        /// </para>
        /// <para>
        /// SUPERSEDED and KNOWN_GAP are exempt: they describe a state of affairs rather than a claim
        /// about the wire, and carry their own explanation instead.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<string> FindClaimsWithoutEvidence(JsonElement element)
        {
            List<string> offenders = new List<string>();
            Walk(element, string.Empty, offenders);
            return offenders;
        }

        /// <summary>
        /// Parses a <c>0x</c>-prefixed hex string.
        /// </summary>
        /// <param name="text">
        /// The text, with or without the prefix.
        /// </param>
        /// <returns>
        /// The value.
        /// </returns>
        public static long ParseHex(string text)
        {
            string trimmed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? text.Substring(2)
                : text;
            return long.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static void Walk(JsonElement element, string path, List<string> offenders)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("status", out JsonElement status))
                {
                    string value = status.GetString() ?? string.Empty;
                    bool needsEvidence = value == "MEASURED" || value == "INFERRED" || value == "UNKNOWN";
                    bool hasEvidence = element.TryGetProperty("evidence", out JsonElement evidence)
                        && !string.IsNullOrWhiteSpace(evidence.GetString());

                    if (needsEvidence && !hasEvidence)
                    {
                        offenders.Add("  " + path + " (status " + value + ")");
                    }
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, path + "/" + property.Name, offenders);
                }

                return;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", offenders);
                    index++;
                }
            }
        }
    }
}
