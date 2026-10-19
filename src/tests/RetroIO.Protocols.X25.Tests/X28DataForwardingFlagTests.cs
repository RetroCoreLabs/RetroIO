//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// Checks X.28 data forwarding against X.3 section 3.3 reference 3 for every value of
    /// parameter 3 (0 to 127) and every character 0 to 127. The expected answer comes from a table
    /// written here from the Recommendation, not from the code under test.
    /// </summary>
    [TestFixture]
    public class X28DataForwardingFlagTests
    {
        /// <summary>
        /// The X.3 3.3 reference 3 flag that covers a character, or 0 when the character is in no flag.
        /// Flag 1 alphanumerics, 2 CR, 4 ESC BEL ENQ ACK, 8 DEL CAN DC2, 16 ETX EOT, 32 HT LF VT FF,
        /// 64 every other character of columns 0 and 1 of International Alphabet No. 5.
        /// </summary>
        private static int FlagOf(int c)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                return 1;
            }

            if (c == 13)
            {
                return 2;
            }

            if (c == 27 || c == 7 || c == 5 || c == 6)
            {
                return 4;
            }

            if (c == 127 || c == 24 || c == 18)
            {
                return 8;
            }

            if (c == 3 || c == 4)
            {
                return 16;
            }

            if (c == 9 || c == 10 || c == 11 || c == 12)
            {
                return 32;
            }

            if (c < 32)
            {
                return 64;
            }

            return 0;   // space and the symbols of columns 2 to 7 are in no flag
        }

        private static bool Forwards(int parameter3, char character)
        {
            X3 x3 = new X3();
            x3.SetParameter(3, (byte)parameter3);
            // Switch off everything else that takes a character before the forwarding rule sees it.
            x3.SetParameter(1, 0);    // no PAD recall character
            x3.SetParameter(2, 0);    // no echo
            x3.SetParameter(5, 0);    // no X-ON / X-OFF
            x3.SetParameter(12, 0);   // no flow control of the PAD
            x3.SetParameter(15, 0);   // no editing, so DEL / CAN / DC2 are plain data
            X28 x28 = new X28(x3);
            x28.SwitchToDataTransferMode();
            int forwarded = 0;
            x28.DataForwarded += (s, e) => forwarded++;

            x28.ProcessCharacter(character);

            return forwarded > 0;
        }

        /// <summary>
        /// With nobody listening to CharacterProcessed the PAD still processes the character and
        /// says so in the return value.
        /// </summary>
        [Test]
        public void ProcessCharacter_ReturnsTrue_WhenNobodyListensToCharacterProcessed()
        {
            X28 x28 = new X28(new X3());
            x28.SwitchToDataTransferMode();

            Assert.That(x28.ProcessCharacter('a'), Is.True);
        }

        /// <summary>
        /// All 128 values of parameter 3 against all 128 characters: a character forwards exactly when
        /// the value has the flag of the character's group.
        /// </summary>
        [Test]
        public void EveryValueOfParameter3_ForwardsExactlyTheCharactersOfItsFlags()
        {
            StringBuilder wrong = new StringBuilder();
            int count = 0;
            for (int value = 0; value <= 127; value++)
            {
                for (int c = 0; c <= 127; c++)
                {
                    bool expected = (value & FlagOf(c)) != 0;
                    bool actual = Forwards(value, (char)c);
                    if (expected != actual)
                    {
                        count++;
                        if (count <= 20)
                        {
                            wrong.Append("parameter 3 = ").Append(value).Append(", character ").Append(c)
                                .Append(": expected ").Append(expected).Append(", got ").Append(actual).Append("; ");
                        }
                    }
                }
            }

            Assert.That(count, Is.EqualTo(0), wrong.ToString());
        }
    }
}
