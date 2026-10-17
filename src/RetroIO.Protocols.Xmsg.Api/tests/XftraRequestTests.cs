//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Api.Tests
{
    /// <summary>
    /// Checks the <c>*XFTRA</c> request builder against the bytes a real SINTRAN put on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expected bytes are the parameter area of frame 1 of
    /// <c>append-remote-batch-102-to-100-2026-07-31.pcapng</c>, node 102 to node 100, both
    /// SINTRAN K, written up in <c>DOC/XMSG-APPEND-REMOTE-BATCH-CAPTURED-2026-07-31.md</c>. They
    /// are a RECORDING, not our own output - which is the only kind of expected value that can
    /// catch a builder that is confidently wrong.
    /// </para>
    /// <para>
    /// This matters here more than usual: a client that copies parameter 11 from the TRANSFER-FILE
    /// capture can only ever transfer, and would look correct in every test written from its own
    /// output.
    /// </para>
    /// </remarks>
    public sealed class XftraRequestTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink, used to print the built bytes so a mismatch shows WHAT was built
        /// rather than only that it differed.
        /// </param>
        public XftraRequestTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The captured parameter area, tag by tag, exactly as node 102 sent it.
        /// </summary>
        /// <remarks>
        /// <code>
        /// ff 06 2a 58 46 54 52 41                    "*XFTRA"
        /// fe 04 44 31 30 30                          "D100"
        /// f4 06 53 59 53 54 45 4d                    "SYSTEM"
        /// 0d 02 0000                                 password absent -> INTEGER 0
        /// f8 0c 41 52 42 54 45 53 54 3a 53 59 4d 42  "ARBTEST:SYMB"
        /// f7 04 53 59 4d 42                          "SYMB"
        /// 0a 02 0400                                 1024
        /// 0b 02 0003                                 operation 3 = APPEND-REMOTE-BATCH
        /// f0 0b 41 52 42 4f 55 54 3a 53 59 4d 42 00  "ARBOUT:SYMB" plus the odd-length pad
        /// </code>
        /// </remarks>
        private const string CapturedParameters =
            "FF062A58465452"
            + "41"
            + "FE0444313030"
            + "F40653595354454D"
            + "0D020000"
            + "F80C415242544553543A53594D42"
            + "F70453594D42"
            + "0A020400"
            + "0B020003"
            + "F00B4152424F55543A53594D4200";

        /// <summary>
        /// The builder reproduces the captured batch request byte for byte.
        /// </summary>
        [Fact]
        public void AppendRemoteBatch_MatchesTheCapturedRequest()
        {
            XroutMessage message = XftraRequests.AppendRemoteBatch(
                serial: 0x1B,
                remoteSystem: "D100",
                remoteUser: "SYSTEM",
                batchInputFile: "ARBTEST:SYMB",
                batchOutputFile: "ARBOUT:SYMB",
                password: null);

            byte[] all = message.ToArray();

            // The message begins with the XROUT header (serial, service, declared length); the
            // parameters follow it. Compare the parameter area only - the header's declared length
            // is checked by the guard below, and the frame envelope is a separate concern.
            string parameters = System.Convert.ToHexString(all, XroutMessage.HeaderSize, all.Length - XroutMessage.HeaderSize);

            _output.WriteLine("whole message: " + System.Convert.ToHexString(all));
            _output.WriteLine("parameters   : " + parameters);
            _output.WriteLine("captured     : " + CapturedParameters);

            Assert.Equal(CapturedParameters, parameters);
        }

        /// <summary>
        /// The operation is what separates the two commands, so it is asserted on its own rather
        /// than only inside the byte comparison.
        /// </summary>
        /// <remarks>
        /// Parameter 11 is tagged <c>0B</c> and carries 3 for a batch and 2 for a transfer. This
        /// is the field a client copied from a transfer capture would get wrong while every other
        /// byte looked right.
        /// </remarks>
        [Fact]
        public void TheOperationParameterSeparatesBatchFromTransfer()
        {
            XroutMessage batch = XftraRequests.AppendRemoteBatch(
                0x1B, "D100", "SYSTEM", "ARBTEST:SYMB", "ARBOUT:SYMB", null);
            XroutMessage transfer = XftraRequests.TransferFile(
                0x1B, "D100", "SYSTEM", "ARBTEST:SYMB", null);

            Assert.Contains("0B020003", System.Convert.ToHexString(batch.ToArray()));
            Assert.Contains("0B020002", System.Convert.ToHexString(transfer.ToArray()));

            Assert.Equal(3, (ushort)XftraOperation.AppendRemoteBatch);
            Assert.Equal(2, (ushort)XftraOperation.TransferFile);
        }

        /// <summary>
        /// A transfer carries no parameter 16 - the transfer capture has nothing above 13.
        /// </summary>
        [Fact]
        public void TransferFile_SendsNoOutputFileParameter()
        {
            XroutMessage transfer = XftraRequests.TransferFile(
                0x1B, "D100", "SYSTEM", "ARBTEST:SYMB", null);

            Assert.DoesNotContain("F00B", System.Convert.ToHexString(transfer.ToArray()));
        }

        /// <summary>
        /// An absent password is an INTEGER 0, not an empty string - the one place the parameter
        /// changes TYPE with its value.
        /// </summary>
        /// <remarks>
        /// INFERRED from one observation: the batch capture omitted the password and carries
        /// <c>0D 02 0000</c>, where the transfer capture carries a string. A non-empty password
        /// has not been tested on the batch path.
        /// </remarks>
        [Fact]
        public void AbsentPassword_IsIntegerZeroRatherThanAnEmptyString()
        {
            XroutMessage withoutPassword = XftraRequests.AppendRemoteBatch(
                0x1B, "D100", "SYSTEM", "ARBTEST:SYMB", "ARBOUT:SYMB", null);
            XroutMessage withPassword = XftraRequests.AppendRemoteBatch(
                0x1B, "D100", "SYSTEM", "ARBTEST:SYMB", "ARBOUT:SYMB", "SECRET");

            // Integer parameter 13 is tagged 0D; the string form is tagged 256-13 = F3.
            Assert.Contains("0D020000", System.Convert.ToHexString(withoutPassword.ToArray()));
            Assert.Contains("F306534543524554", System.Convert.ToHexString(withPassword.ToArray()));
        }
    }
}
