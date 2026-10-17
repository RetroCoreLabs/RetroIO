//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using RetroIO.Protocols.Xmsg.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The registry and the enums it describes are complete in BOTH directions.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the other conformance tests are not enough</b></para>
    /// <para>
    /// They check that every entry in a registry exists in the code with the value the registry
    /// gives it. That catches an edited value, and misses the thing that actually happens over
    /// time: somebody adds a member to an enum and does not write it down. The registry stays true
    /// and becomes a SUBSET, which reads as complete and is not. That is how code and documentation
    /// grow apart while every test stays green.
    /// </para>
    /// <para>
    /// So this walks the enum by reflection and requires the two sets to match exactly. A new
    /// member fails the build until it is described - with a status and evidence, because the
    /// evidence rule applies to it too.
    /// </para>
    /// <para><b>Reflection rather than XML comments</b></para>
    /// <para>
    /// An XML doc comment would have to be generated into a file, shipped beside the assembly and
    /// parsed back - three things that can be missing or stale. The member names and values are in
    /// the metadata already, exactly and always. Where prose is genuinely needed it lives in the
    /// registry, which is the thing being checked.
    /// </para>
    /// <para><b>Why this lives in the node test project</b></para>
    /// <para>
    /// The bound enums span assemblies - the frame flags are in the protocol library, TadOp in the
    /// node library, FaServerStatus in the servers library. This project references all three, so
    /// it is the only place every binding can actually be resolved. Running it where one is
    /// invisible would report "no such enum" for a type that exists, which is worse than not
    /// checking: it teaches the reader to ignore the failure.
    /// </para>
    /// <para><b>The escape hatch, deliberately narrow</b></para>
    /// <para>
    /// A block may list <c>only_in_code</c> members - an empty <c>None</c>, or a combination listed
    /// under <c>combos</c> instead of as a bit - and must say WHY in <c>only_in_code_why</c>. An
    /// unexplained exemption is refused, so the hatch cannot be used to silence the test.
    /// </para>
    /// </remarks>
    public sealed class RegistryEnumCompletenessTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the fixture.
        /// </summary>
        /// <param name="output">
        /// xunit's output sink.
        /// </param>
        public RegistryEnumCompletenessTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The registries whose blocks name a C# enum.
        /// </summary>
        /// <returns>
        /// One row per registry file.
        /// </returns>
        public static IEnumerable<object[]> Registries()
        {
            // FROM THE CATALOG, never a list written here. A registry missing from a hand-kept list
            // is never checked and nothing says so - the list is what decides what gets looked at.
            foreach (string file in ProtocolRegistry.CataloguedRegistries())
            {
                yield return new object[] { file };
            }
        }

        /// <summary>
        /// Every enum a registry names is described completely, and describes nothing that is gone.
        /// </summary>
        /// <param name="fileName">
        /// The registry file under test.
        /// </param>
        [Theory]
        [MemberData(nameof(Registries))]
        public void EveryBoundEnumIsDescribedInFull(string fileName)
        {
            using JsonDocument doc = ProtocolRegistry.Load(fileName);

            List<string> failures = new List<string>();
            int blocks = 0;

            foreach ((string path, JsonElement block) in FindBoundBlocks(doc.RootElement))
            {
                blocks++;
                JsonElement binding = block.GetProperty("csharp_enum");
                string typeName = binding.GetProperty("type").GetString()!;

                Type? type = FindEnum(typeName);
                if (type == null)
                {
                    failures.Add(path + ": no such enum as " + typeName
                        + " - it was renamed or removed, and the registry still describes it");
                    continue;
                }

                bool declaredFlags = binding.GetProperty("is_flags").GetBoolean();
                bool actuallyFlags = type.GetCustomAttribute<FlagsAttribute>() != null;
                if (declaredFlags != actuallyFlags)
                {
                    failures.Add(path + ": the registry says is_flags=" + declaredFlags
                        + " but " + type.Name + (actuallyFlags ? " HAS" : " does NOT have")
                        + " [Flags]. A bitfield that stops being one changes how every value combines.");
                }

                HashSet<string> inRegistry = NamesIn(block);
                HashSet<string> exempt = Exempt(binding, path, failures);
                HashSet<string> inCode = new HashSet<string>(Enum.GetNames(type));

                foreach (string name in inCode)
                {
                    if (!inRegistry.Contains(name) && !exempt.Contains(name))
                    {
                        failures.Add(path + ": " + type.Name + "." + name
                            + " exists in the code and is NOT described in the registry."
                            + " Add it with a status and evidence, or list it under only_in_code with a reason.");
                    }
                }

                foreach (string name in inRegistry)
                {
                    if (!inCode.Contains(name))
                    {
                        failures.Add(path + ": the registry describes " + name
                            + ", which " + type.Name + " no longer has.");
                    }
                }
            }

            _output.WriteLine($"{fileName}: {blocks} enum-bound block(s) checked");
            Assert.True(blocks > 0, fileName + " names no enums at all - the binding was lost.");

            if (failures.Count > 0)
            {
                Assert.Fail(
                    "The registry and the code have grown apart:" + Environment.NewLine
                    + string.Join(Environment.NewLine, failures.Select(f => "  " + f)));
            }
        }

        /// <summary>
        /// Finds every object carrying a <c>csharp_enum</c> binding.
        /// </summary>
        /// <param name="element">
        /// The node to walk.
        /// </param>
        /// <returns>
        /// The path and the block, for each binding found.
        /// </returns>
        private static IEnumerable<(string, JsonElement)> FindBoundBlocks(JsonElement element, string path = "")
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("csharp_enum", out _))
                {
                    yield return (path, element);
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    foreach (var found in FindBoundBlocks(property.Value, path + "/" + property.Name))
                    {
                        yield return found;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    foreach (var found in FindBoundBlocks(item, path + "[" + i + "]"))
                    {
                        yield return found;
                    }

                    i++;
                }
            }
        }

        /// <summary>
        /// Collects the names a block describes, from whichever shape it uses.
        /// </summary>
        /// <param name="block">
        /// The registry block.
        /// </param>
        /// <returns>
        /// Every name mentioned, including both halves of a shared bit.
        /// </returns>
        private static HashSet<string> NamesIn(JsonElement block)
        {
            HashSet<string> names = new HashSet<string>();

            foreach (string container in new[] { "values", "bits", "combos" })
            {
                if (!block.TryGetProperty(container, out JsonElement list))
                {
                    continue;
                }

                foreach (JsonElement entry in list.EnumerateArray())
                {
                    if (entry.TryGetProperty("name", out JsonElement single))
                    {
                        names.Add(single.GetString()!);
                    }

                    // A shared bit carries several names for one value - XFHIP and XFRRO.
                    if (entry.TryGetProperty("names", out JsonElement several))
                    {
                        foreach (JsonElement n in several.EnumerateArray())
                        {
                            names.Add(n.GetString()!);
                        }
                    }
                }
            }

            return names;
        }

        /// <summary>
        /// Reads the deliberately-exempt members, refusing any that give no reason.
        /// </summary>
        /// <param name="binding">
        /// The <c>csharp_enum</c> block.
        /// </param>
        /// <param name="path">
        /// Where it sits, for the failure message.
        /// </param>
        /// <param name="failures">
        /// Collects an unexplained exemption as a failure.
        /// </param>
        /// <returns>
        /// The exempt member names.
        /// </returns>
        private static HashSet<string> Exempt(JsonElement binding, string path, List<string> failures)
        {
            HashSet<string> exempt = new HashSet<string>();
            if (!binding.TryGetProperty("only_in_code", out JsonElement list))
            {
                return exempt;
            }

            bool explained = binding.TryGetProperty("only_in_code_why", out JsonElement why)
                && !string.IsNullOrWhiteSpace(why.GetString());

            foreach (JsonElement entry in list.EnumerateArray())
            {
                exempt.Add(entry.GetString()!);
            }

            if (exempt.Count > 0 && !explained)
            {
                failures.Add(path + ": only_in_code lists " + exempt.Count
                    + " member(s) with no only_in_code_why. An exemption with no reason is how this"
                    + " test gets silenced instead of satisfied.");
            }

            return exempt;
        }

        /// <summary>
        /// Finds an enum by full name across the loaded assemblies.
        /// </summary>
        /// <param name="fullName">
        /// The namespace-qualified type name.
        /// </param>
        /// <returns>
        /// The type, or null when nothing matches.
        /// </returns>
        private static Type? FindEnum(string fullName)
        {
            // LOAD EVERY LIBRARY BESIDE US FIRST, rather than walking referenced assemblies.
            //
            // Two things defeat the obvious approach. .NET loads an assembly lazily, so a type
            // nothing has touched is not there yet; and the C# compiler DROPS a project reference
            // that no code actually uses, so it is not even listed in our metadata. Adding the
            // reference to the .csproj is therefore not enough on its own - which cost a confusing
            // round of "no such enum" for an enum sitting right there.
            //
            // The assemblies are named RetroIO.Protocols.Xmsg.*, not Xmsg.* like the projects -
            // assuming otherwise silently matched nothing and reported two live enums as
            // missing. Probing the output folder also makes this self-driving: a new library needs
            // no edit here to be covered.
            foreach (string dll in Directory.GetFiles(AppContext.BaseDirectory, "RetroIO.Protocols.Xmsg*.dll"))
            {
                try
                {
                    Assembly.LoadFrom(dll);
                }
                catch (Exception)
                {
                    // Not a managed assembly, or already loaded. The search below tells the truth
                    // either way.
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? found = assembly.GetType(fullName, throwOnError: false);
                if (found != null && found.IsEnum)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
