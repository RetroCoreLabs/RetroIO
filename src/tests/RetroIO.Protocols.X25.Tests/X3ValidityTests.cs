//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// One test per X.3 parameter. Each test lists the values the section of T-REC-X.3 (03/00)
    /// allows and some values outside them, and checks X3.IsValidParameter (and X3Parameters, which
    /// uses the same rule) against that list.
    /// </summary>
    [TestFixture]
    public class X3ValidityTests
    {
        private static void AssertValid(byte parameter, params int[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                Assert.That(X3.IsValidParameter(parameter, (byte)values[i]), Is.True, "X3 parameter " + parameter + " value " + values[i]);
                Assert.That(X3Parameters.IsValidParameter(parameter, (byte)values[i]), Is.True, "X3Parameters parameter " + parameter + " value " + values[i]);
            }
        }

        private static void AssertInvalid(byte parameter, params int[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                Assert.That(X3.IsValidParameter(parameter, (byte)values[i]), Is.False, "X3 parameter " + parameter + " value " + values[i]);
                Assert.That(X3Parameters.IsValidParameter(parameter, (byte)values[i]), Is.False, "X3Parameters parameter " + parameter + " value " + values[i]);
            }
        }

        /// <summary>
        /// X.3 3.1: 0 not possible, 1 DLE, 32 to 126 a graphic character chosen by the user.
        /// </summary>
        [Test]
        public void Parameter1_PadRecall_Allows_0_1_32To126()
        {
            AssertValid(1, 0, 1, 32, 65, 126);
            AssertInvalid(1, 2, 31, 127, 255);
        }

        /// <summary>
        /// X.3 3.2: 0 no echo, 1 echo, 2 echo all except the forwarding sequence, 32 to 126 scrambled echo character.
        /// </summary>
        [Test]
        public void Parameter2_Echo_Allows_0_1_2_32To126()
        {
            AssertValid(2, 0, 1, 2, 32, 126);
            AssertInvalid(2, 3, 31, 127, 255);
        }

        /// <summary>
        /// X.3 3.3: a sum of the flags 1, 2, 4, 8, 16, 32 and 64, so 0 to 127.
        /// </summary>
        [Test]
        public void Parameter3_DataForwarding_Allows_0To127()
        {
            AssertValid(3, 0, 1, 126, 127);
            AssertInvalid(3, 128, 255);
        }

        /// <summary>
        /// X.3 3.4: any number from 0 to 255.
        /// </summary>
        [Test]
        public void Parameter4_IdleTimer_Allows_0To255()
        {
            AssertValid(4, 0, 1, 255);
        }

        /// <summary>
        /// X.3 3.5: 0 no X-ON/X-OFF, 1 use in data transfer, 2 use in data transfer and command.
        /// </summary>
        [Test]
        public void Parameter5_AncillaryDeviceControl_Allows_0_1_2()
        {
            AssertValid(5, 0, 1, 2);
            AssertInvalid(5, 3, 255);
        }

        /// <summary>
        /// X.3 3.6: flags 1, 2, 4, values 8 to 15 network dependent, and 16 to 240 in steps of 16 (languages)
        /// that may be combined with them (NOTE 3 and NOTE 4), so every value 0 to 255 is a legal combination.
        /// </summary>
        [Test]
        public void Parameter6_ServiceSignals_Allows_0To255()
        {
            AssertValid(6, 0, 1, 2, 4, 5, 15, 16, 41, 64, 240, 255);
        }

        /// <summary>
        /// X.3 3.7: a sum of the flags 1, 2, 4, 8 and 16, so 0 to 31.
        /// </summary>
        [Test]
        public void Parameter7_Break_Allows_FlagSums_0To31()
        {
            AssertValid(7, 0, 1, 2, 21, 31);
            AssertInvalid(7, 32, 127, 255);
        }

        /// <summary>
        /// X.3 3.8: 0 normal delivery, 1 discard output.
        /// </summary>
        [Test]
        public void Parameter8_DiscardOutput_Allows_0_1()
        {
            AssertValid(8, 0, 1);
            AssertInvalid(8, 2, 255);
        }

        /// <summary>
        /// X.3 3.9: any number from 0 to 255 (the number of padding characters).
        /// </summary>
        [Test]
        public void Parameter9_PaddingAfterCr_Allows_0To255()
        {
            AssertValid(9, 0, 7, 8, 255);
        }

        /// <summary>
        /// X.3 3.10: 0 no folding, 1 to 255 the characters per line.
        /// </summary>
        [Test]
        public void Parameter10_LineFolding_Allows_0To255()
        {
            AssertValid(10, 0, 1, 255);
        }

        /// <summary>
        /// X.3 3.11: the binary speed values are 0 to 19. The parameter is read only for the two DTEs;
        /// the rule here only says which numbers are speeds.
        /// </summary>
        [Test]
        public void Parameter11_BinarySpeed_Allows_0To19()
        {
            AssertValid(11, 0, 14, 19);
            AssertInvalid(11, 20, 255);
        }

        /// <summary>
        /// X.3 3.12: 0 or 1.
        /// </summary>
        [Test]
        public void Parameter12_FlowControl_Allows_0_1()
        {
            AssertValid(12, 0, 1);
            AssertInvalid(12, 2, 255);
        }

        /// <summary>
        /// X.3 3.13: a sum of the flags 1, 2 and 4, so 0 to 7.
        /// </summary>
        [Test]
        public void Parameter13_LinefeedInsertion_Allows_0To7()
        {
            AssertValid(13, 0, 1, 7);
            AssertInvalid(13, 8, 255);
        }

        /// <summary>
        /// X.3 3.14: any number from 0 to 255 (the number of padding characters).
        /// </summary>
        [Test]
        public void Parameter14_LinefeedPadding_Allows_0To255()
        {
            AssertValid(14, 0, 7, 8, 255);
        }

        /// <summary>
        /// X.3 3.15: 0 or 1.
        /// </summary>
        [Test]
        public void Parameter15_Editing_Allows_0_1()
        {
            AssertValid(15, 0, 1);
            AssertInvalid(15, 2, 255);
        }

        /// <summary>
        /// X.3 3.16: a character 0 to 127, or 128, 129, 130 for the three character sequences.
        /// </summary>
        [Test]
        public void Parameter16_CharacterDelete_Allows_0To130()
        {
            AssertValid(16, 0, 127, 128, 129, 130);
            AssertInvalid(16, 131, 255);
        }

        /// <summary>
        /// X.3 3.17: a character 0 to 127.
        /// </summary>
        [Test]
        public void Parameter17_LineDelete_Allows_0To127()
        {
            AssertValid(17, 0, 127);
            AssertInvalid(17, 128, 255);
        }

        /// <summary>
        /// X.3 3.18: a character 0 to 127.
        /// </summary>
        [Test]
        public void Parameter18_LineDisplay_Allows_0To127()
        {
            AssertValid(18, 0, 127);
            AssertInvalid(18, 128, 255);
        }

        /// <summary>
        /// X.3 3.19: 0, 1, 2, 8, or a character 32 to 126.
        /// </summary>
        [Test]
        public void Parameter19_EditingServiceSignals_Allows_0_1_2_8_32To126()
        {
            AssertValid(19, 0, 1, 2, 8, 32, 126);
            AssertInvalid(19, 3, 7, 9, 31, 127, 255);
        }

        /// <summary>
        /// X.3 3.20: a sum of the flags 1, 2, 4, 8, 16, 32, 64 and 128, so 0 to 255.
        /// </summary>
        [Test]
        public void Parameter20_EchoMask_Allows_0To255()
        {
            AssertValid(20, 0, 1, 128, 255);
        }

        /// <summary>
        /// X.3 3.21: a sum of the flags 1, 2 and 4, so 0 to 7.
        /// </summary>
        [Test]
        public void Parameter21_ParityTreatment_Allows_0To7()
        {
            AssertValid(21, 0, 1, 2, 4, 7);
            AssertInvalid(21, 8, 255);
        }

        /// <summary>
        /// X.3 3.22: 0 disabled, 1 to 255 the number of linefeeds.
        /// </summary>
        [Test]
        public void Parameter22_PageWait_Allows_0To255()
        {
            AssertValid(22, 0, 1, 255);
        }

        /// <summary>
        /// Parameter numbers outside 1 to 22 are not carried by this class.
        /// </summary>
        [Test]
        public void ParameterNumbers_Outside1To22_AreInvalid()
        {
            AssertInvalid(0, 0, 1);
            AssertInvalid(23, 0, 1);
        }
    }
}
