//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Pins the X.3 parameter values and the two standard profiles to the ITU-T texts.
    /// Sources: T-REC-X.3 (03/00) section 3 for the meaning of each parameter value,
    /// T-REC-X.28 (12/97) Table 1/X.28 (pages 9 and 10) for the two standard profiles.
    /// </summary>
    [TestFixture]
    public class X3SpecTests
    {
        /// <summary>
        /// Table 1/X.28, transparent standard profile, parameters 1 to 22 in order.
        /// Parameter 11 is read only (the speed of the terminal), the table gives no number, so 0 is used.
        /// </summary>
        private static readonly byte[] TransparentProfile =
        {
            0,   // 1  PAD recall: not possible
            0,   // 2  echo: no echo
            0,   // 3  data forwarding: no data forwarding signal
            20,  // 4  idle timer: one second (20 twentieths)
            0,   // 5  ancillary device control: no X-ON / X-OFF
            0,   // 6  PAD service signals: none sent
            2,   // 7  break: reset
            0,   // 8  discard output: normal delivery
            0,   // 9  padding after CR: none
            0,   // 10 line folding: none
            0,   // 11 binary speed: read only
            0,   // 12 flow control of the PAD: no X-ON / X-OFF
            0,   // 13 linefeed insertion: none
            0,   // 14 linefeed padding: none
            0,   // 15 editing: none
            127, // 16 character delete: DEL
            24,  // 17 line delete: CAN
            18,  // 18 line display: DC2
            1,   // 19 editing PAD service signals: printing terminals
            0,   // 20 echo mask: echo all characters
            0,   // 21 parity treatment: none
            0    // 22 page wait: disabled
        };

        /// <summary>
        /// Table 1/X.28, simple standard profile, parameters 1 to 22 in order.
        /// </summary>
        private static readonly byte[] SimpleProfile =
        {
            1,   // 1  PAD recall: possible (DLE)
            1,   // 2  echo: echo
            126, // 3  data forwarding: all of columns 0 and 1 and DEL (2+4+8+16+32+64)
            0,   // 4  idle timer: no time-out
            1,   // 5  ancillary device control: use X-ON / X-OFF
            1,   // 6  PAD service signals: sent
            2,   // 7  break: reset
            0,   // 8  discard output: normal delivery
            0,   // 9  padding after CR: none
            0,   // 10 line folding: none
            0,   // 11 binary speed: read only
            1,   // 12 flow control of the PAD: use X-ON / X-OFF
            0,   // 13 linefeed insertion: none
            0,   // 14 linefeed padding: none
            0,   // 15 editing: none
            127, // 16 character delete: DEL
            24,  // 17 line delete: CAN
            18,  // 18 line display: DC2
            1,   // 19 editing PAD service signals: printing terminals
            0,   // 20 echo mask: echo all characters
            0,   // 21 parity treatment: none
            0    // 22 page wait: disabled
        };

        private static void AssertProfile(Dictionary<byte, byte> actual, byte[] expected, string name)
        {
            Assert.That(actual.Count, Is.EqualTo(22), name + ": parameter count");
            for (int i = 0; i < expected.Length; i++)
            {
                byte number = (byte)(i + 1);
                Assert.That(actual[number], Is.EqualTo(expected[i]),
                    name + ": parameter " + number + " against Table 1/X.28");
            }
        }

        /// <summary>
        /// X.3 reference 12 is flow control of the PAD: 0 no X-ON/X-OFF, 1 use X-ON/X-OFF.
        /// </summary>
        [Test]
        public void X3_Parameter12_IsFlowControl_ZeroOrOne()
        {
            Assert.That(X3.IsValidParameter(12, 0), Is.True);
            Assert.That(X3.IsValidParameter(12, 1), Is.True);
            Assert.That(X3.IsValidParameter(12, 2), Is.False, "X.3 3.12 has only the values 0 and 1");
            Assert.That(X3.IsValidParameter(12, 3), Is.False);
            Assert.That(X3.GetParameterDescription(12).ToLowerInvariant(), Does.Contain("flow control"));
        }

        /// <summary>
        /// Same check on the class the TAD server and the local TAD protocol use.
        /// </summary>
        [Test]
        public void X3Parameters_Parameter12_IsFlowControl_ZeroOrOne()
        {
            Assert.That(X3Parameters.IsValidParameter(12, 1), Is.True);
            Assert.That(X3Parameters.IsValidParameter(12, 2), Is.False);
            Assert.That(X3Parameters.GetParameterDescription(12).ToLowerInvariant(), Does.Contain("flow control"));
        }

        /// <summary>
        /// Reference 3 is a sum of flags, 1+2+4+8+16+32+64 = 127 at most.
        /// </summary>
        [Test]
        public void DataForwarding_AcceptsEveryFlagSum_UpTo127()
        {
            Assert.That(X3.IsValidParameter(3, 127), Is.True, "all seven flags together");
            Assert.That(X3.IsValidParameter(3, 128), Is.False);
            Assert.That(X3Parameters.IsValidParameter(3, 127), Is.True);
            Assert.That(X3Parameters.IsValidParameter(3, 128), Is.False);
        }

        /// <summary>
        /// The default of every parameter is the simple standard profile of Table 1/X.28 (parameter 11 is
        /// read only and the table gives no number, so it stays 0). Replaces the old test that pinned
        /// parameter 3 at 2: the simple profile sets parameter 3 to 126.
        /// </summary>
        [Test]
        public void Defaults_AreTheSimpleStandardProfile_InBothClasses()
        {
            AssertProfile(new X3().GetAllParameters(), SimpleProfile, "X3 default");
            AssertProfile(new X3Parameters().GetAllParameters(), SimpleProfile, "X3Parameters default");
        }

        /// <summary>
        /// The DefaultValue constants of both classes are the simple standard profile too.
        /// </summary>
        [Test]
        public void DefaultValueConstants_AreTheSimpleStandardProfile()
        {
            var table = new Dictionary<byte, byte>();
            X3.FillDefaultValues(table);
            AssertProfile(table, SimpleProfile, "FillDefaultValues");
            Assert.That(X3.DefaultValue.DATA_FORWARDING, Is.EqualTo(126));
            Assert.That(X3Parameters.DefaultValue.DATA_FORWARDING, Is.EqualTo(126));
            Assert.That(X3.DefaultValue.OPERATION_BREAKOUT_PROCEDURE, Is.EqualTo(2));
            Assert.That(X3.DefaultValue.DISCARD_OUTPUT, Is.EqualTo(0));
            Assert.That(X3.DefaultValue.ANCILLARY_DEVICE_CONTROL, Is.EqualTo(1));
            Assert.That(X3.DefaultValue.FLOW_CONTROL_X_ON_X_OFF, Is.EqualTo(1));
            Assert.That(X3.DefaultValue.EDITING, Is.EqualTo(0));
            Assert.That(X3.DefaultValue.EDITING_PAD_SERVICE_SIGNALS, Is.EqualTo(1));
        }

        /// <summary>
        /// ResetParameters goes back to the default, which is the simple profile.
        /// </summary>
        [Test]
        public void ResetParameters_RestoresTheSimpleProfileValue()
        {
            var x3 = new X3();
            x3.SetParameter(3, 1);
            x3.ResetParameters(new byte[] { 3 });
            Assert.That(x3.GetParameter(3), Is.EqualTo(126));
        }

        /// <summary>
        /// Transparent profile of Table 1/X.28.
        /// </summary>
        [Test]
        public void X3_TransparentProfile_MatchesTable1()
        {
            var x3 = new X3();
            Assert.That(x3.SetProfile("TRANS"), Is.True);
            AssertProfile(x3.GetAllParameters(), TransparentProfile, "X3 transparent");
        }

        /// <summary>
        /// Simple profile of Table 1/X.28.
        /// </summary>
        [Test]
        public void X3_SimpleProfile_MatchesTable1()
        {
            var x3 = new X3();
            Assert.That(x3.SetProfile("SIMPLE"), Is.True);
            AssertProfile(x3.GetAllParameters(), SimpleProfile, "X3 simple");
        }

        /// <summary>
        /// Transparent profile of Table 1/X.28 on the class the TAD server uses.
        /// </summary>
        [Test]
        public void X3Parameters_TransparentProfile_MatchesTable1()
        {
            var parameters = new X3Parameters();
            parameters.SetTransparentProfile();
            AssertProfile(parameters.GetAllParameters(), TransparentProfile, "X3Parameters transparent");
        }

        /// <summary>
        /// Simple profile of Table 1/X.28 on the class the TAD server uses.
        /// </summary>
        [Test]
        public void X3Parameters_SimpleProfile_MatchesTable1()
        {
            var parameters = new X3Parameters();
            parameters.SetSimpleProfile();
            AssertProfile(parameters.GetAllParameters(), SimpleProfile, "X3Parameters simple");
        }

        /// <summary>
        /// The two classes must agree on the default of every parameter, on every legal and illegal value,
        /// on every description, and on both profiles.
        /// </summary>
        [Test]
        public void X3_And_X3Parameters_AgreeOnEverything()
        {
            var x3 = new X3();
            var parameters = new X3Parameters();
            for (int number = 0; number <= 30; number++)
            {
                byte n = (byte)number;
                Assert.That(X3Parameters.GetParameterDescription(n), Is.EqualTo(X3.GetParameterDescription(n)), "description " + n);
                Assert.That(parameters.GetParameter(n), Is.EqualTo(x3.GetParameter(n)), "default " + n);
                for (int value = 0; value <= 255; value++)
                {
                    Assert.That(X3Parameters.IsValidParameter(n, (byte)value), Is.EqualTo(X3.IsValidParameter(n, (byte)value)),
                        "valid " + n + "=" + value);
                }
            }

            x3.SetProfile("TRANS");
            parameters.SetTransparentProfile();
            Assert.That(parameters.GetAllParameters(), Is.EquivalentTo(x3.GetAllParameters()), "transparent");

            x3.SetProfile("SIMPLE");
            parameters.SetSimpleProfile();
            Assert.That(parameters.GetAllParameters(), Is.EquivalentTo(x3.GetAllParameters()), "simple");
        }

        /// <summary>
        /// Both classes use the same parameter numbers for the same names.
        /// </summary>
        [Test]
        public void X3_And_X3Parameters_UseTheSameParameterNumbers()
        {
            Assert.That(X3Parameters.ParameterNumber.FLOW_CONTROL_X_ON_X_OFF, Is.EqualTo(X3.ParameterNumber.FLOW_CONTROL_X_ON_X_OFF));
            Assert.That(X3.ParameterNumber.FLOW_CONTROL_X_ON_X_OFF, Is.EqualTo((byte)12));
            Assert.That(X3Parameters.ParameterNumber.DATA_FORWARDING, Is.EqualTo(X3.ParameterNumber.DATA_FORWARDING));
            Assert.That(X3Parameters.DefaultValue.DATA_FORWARDING, Is.EqualTo(X3.DefaultValue.DATA_FORWARDING));
        }

        /// <summary>
        /// Every profile value is itself a legal value.
        /// </summary>
        [Test]
        public void ProfileValues_AreAllValid()
        {
            for (int i = 0; i < SimpleProfile.Length; i++)
            {
                Assert.That(X3.IsValidParameter((byte)(i + 1), SimpleProfile[i]), Is.True, "simple " + (i + 1));
                Assert.That(X3.IsValidParameter((byte)(i + 1), TransparentProfile[i]), Is.True, "transparent " + (i + 1));
            }
        }

        /// <summary>
        /// X.28 decodes parameter 3 as flags. Each row is: parameter 3 value, character, whether the character forwards.
        /// </summary>
        [TestCase(2, '\r', true)]
        [TestCase(2, 'a', false)]
        [TestCase(13, '\r', false, Description = "13 is 1+4+8: CR is not in it")]
        [TestCase(1, 'a', true)]
        [TestCase(1, '7', true)]
        [TestCase(1, '\r', false)]
        [TestCase(4, (char)7, true)]   // BEL
        [TestCase(4, (char)27, true)]  // ESC
        [TestCase(4, 'a', false)]
        [TestCase(8, (char)24, true)]  // CAN
        [TestCase(16, (char)3, true)]  // ETX
        [TestCase(16, (char)4, true)]  // EOT
        [TestCase(32, '\n', true)]     // LF
        [TestCase(32, '\t', true)]     // HT
        [TestCase(64, (char)1, true)]  // SOH, a column 0 character in none of the other groups
        [TestCase(64, '\r', false)]
        [TestCase(0, '\r', false)]
        public void X28_DecodesDataForwardingAsFlags(int parameter3, char character, bool forwards)
        {
            var x3 = new X3();
            Assert.That(x3.SetParameter(3, (byte)parameter3), Is.True);
            x3.SetParameter(15, 0); // no editing, so DEL / CAN / DC2 are plain data
            var x28 = new X28(x3);
            x28.SwitchToDataTransferMode();
            int forwarded = 0;
            x28.DataForwarded += (s, e) => forwarded++;

            x28.ProcessCharacter(character);

            Assert.That(forwarded > 0, Is.EqualTo(forwards));
        }
    }
}
