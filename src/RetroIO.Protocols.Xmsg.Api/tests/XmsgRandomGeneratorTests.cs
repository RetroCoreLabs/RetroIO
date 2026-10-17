//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

using Xunit;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Pins the behaviour of the kernel's magic-number randomiser (ZRAND) carved from the XMSG L03
    /// kernel, and checks it against the values actually observed on the wire.
    /// </summary>
    /// <remarks>
    /// Evidence: DOC/XMSG-MAGIC-NUMBER-LAYOUT-CARVED-2026-07-26.md and
    /// DOC/XMSG-WIRE-PORT-IS-MAGIC-LOW-WORD-2026-07-26.md.
    /// </remarks>
    public sealed class XmsgRandomGeneratorTests
    {
        /// <summary>
        /// The constants are the ones the kernel holds next to ZRAND, in octal 012465 and 033031.
        /// </summary>
        [Fact]
        public void Constants_MatchTheKernelWords()
        {
            Assert.Equal(XmsgRandomGenerator.Multiplier, Convert.ToInt32("12465", 8));
            Assert.Equal(XmsgRandomGenerator.Increment, Convert.ToInt32("33031", 8));
            Assert.Equal(53, XmsgRandomGenerator.Low7Multiplier);
            Assert.Equal(25, XmsgRandomGenerator.Low7Increment);
        }

        /// <summary>
        /// The low seven bits form a full-period generator: 128 steps visit every value exactly
        /// once. This is what makes a value's cycle position an allocation ordinal.
        /// </summary>
        [Fact]
        public void Low7Sequence_HasFullPeriod()
        {
            bool[] seen = new bool[128];
            int draw = 0;

            for (int i = 0; i < 128; i++)
            {
                Assert.False(seen[draw], "value repeated before the cycle completed");
                seen[draw] = true;
                draw = XmsgRandomGenerator.Step(draw);
            }

            Assert.Equal(0, draw);
        }

        /// <summary>
        /// The kernel redraws 0 and 127, so a minted random part is always 1..126.
        /// </summary>
        [Fact]
        public void Next_NeverReturnsTheRejectedValues()
        {
            XmsgRandomGenerator generator = new XmsgRandomGenerator(0x1234);

            for (int i = 0; i < 2000; i++)
            {
                int draw = generator.Next();

                Assert.InRange(draw, XmsgRandomGenerator.MinimumDraw, XmsgRandomGenerator.MaximumDraw);
                Assert.True(XmsgRandomGenerator.IsMintable(draw));
            }
        }

        /// <summary>
        /// The two rejected values are exactly 0 and 127, and nothing else is rejected.
        /// </summary>
        [Fact]
        public void IsMintable_RejectsOnlyZeroAndOneTwentySeven()
        {
            Assert.False(XmsgRandomGenerator.IsMintable(0));
            Assert.False(XmsgRandomGenerator.IsMintable(127));

            for (int draw = 1; draw <= 126; draw++)
            {
                Assert.True(XmsgRandomGenerator.IsMintable(draw));
            }
        }

        /// <summary>
        /// The full 16-bit generator and the low-seven-bit shortcut agree step for step.
        /// </summary>
        /// <remarks>
        /// The shortcut is only valid because the high bits of a power-of-two-modulus generator
        /// never feed back into the low ones; this test is what protects that claim.
        /// </remarks>
        [Fact]
        public void Low7Shortcut_TracksTheFullGenerator()
        {
            ushort seed = 0x4321;
            int shortcut = seed & 0x7F;

            for (int i = 0; i < 500; i++)
            {
                seed = (ushort)((seed * XmsgRandomGenerator.Multiplier) + XmsgRandomGenerator.Increment);
                shortcut = XmsgRandomGenerator.Step(shortcut);

                Assert.Equal(seed & 0x7F, shortcut);
            }
        }

        /// <summary>
        /// The ten consecutive draws observed in the capture corpus really are consecutive outputs
        /// of this generator.
        /// </summary>
        /// <remarks>
        /// These are the low seven bits of ten wire port words taken from the HDLC captures. That
        /// an unbroken run of ten lands in sequence is the evidence that the wire "low7" is this
        /// generator's output and nothing else.
        /// </remarks>
        [Fact]
        public void ObservedCaptureRun_IsConsecutiveGeneratorOutput()
        {
            int[] observed = new int[] { 57, 102, 55, 124, 69, 98, 99, 24, 17, 30 };

            for (int i = 1; i < observed.Length; i++)
            {
                Assert.Equal(observed[i], XmsgRandomGenerator.Step(observed[i - 1]));
            }
        }

        /// <summary>
        /// Every distinct random part observed on the wire is one the kernel could have minted.
        /// </summary>
        [Fact]
        public void ObservedCaptureValues_AreAllMintable()
        {
            int[] observed = new int[]
            {
                8, 10, 17, 19, 23, 24, 25, 30, 37, 43, 55, 57,
                65, 66, 69, 70, 77, 82, 86, 98, 99, 100, 102, 124,
            };

            for (int i = 0; i < observed.Length; i++)
            {
                Assert.True(XmsgRandomGenerator.IsMintable(observed[i]));
            }
        }

        /// <summary>
        /// The step distance between two draws counts the allocations in between.
        /// </summary>
        [Fact]
        public void Distance_CountsGeneratorSteps()
        {
            Assert.Equal(1, XmsgRandomGenerator.Distance(57, 102));
            Assert.Equal(2, XmsgRandomGenerator.Distance(57, 55));
            Assert.Equal(9, XmsgRandomGenerator.Distance(57, 30));

            // A value is a full lap away from itself.
            Assert.Equal(XmsgRandomGenerator.CycleLength, XmsgRandomGenerator.Distance(57, 57));
        }

        /// <summary>
        /// Skipping the rejected values never lands on one.
        /// </summary>
        [Fact]
        public void NextAccepted_SkipsRejectedValues()
        {
            int draw = 1;

            for (int i = 0; i < 300; i++)
            {
                draw = XmsgRandomGenerator.NextAccepted(draw);
                Assert.True(XmsgRandomGenerator.IsMintable(draw));
            }
        }

        /// <summary>
        /// A magic number carrying a rejected random part is not an allocated port - which is how
        /// the XROUT protocol sink at port 0 is told apart from a minted identifier.
        /// </summary>
        [Fact]
        public void MagicNumber_ReportsWhetherItsRandomIsMintable()
        {
            Assert.True(XmsgMagicNumber.Create(102, 5, 82).HasMintableRandom);
            Assert.False(XmsgMagicNumber.Create(102, 0, 0).HasMintableRandom);
            Assert.False(XmsgMagicNumber.Create(102, 5, 127).HasMintableRandom);
        }
    }
}
