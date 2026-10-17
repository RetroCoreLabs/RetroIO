//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Qform;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Every distinct request a real client sent while writing a file, parsed with our own reader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bodies are taken byte for byte out of
    /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-write.txt</c>, node 100 writing to node
    /// 102. Node 100 is the asker - its conversation word is <c>0x0044</c>.
    /// </para>
    /// <para>
    /// This is the ASKER's side. The reader had been exercised on server traffic; these are the
    /// requests a client has to produce, so parsing them is what shows the reader handles both
    /// ends, and it is the structure any request builder has to reproduce.
    /// </para>
    /// </remarks>
    public sealed class FaCapturedWriteRequestTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink, used to print the fields each request carries.
        /// </param>
        public FaCapturedWriteRequestTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// One captured request.
        /// </summary>
        private sealed class Captured
        {
            /// <summary>
            /// What the request asks for.
            /// </summary>
            public FaOperation Operation;

            /// <summary>
            /// The exchange sequence word, including the high bit where the capture set one.
            /// </summary>
            public ushort Sequence;

            /// <summary>
            /// The whole message body, from the message type onwards.
            /// </summary>
            public string Hex = string.Empty;
        }

        /// <summary>
        /// The distinct requests of the captured write, in wire order.
        /// </summary>
        /// <remarks>
        /// The eight repeated WriteFile requests differ only in their block number and sequence, so
        /// one of them stands for all nine here; <c>FaWriteLadder</c> holds the repetition.
        /// </remarks>
        private static Captured[] Requests()
        {
            return new Captured[]
            {
                new Captured
                {
                    Operation = FaOperation.ReserveFileEntry,
                    Sequence = 0x0001,
                    Hex = "07F0004480000001920002920001F20001A207D0F200028C06920001920001F2"
                        + "0003BD42414B3034202053595354454DF200048C38B01053595354454D2700000"
                        + "0000000000000E180B01000000000000000000000000000000000B01000000000"
                        + "000000000000000000000000F200FF",
                },
                new Captured
                {
                    Operation = FaOperation.OpenFile,
                    Sequence = 0x0002,
                    Hex = "07F000448100D761920005920002F20002BF22575254455354313A4F55542227"
                        + "57F20003920001F200FF",
                },
                new Captured
                {
                    Operation = FaOperation.SetBlockSize,
                    Sequence = 0x0003,
                    Hex = "07F000448200D761920007920003F20001A20800F200FF0A",
                },
                new Captured
                {
                    Operation = FaOperation.WriteFile,
                    Sequence = 0x0004,
                    Hex = "07F000448300D761920009920004F20001A400000000F200FFBF",
                },
                new Captured
                {
                    Operation = FaOperation.SiiiSpecial,
                    Sequence = 0x000D,
                    Hex = "07F000449E00D76192000C92000DF2000192003BF200028C8005A4000045F0F200FF",
                },
                new Captured
                {
                    Operation = FaOperation.CloseFile,
                    Sequence = 0x000E,
                    Hex = "07F000449F00D76192000692000EF200FF0A",
                },
                new Captured
                {
                    Operation = FaOperation.ReleaseFileEntry,
                    Sequence = 0x000F,
                    Hex = "07F00044A000D76192000392000FF200FF0E",
                },
            };
        }

        private static byte[] Bytes(string hex)
        {
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return result;
        }

        /// <summary>
        /// Each captured request carries the envelope the codec expects.
        /// </summary>
        /// <remarks>
        /// Reading the envelope with <c>TryReadEnvelope</c> is what proves the writer and the
        /// reader agree on a real client's bytes, not just on ours.
        /// </remarks>
        [Fact]
        public void EveryCapturedRequestHasAReadableEnvelope()
        {
            Captured[] requests = Requests();

            for (int i = 0; i < requests.Length; i++)
            {
                byte[] body = Bytes(requests[i].Hex);

                FaMessageType messageType;
                ushort conversation;
                byte sequenceByte;
                ushort sessionToken;

                Assert.True(FaExchangeCodec.TryReadEnvelope(
                    body, out messageType, out conversation, out sequenceByte, out sessionToken));

                Assert.Equal(FaMessageType.Request, messageType);

                // The asker's own conversation word, the same on every request of the session.
                Assert.Equal(0x0044, conversation);

                _output.WriteLine(requests[i].Operation + ": counter=0x" + sequenceByte.ToString("X2")
                    + " token=0x" + sessionToken.ToString("X4"));
            }
        }

        /// <summary>
        /// The first exchange carries the first-exchange token, and the rest carry the asker's
        /// steady-state one.
        /// </summary>
        /// <remarks>
        /// Measured here rather than assumed: the reserve carries <c>0x0001</c> and every later
        /// request carries <c>0xD761</c>, which is exactly what
        /// <c>FaExchangeCodec.SessionTokenFirst</c> and <c>SessionTokenAsker</c> say.
        /// </remarks>
        [Fact]
        public void TheFirstExchangeTokenDiffersFromTheRest()
        {
            Captured[] requests = Requests();

            FaMessageType type;
            ushort conversation;
            byte counter;
            ushort token;

            FaExchangeCodec.TryReadEnvelope(
                Bytes(requests[0].Hex), out type, out conversation, out counter, out token);
            Assert.Equal(FaExchangeCodec.SessionTokenFirst, token);

            for (int i = 1; i < requests.Length; i++)
            {
                FaExchangeCodec.TryReadEnvelope(
                    Bytes(requests[i].Hex), out type, out conversation, out counter, out token);
                Assert.Equal(FaExchangeCodec.SessionTokenAsker, token);
            }
        }

        /// <summary>
        /// The operation and sequence read back as the capture recorded them.
        /// </summary>
        [Fact]
        public void TheOperationAndSequenceAreReadable()
        {
            Captured[] requests = Requests();

            for (int i = 0; i < requests.Length; i++)
            {
                byte[] body = Bytes(requests[i].Hex);

                FaOperation operation;
                ushort sequence;
                Assert.True(FaExchangeCodec.TryReadOperation(body, out operation, out sequence));

                Assert.Equal(requests[i].Operation, operation);
                Assert.Equal(requests[i].Sequence, sequence);
            }
        }

        /// <summary>
        /// Our QFORM reader walks every captured request without complaint.
        /// </summary>
        /// <remarks>
        /// The reader's own remarks record that a naive flat walk parsed only 21 of 65 frames,
        /// because constructed values have to be descended into rather than read as top-level
        /// tags. These requests include the reserve, whose body nests two constructed blocks, so
        /// this exercises exactly that.
        /// </remarks>
        [Fact]
        public void OurQformReaderWalksEveryCapturedRequest()
        {
            Captured[] requests = Requests();

            for (int i = 0; i < requests.Length; i++)
            {
                byte[] body = Bytes(requests[i].Hex);
                ReadOnlySpan<byte> qform = new ReadOnlySpan<byte>(
                    body, FaExchangeCodec.QformOffset, body.Length - FaExchangeCodec.QformOffset);

                IReadOnlyList<QformField> fields = QformReader.Read(qform);

                _output.WriteLine(requests[i].Operation + " -> " + fields.Count + " fields");
                for (int f = 0; f < fields.Count; f++)
                {
                    _output.WriteLine("    " + fields[f]);
                }

                Assert.True(fields.Count > 0);
            }
        }

        /// <summary>
        /// The block size the captured client asked for is 2048.
        /// </summary>
        /// <remarks>
        /// <c>A2 0800</c> in the SetBlockSize request. Recorded because a builder has to send
        /// something here and 2048 is what a real client chose - not because any other value is
        /// known to fail.
        /// </remarks>
        [Fact]
        public void TheCapturedBlockSizeIs2048()
        {
            byte[] body = Bytes("07F000448200D761920007920003F20001A20800F200FF0A");

            // A2 is a 16-bit value. Found by tag rather than by counting back from the end - the
            // body has a trailing pad byte, and counting past it is how the first version of this
            // assertion landed one byte off.
            int at = -1;
            for (int i = FaExchangeCodec.QformOffset; i < body.Length - 2; i++)
            {
                if (body[i] == 0xA2) { at = i; break; }
            }

            Assert.True(at >= 0, "no A2 value found in the SetBlockSize request");
            Assert.Equal(0x0800, (body[at + 1] << 8) | body[at + 2]);
        }

        /// <summary>
        /// The open request carries the file specification IN QUOTES, with an access letter.
        /// </summary>
        /// <remarks>
        /// <c>"WRTEST1:OUT"'W</c>. The quotes are part of what goes on the wire here, which is
        /// worth pinning: quoting differs per SINTRAN command, and a builder that strips them
        /// because CREATE-FILE wants them bare would break the open.
        /// </remarks>
        [Fact]
        public void TheOpenRequestQuotesTheFileSpecAndCarriesAnAccessLetter()
        {
            byte[] body = Bytes(Requests()[1].Hex);
            string text = System.Text.Encoding.ASCII.GetString(body);

            // The quotes are ON THE WIRE, and the access letter follows the closing quote
            // after an apostrophe: "WRTEST1:OUT"'W
            Assert.Contains("\"WRTEST1:OUT\"", text);
            Assert.Contains("\"'W", text);
            Assert.EndsWith("W", text.Substring(0, text.IndexOf(' ') < 0 ? text.Length : text.Length).TrimEnd(' ').Split('"')[2].Substring(0, 2).Trim('\''));
        }
    }
}
