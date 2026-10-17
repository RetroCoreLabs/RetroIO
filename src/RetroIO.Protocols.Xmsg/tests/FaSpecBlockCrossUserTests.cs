//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Protocols.Xmsg.Fa;
using RetroIO.Protocols.Xmsg.Hdlc;
using RetroIO.Protocols.Xmsg.Tests;
using Xunit;
using Xunit.Abstractions;

namespace RetroIO.Protocols.Xmsg.Tests
{
    /// <summary>
    /// Tests whether the two values the listing codec treats as fixed - the selector-1 value
    /// <c>0x0078</c> and the 62-byte directory/user spec block - are genuinely fixed, or whether they
    /// only looked fixed because a single directory was ever examined.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the trap that has already bitten three times in this decode: a value that is "constant
    /// on every captured frame" where every captured frame belonged to ONE case. <c>LeadingConstant</c>
    /// was documented as an unknown constant until a second operation showed it was the operation code.
    /// </para>
    /// <para>
    /// No new capture is needed to run the check. Two listings against DIFFERENT users on node 100 are
    /// already on disk: user <c>SYSTEM</c> and user <c>SECRET</c>. If the spec block carries the user
    /// name - and the ASCII in it says it does - then the two captures must differ inside the block
    /// while agreeing on everything the codec hard-codes.
    /// </para>
    /// </remarks>
    public sealed class FaSpecBlockCrossUserTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initialises the test.
        /// </summary>
        /// <param name="output">
        /// xUnit output sink.
        /// </param>
        public FaSpecBlockCrossUserTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// A listing of user SYSTEM's directory on node 100.
        /// </summary>
        private const string SystemCapture = "claude-list-files-d100-system-2026-07-29.pcapng";

        /// <summary>
        /// A listing of user SECRET's directory on node 100.
        /// </summary>
        private const string SecretCapture = "fa-access-secret-102-to-100-2026-07-29.pcapng";

        /// <summary>
        /// Frame offset at which the message body begins.
        /// </summary>
        private const int BodyOffsetFullHeader = 28;

        /// <summary>
        /// Requires the selector-1 value to be the same across two different users, and the spec
        /// block to have the same length while differing in content.
        /// </summary>
        /// <remarks>
        /// A pass means both hard-coded values survive a second, independent case. A failure in the
        /// selector-1 assertion would mean <see cref="FaListFilesCodec.RequestSelector1Value"/> is
        /// per-directory and must become a parameter; a failure in the length assertion would mean the
        /// block is variable-length and <see cref="FaListFilesCodec.SpecBlockLength"/> is wrong.
        /// </remarks>
        [Fact]
        public void SpecBlock_IsSameShapeButDifferentContentForTwoUsers()
        {
            List<SpecBlock> forSystem = ReadSpecBlocks(SystemCapture);
            List<SpecBlock> forSecret = ReadSpecBlocks(SecretCapture);

            if (forSystem.Count == 0 || forSecret.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            _output.WriteLine("SYSTEM listing: " + forSystem.Count + " requests");
            _output.WriteLine("SECRET listing: " + forSecret.Count + " requests");

            SpecBlock a = forSystem[0];
            SpecBlock b = forSecret[0];

            _output.WriteLine("SYSTEM selector1=0x" + a.Selector1.ToString("x4")
                + " blockLength=" + a.Block.Length + " text=" + Printable(a.Block));
            _output.WriteLine("SECRET selector1=0x" + b.Selector1.ToString("x4")
                + " blockLength=" + b.Block.Length + " text=" + Printable(b.Block));

            // 1. The selector-1 value survives a second directory.
            Assert.Equal(FaListFilesCodec.RequestSelector1Value, a.Selector1);
            Assert.Equal(FaListFilesCodec.RequestSelector1Value, b.Selector1);

            // 2. The block length survives a second directory - and a second USER NAME LENGTH.
            //    "SYSTEM" is six characters, "SECRET" is six as well, so this pair cannot by itself
            //    rule out a length that tracks the name; that is recorded in the doc, not hidden here.
            Assert.Equal(FaListFilesCodec.SpecBlockLength, a.Block.Length);
            Assert.Equal(FaListFilesCodec.SpecBlockLength, b.Block.Length);

            // 3. The blocks must NOT be identical - they name different users. If they were equal the
            //    block would not be carrying the directory spec at all and section 4 of the doc would
            //    be wrong about what it holds.
            Assert.NotEqual(a.Block, b.Block);

            int differing = 0;
            for (int i = 0; i < a.Block.Length; i++)
            {
                if (a.Block[i] != b.Block[i]) { differing++; }
            }

            _output.WriteLine("bytes differing between the two users: " + differing
                + " of " + FaListFilesCodec.SpecBlockLength);
        }

        /// <summary>
        /// Requires the entry walk - the run of requests beginning with the
        /// <see cref="FaListFilesCodec.FirstEntryCursor"/> request - to share one spec block, and
        /// records that any enquiry BEFORE that one may carry a different block.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>CORRECTION, 2026-07-30.</b> <c>FaListFilesCodec</c> documented the request body as
        /// "byte-identical from one entry to the next except for the serial and the cursor". Measured
        /// over the eleven directory enquiries in the SYSTEM capture, that is only true from the
        /// <c>0xFFFF</c> request onward. The enquiry that PRECEDES it carries the same leading
        /// filespec text but a zero-filled tail where the later ones carry values - so part of the
        /// block is state that appears only once the walk has started, not a caller-supplied constant.
        /// </para>
        /// <para>
        /// What those extra bytes MEAN is not established. They are consistent with a position handle
        /// carried forward from the previous reply, but nothing in these two captures tests that
        /// reading, so the block stays opaque and must be replayed, never synthesised.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(SystemCapture)]
        [InlineData(SecretCapture)]
        public void EntryWalkRequests_ShareOneSpecBlock(string captureName)
        {
            List<SpecBlock> blocks = ReadSpecBlocks(captureName);
            if (blocks.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            _output.WriteLine(captureName + ": " + blocks.Count + " listing requests");

            for (int i = 0; i < blocks.Count; i++)
            {
                _output.WriteLine("  [" + i + "] cursor=0x" + blocks[i].Cursor.ToString("x4")
                    + " block=" + Printable(blocks[i].Block));
                _output.WriteLine("       hex=" + Hex(blocks[i].Block));
            }

            // The selector-1 value is constant across the whole listing, walk or not.
            for (int i = 1; i < blocks.Count; i++)
            {
                Assert.Equal(blocks[0].Selector1, blocks[i].Selector1);
            }

            // Find where the entry walk starts: the request that asks for the first entry.
            int walkStart = -1;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Cursor == FaListFilesCodec.FirstEntryCursor) { walkStart = i; break; }
            }

            Assert.True(walkStart >= 0, "no request carried the first-entry cursor");
            _output.WriteLine("entry walk starts at request " + walkStart);

            // From there on the block is fixed and only the cursor moves.
            for (int i = walkStart + 1; i < blocks.Count; i++)
            {
                Assert.Equal(blocks[walkStart].Block, blocks[i].Block);
            }

            // And the cursor must actually advance 1, 2, 3, ... after the first-entry request, or the
            // comparison above proved nothing about a walk.
            for (int i = walkStart + 1; i < blocks.Count; i++)
            {
                Assert.Equal((ushort)(i - walkStart), blocks[i].Cursor);
            }

            _output.WriteLine("walk length=" + (blocks.Count - walkStart)
                + " cursors ffff then 1.." + (blocks.Count - walkStart - 1));
        }

        /// <summary>
        /// A listing that names ONE file: <c>FILE-STATISTICS</c> rather than <c>LIST-FILES</c>.
        /// </summary>
        private const string FileStatCapture = "claude-file-stat-102-to-100-2026-07-29.pcapng";

        /// <summary>
        /// Locates the FILE name inside the spec block, by comparing a listing that names one file
        /// against listings that name none.
        /// </summary>
        /// <remarks>
        /// <para><b>What this is for</b></para>
        /// Our server ignores the spec block entirely, so a <c>FILE-STATISTICS</c> of one file walks
        /// the whole folder and returns every file in it. Fixing that needs one thing only: where in
        /// the block the file name sits. It does NOT need the other 50-odd bytes decoded, and this
        /// test deliberately does not claim to have decoded them.
        /// <para><b>The method</b></para>
        /// The two <c>LIST-FILES</c> captures carry only a user name - <c>(SYSTEM)</c> and
        /// <c>(SECRET)</c> - because no file was named. The <c>FILE-STATISTICS</c> capture named
        /// one. So whatever text appears in the third block and not in the other two is the file
        /// name, and its offset is what a server needs.
        /// <para><b>Reporting, with one assertion</b></para>
        /// It prints all three blocks side by side and requires only that the file-stat block is a
        /// different length or content from the plain listing's - if it were identical, the block
        /// would not be carrying the file name at all and the whole approach is wrong.
        /// </remarks>
        [Fact]
        public void TheFileStatBlockNamesAFileWhereAPlainListingDoesNot()
        {
            List<SpecBlock> plain = ReadSpecBlocks(SystemCapture);
            List<SpecBlock> named = ReadSpecBlocks(FileStatCapture);

            if (plain.Count == 0 || named.Count == 0)
            {
                _output.WriteLine("recorded .pcapng files absent and XMSG_PCAP_OPTIONAL is set; skipping.");
                return;
            }

            _output.WriteLine("LIST-FILES      requests: " + plain.Count);
            _output.WriteLine("FILE-STATISTICS requests: " + named.Count);

            for (int i = 0; i < named.Count; i++)
            {
                _output.WriteLine("  stat[" + i + "] cursor=0x" + named[i].Cursor.ToString("x4")
                    + " len=" + named[i].Block.Length
                    + " text=" + Printable(named[i].Block));
                _output.WriteLine("           hex=" + Hex(named[i].Block));
            }

            _output.WriteLine("  list[0]  len=" + plain[0].Block.Length
                + " text=" + Printable(plain[0].Block));

            // Byte by byte against the plain listing, so the region carrying the file name shows up
            // as a run rather than having to be eyeballed out of the text.
            byte[] a = plain[0].Block;
            byte[] b = named[0].Block;
            int shared = a.Length < b.Length ? a.Length : b.Length;

            System.Text.StringBuilder marks = new System.Text.StringBuilder(shared);
            for (int i = 0; i < shared; i++)
            {
                marks.Append(a[i] == b[i] ? '.' : 'X');
            }

            _output.WriteLine("  differs = " + marks.ToString());

            // If the two were identical the block would not be carrying the file name, and the plan
            // to read it out of there is wrong from the start.
            Assert.NotEqual(a, b);
        }

        /// <summary>
        /// One request's selector-1 value, spec block and cursor.
        /// </summary>
        private struct SpecBlock
        {
            /// <summary>
            /// The value carried under selector 1.
            /// </summary>
            public ushort Selector1;

            /// <summary>
            /// The opaque directory and user block.
            /// </summary>
            public byte[] Block;

            /// <summary>
            /// The directory cursor.
            /// </summary>
            public ushort Cursor;
        }

        /// <summary>
        /// Pulls the spec block out of every directory-enquiry request in a capture.
        /// </summary>
        /// <param name="captureName">
        /// The capture file name.
        /// </param>
        /// <remarks>
        /// The shape being matched is the one documented on <see cref="FaListFilesCodec"/>:
        /// <c>F2 0001 92 selector1 F2 0002 8C 80 len B0 blockLen block A2 .. A2 cursor</c>.
        /// Requests that do not match are skipped rather than guessed at.
        /// </remarks>
        private static List<SpecBlock> ReadSpecBlocks(string captureName)
        {
            List<SpecBlock> found = new List<SpecBlock>();

            string? path = PcapFiles.File(captureName);
            if (path == null) { return found; }

            IReadOnlyList<LapbFrame> frames = HdlcPcap.ReadFramesInCaptureOrder(path);
            for (int f = 0; f < frames.Count; f++)
            {
                LapbFrame frame = frames[f];
                if (frame.Kind != LapbFrameKind.Information
                    || frame.Info.Length <= BodyOffsetFullHeader)
                {
                    continue;
                }

                ReadOnlySpan<byte> info = frame.Info.Span;
                if (info[3] != (byte)SintranPacketSubtype.Data) { continue; }

                ReadOnlySpan<byte> body = info.Slice(BodyOffsetFullHeader);

                FaOperation operation;
                ushort sequence;
                if (!FaExchangeCodec.TryReadOperation(body, out operation, out sequence)) { continue; }
                if (operation != FaOperation.SiiiSpecial) { continue; }

                SpecBlock parsed;
                if (TryParseSpecBlock(body, out parsed)) { found.Add(parsed); }
            }

            return found;
        }

        /// <summary>
        /// Matches the request shape and lifts out its three interesting values.
        /// </summary>
        /// <param name="body">
        /// The QFORM body.
        /// </param>
        /// <param name="parsed">
        /// The values found.
        /// </param>
        private static bool TryParseSpecBlock(ReadOnlySpan<byte> body, out SpecBlock parsed)
        {
            parsed = default;

            for (int i = 0; i + 14 < body.Length; i++)
            {
                // F2 0001 92 <selector1> F2 0002 8C 80 <len> B0 <blockLen>
                if (body[i] != 0xF2 || body[i + 1] != 0x00 || body[i + 2] != 0x01) { continue; }
                if (body[i + 3] != 0x92) { continue; }
                if (body[i + 6] != 0xF2 || body[i + 7] != 0x00 || body[i + 8] != 0x02) { continue; }
                if (body[i + 9] != 0x8C || body[i + 10] != 0x80) { continue; }
                if (body[i + 12] != 0xB0) { continue; }

                int blockLength = body[i + 13];
                int blockAt = i + 14;
                if (blockAt + blockLength + 6 > body.Length) { return false; }

                parsed.Selector1 = (ushort)((body[i + 4] << 8) | body[i + 5]);
                parsed.Block = body.Slice(blockAt, blockLength).ToArray();

                // Two A2 pairs follow the string; the cursor is the second.
                int cursorAt = blockAt + blockLength + 4;
                if (body[cursorAt - 1] != 0xA2 && body[cursorAt - 4] != 0xA2) { return false; }
                parsed.Cursor = (ushort)((body[cursorAt] << 8) | body[cursorAt + 1]);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Renders bytes as spaced hex.
        /// </summary>
        /// <param name="data">
        /// The bytes to render.
        /// </param>
        private static string Hex(byte[] data)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                text.Append(data[i].ToString("x2"));
                if ((i % 2) == 1) { text.Append(' '); }
            }

            return text.ToString();
        }

        /// <summary>
        /// Renders the printable characters of a block, dots elsewhere.
        /// </summary>
        /// <param name="data">
        /// The bytes to render.
        /// </param>
        private static string Printable(byte[] data)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder(data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                text.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            return text.ToString();
        }

    }
}
