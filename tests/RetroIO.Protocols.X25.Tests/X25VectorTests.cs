using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Runs the shared X.25 conformance vectors in <c>tests/x25-vectors/x25-vectors.json</c> against
    /// the C# codec: facility decode and encode, address block encoding, packet build and parse,
    /// and malformed-packet rejection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vector file is the contract between the C# stack and the C emulator (x25emu); a C reader
    /// for the same file is planned (Migration/X25 one-stack plan.md, Phase F1). Every vector names
    /// the X.25 clause it was read from, so a disagreement between the two implementations is
    /// settled by the text, not by whichever one happened to be written first.
    /// </para>
    /// <para>
    /// <b>How status works.</b> A vector marked <c>pass</c> must pass. A vector marked
    /// <c>knownFail:&lt;rule&gt;</c> documents a defect the code has TODAY; this class asserts that
    /// it still fails, so the moment a fix lands the test goes red and somebody has to open the
    /// vector, read the rule, and flip the status to <c>pass</c> on purpose. That is the same fence
    /// the HDLC merge used (<c>_WHICH_IS_WRONG</c> tests), and it exists so that a behaviour change
    /// can never slip through as a silent improvement.
    /// </para>
    /// <para>
    /// No LINQ and no <c>foreach</c>, per the repository rules; <see cref="JsonElement"/> arrays are
    /// walked by index.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class X25VectorTests
    {
        private const string VectorFileRelative = "x25-vectors/x25-vectors.json";

        private static JsonDocument? s_document;

        /// <summary>
        /// The outcome of running one vector: whether the code agreed with the expectation, and why not.
        /// </summary>
        private readonly struct Outcome
        {
            public Outcome(bool passed, string detail)
            {
                Passed = passed;
                Detail = detail;
            }

            public bool Passed { get; }
            public string Detail { get; }

            public static Outcome Ok() => new Outcome(true, "ok");
            public static Outcome Fail(string detail) => new Outcome(false, detail);
        }

        // ------------------------------------------------------------------ loading

        private static JsonDocument Document
        {
            get
            {
                if (s_document == null)
                {
                    string path = Path.Combine(TestContext.CurrentContext.TestDirectory, VectorFileRelative);
                    Assert.That(File.Exists(path), Is.True,
                        "vector file missing - the csproj must copy tests/x25-vectors/x25-vectors.json to the output: " + path);
                    s_document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                }

                return s_document;
            }
        }

        private static IEnumerable<TestCaseData> Section(string name)
        {
            JsonElement array = Document.RootElement.GetProperty(name);
            int count = array.GetArrayLength();
            for (int i = 0; i < count; i++)
            {
                JsonElement v = array[i];
                string id = v.GetProperty("id").GetString() ?? ("#" + i);
                yield return new TestCaseData(v).SetName(name + "." + id);
            }
        }

        public static IEnumerable<TestCaseData> FacilityDecodeVectors() => Section("facilityDecode");
        public static IEnumerable<TestCaseData> FacilityEncodeVectors() => Section("facilityEncode");
        public static IEnumerable<TestCaseData> AddressVectors() => Section("addresses");
        public static IEnumerable<TestCaseData> PacketVectors() => Section("packets");
        public static IEnumerable<TestCaseData> MalformedVectors() => Section("malformed");

        // ------------------------------------------------------------------ the tests

        [TestCaseSource(nameof(FacilityDecodeVectors))]
        public void FacilityDecode(JsonElement v) => Judge(v, RunFacilityDecode(v));

        [TestCaseSource(nameof(FacilityEncodeVectors))]
        public void FacilityEncode(JsonElement v) => Judge(v, RunFacilityEncode(v));

        [TestCaseSource(nameof(AddressVectors))]
        public void Address(JsonElement v) => Judge(v, RunAddress(v));

        [TestCaseSource(nameof(PacketVectors))]
        public void Packet(JsonElement v) => Judge(v, RunPacket(v));

        [TestCaseSource(nameof(MalformedVectors))]
        public void Malformed(JsonElement v) => Judge(v, RunMalformed(v));

        /// <summary>
        /// Prints how many vectors pass and how many are documented as failing, so the number is in
        /// the test output every run. The count of known failures must only ever go DOWN.
        /// </summary>
        [Test]
        public void Summary_PrintsPassAndKnownFailCounts()
        {
            string[] sections = { "facilityDecode", "facilityEncode", "addresses", "packets", "malformed" };
            int pass = 0;
            int knownFail = 0;
            StringBuilder report = new StringBuilder();

            for (int s = 0; s < sections.Length; s++)
            {
                JsonElement array = Document.RootElement.GetProperty(sections[s]);
                int count = array.GetArrayLength();
                for (int i = 0; i < count; i++)
                {
                    string status = array[i].GetProperty("status").GetString() ?? "";
                    if (status == "pass")
                    {
                        pass++;
                    }
                    else if (status.StartsWith("knownFail:", StringComparison.Ordinal))
                    {
                        knownFail++;
                        report.Append("  known fail ").Append(status.Substring(10)).Append(' ')
                              .Append(sections[s]).Append('.')
                              .Append(array[i].GetProperty("id").GetString()).AppendLine();
                    }
                    else
                    {
                        Assert.Fail("vector " + sections[s] + "[" + i + "] has an unknown status '" + status + "'");
                    }
                }
            }

            TestContext.Out.WriteLine("x25-vectors: " + pass + " pass, " + knownFail + " known failures");
            TestContext.Out.Write(report.ToString());
            Assert.That(pass + knownFail, Is.GreaterThan(0));
        }

        /// <summary>
        /// Applies the vector's status to its outcome.
        /// </summary>
        private static void Judge(JsonElement v, Outcome outcome)
        {
            string status = v.GetProperty("status").GetString() ?? "";
            string spec = v.TryGetProperty("spec", out JsonElement specEl) ? specEl.GetString() ?? "" : "";

            if (status == "pass")
            {
                Assert.That(outcome.Passed, Is.True, "spec: " + spec + Environment.NewLine + outcome.Detail);
                return;
            }

            if (status.StartsWith("knownFail:", StringComparison.Ordinal))
            {
                // The code is WRONG today and the vector says so. If it now passes, the defect has
                // been fixed - and that must be recorded by flipping the status to "pass", not by
                // letting a green test go unnoticed.
                Assert.That(outcome.Passed, Is.False,
                    "FIXED: this vector was marked " + status + " and now passes. Open the vector, "
                    + "check the fix against the clause (" + spec + ") and set its status to \"pass\".");
                Assert.Pass("known failure (" + status.Substring(10) + "): " + outcome.Detail);
                return;
            }

            Assert.Fail("unknown status '" + status + "'");
        }

        // ------------------------------------------------------------------ facility decode

        private static Outcome RunFacilityDecode(JsonElement v)
        {
            byte[] bytes = Hex(v.GetProperty("hex").GetString());
            JsonElement expect = v.GetProperty("expect");

            X25FacilityError error = X25Facilities.TryDecode(bytes, out X25Facilities result);

            if (ExpectsInvalid(expect))
            {
                return error != X25FacilityError.None
                    ? Outcome.Ok()
                    : Outcome.Fail("expected the field to be rejected as invalid; the decoder returned window "
                        + result.WindowFromCalled + "/" + result.WindowFromCalling + " packet "
                        + result.PacketSizeFromCalled + "/" + result.PacketSizeFromCalling);
            }

            if (error != X25FacilityError.None)
            {
                return Outcome.Fail("decoder rejected a valid field: " + error);
            }

            StringBuilder why = new StringBuilder();
            CheckInt(why, "packetCalled", result.PacketSizeFromCalled, expect);
            CheckInt(why, "packetCalling", result.PacketSizeFromCalling, expect);
            CheckInt(why, "windowCalled", result.WindowFromCalled, expect);
            CheckInt(why, "windowCalling", result.WindowFromCalling, expect);
            CheckHex(why, "passThroughHex", result.PassThrough, expect);

            return why.Length == 0 ? Outcome.Ok() : Outcome.Fail(why.ToString());
        }

        // ------------------------------------------------------------------ facility encode

        private static Outcome RunFacilityEncode(JsonElement v)
        {
            JsonElement p = v.GetProperty("params");
            X25Facilities f = new X25Facilities
            {
                HasWindowSize = true,
                HasPacketSize = true,
                WindowFromCalled = (byte)p.GetProperty("windowCalled").GetInt32(),
                WindowFromCalling = (byte)p.GetProperty("windowCalling").GetInt32(),
                PacketSizeFromCalled = p.GetProperty("packetCalled").GetInt32(),
                PacketSizeFromCalling = p.GetProperty("packetCalling").GetInt32(),
            };
            bool wantInvalid = v.TryGetProperty("expect", out JsonElement expect) && ExpectsInvalid(expect);

            byte[] encoded;
            try
            {
                encoded = f.Encode();
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return wantInvalid ? Outcome.Ok() : Outcome.Fail("encoder refused: " + ex.Message);
            }

            if (wantInvalid)
            {
                return Outcome.Fail("expected refusal; encoder produced " + ToHex(encoded));
            }

            return CompareHex("encoded", encoded, v.GetProperty("hex").GetString());
        }

        // ------------------------------------------------------------------ addresses

        /// <summary>
        /// The builder has no public address-block encoder, so the block is lifted out of a Call
        /// Request: octets from index 3 up to, but not including, the trailing facility-length octet.
        /// </summary>
        private static Outcome RunAddress(JsonElement v)
        {
            string called = v.GetProperty("called").GetString() ?? "";
            string calling = v.GetProperty("calling").GetString() ?? "";
            bool wantInvalid = v.TryGetProperty("expect", out JsonElement expect) && ExpectsInvalid(expect);

            byte[] packet;
            try
            {
                packet = X25PacketBuilder.CreateCallRequest(1, called, calling);
            }
            catch (Exception ex)
            {
                return wantInvalid ? Outcome.Ok() : Outcome.Fail("builder threw " + ex.GetType().Name + ": " + ex.Message);
            }

            if (wantInvalid)
            {
                return Outcome.Fail("expected the address to be refused; builder produced " + ToHex(packet));
            }

            // GFI, LCN, PTI, then the address block, then one facility-length octet (zero here).
            int blockLength = packet.Length - 3 - 1;
            if (blockLength < 1)
            {
                return Outcome.Fail("packet too short to hold an address block: " + ToHex(packet));
            }

            byte[] block = new byte[blockLength];
            Array.Copy(packet, 3, block, 0, blockLength);
            return CompareHex("address block", block, v.GetProperty("hex").GetString());
        }

        // ------------------------------------------------------------------ packets

        private static Outcome RunPacket(JsonElement v)
        {
            string wantHex = v.GetProperty("hex").GetString() ?? "";
            StringBuilder why = new StringBuilder();

            // 1. Build, and compare with the spec bytes.
            if (v.TryGetProperty("build", out JsonElement build))
            {
                byte[] built;
                try
                {
                    built = Build(build);
                }
                catch (Exception ex)
                {
                    return Outcome.Fail("builder threw " + ex.GetType().Name + ": " + ex.Message);
                }

                Outcome b = CompareHex("built", built, wantHex);
                if (!b.Passed)
                {
                    why.Append(b.Detail).Append("; ");
                }
            }

            // 2. Parse the spec bytes, and compare the fields the vector names.
            if (v.TryGetProperty("parse", out JsonElement parse))
            {
                X25PacketInfo info;
                try
                {
                    info = X25PacketParser.ParsePacket(Hex(wantHex));
                }
                catch (Exception ex)
                {
                    return Outcome.Fail(why + "parser threw " + ex.GetType().Name + ": " + ex.Message);
                }

                CheckParsed(why, info, parse);
            }

            return why.Length == 0 ? Outcome.Ok() : Outcome.Fail(why.ToString());
        }

        private static byte[] Build(JsonElement b)
        {
            string type = b.GetProperty("type").GetString() ?? "";
            ushort lcn = (ushort)OptInt(b, "lcn", 0);
            byte cause = (byte)OptInt(b, "cause", 0);
            byte diag = (byte)OptInt(b, "diag", 0);
            byte ps = (byte)OptInt(b, "ps", 0);
            byte pr = (byte)OptInt(b, "pr", 0);
            bool q = OptBool(b, "q");
            bool d = OptBool(b, "d");
            bool m = OptBool(b, "m");
            byte[]? facilities = OptHex(b, "facilitiesHex");
            byte[]? cud = OptHex(b, "cudHex");
            byte[] data = OptHex(b, "dataHex") ?? Array.Empty<byte>();
            string called = OptString(b, "called");
            string calling = OptString(b, "calling");

            switch (type)
            {
                case "CallRequest": return X25PacketBuilder.CreateCallRequest(lcn, called, calling, facilities, cud, q, d);
                case "CallAccepted": return X25PacketBuilder.CreateCallAccepted(lcn, facilities, cud, q, d);
                case "ClearRequest": return X25PacketBuilder.CreateClearRequest(lcn, cause, diag, facilities, cud);
                case "ClearConfirmation": return X25PacketBuilder.CreateClearConfirmation(lcn);
                case "Data": return X25PacketBuilder.CreateDataPacket(lcn, ps, pr, data, m, q, d);
                case "RR": return X25PacketBuilder.CreateRR(lcn, pr);
                case "RNR": return X25PacketBuilder.CreateRNR(lcn, pr);
                case "REJ": return X25PacketBuilder.CreateREJ(lcn, pr);
                case "Interrupt": return X25PacketBuilder.CreateInterrupt(lcn, data);
                case "InterruptConfirmation": return X25PacketBuilder.CreateInterruptConfirmation(lcn);
                case "ResetRequest": return X25PacketBuilder.CreateResetRequest(lcn, cause, diag);
                case "ResetConfirmation": return X25PacketBuilder.CreateResetConfirmation(lcn);
                case "RestartRequest": return X25PacketBuilder.CreateRestartRequest(cause, diag);
                case "RestartConfirmation": return X25PacketBuilder.CreateRestartConfirmation();
                case "Diagnostic": return X25PacketBuilder.CreateDiagnostic(diag, data);
                default: throw new NotSupportedException("no builder mapping for packet type '" + type + "'");
            }
        }

        private static void CheckParsed(StringBuilder why, X25PacketInfo info, JsonElement parse)
        {
            string wantType = parse.GetProperty("type").GetString() ?? "";
            if (info.PacketType.ToString() != wantType)
            {
                why.Append("type ").Append(info.PacketType).Append(" expected ").Append(wantType).Append("; ");
            }

            CheckInt(why, "lcn", info.LCN, parse);
            CheckInt(why, "ps", info.PS, parse);
            CheckInt(why, "pr", info.PR, parse);
            CheckInt(why, "diag", info.DiagnosticCode, parse);
            CheckBool(why, "q", info.QBit, parse);
            CheckBool(why, "d", info.DBit, parse);
            CheckBool(why, "m", info.MBit, parse);
            CheckString(why, "called", info.CalledAddress, parse);
            CheckString(why, "calling", info.CallingAddress, parse);
            CheckHex(why, "facilitiesHex", info.Facilities, parse);
            CheckHex(why, "cudHex", info.CallUserData, parse);

            // "cause" lives in a different property per packet type.
            if (parse.TryGetProperty("cause", out JsonElement causeEl))
            {
                int want = causeEl.GetInt32();
                int got;
                switch (wantType)
                {
                    case "ResetRequest": got = info.ResetCause; break;
                    case "RestartRequest": got = info.RestartCause; break;
                    default: got = info.ClearingCause; break;
                }

                if (got != want)
                {
                    why.Append("cause ").Append(got).Append(" expected ").Append(want).Append("; ");
                }
            }

            // "dataHex" likewise.
            if (parse.TryGetProperty("dataHex", out JsonElement dataEl))
            {
                byte[] got;
                switch (wantType)
                {
                    case "Interrupt": got = info.InterruptData; break;
                    case "Diagnostic": got = info.DiagnosticInfo; break;
                    default: got = info.UserData; break;
                }

                Outcome o = CompareHex("dataHex", got, dataEl.GetString());
                if (!o.Passed)
                {
                    why.Append(o.Detail).Append("; ");
                }
            }
        }

        // ------------------------------------------------------------------ malformed

        /// <summary>
        /// "Rejected" means the parser refused the packet on purpose (an <see cref="ArgumentException"/>
        /// or an Unknown type). An <see cref="IndexOutOfRangeException"/> is a crash, not a rejection,
        /// and counts as a failure.
        /// </summary>
        private static Outcome RunMalformed(JsonElement v)
        {
            byte[] bytes = Hex(v.GetProperty("hex").GetString());
            try
            {
                X25PacketInfo info = X25PacketParser.ParsePacket(bytes);
                return info.PacketType == X25PacketType.Unknown || info.ParseError != null
                    ? Outcome.Ok()
                    : Outcome.Fail("parser accepted the packet as " + info.PacketType);
            }
            catch (ArgumentException)
            {
                return Outcome.Ok();
            }
            catch (Exception ex)
            {
                return Outcome.Fail("parser crashed with " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static bool ExpectsInvalid(JsonElement expect) =>
            expect.TryGetProperty("invalid", out JsonElement inv) && inv.GetBoolean();

        private static void CheckInt(StringBuilder why, string name, int got, JsonElement expect)
        {
            if (expect.TryGetProperty(name, out JsonElement el) && el.GetInt32() != got)
            {
                why.Append(name).Append(' ').Append(got).Append(" expected ").Append(el.GetInt32()).Append("; ");
            }
        }

        private static void CheckBool(StringBuilder why, string name, bool got, JsonElement expect)
        {
            if (expect.TryGetProperty(name, out JsonElement el) && el.GetBoolean() != got)
            {
                why.Append(name).Append(' ').Append(got).Append(" expected ").Append(el.GetBoolean()).Append("; ");
            }
        }

        private static void CheckString(StringBuilder why, string name, string got, JsonElement expect)
        {
            if (expect.TryGetProperty(name, out JsonElement el) && (el.GetString() ?? "") != got)
            {
                why.Append(name).Append(" '").Append(got).Append("' expected '").Append(el.GetString()).Append("'; ");
            }
        }

        private static void CheckHex(StringBuilder why, string name, byte[] got, JsonElement expect)
        {
            if (expect.TryGetProperty(name, out JsonElement el))
            {
                Outcome o = CompareHex(name, got, el.GetString());
                if (!o.Passed)
                {
                    why.Append(o.Detail).Append("; ");
                }
            }
        }

        private static Outcome CompareHex(string what, byte[] got, string? wantHex)
        {
            byte[] want = Hex(wantHex);
            if (got.Length == want.Length)
            {
                bool same = true;
                for (int i = 0; i < got.Length; i++)
                {
                    if (got[i] != want[i])
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    return Outcome.Ok();
                }
            }

            return Outcome.Fail(what + " = " + ToHex(got) + " expected " + ToHex(want));
        }

        private static int OptInt(JsonElement e, string name, int fallback) =>
            e.TryGetProperty(name, out JsonElement el) ? el.GetInt32() : fallback;

        private static bool OptBool(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement el) && el.GetBoolean();

        private static string OptString(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement el) ? el.GetString() ?? "" : "";

        private static byte[]? OptHex(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement el) ? Hex(el.GetString()) : null;

        /// <summary>
        /// Parses "42 07 07" (spaces optional) into bytes. An empty or null string is an empty array.
        /// </summary>
        private static byte[] Hex(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<byte>();
            }

            string clean = text.Replace(" ", "");
            byte[] result = new byte[clean.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            }

            return result;
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes.Length == 0)
            {
                return "(empty)";
            }

            StringBuilder sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(bytes[i].ToString("X2"));
            }

            return sb.ToString();
        }
    }
}
