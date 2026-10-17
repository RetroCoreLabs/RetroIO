//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Protocols.Xmsg.Api;
using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// The SINTRAN password fold, checked against words whose values were established
    /// independently of this implementation.
    /// </summary>
    public sealed class SintranPasswordTests
    {
        /// <summary>
        /// The published vectors from the disassembly write-up.
        /// </summary>
        /// <remarks>
        /// These come from PASSWORD-ALGORITHM.md, which derived them from the carved LOGIN routine
        /// and checked them against real stored account values. They cover letters, letters plus
        /// digits, and a lowercase string with punctuation - the three cases where a wrong
        /// implementation diverges.
        /// </remarks>
        [Theory]
        [InlineData("ORANGE", 14378)]
        [InlineData("TIGER42", 37323)]
        [InlineData("COFFEE7", 32983)]
        [InlineData("sky-9", 56806)]
        public void PublishedVectors_EncodeToTheirDocumentedWords(string password, int expected)
        {
            Assert.Equal((ushort)expected, SintranPassword.Encode(password));
        }

        /// <summary>
        /// The word captured travelling between two live machines.
        /// </summary>
        /// <remarks>
        /// On 2026-07-29 node 102 listed user SECRET's files on node 100 over HDLC. The request
        /// carried 0x6D2A and the plaintext appeared nowhere in the frame. This is the fold
        /// confirmed on the wire rather than against a stored table.
        /// </remarks>
        [Fact]
        public void CapturedWireWord_MatchesTheCapturedPassword()
        {
            Assert.Equal((ushort)0x6D2A, SintranPassword.Encode("secret"));
        }

        /// <summary>
        /// The wrong password used as the control in that same capture.
        /// </summary>
        /// <remarks>
        /// ORANGE was chosen for the wrong-password run precisely because its word was already
        /// published, so the wire could be checked against a value nobody derived that day. The
        /// captured frame carried 0x382A.
        /// </remarks>
        [Fact]
        public void CapturedControlWord_IsTheDocumentedOrangeValue()
        {
            Assert.Equal((ushort)0x382A, SintranPassword.Encode("ORANGE"));
        }

        /// <summary>
        /// The fold ignores case, so any casing of a password opens the same account.
        /// </summary>
        [Theory]
        [InlineData("orange")]
        [InlineData("ORANGE")]
        [InlineData("OrAnGe")]
        public void CaseDoesNotChangeTheWord(string password)
        {
            Assert.Equal((ushort)14378, SintranPassword.Encode(password));
        }

        /// <summary>
        /// Digits must NOT be uppercased. A blanket "clear bit 5" would corrupt them.
        /// </summary>
        /// <remarks>
        /// This is the specific mistake the algorithm write-up calls out: digits have bit 5 set, so
        /// treating every character the way letters are treated yields a plausible but wrong word.
        /// TIGER42 and COFFEE7 both carry digits and are covered above; this pins the rule directly
        /// by showing a digits-only password is folded by raw ASCII.
        /// </remarks>
        [Fact]
        public void DigitsAreFoldedByRawAsciiValue()
        {
            // '1' = 0x31, '2' = 0x32. acc = ROL16(0,3) + 0x31 = 0x31; then ROL16(0x31,3) = 0x188,
            // + 0x32 = 0x1BA.
            Assert.Equal((ushort)0x01BA, SintranPassword.Encode("12"));
        }

        /// <summary>
        /// No password stores zero.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void EmptyPasswordEncodesToZero(string? password)
        {
            Assert.Equal((ushort)0, SintranPassword.Encode(password));
        }

        /// <summary>
        /// The rotate is genuinely 16-bit: bits shifted off the top must re-enter at the bottom.
        /// </summary>
        /// <remarks>
        /// A password long enough to wrap distinguishes a real ROL16 from a plain left shift, which
        /// would quietly discard the high bits and agree with the rotate on short inputs.
        /// </remarks>
        [Fact]
        public void LongPasswordExercisesTheRotateNotAShift()
        {
            // Folded independently by the same rule applied by hand in the test, so this asserts
            // the wrap-around behaviour rather than restating the implementation.
            ushort expected = 0;
            string password = "ABCDEFGHIJ";
            for (int i = 0; i < password.Length; i++)
            {
                int rotated = ((expected << 3) | (expected >> 13)) & 0xFFFF;
                expected = (ushort)((rotated + password[i]) & 0xFFFF);
            }

            Assert.Equal(expected, SintranPassword.Encode(password));

            // And it must differ from a shift-only fold, or the test proves nothing.
            int shiftOnly = 0;
            for (int i = 0; i < password.Length; i++)
            {
                shiftOnly = ((shiftOnly << 3) + password[i]) & 0xFFFF;
            }

            Assert.NotEqual((ushort)shiftOnly, SintranPassword.Encode(password));
        }

        /// <summary>
        /// The wire form is high byte first, as captured.
        /// </summary>
        [Fact]
        public void EncodeTo_EmitsHighByteFirst()
        {
            byte[] buffer = new byte[2];
            SintranPassword.EncodeTo("secret", buffer);

            Assert.Equal(0x6D, buffer[0]);
            Assert.Equal(0x2A, buffer[1]);
        }

        /// <summary>
        /// A destination too small to hold the word is refused rather than half-written.
        /// </summary>
        [Fact]
        public void EncodeTo_RejectsATooSmallDestination()
        {
            byte[] buffer = new byte[1];
            Assert.Throws<ArgumentException>(() => SintranPassword.EncodeTo("secret", buffer));
        }

        /// <summary>
        /// Matching a typed password against a captured word, including the case-insensitivity
        /// that follows from the fold.
        /// </summary>
        [Fact]
        public void Matches_ComparesAgainstAStoredWord()
        {
            Assert.True(SintranPassword.Matches("secret", 0x6D2A));
            Assert.True(SintranPassword.Matches("SECRET", 0x6D2A));
            Assert.False(SintranPassword.Matches("orange", 0x6D2A));
        }
    }
}
