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

        [Test]
        public void EchoOff_SendsNothing()
        {
            Assert.That(TadServer.ProcessTextForEcho(With(2, 0, 9, 4), "abc\r"), Is.Empty);
        }
    }
}
