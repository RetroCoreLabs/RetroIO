//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Tests
{
    /// <summary>
    /// X.3 checks over every parameter number and every value from 0 to 255: the legal values come
    /// from a table written here from T-REC-X.3 (03/00) section 3, then X3 and X3Parameters (the
    /// static rule and the Set methods) are checked against it. Also the profile switches of both
    /// classes, with and without a logger.
    /// </summary>
    [TestFixture]
    public class X3EveryValueTests
    {
        /// <summary>
        /// The legal values of parameters 1 to 22 as pairs of lowest and highest value. Index 0 is
        /// parameter 1.
        /// </summary>
        private static readonly int[][] Legal =
        {
            new[] { 0, 1, 32, 126 },    // 1  0 not possible, 1 DLE, 32 to 126 a graphic character
            new[] { 0, 2, 32, 126 },    // 2  0 no echo, 1 echo, 2 echo except forwarding, 32 to 126 scrambled echo
            new[] { 0, 127 },           // 3  sum of seven flags
            new[] { 0, 255 },           // 4  any number
            new[] { 0, 2 },             // 5  0, 1, 2
            new[] { 0, 255 },           // 6  flags and network values, every number is a legal sum
            new[] { 0, 31 },            // 7  sum of five flags
            new[] { 0, 1 },             // 8  0 or 1
            new[] { 0, 255 },           // 9  any number
            new[] { 0, 255 },           // 10 any number
            new[] { 0, 19 },            // 11 speeds 0 to 19
            new[] { 0, 1 },             // 12 0 or 1
            new[] { 0, 7 },             // 13 sum of three flags
            new[] { 0, 255 },           // 14 any number
            new[] { 0, 1 },             // 15 0 or 1
            new[] { 0, 130 },           // 16 a character 0 to 127 or 128 to 130
            new[] { 0, 127 },           // 17 a character 0 to 127
            new[] { 0, 127 },           // 18 a character 0 to 127
            new[] { 0, 2, 8, 8, 32, 126 }, // 19 0, 1, 2, 8 or a character 32 to 126
            new[] { 0, 255 },           // 20 sum of eight flags
            new[] { 0, 7 },             // 21 sum of three flags
            new[] { 0, 255 }            // 22 any number
        };

        private static bool IsLegal(int parameter, int value)
        {
            if (parameter < 1 || parameter > 22)
            {
                return false;
            }

            int[] pairs = Legal[parameter - 1];
            for (int i = 0; i < pairs.Length; i += 2)
            {
                if (value >= pairs[i] && value <= pairs[i + 1])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// For every parameter 0 to 255 and every value 0 to 255 the static rule of both classes
        /// gives the answer of the table. That covers the lowest value, the highest value and the first
        /// value above it of every parameter, and the holes inside parameters 1, 2 and 19.
        /// </summary>
        [Test]
        public void IsValidParameter_MatchesTheTable_ForEveryParameterAndValue()
        {
            StringBuilder wrong = new StringBuilder();
            int count = 0;
            for (int parameter = 0; parameter <= 255; parameter++)
            {
                for (int value = 0; value <= 255; value++)
                {
                    bool expected = IsLegal(parameter, value);
                    bool x3 = X3.IsValidParameter((byte)parameter, (byte)value);
                    bool x3Parameters = X3Parameters.IsValidParameter((byte)parameter, (byte)value);
                    if (x3 != expected || x3Parameters != expected)
                    {
                        count++;
                        if (count <= 20)
                        {
                            wrong.Append("parameter ").Append(parameter).Append(" value ").Append(value)
                                .Append(": table ").Append(expected).Append(", X3 ").Append(x3)
                                .Append(", X3Parameters ").Append(x3Parameters).Append("; ");
                        }
                    }
                }
            }

            Assert.That(count, Is.EqualTo(0), wrong.ToString());
        }

        /// <summary>
        /// SetParameter on an X3 and on an X3Parameters accepts exactly the legal values, stores them,
        /// and leaves the stored value alone when it refuses. X3 raises ParametersChanged for an
        /// accepted value only.
        /// </summary>
        [Test]
        public void SetParameter_AcceptsExactlyTheLegalValues_InBothClasses()
        {
            StringBuilder wrong = new StringBuilder();
            int count = 0;
            for (int parameter = 1; parameter <= 22; parameter++)
            {
                for (int value = 0; value <= 255; value++)
                {
                    X3 x3 = new X3();
                    X3Parameters parameters = new X3Parameters();
                    byte before = x3.GetParameter((byte)parameter);
                    int changed = 0;
                    x3.ParametersChanged += (s, e) => changed++;
                    bool expected = IsLegal(parameter, value);

                    bool x3Result = x3.SetParameter((byte)parameter, (byte)value);
                    bool parametersResult = parameters.SetParameter((byte)parameter, (byte)value);

                    byte x3After = x3.GetParameter((byte)parameter);
                    byte parametersAfter = parameters.GetParameter((byte)parameter);
                    byte expectedAfter = expected ? (byte)value : before;
                    bool bad = x3Result != expected || parametersResult != expected
                        || x3After != expectedAfter || parametersAfter != expectedAfter
                        || changed != (expected ? 1 : 0);
                    if (bad)
                    {
                        count++;
                        if (count <= 20)
                        {
                            wrong.Append("parameter ").Append(parameter).Append(" value ").Append(value).Append("; ");
                        }
                    }
                }
            }

            Assert.That(count, Is.EqualTo(0), wrong.ToString());
        }

        /// <summary>
        /// A parameter number outside 1 to 22 is refused by both classes and reads back as 0.
        /// </summary>
        [Test]
        public void ParameterNumbersOutside1To22_AreRefused_AndReadAsZero()
        {
            X3 x3 = new X3();
            X3Parameters parameters = new X3Parameters();
            int[] numbers = { 0, 23, 29, 255 };
            for (int i = 0; i < numbers.Length; i++)
            {
                byte n = (byte)numbers[i];
                Assert.That(x3.SetParameter(n, 1), Is.False, "X3 " + n);
                Assert.That(parameters.SetParameter(n, 1), Is.False, "X3Parameters " + n);
                Assert.That(x3.GetParameter(n), Is.EqualTo(0), "X3 read " + n);
                Assert.That(parameters.GetParameter(n), Is.EqualTo(0), "X3Parameters read " + n);
            }

            Assert.That(x3.GetAllParameters().Count, Is.EqualTo(22));
            Assert.That(parameters.GetAllParameters().Count, Is.EqualTo(22));
        }

        private static void AssertSameTable(Dictionary<byte, byte> expected, Dictionary<byte, byte> actual, string name)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count), name + ": count");
            for (int number = 1; number <= 22; number++)
            {
                Assert.That(actual[(byte)number], Is.EqualTo(expected[(byte)number]), name + ": parameter " + number);
            }
        }

        /// <summary>
        /// SetProfile takes STD, TRANS and SIMPLE in any letter case, with or without a logger, and
        /// each profile gives the same table as the matching fill method. The standard profile is the
        /// default. A profile name that does not exist, and null, are refused and leave the table alone.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void SetProfile_AppliesEachProfile_AndRefusesUnknownNames(bool withLogger)
        {
            X3 x3 = withLogger ? new X3(NullLogger.Instance) : new X3();
            Dictionary<byte, byte> table = new Dictionary<byte, byte>();

            X3.FillTransparentProfile(table);
            Assert.That(x3.SetProfile("trans"), Is.True);
            AssertSameTable(table, x3.GetAllParameters(), "TRANS");

            X3.FillSimpleProfile(table);
            Assert.That(x3.SetProfile("Simple"), Is.True);
            AssertSameTable(table, x3.GetAllParameters(), "SIMPLE");

            x3.SetProfile("TRANS");
            X3.FillDefaultValues(table);
            Assert.That(x3.SetProfile("std"), Is.True);
            AssertSameTable(table, x3.GetAllParameters(), "STD");

            x3.SetProfile("TRANS");
            Dictionary<byte, byte> transparent = x3.GetAllParameters();
            Assert.That(x3.SetProfile("NOPE"), Is.False);
            Assert.That(x3.SetProfile(null!), Is.False);
            AssertSameTable(transparent, x3.GetAllParameters(), "after a refused name");
        }

        /// <summary>
        /// The public profile methods of X3 (standard, transparent, simple, default) give the same
        /// table as the fill methods, with and without a logger.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void ProfileMethods_GiveTheTablesOfTheFillMethods(bool withLogger)
        {
            X3 x3 = withLogger ? new X3(NullLogger.Instance) : new X3();
            Dictionary<byte, byte> table = new Dictionary<byte, byte>();

            x3.SetTransparentProfile();
            X3.FillTransparentProfile(table);
            AssertSameTable(table, x3.GetAllParameters(), "SetTransparentProfile");

            x3.SetSimpleProfile();
            X3.FillSimpleProfile(table);
            AssertSameTable(table, x3.GetAllParameters(), "SetSimpleProfile");

            x3.SetTransparentProfile();
            x3.SetStandardProfile();
            X3.FillDefaultValues(table);
            AssertSameTable(table, x3.GetAllParameters(), "SetStandardProfile");

            x3.SetTransparentProfile();
            x3.SetDefaultProfile();
            AssertSameTable(table, x3.GetAllParameters(), "SetDefaultProfile");
        }

        /// <summary>
        /// The two profiles differ in exactly the parameters Table 1/X.28 gives different numbers for:
        /// 1, 2, 3, 4, 5, 6 and 12.
        /// </summary>
        [Test]
        public void TheTwoProfiles_DifferInExactlyParameters_1To6_And_12()
        {
            Dictionary<byte, byte> transparent = new Dictionary<byte, byte>();
            Dictionary<byte, byte> simple = new Dictionary<byte, byte>();
            X3.FillTransparentProfile(transparent);
            X3.FillSimpleProfile(simple);

            for (int number = 1; number <= 22; number++)
            {
                bool shouldDiffer = number <= 6 || number == 12;
                bool differs = transparent[(byte)number] != simple[(byte)number];
                Assert.That(differs, Is.EqualTo(shouldDiffer), "parameter " + number);
            }
        }

        /// <summary>
        /// X3Parameters keeps its own table: a clone has the same values, changing the clone does not
        /// change the original, and the other way round. ToString lists every parameter as number
        /// and value.
        /// </summary>
        [Test]
        public void X3Parameters_Clone_IsIndependent_AndToStringListsEveryParameter()
        {
            X3Parameters original = new X3Parameters();
            Assert.That(original.SetParameter(3, 10), Is.True);

            X3Parameters clone = original.Clone();
            AssertSameTable(original.GetAllParameters(), clone.GetAllParameters(), "clone");

            Assert.That(clone.SetParameter(3, 20), Is.True);
            Assert.That(original.GetParameter(3), Is.EqualTo(10));
            Assert.That(original.SetParameter(4, 9), Is.True);
            Assert.That(clone.GetParameter(4), Is.EqualTo(0));

            string text = original.ToString();
            string[] parts = text.Split(',');
            Assert.That(parts.Length, Is.EqualTo(22));
            Assert.That(text, Does.Contain("3:10"));
            Assert.That(text, Does.Contain("4:9"));
            Assert.That(text, Does.Contain("22:0"));
        }

        /// <summary>
        /// SetDefaultValues puts the table back to the default after other changes.
        /// </summary>
        [Test]
        public void X3Parameters_SetDefaultValues_RestoresTheDefaultTable()
        {
            X3Parameters parameters = new X3Parameters();
            parameters.SetTransparentProfile();
            Assert.That(parameters.GetParameter(2), Is.EqualTo(0));

            parameters.SetDefaultValues();

            Dictionary<byte, byte> table = new Dictionary<byte, byte>();
            X3.FillDefaultValues(table);
            AssertSameTable(table, parameters.GetAllParameters(), "defaults");
            Assert.That(parameters.GetParameter(2), Is.EqualTo(1));
        }

        /// <summary>
        /// The password mode flags: the named flag constants add up to the values the code uses.
        /// </summary>
        [Test]
        public void DataForwardingFlags_AddUpToTheNamedSums()
        {
            int all = X3.DataForwardingFlag.ALPHANUMERIC + X3.DataForwardingFlag.CARRIAGE_RETURN
                + X3.DataForwardingFlag.ESC_BEL_ENQ_ACK + X3.DataForwardingFlag.DEL_CAN_DC2
                + X3.DataForwardingFlag.ETX_EOT + X3.DataForwardingFlag.HT_LF_VT_FF
                + X3.DataForwardingFlag.OTHER_COLUMNS_0_1;

            Assert.That(all, Is.EqualTo(X3.DataForwardingFlag.ALL_FLAGS));
            Assert.That(X3.DataForwardingFlag.ALL_FLAGS, Is.EqualTo(127));
            Assert.That(X3.DataForwardingFlag.ALL_CONTROL, Is.EqualTo(X3.DataForwardingFlag.ALL_FLAGS - X3.DataForwardingFlag.ALPHANUMERIC));
        }
    }
}
