//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Servers.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Node.Tests
{
    /// <summary>
    /// Pins each <see cref="FaServerStatus"/> number to the SINTRAN error it actually names.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this test exists</b></para>
    /// These are not our numbers. The client prints them through SINTRAN's own error table, so a
    /// value chosen because the English word we had in mind sounded right will make a real machine
    /// print something false.
    /// <para>
    /// That is not hypothetical - it is what happened. Three of these were picked that way, and when
    /// they were finally checked against ND's table (<c>systemerrors.h</c>, NDIX release 3) all
    /// three named something else. The worst told a client "END OF FILE" when we had failed to parse
    /// its request.
    /// </para>
    /// <para>
    /// So each number is asserted here beside ND's text for it. Changing a value fails this test,
    /// which is the point: the next person has to look the replacement up rather than judge it by
    /// how it reads.
    /// </para>
    /// </remarks>
    public sealed class FaServerStatusMeaningTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaServerStatusMeaningTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Every status this server can send names a real SINTRAN condition, never ND's filler.
        /// </summary>
        /// <remarks>
        /// This is the check the earlier tests could not make. They pin the NUMBERS; this one asks
        /// ND's own table what each number MEANS, and fails if any of them is one of the twelve
        /// codes ND fills with "ERROR CODE OUTSIDE RANGE" - which a real machine would print
        /// verbatim, telling the user nothing.
        /// <para>
        /// It also prints the wording, so a reviewer can see what a client is actually shown
        /// without going to look it up.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryStatusWeSendNamesARealSintranCondition()
        {
            Array values = Enum.GetValues(typeof(FaServerStatus));

            for (int i = 0; i < values.Length; i++)
            {
                FaServerStatus status = (FaServerStatus)values.GetValue(i)!;

                // Ok is never written to the wire - a success omits the field entirely.
                if (status == FaServerStatus.Ok)
                {
                    continue;
                }

                int code = (int)status;
                _output.WriteLine($"{status} = {code}: {SintranErrors.Describe(code)}");

                Assert.True(
                    SintranErrors.IsMeaningful(code),
                    $"{status} = {code} is one of ND's filler codes, which prints as nonsense");
            }
        }

        /// <summary>
        /// The three codes read off real captures, each matched against what the terminal printed.
        /// </summary>
        [Fact]
        public void TheCapturedCodesKeepTheirMeasuredValues()
        {
            // "NO SUCH FILE NAME" - capture-open-error.txt, terminal printed exactly that.
            Assert.Equal(46, (int)FaServerStatus.NoSuchFile);

            // "OBJECT INDEX TOO LARGE" - ends every directory walk, capture-list-files.txt.
            Assert.Equal(197, (int)FaServerStatus.EndOfDirectory);

            // "WRONG PASSWORD" - the 2026-07-29 access capture.
            Assert.Equal(48, (int)FaServerStatus.WrongPassword);
        }

        /// <summary>
        /// The three codes we chose ourselves sit on SINTRAN numbers that mean what we intend.
        /// </summary>
        /// <remarks>
        /// Still NOT captured - a real COSMOS server may well answer differently. What this test
        /// guarantees is only that the number does not name a DIFFERENT condition, which is the
        /// mistake that was actually made.
        /// </remarks>
        [Fact]
        public void TheChosenCodesNameTheConditionTheyStandFor()
        {
            // 129 "ILLEGAL FUNCTION CODE" - was 45, which is "RESERVED SPACE ALREADY USED".
            Assert.Equal(129, (int)FaServerStatus.NotSupported);

            // 211 "FILE-ACCESS INTERNAL ERROR; INVALID PARAMETER VALUE" - was 3, "END OF FILE".
            Assert.Equal(211, (int)FaServerStatus.BadRequest);

            // 97 "TRANSFER ERROR" - was 2, "BAD FILE NUMBER", which blamed the caller.
            Assert.Equal(97, (int)FaServerStatus.StoreError);
        }

        /// <summary>
        /// No status we send is one of the SINTRAN codes that mean the transfer ended normally.
        /// </summary>
        /// <remarks>
        /// This is the specific defect that was live: <c>BadRequest</c> was 3, "END OF FILE". A
        /// client reading a file and refused with 3 would stop and report SUCCESS. Any refusal
        /// carrying a normal-completion code is a silent data-loss bug, so it is asserted directly
        /// rather than left to review.
        /// </remarks>
        [Fact]
        public void NoRefusalUsesANormalCompletionCode()
        {
            // SINTRAN 3 = "END OF FILE", 0 = success. A refusal must never carry either.
            FaServerStatus[] refusals =
            {
                FaServerStatus.NoSuchFile,
                FaServerStatus.WrongPassword,
                FaServerStatus.NotSupported,
                FaServerStatus.BadRequest,
                FaServerStatus.StoreError,
            };

            for (int i = 0; i < refusals.Length; i++)
            {
                Assert.NotEqual(3, (int)refusals[i]);
                Assert.NotEqual(0, (int)refusals[i]);
            }

            // EndOfDirectory is deliberately NOT in that list: ending a directory walk IS a normal
            // completion, and 197 is the number a real server sends for it.
            Assert.Equal(197, (int)FaServerStatus.EndOfDirectory);
        }
    }
}
