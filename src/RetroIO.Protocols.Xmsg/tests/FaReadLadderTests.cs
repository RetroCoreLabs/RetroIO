//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.Fa;

using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// The read ladder and its request bodies, checked against every byte a real client sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From <c>DOC/captures/ND-TO-ND-WRITE-2026-08-10/readback-10-blocks.pcapng</c>, decoded with
    /// the <c>fa_view.py</c> beside it. D102 reads <c>BIGPSH3:TXT</c> from D100 - both real ND
    /// machines, no C# anywhere in the path, which is what makes this a reference rather than a
    /// recording of our own behaviour.
    /// </para>
    /// <para>
    /// The expected list is EVERY request in the capture, not a sample. A partial expectation is
    /// how the write ladder came to be wrong for days while its test passed: it asserted the
    /// opening four operations because only the start of the capture had been read, so a client
    /// built from it would have stopped without ever closing the file.
    /// </para>
    /// </remarks>
    public sealed class FaReadLadderTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Creates the fixture.
        /// </summary>
        /// <param name="output">
        /// Where to write the comparison, so a failure shows what the capture held.
        /// </param>
        public FaReadLadderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The whole ladder the captured reader sent, in order.
        /// </summary>
        [Fact]
        public void TheLadderIsEveryOperationTheCapturedReaderSent()
        {
            // Ten blocks: the file is 20400 bytes and the block size is 2048.
            FaOperation[] expected = new FaOperation[]
            {
                FaOperation.ReserveFileEntry,   // 92 0002  seq 0001
                FaOperation.OpenFile,           // 92 0005  seq 0002
                FaOperation.SetBlockSize,       // 92 0007  seq 0003
                FaOperation.SiiiSpecial,        // 92 000C  seq 0004, sub-function 0021
                FaOperation.ReadFile,           // 92 0008  seq 0005  position 0
                FaOperation.ReadFile,           // 92 0008  seq 8006  position 1
                FaOperation.ReadFile,           // 92 0008  seq 0007  position 2
                FaOperation.ReadFile,           // 92 0008  seq 8008  position 3
                FaOperation.ReadFile,           // 92 0008  seq 0009  position 4
                FaOperation.ReadFile,           // 92 0008  seq 800A  position 5
                FaOperation.ReadFile,           // 92 0008  seq 000B  position 6
                FaOperation.ReadFile,           // 92 0008  seq 800C  position 7
                FaOperation.ReadFile,           // 92 0008  seq 000D  position 8
                FaOperation.ReadFile,           // 92 0008  seq 800E  position 9
                FaOperation.CloseFile,          // 92 0006  seq 000F
                FaOperation.ReleaseFileEntry,   // 92 0003  seq 0010
            };

            FaOperation[] actual = FaReadLadder.ForBlockCount(10);

            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }

        /// <summary>
        /// A read ends with the close and the release, and NEVER declares an end of file.
        /// </summary>
        /// <remarks>
        /// The write epilogue opens with <see cref="FaSpecialFunction.SetEndOfFile"/>, which tells
        /// the server how long the file really is. Copying that into the read ladder "for symmetry"
        /// would tell the server to TRUNCATE a file we were only reading, so this pins the absence
        /// rather than leaving it to be noticed.
        /// </remarks>
        [Fact]
        public void AReadNeverDeclaresAnEndOfFile()
        {
            FaOperation[] epilogue = FaReadLadder.Epilogue();

            Assert.Equal(2, epilogue.Length);
            Assert.Equal(FaOperation.CloseFile, epilogue[0]);
            Assert.Equal(FaOperation.ReleaseFileEntry, epilogue[1]);

            // The write's epilogue is the one that carries it, and that must stay true - if the
            // write ever stopped sending it, this test would be pinning the wrong thing.
            FaOperation[] writeEpilogue = FaWriteLadder.Epilogue();
            Assert.Equal(FaOperation.SiiiSpecial, writeEpilogue[0]);
        }

        /// <summary>
        /// The prologue and epilogue length constants match the arrays they describe.
        /// </summary>
        /// <remarks>
        /// The constants exist so the hot path does not allocate an array just to ask its length.
        /// That is only safe while they agree, and this is what keeps them agreeing.
        /// </remarks>
        [Fact]
        public void TheLengthConstantsMatchTheLadderTheyDescribe()
        {
            Assert.Equal(FaReadLadder.PrologueLength, FaReadLadder.Prologue().Length);
            Assert.Equal(FaReadLadder.EpilogueLength, FaReadLadder.Epilogue().Length);
        }

        /// <summary>
        /// The block count comes from the file length in the open reply, and gives the capture's ten.
        /// </summary>
        [Fact]
        public void TheCapturedFileLengthGivesTheCapturedNumberOfBlocks()
        {
            // 0x00004FB0 from the open reply.
            Assert.Equal(20400, 0x4FB0);

            Assert.Equal(10, FaReadLadder.BlockCountForLength(0x4FB0));

            // The boundaries either side, so an off-by-one in the rounding cannot hide.
            Assert.Equal(1, FaReadLadder.BlockCountForLength(1));
            Assert.Equal(1, FaReadLadder.BlockCountForLength(2048));
            Assert.Equal(2, FaReadLadder.BlockCountForLength(2049));
            Assert.Equal(10, FaReadLadder.BlockCountForLength(20480));
            Assert.Equal(11, FaReadLadder.BlockCountForLength(20481));

            // An empty file still costs one block - the ladder has no way to say "no content".
            Assert.Equal(1, FaReadLadder.BlockCountForLength(0));
        }

        /// <summary>
        /// The 0x8000 sequence bit ALTERNATES across block requests in both captured directions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the test that turned a long-standing UNKNOWN into a rule. The two captures start
        /// their block requests at different parities, which is exactly what rules out "the parity
        /// of the sequence number" and leaves alternation:
        /// </para>
        /// <code>
        /// write blocks   0004 8005 0006 8007 0008 8009 000A 800B 000C
        /// read blocks    0005 8006 0007 8008 0009 800A 000B 800C 000D 800E
        /// </code>
        /// <para>
        /// Nothing in the library SENDS the bit - our own push omits it across ten blocks and
        /// completes, verified by a second real ND - so this pins the reading of the captures, not
        /// our behaviour.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheBlockSequenceBitAlternatesInBothCapturedDirections()
        {
            // The sequence numbers as they appear on the wire, block by block.
            ushort[] capturedReadBlockSequences = new ushort[]
            {
                0x0005, 0x8006, 0x0007, 0x8008, 0x0009,
                0x800A, 0x000B, 0x800C, 0x000D, 0x800E,
            };

            ushort[] capturedWriteBlockSequences = new ushort[]
            {
                0x0004, 0x8005, 0x0006, 0x8007, 0x0008, 0x8009, 0x000A, 0x800B, 0x000C,
            };

            CheckAlternation("read", capturedReadBlockSequences);
            CheckAlternation("write", capturedWriteBlockSequences);

            // And the thing that makes it alternation rather than parity: block 0 of the read is an
            // ODD sequence with the bit CLEAR, while block 1 of the write is an ODD sequence with
            // the bit SET. One rule cannot be "odd means set".
            Assert.Equal(1, capturedReadBlockSequences[0] & 1);
            Assert.Equal(0, capturedReadBlockSequences[0] & FaReadLadder.AlternatingBlockSequenceBit);
            Assert.Equal(1, capturedWriteBlockSequences[1] & 1);
            Assert.Equal(
                FaReadLadder.AlternatingBlockSequenceBit,
                capturedWriteBlockSequences[1] & FaReadLadder.AlternatingBlockSequenceBit);
        }

        /// <summary>
        /// Checks one captured run of block sequences against the alternation rule.
        /// </summary>
        /// <param name="which">
        /// Which capture, for the failure message.
        /// </param>
        /// <param name="sequences">
        /// The sequence numbers as captured, block by block.
        /// </param>
        private void CheckAlternation(string which, ushort[] sequences)
        {
            for (int block = 0; block < sequences.Length; block++)
            {
                bool onTheWire =
                    (sequences[block] & FaReadLadder.AlternatingBlockSequenceBit) != 0;
                bool byTheRule = FaReadLadder.CapturedSequenceBitSetForBlock(block);

                _output.WriteLine(
                    which + " block " + block + ": wire 0x" + sequences[block].ToString("X4")
                        + " bit " + (onTheWire ? "set" : "clear")
                        + ", rule says " + (byTheRule ? "set" : "clear"));

                Assert.Equal(byTheRule, onTheWire);

                // The count underneath the flag must still be the plain step number.
                ushort withoutBit =
                    (ushort)(sequences[block] & ~FaReadLadder.AlternatingBlockSequenceBit);
                Assert.Equal(sequences[0] + block, withoutBit);
            }
        }

        /// <summary>
        /// The read open request is the captured bytes exactly.
        /// </summary>
        /// <remarks>
        /// The whole field from the capture, taken from the QFORM body of D102's step
        /// <c>8100</c>:
        /// <code>
        /// 92 0005 92 0002  F2 0002 BD "BIGPSH3:TXT'."  F2 00FF
        /// </code>
        /// This asserts the part <see cref="FaReadRequests.OpenFile"/> builds - everything after
        /// the operation and sequence pair.
        /// </remarks>
        [Fact]
        public void TheReadOpenRequestIsTheCapturedBytes()
        {
            byte[] expected = FromHex("f20002" + "bd" + "424947505348333a545854272e" + "f200ff");

            byte[] actual = FaReadRequests.OpenFile("BIGPSH3:TXT");

            AssertBytesEqual(expected, actual);
        }

        /// <summary>
        /// A read open carries NO access selector, where a write open does.
        /// </summary>
        /// <remarks>
        /// The difference our own <see cref="FaOperation.OpenFile"/> remark predicted before this
        /// capture existed: "the access mode rides under field selector 3 and is omitted entirely
        /// for read". Sending the write's selector 3 on a read would be describing a write.
        /// </remarks>
        [Fact]
        public void AReadOpenOmitsTheAccessSelectorThatAWriteOpenCarries()
        {
            byte[] read = FaReadRequests.OpenFile("BIGPSH3:TXT");
            byte[] write = FaWriteRequests.OpenFile("\"WRTEST1:OUT\"", 'W');

            // F2 0003 - the selector a write uses for the access mode.
            byte[] accessSelector = FromHex("f20003");

            Assert.True(Contains(write, accessSelector),
                "the WRITE open should carry selector 3; if it no longer does, this test is "
                + "pinning the wrong difference");
            Assert.False(Contains(read, accessSelector),
                "a READ open must not carry selector 3 - the capture has nothing between the "
                + "file specification and the end-of-list");
        }

        /// <summary>
        /// The file-information request is the captured bytes exactly.
        /// </summary>
        /// <remarks>
        /// From D102's step <c>8300</c>, and identically from
        /// <c>DOC/captures/FA-READ-WRITE-2026-08-04/capture-read.txt</c> line 63 two months
        /// earlier:
        /// <code>
        /// 92 000C 92 0004  F2 0001 92 0021  F2 00FF
        /// </code>
        /// </remarks>
        [Fact]
        public void TheFileInformationRequestIsTheCapturedBytes()
        {
            byte[] expected = FromHex("f20001" + "920021" + "f200ff");

            byte[] actual = FaReadRequests.FileInformation();

            AssertBytesEqual(expected, actual);
        }

        /// <summary>
        /// The block request is the captured bytes exactly, for the first and last block.
        /// </summary>
        /// <remarks>
        /// From D102's steps <c>8400</c> and <c>8d00</c>:
        /// <code>
        /// 92 0008 92 0005  F2 0001 A4 00000000  F2 00FF     block 0
        /// 92 0008 92 800E  F2 0001 A4 00000009  F2 00FF     block 9
        /// </code>
        /// The position is a 32-bit <c>A4</c> value and it counts BLOCKS, not bytes.
        /// </remarks>
        [Fact]
        public void TheBlockRequestIsTheCapturedBytes()
        {
            AssertBytesEqual(
                FromHex("f20001" + "a400000000" + "f200ff"), FaReadRequests.ReadFile(0));

            AssertBytesEqual(
                FromHex("f20001" + "a400000009" + "f200ff"), FaReadRequests.ReadFile(9));
        }

        /// <summary>
        /// The open reply's length field is read back as the file's true size.
        /// </summary>
        /// <remarks>
        /// The whole captured reply from D100's step <c>8100</c>. This is the field that tells a
        /// reader where the file ends, because the last block arrives PADDED and there is no end
        /// marker anywhere in the transfer.
        /// </remarks>
        [Fact]
        public void TheOpenReplyCarriesTheFileLengthAndTheFileNumber()
        {
            byte[] reply = FromHex(
                "920005" + "920002" + "f20002" + "a20040" + "f20003" + "a400004fb0" + "f200ff");

            ushort serial;
            ushort fileNumber;
            long byteLength;
            bool read = FaOpenFileCodec.TryReadReply(reply, out serial, out fileNumber, out byteLength);

            Assert.True(read);
            Assert.Equal(2, serial);
            Assert.Equal(0x0040, fileNumber);
            Assert.Equal(20400, byteLength);

            // And that length is exactly the file that had been pushed, which is what makes this a
            // cross-check rather than a restatement of the same bytes.
            Assert.Equal(10, FaReadLadder.BlockCountForLength(byteLength));
        }

        /// <summary>
        /// An open reply with no length is read, not rejected.
        /// </summary>
        /// <remarks>
        /// The captured WRITE open reply genuinely carries only the file number:
        /// <c>92 0005 92 0002 F2 0002 A2 0040 F2 00FF</c>. Failing to parse it would fail a
        /// perfectly good reply - it is the CALLER's business to decide whether it needed a length.
        /// </remarks>
        [Fact]
        public void AnOpenReplyWithoutALengthStillParses()
        {
            byte[] reply = FromHex("920005" + "920002" + "f20002" + "a20040" + "f200ff");

            ushort serial;
            ushort fileNumber;
            long byteLength;
            bool read = FaOpenFileCodec.TryReadReply(reply, out serial, out fileNumber, out byteLength);

            Assert.True(read);
            Assert.Equal(0x0040, fileNumber);
            Assert.Equal(0, byteLength);
        }

        /// <summary>
        /// A filespec too long for the open request is refused before it reaches the wire.
        /// </summary>
        /// <remarks>
        /// Thirteen characters is the read's ceiling: fifteen bytes of compact string less the
        /// apostrophe and the suffix. The WRITE ceiling is eleven, because its quotes cost two more
        /// - which is the trap for anything that pushes a file and later pulls it back.
        /// </remarks>
        [Fact]
        public void AFileSpecTooLongForTheOpenRequestIsRefused()
        {
            // Thirteen characters - the most a read can carry.
            byte[] atTheLimit = FaReadRequests.OpenFile("ABCDEFGH:TXT1");
            Assert.NotNull(atTheLimit);

            // Fourteen. What a real client does here is UNKNOWN, so we refuse rather than invent
            // an encoding no capture has ever shown.
            Assert.Throws<ArgumentException>(
                delegate { FaReadRequests.OpenFile("ABCDEFGHI:TXT1"); });
        }

        /// <summary>
        /// Whether one byte sequence appears inside another.
        /// </summary>
        /// <param name="haystack">
        /// The bytes to search.
        /// </param>
        /// <param name="needle">
        /// The bytes to look for.
        /// </param>
        /// <returns>
        /// <c>true</c> when <paramref name="needle"/> appears in
        /// <paramref name="haystack"/>.
        /// </returns>
        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (int at = 0; at + needle.Length <= haystack.Length; at++)
            {
                bool same = true;
                for (int i = 0; i < needle.Length; i++)
                {
                    if (haystack[at + i] != needle[i])
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Turns a hex string into bytes.
        /// </summary>
        /// <param name="hex">
        /// The hex, without separators.
        /// </param>
        /// <returns>
        /// The bytes.
        /// </returns>
        private static byte[] FromHex(string hex)
        {
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        /// <summary>
        /// Compares two byte arrays and reports both in hex when they differ.
        /// </summary>
        /// <param name="expected">
        /// What the capture held.
        /// </param>
        /// <param name="actual">
        /// What we built.
        /// </param>
        private void AssertBytesEqual(byte[] expected, byte[] actual)
        {
            _output.WriteLine("capture: " + ToHex(expected));
            _output.WriteLine("ours   : " + ToHex(actual));

            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }

        /// <summary>
        /// The diagnostic probe ladder reserves an entry and sets the block size, and opens nothing.
        /// </summary>
        /// <remarks>
        /// The whole point of the probe is the ABSENCE of the open - it is what separates "no file
        /// is open" from "an earlier operation failed" in the <c>A2 4104</c> question. If an open
        /// ever creeps back into this ladder the experiment silently stops measuring anything, and
        /// the run would look exactly like the ordinary refusal we already have.
        /// </remarks>
        [Fact]
        public void TheProbeLadderHasNoOpen()
        {
            FaOperation[] probe = FaReadLadder.ProbeWithoutOpen();

            Assert.Equal(2, probe.Length);
            Assert.Equal(FaOperation.ReserveFileEntry, probe[0]);
            Assert.Equal(FaOperation.SetBlockSize, probe[1]);

            for (int i = 0; i < probe.Length; i++)
            {
                Assert.NotEqual(FaOperation.OpenFile, probe[i]);
            }
        }

        /// <summary>
        /// The probe ladder does not disturb the real prologue.
        /// </summary>
        /// <remarks>
        /// Guards the reason the probe is a SEPARATE ladder rather than a shortened prologue:
        /// <see cref="FaReadLadder.PrologueLength"/> feeds the block-index arithmetic on the
        /// transfer path, so it must keep counting the real four steps.
        /// </remarks>
        [Fact]
        public void TheProbeLeavesTheRealPrologueAlone()
        {
            Assert.Equal(4, FaReadLadder.PrologueLength);
            Assert.Equal(FaReadLadder.PrologueLength, FaReadLadder.Prologue().Length);
            Assert.Contains(FaOperation.OpenFile, FaReadLadder.Prologue());
        }

        /// <summary>
        /// Renders bytes as hex, for a failure message.
        /// </summary>
        /// <param name="bytes">
        /// The bytes.
        /// </param>
        /// <returns>
        /// The hex text.
        /// </returns>
        private static string ToHex(byte[] bytes)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                text.Append(bytes[i].ToString("x2"));
            }

            return text.ToString();
        }
    }
}
