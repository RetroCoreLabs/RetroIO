//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// The terminal server's echo pads with NUL (X.28 3.5.20), parameter 9 after a CR (4.12), parameter 14 after an LF
    /// (4.16), and puts the linefeed in the echo only for parameter 13 value 4 (X.3 3.13). Added 2026-11-21, BUGS 44:
    /// before, it padded with spaces, inserted the LF for any non-zero parameter 13 and ahead of the CR's padding.
    /// </summary>
    [TestFixture]
    public class TadServerEchoPaddingTests
    {
        private static X3Parameters With(params byte[] pairs)
        {
            X3Parameters x3 = new X3Parameters();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                Assert.That(x3.SetParameter(pairs[i], pairs[i + 1]), Is.True, "parameter " + pairs[i]);
            }

            return x3;
        }

        [Test]
        public void AnEchoedCr_IsPaddedWithNul()
        {
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 9, 2), "a\r"), Is.EqualTo("a\r\0\0"));
        }

        [Test]
        public void AnEchoedLf_IsPaddedWithParameter14()
        {
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 14, 3), "\n"), Is.EqualTo("\n\0\0\0"));
        }

        [Test]
        public void Parameter13Value4_PutsTheLfAfterTheCrsPadding_AndPadsItToo()
        {
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 9, 1, 13, 4, 14, 2), "\r"), Is.EqualTo("\r\0\n\0\0"));
        }

        [Test]
        public void Parameter13Value1_IsNotAboutTheEcho()
        {
            // X.3 3.13 value 1: "after each carriage return in the data stream to the start-stop mode DTE"; not the echo.
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 13, 1), "\r"), Is.EqualTo("\r"));
        }

        /// <summary>
        /// X.3 3.20 by bit (2026-11-21; before, any non-zero mask echoed exactly the printable characters).
        /// </summary>
        [Test]
        public void TheEchoMask_IsReadBitByBit()
        {
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 20, 1, 9, 2), "a\rb"), Is.EqualTo("ab"), "1: no CR, and so no padding after it");
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 20, 2), "a\nb"), Is.EqualTo("ab"), "2: no LF");
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 20, 4), "a\tb"), Is.EqualTo("ab"), "4: no HT");
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 20, 8), "a\tb\x07"), Is.EqualTo("a\tb"), "8: BEL gone, HT kept");
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 20, 128), "a\x7f\x1b"), Is.EqualTo("a\x1b"), "128: DEL gone, ESC is bit 16");
        }

        [Test]
        public void Bit64_HidesTheEditingCharacters_OnlyWithEditingOn()
        {
            // Parameter 16, character delete, defaults to DEL (127) in this model; 15 = 1 turns editing on.
            byte del = new X3Parameters().GetParameter(16);
            string typed = "a" + (char)del;
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 20, 64, 15, 1), typed), Is.EqualTo("a"));
            Assert.That(TadServer.ProcessTextForEcho(With(2, 1, 20, 64, 15, 0), typed), Is.EqualTo(typed), "Note 6: not without editing");
        }

        [Test]
        public void EchoOff_SendsNothing()
        {
            Assert.That(TadServer.ProcessTextForEcho(With(2, 0, 9, 4), "abc\r"), Is.Empty);
        }
    }
}
