//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg.Fa;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// The credentials block of <c>ReserveFileEntry</c>, pinned against a capture that fills it in.
    /// </summary>
    /// <remarks>
    /// <para><b>Why these exist</b></para>
    /// <para>
    /// Every capture the builder was originally written from had the client reading its OWN
    /// directory with no password, so three of field 4's four sub-fields were NUL or duplicated and
    /// the comment recorded them as "meaning UNKNOWN". A push therefore always landed in the
    /// session's own user, whatever the caller asked for.
    /// </para>
    /// <para>
    /// <c>DOC/captures/ARCHIVE-2026-07/2026-07-29-1426-fa-access-secret-d102-to-d100.pcapng</c> separates
    /// them: node 102 reads user <c>SECRET</c>, password <c>secret</c>, on node 100. Its request
    /// carries <c>SECRET</c> in field 4 while field 3 still says <c>BAK03  SYSTEM</c>, and the slot
    /// that used to be NUL carries <c>6D 2A</c>.
    /// </para>
    /// </remarks>
    public sealed class FaReserveCredentialsTests
    {
        /// <summary>
        /// The exact 56-byte credentials block from the captured frame.
        /// </summary>
        /// <remarks>
        /// Copied from the frame, not from our own output - otherwise this test would only prove
        /// the builder agrees with itself.
        /// </remarks>
        private const string CapturedBlock =
            "8c38"
            + "b010" + "53454352455427000000000000000000"
            + "e180"
            + "b010" + "6d2a0000000000000000000000000000"
            + "b010" + "00000000000000000000000000000000";

        /// <summary>
        /// The word the fold produces for the captured password.
        /// </summary>
        /// <remarks>
        /// <c>secret</c> under <c>acc = ROL16(acc,3) + toupper(c)</c>. Written as a literal here on
        /// purpose: this file is testing the wire shape, and taking the value from the encoder
        /// would hide a change in either one behind a change in the other.
        /// </remarks>
        private const ushort SecretWord = 0x6D2A;

        /// <summary>
        /// The remote user and the folded password land where the capture puts them.
        /// </summary>
        [Fact]
        public void TheCredentialsBlockMatchesTheCapturedFrame()
        {
            byte[] fields = FaWriteRequests.ReserveFileEntry("BAK03", "SYSTEM", "SECRET", SecretWord);

            string hex = Convert.ToHexString(fields).ToLowerInvariant();

            Assert.Contains(CapturedBlock, hex);
        }

        /// <summary>
        /// Field 3 keeps naming the LOCAL user, not the remote one.
        /// </summary>
        /// <remarks>
        /// The whole point of the decode: field 3 is who is ASKING and field 4 is whose DIRECTORY.
        /// A build that put SECRET in both would still contain the block above, so this is checked
        /// separately.
        /// </remarks>
        [Fact]
        public void FieldThreeNamesTheAskerNotTheRemoteUser()
        {
            byte[] fields = FaWriteRequests.ReserveFileEntry("BAK03", "SYSTEM", "SECRET", SecretWord);

            string ascii = System.Text.Encoding.ASCII.GetString(fields);

            Assert.Contains("BAK03  SYSTEM", ascii);
        }

        /// <summary>
        /// A user with no password leaves the slot NUL, exactly as the same-user captures show.
        /// </summary>
        [Fact]
        public void NoPasswordLeavesTheSlotEmpty()
        {
            byte[] fields = FaWriteRequests.ReserveFileEntry("BAK04", "SYSTEM", "UTILITY", 0);

            string hex = Convert.ToHexString(fields).ToLowerInvariant();

            Assert.Contains("b010" + new string('0', 32) + "b010" + new string('0', 32), hex);
        }

        /// <summary>
        /// The old two-argument form still builds what it always built.
        /// </summary>
        /// <remarks>
        /// It now delegates, so this guards every existing caller: same user both ends, no
        /// password.
        /// </remarks>
        [Fact]
        public void TheTwoArgumentFormStillReadsOurOwnDirectory()
        {
            byte[] viaOld = FaWriteRequests.ReserveFileEntry("BAK04", "SYSTEM");
            byte[] viaNew = FaWriteRequests.ReserveFileEntry("BAK04", "SYSTEM", "SYSTEM", 0);

            Assert.Equal(viaNew, viaOld);
        }

        /// <summary>
        /// The plaintext password is never written into the buffer.
        /// </summary>
        /// <remarks>
        /// The builder takes an already-folded word, so this cannot regress by accident - but it is
        /// the property the whole scheme rests on, and a future overload taking a string could
        /// break it silently.
        /// </remarks>
        [Fact]
        public void ThePlaintextPasswordNeverReachesTheBuffer()
        {
            byte[] fields = FaWriteRequests.ReserveFileEntry("BAK03", "SYSTEM", "SECRET", SecretWord);

            string ascii = System.Text.Encoding.ASCII.GetString(fields);

            Assert.DoesNotContain("secret", ascii);
        }

        /// <summary>
        /// A remote user too long for its sixteen-byte slot is refused, not truncated.
        /// </summary>
        /// <param name="remoteUser">
        /// A user name that cannot fit with its terminator.
        /// </param>
        [Theory]
        [InlineData("ABCDEFGHIJKLMNOP")]
        [InlineData("THIS-USER-NAME-IS-FAR-TOO-LONG")]
        public void AnOverlongRemoteUserIsRefused(string remoteUser)
        {
            Assert.Throws<ArgumentException>(
                () => FaWriteRequests.ReserveFileEntry("BAK03", "SYSTEM", remoteUser, 0));
        }
    }
}
