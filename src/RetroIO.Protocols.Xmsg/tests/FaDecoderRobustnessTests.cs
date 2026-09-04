using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Qform;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// A damaged message is refused, never thrown on.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists</b></para>
    /// <para>
    /// Every field these decoders read is a byte chosen by the other machine. A length that points
    /// past the end of the body is not a hypothetical: SINTRAN pads a body to an even length, and
    /// on 2026-08-06 that stray pad byte was read as the start of a field, ran off the end and
    /// threw - and every listing request was refused until it was found.
    /// </para>
    /// <para>
    /// The decoders are careful today. <c>QformReader.Read</c> checks each length against the body
    /// and raises <see cref="QformFormatException"/>, and every FA codec goes through
    /// <c>QformReader.TryRead</c>, which turns that into a plain false. This file pins that
    /// arrangement, because it is one careless <c>Read</c> away from being untrue again and the
    /// symptom would be a server that stops on a message from a peer having a bad day.
    /// </para>
    /// <para><b>Why real captured bytes and not random ones</b></para>
    /// <para>
    /// The starting points are frames a real ND actually sent. Damaging those reaches the awkward
    /// halfway states - a valid tag with a length one byte too long, a truncated escape - that
    /// random bytes almost never produce, because random bytes are rejected by the first tag.
    /// </para>
    /// <para><b>The sweep is exhaustive, not sampled</b></para>
    /// <para>
    /// Every prefix, and every single-byte change at every position, over a small deliberately
    /// chosen set of replacement values. No random numbers: the same bytes are tested on every run,
    /// so a failure can be reproduced from the test name alone.
    /// </para>
    /// </remarks>
    public sealed class FaDecoderRobustnessTests
    {
        /// <summary>
        /// A CreateFile request, as a real SINTRAN sent it.
        /// </summary>
        private const string CapturedCreateFileHex =
            "92000A"                                            // operation 0x000A = CreateFile
            + "920002"                                          // sequence 2
            + "F20002"                                          // selector 2 - the name follows
            + "BE" + "524F4E4E592D52323A545854" + "27" + "52"    // "RONNY-R2:TXT"
            + "F20004"                                          // selector 4 - page count follows
            + "A400000001"                                      // 32-bit page count = 1
            + "F200FF"                                          // end of list
            + "FF";                                             // the pad byte that once threw

        /// <summary>
        /// The replacement bytes used to damage a position.
        /// </summary>
        /// <remarks>
        /// Chosen rather than random, and each one is a length trap:
        ///  - <c>0xFF</c> is a tag whose low nibble asks for fifteen bytes that may not be there.
        ///  - <c>0x80</c> is the escape marker, so the real length is the NEXT byte - and at the end
        ///    of a body there is no next byte.
        ///  - <c>0x00</c> is a zero-length escape, the form the 2026-08-06 defect went through.
        /// </remarks>
        private static readonly byte[] Replacements = new byte[] { 0xFF, 0x80, 0x00 };

        /// <summary>
        /// Every prefix of a real message is refused or read, never thrown on.
        /// </summary>
        /// <remarks>
        /// Truncation is the damage a link actually produces. A body cut mid-field leaves a valid
        /// tag announcing bytes that no longer exist, which is the exact shape that threw before.
        /// </remarks>
        [Fact]
        public void EveryTruncationOfARealMessageIsRefusedRatherThanThrown()
        {
            byte[] whole = FromHex(CapturedCreateFileHex);

            for (int length = 0; length <= whole.Length; length++)
            {
                byte[] cut = new byte[length];
                Array.Copy(whole, cut, length);

                AssertAllDecodersSurvive(cut, "truncated to " + length.ToString() + " byte(s)");
            }
        }

        /// <summary>
        /// Changing any single byte to a length trap is refused or read, never thrown on.
        /// </summary>
        /// <remarks>
        /// This is the half a truncation cannot reach: the body stays its full length while a field
        /// inside it claims more than the room left. A decoder that trusts the claim walks off the
        /// end of a buffer that is still perfectly present.
        /// </remarks>
        [Fact]
        public void EverySingleByteCorruptionIsRefusedRatherThanThrown()
        {
            byte[] whole = FromHex(CapturedCreateFileHex);

            for (int at = 0; at < whole.Length; at++)
            {
                for (int r = 0; r < Replacements.Length; r++)
                {
                    byte[] damaged = new byte[whole.Length];
                    Array.Copy(whole, damaged, whole.Length);
                    damaged[at] = Replacements[r];

                    AssertAllDecodersSurvive(
                        damaged,
                        "byte " + at.ToString() + " set to 0x" + Replacements[r].ToString("X2"));
                }
            }
        }

        /// <summary>
        /// A truncated message damaged as well is still only refused.
        /// </summary>
        /// <remarks>
        /// The two kinds of damage together, because they interact: a trap byte planted near the
        /// end of an already-short body is the case with the least room left to run into, and it is
        /// the one a plain truncation sweep steps straight past.
        /// </remarks>
        [Fact]
        public void TruncationCombinedWithCorruptionIsStillOnlyRefused()
        {
            byte[] whole = FromHex(CapturedCreateFileHex);

            for (int length = 1; length <= whole.Length; length++)
            {
                for (int r = 0; r < Replacements.Length; r++)
                {
                    byte[] cut = new byte[length];
                    Array.Copy(whole, cut, length);
                    cut[length - 1] = Replacements[r];

                    AssertAllDecodersSurvive(
                        cut,
                        "cut to " + length.ToString() + " with a trailing 0x"
                            + Replacements[r].ToString("X2"));
                }
            }
        }

        /// <summary>
        /// The reader's own door refuses a length that points past the end.
        /// </summary>
        /// <remarks>
        /// Stated directly as well as swept, so the reason the sweeps pass is on the record: it is
        /// <c>TryRead</c> converting the exception, not the absence of a check.
        /// </remarks>
        [Fact]
        public void ALengthPointingPastTheEndIsRefusedByTryRead()
        {
            // A byte-string tag claiming fourteen bytes, with two present.
            byte[] body = new byte[] { 0xBE, 0x41, 0x42 };

            IReadOnlyList<QformField> fields;
            Assert.False(QformReader.TryRead(body, out fields));
            Assert.Empty(fields);

            // And the throwing door still throws - TryRead is a wrapper, not a second parser.
            Assert.Throws<QformFormatException>(() => QformReader.Read(body));
        }

        /// <summary>
        /// Runs every wire-facing decoder over a body and fails if any of them throws.
        /// </summary>
        /// <param name="body">
        /// The damaged message.
        /// </param>
        /// <param name="what">
        /// How it was damaged, for the failure message.
        /// </param>
        /// <remarks>
        /// The RETURN VALUES are deliberately not asserted. What a decoder makes of a damaged
        /// message is its business - refusing is normal, and reading a plausible value out of bytes
        /// that happen to still parse is also fine. The property under test is only that the caller
        /// gets an answer instead of an exception.
        /// </remarks>
        private static void AssertAllDecodersSurvive(byte[] body, string what)
        {
            try
            {
                IReadOnlyList<QformField> fields;
                QformReader.TryRead(body, out fields);

                FaMessageType messageType;
                ushort conversation;
                byte sequenceByte;
                ushort sessionToken;
                FaExchangeCodec.TryReadEnvelope(
                    body, out messageType, out conversation, out sequenceByte, out sessionToken);

                FaOperation operation;
                ushort sequence;
                FaExchangeCodec.TryReadOperation(body, out operation, out sequence);

                ushort serial;
                ushort cursor;
                FaSpecialFunction listingFunction;
                FaListFilesCodec.TryReadRequest(body, out serial, out cursor, out listingFunction);

                FaSpecialFunction function;
                FaListFilesCodec.TryReadFunction(body, out function);

                uint byteLength;
                FaListFilesCodec.TryReadEndOfFile(body, out byteLength);

                string fileName;
                FaListFilesCodec.TryReadRequestedFileName(body, out fileName);
                FaListFilesCodec.TryReadDeleteFileName(body, out fileName);

                uint pageCount;
                FaListFilesCodec.TryReadCreateFile(body, out fileName, out pageCount);

                byte[] entryRecord;
                FaListFilesCodec.TryReadReply(body, out serial, out entryRecord);

                uint position;
                ushort wantedBytes;
                FaFileDataCodec.TryReadRequest(body, out serial, out position, out wantedBytes);

                ushort blockSize;
                FaFileDataCodec.TryReadBlockSize(body, out blockSize);

                bool forWrite;
                FaOpenFileCodec.TryReadRequest(body, out serial, out fileName, out forWrite);
            }
            catch (Exception error)
            {
                Assert.Fail(
                    "A decoder threw " + error.GetType().Name + " on a message " + what
                    + ". A damaged message must be REFUSED, not thrown on: this runs against a real"
                    + " machine, and an exception here stops the server on a peer's bad frame."
                    + Environment.NewLine + "Body: " + Convert.ToHexString(body)
                    + Environment.NewLine + error.Message);
            }
        }

        /// <summary>
        /// Turns a hex string into bytes.
        /// </summary>
        /// <param name="hex">
        /// The hex text, with no separators.
        /// </param>
        /// <returns>
        /// The bytes it spells.
        /// </returns>
        private static byte[] FromHex(string hex)
        {
            return Convert.FromHexString(hex);
        }

        /// <summary>
        /// The sweep really does reach the states that would throw.
        /// </summary>
        /// <remarks>
        /// <para><b>Without this, the three sweeps above prove nothing.</b></para>
        /// <para>
        /// They pass if no decoder throws - and a sweep whose every damaged body was rejected at the
        /// first byte would pass just as quietly, having tested nothing at all. That is the failure
        /// mode of a test written to confirm rather than to find out.
        /// </para>
        /// <para>
        /// So this counts the damaged bodies that make the THROWING door, <c>QformReader.Read</c>,
        /// actually throw. Each one is a body that walked far enough in to hit a length running past
        /// the end - exactly the state the 2026-08-06 defect died in. That the same bodies come back
        /// as a plain false through <c>TryRead</c> is then a real result rather than a vacuous one.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheSweepActuallyReachesBodiesThatWouldThrow()
        {
            byte[] whole = FromHex(CapturedCreateFileHex);
            int wouldThrow = 0;
            int total = 0;

            for (int length = 0; length <= whole.Length; length++)
            {
                byte[] cut = new byte[length];
                Array.Copy(whole, cut, length);
                total++;
                if (Throws(cut)) { wouldThrow++; }
            }

            for (int at = 0; at < whole.Length; at++)
            {
                for (int r = 0; r < Replacements.Length; r++)
                {
                    byte[] damaged = new byte[whole.Length];
                    Array.Copy(whole, damaged, whole.Length);
                    damaged[at] = Replacements[r];
                    total++;
                    if (Throws(damaged)) { wouldThrow++; }
                }
            }

            // The threshold is deliberately low. The point is not how many, it is that the number is
            // NOT ZERO: a zero here would mean the sweep never got past the first tag and the other
            // tests in this file are decoration.
            Assert.True(
                wouldThrow >= 10,
                "Only " + wouldThrow.ToString() + " of " + total.ToString()
                + " damaged bodies reached a state that would throw. The sweep is not exercising the"
                + " length checks, so the robustness tests above are not proving anything.");
        }

        /// <summary>
        /// Reports whether the throwing reader rejects this body by exception.
        /// </summary>
        /// <param name="body">
        /// The damaged message.
        /// </param>
        /// <returns>
        /// True when <c>QformReader.Read</c> throws on it.
        /// </returns>
        private static bool Throws(byte[] body)
        {
            try
            {
                QformReader.Read(body);
                return false;
            }
            catch (QformFormatException)
            {
                return true;
            }
        }
    }
}