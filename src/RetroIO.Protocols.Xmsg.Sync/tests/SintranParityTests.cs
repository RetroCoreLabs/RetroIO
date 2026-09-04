using System;
using System.Text;

using RetroIO.Protocols.Xmsg.Sync;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Sync.Tests
{
    /// <summary>
    /// The parity bit, checked against bytes taken off a live SINTRAN.
    /// </summary>
    /// <remarks>
    /// The expected values here are a RECORDING, not our own output. See
    /// <c>DOC/SINTRAN-FILE-PARITY-BIT-MEASURED-2026-08-09.md</c>.
    /// </remarks>
    public sealed class SintranParityTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public SintranParityTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The exact bytes a live SINTRAN sent for a filename inside a patch script.
        /// </summary>
        /// <remarks>
        /// From <c>capture-read.txt</c>. This string occurs three times in that file, at offsets
        /// 3, 197 and 422, with an identical bit-7 pattern each time - which is what proved the
        /// bit is decided by the characters rather than by position.
        /// <code>
        /// @(ND-PATCH-SIN-:SYST)NEW-SYST
        /// ^......^^...^......^^.^^....^
        /// </code>
        /// </remarks>
        private const string CapturedFilename =
            "C0284E442D5041D4C3482D53C94E2D3A535953D4A94EC5D72D535953D4";

        private static byte[] Captured()
        {
            byte[] result = new byte[CapturedFilename.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(CapturedFilename.Substring(i * 2, 2), 16);
            }

            return result;
        }

        /// <summary>
        /// Stripping the captured bytes recovers the filename SINTRAN meant.
        /// </summary>
        [Fact]
        public void StrippingTheCapturedBytesRecoversTheText()
        {
            byte[] data = Captured();
            int stripped = SintranParity.Strip(data);

            string text = Encoding.ASCII.GetString(data);
            _output.WriteLine("recovered: " + text);

            Assert.Equal("@(ND-PATCH-SIN-:SYST)NEW-SYST", text);
            Assert.Equal(9, stripped);
        }

        /// <summary>
        /// The captured bytes already carry even parity, so applying it changes nothing.
        /// </summary>
        /// <remarks>
        /// This is the test that pins the RULE. If bit 7 were odd parity, or a per-character
        /// constant, or noise, this would fail - our own implementation cannot make it pass by
        /// agreeing with itself, because the expected bytes came off the wire.
        /// </remarks>
        [Fact]
        public void TheCapturedBytesAlreadyCarryEvenParity()
        {
            byte[] captured = Captured();

            Assert.Equal(0, SintranParity.CountParityMismatches(captured));

            byte[] rebuilt = Captured();
            SintranParity.ApplyEven(rebuilt);
            Assert.Equal(captured, rebuilt);
        }

        /// <summary>
        /// Applying even parity to plain text reproduces the captured bytes exactly.
        /// </summary>
        /// <remarks>
        /// The round trip in the useful direction: take the text a person would type, mark it,
        /// and get back what the machine sent.
        /// </remarks>
        [Fact]
        public void MarkingPlainTextReproducesWhatTheMachineSent()
        {
            byte[] plain = Encoding.ASCII.GetBytes("@(ND-PATCH-SIN-:SYST)NEW-SYST");
            SintranParity.ApplyEven(plain);

            Assert.Equal(Captured(), plain);
        }

        /// <summary>
        /// Stripping is safe on text that never carried parity.
        /// </summary>
        /// <remarks>
        /// This is why the return leg can strip unconditionally: real SINTRAN files hold a mixture
        /// of marked and plain characters, and masking has to leave the plain ones alone.
        /// </remarks>
        [Fact]
        public void StrippingPlainTextChangesNothing()
        {
            byte[] plain = Encoding.ASCII.GetBytes("PLAIN TEXT, NO PARITY");
            byte[] before = (byte[])plain.Clone();

            int stripped = SintranParity.Strip(plain);

            Assert.Equal(0, stripped);
            Assert.Equal(before, plain);
        }

        /// <summary>
        /// The mismatch count is what reveals a file holding BOTH conventions.
        /// </summary>
        /// <remarks>
        /// The measured file mixes parity-marked command lines with plain message text. A caller
        /// that wants to know what it actually received asks this rather than assuming, which is
        /// how the per-extension parity table was ruled out.
        /// </remarks>
        [Fact]
        public void MismatchesAreCountedOnMixedContent()
        {
            // "@" needs bit 7 for even parity; the captured form has it, the plain form does not.
            byte[] mixed = new byte[] { 0xC0, 0x40 };

            Assert.Equal(1, SintranParity.CountParityMismatches(mixed));
        }

        /// <summary>
        /// Applying parity recomputes bit 7 rather than trusting what is there.
        /// </summary>
        [Fact]
        public void ApplyingParityRecomputesAWrongBit()
        {
            // 0x41 'A' has two one-bits, so even parity leaves bit 7 clear. Start with it wrongly
            // set and check it is cleared rather than left alone.
            byte[] data = new byte[] { 0xC1 };
            SintranParity.ApplyEven(data);

            Assert.Equal(0x41, data[0]);
        }

        /// <summary>
        /// An empty buffer is not an error.
        /// </summary>
        [Fact]
        public void EmptyInputIsHandled()
        {
            Assert.Equal(0, SintranParity.Strip(Array.Empty<byte>()));
            Assert.Equal(0, SintranParity.CountParityMismatches(Array.Empty<byte>()));
            SintranParity.ApplyEven(Array.Empty<byte>());
        }
    }
}
