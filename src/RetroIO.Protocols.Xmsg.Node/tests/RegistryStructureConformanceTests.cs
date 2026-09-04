using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

using RetroIO.Protocols.Xmsg.TestSupport;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// The C# structures have the shape the registry declares - field by field, bit by bit.
    /// </summary>
    /// <remarks>
    /// <para><b>What this adds to the enum check</b></para>
    /// <para>
    /// The completeness test covers enums: the named values. This covers LAYOUT - that the type
    /// really has the properties the registry names, and that their widths add up to the declared
    /// width of each field. A word documented as 16 bits and implemented as a byte is a truncation
    /// on the wire, and no enum check can see it.
    /// </para>
    /// <para><b>One field may be several properties</b></para>
    /// <para>
    /// The header's first word is <c>Marker1</c> and <c>Marker2</c>, a byte each; its second is
    /// <c>PacketType</c> and <c>Subtype</c>. So the widths of the listed properties are SUMMED and
    /// compared against the field. That is the honest comparison: the registry describes the wire,
    /// the class describes how we hold it, and the two agree on how many bits are involved.
    /// </para>
    /// <para><b>Why the same metadata serves a generator</b></para>
    /// <para>
    /// The widths, the byte order and the names checked here are exactly what a generator for
    /// another language would consume. Validating the C# against them keeps them HONEST while there
    /// is only one implementation - so that when a second one is generated, it is generated from
    /// something already known to describe working code rather than from an untested wish.
    /// </para>
    /// </remarks>
    public sealed class RegistryStructureConformanceTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the fixture.
        /// </summary>
        /// <param name="output">
        /// xunit's output sink.
        /// </param>
        public RegistryStructureConformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The registries that describe a structure.
        /// </summary>
        /// <returns>
        /// One row per registry file.
        /// </returns>
        public static IEnumerable<object[]> Registries()
        {
            foreach (string file in ProtocolRegistry.CataloguedRegistries())
            {
                yield return new object[] { file };
            }
        }

        /// <summary>
        /// Every structure the registry binds to a type matches that type's shape.
        /// </summary>
        /// <param name="fileName">
        /// The registry under test.
        /// </param>
        [Theory]
        [MemberData(nameof(Registries))]
        public void EveryBoundStructureMatchesItsType(string fileName)
        {
            using JsonDocument doc = ProtocolRegistry.Load(fileName);
            if (!doc.RootElement.TryGetProperty("structures", out JsonElement structures))
            {
                _output.WriteLine(fileName + " describes no structures");
                return;
            }

            List<string> failures = new List<string>();
            int fieldsChecked = 0;

            foreach (JsonProperty structure in structures.EnumerateObject())
            {
                if (!structure.Value.TryGetProperty("csharp", out JsonElement typeName))
                {
                    failures.Add(structure.Name + " names no C# type, so nothing holds it to the code");
                    continue;
                }

                Type? type = FindType(typeName.GetString()!);
                if (type == null)
                {
                    failures.Add(structure.Name + ": no such type as " + typeName.GetString());
                    continue;
                }

                foreach (JsonElement field in structure.Value.GetProperty("fields").EnumerateArray())
                {
                    string fieldName = field.GetProperty("name").GetString()!;
                    if (!field.TryGetProperty("csharp", out JsonElement members))
                    {
                        continue;
                    }

                    if (!field.TryGetProperty("width_bits", out JsonElement declaredWidth))
                    {
                        failures.Add(structure.Name + "." + fieldName
                            + " has no width_bits. A generator cannot emit a field whose width is"
                            + " only implied, so it is required here.");
                        continue;
                    }

                    int total = 0;
                    bool ok = true;
                    foreach (JsonElement member in members.EnumerateArray())
                    {
                        string property = member.GetString()!;
                        PropertyInfo? info = type.GetProperty(property);
                        if (info == null)
                        {
                            failures.Add(structure.Name + "." + fieldName + ": " + type.Name
                                + " has no property " + property);
                            ok = false;
                            continue;
                        }

                        int bits = WidthOf(info.PropertyType);
                        if (bits == 0)
                        {
                            failures.Add(structure.Name + "." + fieldName + ": "
                                + property + " is a " + info.PropertyType.Name
                                + ", whose width this test cannot judge");
                            ok = false;
                            continue;
                        }

                        total += bits;
                    }

                    if (ok && total != declaredWidth.GetInt32())
                    {
                        failures.Add(structure.Name + "." + fieldName + ": the registry says "
                            + declaredWidth.GetInt32() + " bits, the code holds " + total
                            + ". A field narrower in code than on the wire truncates silently.");
                    }

                    fieldsChecked++;
                }
            }

            _output.WriteLine($"{fileName}: {fieldsChecked} field(s) checked against the code");

            if (failures.Count > 0)
            {
                Assert.Fail(
                    "The registry and the structures disagree:" + Environment.NewLine
                    + "  " + string.Join(Environment.NewLine + "  ", failures));
            }
        }

        /// <summary>
        /// Every field carries what a code generator needs.
        /// </summary>
        /// <param name="fileName">
        /// The registry under test.
        /// </param>
        /// <remarks>
        /// A C library is planned and TypeScript is under consideration, so the registries must keep
        /// the detail those need rather than being trimmed to whatever C# happens to use. Checking
        /// it now means the door cannot quietly close between deciding and building.
        /// </remarks>
        [Theory]
        [MemberData(nameof(Registries))]
        public void EveryFieldCarriesWhatAGeneratorNeeds(string fileName)
        {
            using JsonDocument doc = ProtocolRegistry.Load(fileName);
            List<string> missing = new List<string>();

            foreach (string section in new[] { "structures", "message_prefix" })
            {
                if (!doc.RootElement.TryGetProperty(section, out JsonElement node))
                {
                    continue;
                }

                foreach (JsonElement structure in Structures(node))
                {
                    if (!structure.TryGetProperty("fields", out JsonElement fields))
                    {
                        continue;
                    }

                    foreach (JsonElement field in fields.EnumerateArray())
                    {
                        string name = field.GetProperty("name").GetString()!;
                        if (!field.TryGetProperty("width_bits", out _))
                        {
                            missing.Add(name + ": width_bits");
                        }

                        if (!field.TryGetProperty("c_type", out _))
                        {
                            missing.Add(name + ": c_type");
                        }
                    }
                }
            }

            if (missing.Count > 0)
            {
                Assert.Fail(
                    fileName + " is missing what a generator needs:" + Environment.NewLine
                    + "  " + string.Join(Environment.NewLine + "  ", missing) + Environment.NewLine
                    + "See catalog.json -> consumers.planned for why these are required.");
            }
        }

        private static IEnumerable<JsonElement> Structures(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("fields", out _))
            {
                yield return node;
                yield break;
            }

            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("fields", out _))
                    {
                        yield return property.Value;
                    }
                }
            }
        }

        /// <summary>
        /// How many bits a CLR type holds on the wire.
        /// </summary>
        /// <param name="type">
        /// The property type.
        /// </param>
        /// <returns>
        /// The width, or zero when it is not a fixed-width integer.
        /// </returns>
        private static int WidthOf(Type type)
        {
            Type actual = type.IsEnum ? Enum.GetUnderlyingType(type) : type;

            if (actual == typeof(byte) || actual == typeof(sbyte)) { return 8; }
            if (actual == typeof(ushort) || actual == typeof(short)) { return 16; }
            if (actual == typeof(uint) || actual == typeof(int)) { return 32; }
            if (actual == typeof(ulong) || actual == typeof(long)) { return 64; }

            return 0;
        }

        private static Type? FindType(string fullName)
        {
            foreach (string dll in Directory.GetFiles(AppContext.BaseDirectory, "RetroIO.Protocols.Xmsg*.dll"))
            {
                try
                {
                    Assembly.LoadFrom(dll);
                }
                catch (Exception)
                {
                    // Covered by the search below reporting the type as missing.
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? found = assembly.GetType(fullName, throwOnError: false);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// Every enum block carries what a C generator needs to name it.
        /// </summary>
        /// <param name="fileName">
        /// The registry under test.
        /// </param>
        /// <remarks>
        /// <para>
        /// <c>generate_c.py</c> checks this too, but nothing runs it automatically - and a check
        /// that depends on being remembered is the one that stops happening. Adding an enum block
        /// without a <c>bindings.c</c> would break C generation the day somebody tried it, months
        /// after the change that caused it.
        /// </para>
        /// <para>
        /// C needs all three: the type name, the constant prefix, and the underlying type. C# needs
        /// none of them, which is exactly why they rot unnoticed unless something asks.
        /// </para>
        /// </remarks>
        [Theory]
        [MemberData(nameof(Registries))]
        public void EveryEnumBlockCanBeGeneratedForC(string fileName)
        {
            using JsonDocument doc = ProtocolRegistry.Load(fileName);
            List<string> missing = new List<string>();

            foreach ((string path, JsonElement block) in EnumBlocks(doc.RootElement))
            {
                if (!block.TryGetProperty("bindings", out JsonElement bindings)
                    || !bindings.TryGetProperty("c", out JsonElement c))
                {
                    missing.Add(path + ": no bindings.c - a C generator cannot name this");
                    continue;
                }

                foreach (string required in new[] { "enum", "prefix", "underlying_type" })
                {
                    if (!c.TryGetProperty(required, out JsonElement value)
                        || string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        missing.Add(path + ": bindings.c is missing " + required);
                    }
                }
            }

            if (missing.Count > 0)
            {
                Assert.Fail(
                    fileName + " cannot be generated for C:" + Environment.NewLine
                    + "  " + string.Join(Environment.NewLine + "  ", missing) + Environment.NewLine
                    + "See catalog.json -> consumers.planned.");
            }
        }

        /// <summary>
        /// No two members collapse to the same C symbol.
        /// </summary>
        /// <param name="fileName">
        /// The registry under test.
        /// </param>
        /// <remarks>
        /// <para>
        /// C has ONE FLAT NAMESPACE. Two members that produce the same <c>PREFIX_NAME</c> would
        /// silently redefine each other, and the second definition wins - a fault with no error
        /// message and no obvious symptom until something reads the wrong constant.
        /// </para>
        /// <para>
        /// Deliberate aliases are fine and expected - XSDMC and XSDSY are one service under two
        /// names - because they are DIFFERENT symbols with the same value. What this catches is two
        /// different names flattening into one symbol.
        /// </para>
        /// </remarks>
        [Theory]
        [MemberData(nameof(Registries))]
        public void NoTwoMembersCollapseToTheSameCSymbol(string fileName)
        {
            using JsonDocument doc = ProtocolRegistry.Load(fileName);
            List<string> clashes = new List<string>();

            foreach ((string path, JsonElement block) in EnumBlocks(doc.RootElement))
            {
                if (!block.TryGetProperty("bindings", out JsonElement bindings)
                    || !bindings.TryGetProperty("c", out JsonElement c)
                    || !c.TryGetProperty("prefix", out JsonElement prefixElement))
                {
                    continue;
                }

                string prefix = prefixElement.GetString()!;
                Dictionary<string, string> seen = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (string member in MemberNames(block))
                {
                    string symbol = CSymbol(prefix, member);
                    if (seen.TryGetValue(symbol, out string? first))
                    {
                        clashes.Add(path + ": " + first + " and " + member
                            + " both become " + symbol);
                        continue;
                    }

                    seen[symbol] = member;
                }
            }

            if (clashes.Count > 0)
            {
                Assert.Fail(
                    fileName + " would produce colliding C symbols:" + Environment.NewLine
                    + "  " + string.Join(Environment.NewLine + "  ", clashes));
            }
        }

        /// <summary>
        /// Builds the C constant name for a member, as the generator does.
        /// </summary>
        /// <param name="prefix">
        /// The constant prefix.
        /// </param>
        /// <param name="member">
        /// The member name.
        /// </param>
        /// <returns>
        /// The C symbol.
        /// </returns>
        /// <remarks>
        /// An ND symbol is ALREADY capitals, so the CamelCase split only applies to names with
        /// lower case in them. Splitting unconditionally turned XSLET into X_S_L_E_T - a bug the C
        /// generator hit and C# could not, because C# keeps the member name verbatim.
        /// </remarks>
        private static string CSymbol(string prefix, string member)
        {
            string text = member;
            bool hasLower = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsLower(text[i])) { hasLower = true; break; }
            }

            System.Text.StringBuilder built = new System.Text.StringBuilder(prefix);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (hasLower && i > 0 && char.IsUpper(ch))
                {
                    built.Append('_');
                }

                built.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_');
            }

            return built.ToString();
        }

        /// <summary>
        /// Every member name a block declares.
        /// </summary>
        /// <param name="block">
        /// The registry block.
        /// </param>
        /// <returns>
        /// The names, including both halves of a shared bit.
        /// </returns>
        private static IEnumerable<string> MemberNames(JsonElement block)
        {
            foreach (string container in new[] { "values", "bits" })
            {
                if (!block.TryGetProperty(container, out JsonElement list))
                {
                    continue;
                }

                foreach (JsonElement entry in list.EnumerateArray())
                {
                    if (entry.TryGetProperty("name", out JsonElement single))
                    {
                        yield return single.GetString()!;
                    }

                    if (entry.TryGetProperty("names", out JsonElement several))
                    {
                        foreach (JsonElement n in several.EnumerateArray())
                        {
                            yield return n.GetString()!;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Finds the blocks that describe a set of named values.
        /// </summary>
        /// <param name="root">
        /// The document root.
        /// </param>
        /// <returns>
        /// The path and block for each.
        /// </returns>
        private static IEnumerable<(string, JsonElement)> EnumBlocks(JsonElement root)
        {
            foreach (string key in new[] { "operations", "services", "errors", "connection_types",
                                           "status_codes", "control_services", "qform" })
            {
                if (root.TryGetProperty(key, out JsonElement block)
                    && (block.TryGetProperty("values", out _) || block.TryGetProperty("classes", out _)))
                {
                    yield return ("/" + key, block);
                }
            }

            if (root.TryGetProperty("bitfields", out JsonElement bitfields))
            {
                foreach (JsonProperty property in bitfields.EnumerateObject())
                {
                    yield return ("/bitfields/" + property.Name, property.Value);
                }
            }
        }
    }
}