//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    /// <summary>
    /// The BIND image's screen sizes (GA23-0061-2 Figure 1-11) and the SSCP-LU session's text
    /// screen (GA23-0061-2 p. 5-36, 5-37), as a TN3270E server hands them on (RFC 2355 sections
    /// 10.3 and 10.5).
    /// </summary>
    /// <remarks>
    /// No host on this machine sends a BIND: TK5 offers no TN3270E (measured 24 November 2026,
    /// its first bytes are FF FD 18, TERMINAL-TYPE, never FF FD 28). The BIND bytes are therefore
    /// IBM's own: GA23-0061-2 p. 5-61, "Bind Default", the logmode setting the manual suggests for
    /// LU type 2, 27 bytes, byte 24 X'02' for a model 2.
    /// </remarks>
    public class BindAndSscpLuTests
    {
        // GA23-0061-2 p. 5-61: the LU type 1 table with the LU type 2 changes (byte 9 X'00', byte
        // 10 X'87', byte 14 X'02', byte 18 X'00', byte 24 X'02' for a model 2). Bytes 0 to 26.
        private static readonly byte[] IbmModel2Bind =
        {
            0x31, 0x01, 0x03, 0x03, 0xB1, 0x90, 0x30, 0x80, 0x00, 0x00, 0x87, 0x87, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00,
        };

        // The same BIND with bytes 20 to 24 as rows, columns, rows, columns, code.
        private static byte[] Bind(byte defaultRows, byte defaultColumns, byte alternateRows, byte alternateColumns, byte code)
        {
            byte[] bind = (byte[])IbmModel2Bind.Clone();
            bind[20] = defaultRows;
            bind[21] = defaultColumns;
            bind[22] = alternateRows;
            bind[23] = alternateColumns;
            bind[24] = code;
            return bind;
        }

        private static (Display3270 Display, List<byte[]> Sent, List<string> Errors) Create(int alternateRows = 24, int alternateColumns = 80)
        {
            var display = new Display3270(24, 80, alternateRows, alternateColumns);
            var sent = new List<byte[]>();
            var errors = new List<string>();
            display.InboundRecord += record => sent.Add(record.ToArray());
            display.ProtocolError += errors.Add;
            return (display, sent, errors);
        }

        private static BindImage Read(byte[] image)
        {
            Assert.True(BindImage.TryRead(image, out BindImage bind, out string? error), error);
            return bind;
        }

        [Fact]
        public void IbmsSuggestedLuType2BindIsRead()
        {
            BindImage bind = Read(IbmModel2Bind);

            Assert.Equal(0x02, bind.LuType);
            Assert.Equal(BindScreenSize.BaseModel2, bind.ScreenSizeCode);

            // X'87': mantissa 8 times 2 to the 7th, 1024 bytes (GA23-0061-2 p. 5-12).
            Assert.Equal(1024, bind.MaxSecondaryRu);
            Assert.Equal(1024, bind.MaxPrimaryRu);
        }

        [Fact]
        public void AnImageThatIsNotABindOrIsTooShortIsRefusedByName()
        {
            byte[] notBind = (byte[])IbmModel2Bind.Clone();
            notBind[0] = 0x32; // UNBIND's request code, not BIND's
            Assert.False(BindImage.TryRead(notBind, out _, out string? error));
            Assert.Contains("X'31'", error);

            Assert.False(BindImage.TryRead(IbmModel2Bind.AsSpan(0, 24), out _, out error));
            Assert.Contains("byte 24", error);
        }

        [Fact]
        public void TheReservedBitOfByte24IsNotPartOfTheCode()
        {
            // Figure 1-11: byte 24 bit 0 is reserved, bits 1 to 7 are the code.
            Assert.Equal(BindScreenSize.ExtendedAlternate, Read(Bind(24, 80, 43, 80, 0xFF)).ScreenSizeCode);
        }

        [Fact]
        public void AModel2BindOnAModel2KeepsTwentyFourByEighty()
        {
            var (d, _, errors) = Create();

            Assert.True(d.ApplyBind(Read(IbmModel2Bind)));

            Assert.Empty(errors);
            Assert.Equal(24, d.DefaultRows);
            Assert.Equal(80, d.DefaultColumns);
            Assert.Equal(24, d.AlternateRows);
            Assert.Equal(80, d.AlternateColumns);
        }

        [Fact]
        public void X7FGivesTheDefaultAndTheAlternateSizeFromBytes20To23()
        {
            // A model 4 (24 x 80 and 43 x 80). Bytes 20 to 24 18 50 2B 50 7F are the screen part
            // of the PSERVIC value X'00000000000018502B507F00' that the CICS manual (SC34-6814,
            // "Matching models and logon mode entries") lists for a 43 x 80 alternate.
            var (d, _, errors) = Create(43, 80);

            Assert.True(d.ApplyBind(Read(Bind(0x18, 0x50, 0x2B, 0x50, BindScreenSize.ExtendedAlternate))));
            Assert.Empty(errors);
            Assert.Equal(24, d.DefaultRows);
            Assert.Equal(43, d.AlternateRows);

            // Erase/Write Alternate now gives the BIND's alternate size.
            Assert.True(d.ProcessOutbound(new byte[] { Command3270.EraseWriteAlternate, 0x02 }));
            Assert.Equal(43, d.Rows);
            Assert.Equal(80, d.Columns);
        }

        [Fact]
        public void X7EGivesOneSizeAndEraseWriteAlternateRunsAsEraseWrite()
        {
            // GA23-0061-2 p. 1-21: with X'7E' "An Erase/Write Alternate command is accepted by
            // the device but is interpreted as an Erase/Write command"; bytes 22 and 23 ignored.
            var (d, _, errors) = Create(43, 80);

            Assert.True(d.ApplyBind(Read(Bind(32, 80, 0x2B, 0x50, BindScreenSize.ExtendedDefault))));
            Assert.Empty(errors);
            Assert.Equal(32, d.Rows);

            Assert.True(d.ProcessOutbound(new byte[] { Command3270.EraseWriteAlternate, 0x02 }));
            Assert.Equal(32, d.Rows);
            Assert.Equal(80, d.Columns);

            // Clear goes to the session's default size too (GA23-0061-2 p. 1-20).
            Assert.True(d.Attention(Aid.Clear));
            Assert.Equal(32, d.Rows);
        }

        [Fact]
        public void X03GivesTwentyFourByEightyAndTheTerminalsOwnAlternate()
        {
            var (d, _, errors) = Create(43, 80);

            Assert.True(d.ApplyBind(Read(Bind(0, 0, 0, 0, BindScreenSize.DefaultAndQueryAlternate))));

            Assert.Empty(errors);
            Assert.Equal(24, d.DefaultRows);
            Assert.Equal(43, d.AlternateRows);
        }

        [Theory]
        [InlineData(0x18, 0x50, 0x2B, 0x50, 0x7F, "43 rows")]          // a model 2 has no 43 rows
        [InlineData(0x18, 0x84, 0x18, 0x84, 0x7E, "132 columns")]      // nor 132 columns
        [InlineData(0x18, 0x41, 0x18, 0x50, 0x7F, "65 columns")]       // 65 is not in Figure 1-11
        [InlineData(0x00, 0x50, 0x18, 0x50, 0x7F, "0 rows")]
        [InlineData(0x0C, 0x28, 0x0C, 0x28, 0x01, "X'01'")]            // model 1's 12 x 40
        [InlineData(0x18, 0x50, 0x18, 0x50, 0x05, "X'05'")]            // not a code at all
        public void ASizeThisModelCannotShowIsNotTakenAndSaidOutLoud(byte rows, byte columns, byte alternateRows, byte alternateColumns, byte code, string named)
        {
            var (d, _, errors) = Create();

            Assert.False(d.ApplyBind(Read(Bind(rows, columns, alternateRows, alternateColumns, code))));

            Assert.Contains(named, Assert.Single(errors));
            Assert.Equal(24, d.DefaultRows);
            Assert.Equal(80, d.DefaultColumns);
            Assert.Equal(24, d.AlternateRows);
            Assert.Equal(80, d.AlternateColumns);
        }

        [Fact]
        public void AnUnbindPutsTheModelsSizesBack()
        {
            var (d, _, _) = Create(43, 80);
            Assert.True(d.ApplyBind(Read(Bind(32, 80, 0, 0, BindScreenSize.ExtendedDefault))));
            Assert.Equal(32, d.AlternateRows);

            d.Unbind();

            Assert.Equal(24, d.Rows);
            Assert.Equal(24, d.DefaultRows);
            Assert.Equal(43, d.AlternateRows);
        }

        [Theory]
        [InlineData(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x09, 0x00, 0x01 })]       // extended field
        [InlineData(new byte[] { Command3270.WriteStructuredField, 0x00, 0x06, 0x09, 0x00, 0x02, 0x42 })] // character, colour
        public void ABindResetsTheInboundReplyModeToField(byte[] setReplyMode)
        {
            // GA23-0059-4 Table C-1 (p. C-1), row "Bind Unowned": Inbound Reply Mode R; reset
            // means field mode (Table 3-3, p. 3-4).
            var (d, _, errors) = Create();
            Assert.True(d.ProcessOutbound(setReplyMode));
            Assert.NotEqual(ReplyMode.Field, d.ReplyMode);

            Assert.True(d.ApplyBind(Read(IbmModel2Bind)));

            Assert.Empty(errors);
            Assert.Equal(ReplyMode.Field, d.ReplyMode);
        }

        [Fact]
        public void ABindWhoseSizesAreRefusedStillResetsTheInboundReplyMode()
        {
            // The reset belongs to the BIND (Table C-1), not to the sizes it carries.
            var (d, _, _) = Create();
            Assert.True(d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x09, 0x00, 0x01 }));

            Assert.False(d.ApplyBind(Read(Bind(0x18, 0x50, 0x2B, 0x50, 0x7F))));

            Assert.Equal(ReplyMode.Field, d.ReplyMode);
        }

        [Fact]
        public void ABindEndsTheRetryStateSoReadModifiedReadsTheScreenAgain()
        {
            // Table C-1 (p. C-1), row "Bind Unowned": INPID/INOP R - INOP back to Read Modified
            // in the normal read state, where a Read Modified command is an ordinary read and
            // repeats nothing (p. 3-14, 3-19).
            var (d, sent, errors) = Create();
            byte[] field = { 0x00, 0x05, 0xD0, 0x46, 0x05 };
            d.SendStructuredFieldReply(field, retry: true);
            Assert.Single(sent);

            Assert.True(d.ApplyBind(Read(IbmModel2Bind)));
            Assert.True(d.ProcessOutbound(new byte[] { Command3270.ReadModified }));

            Assert.Empty(errors);
            Assert.Equal(2, sent.Count);

            // Not the kept structured field again (AID X'88'), but a read of the screen.
            Assert.NotEqual(Aid.StructuredField, sent[1][0]);
        }

        // "IKJ" in EBCDIC.
        private static readonly byte[] Ikj = { 0xC9, 0xD2, 0xD1 };

        [Fact]
        public void SscpTextIsShownAtTheCursorAndNewLineFillsTheLineWithNulls()
        {
            var (d, sent, errors) = Create();

            // "IKJ" NL "IKJ": GA23-0061-2 p. 5-37.
            Assert.True(d.ProcessSscpLuData(new byte[] { 0xC9, 0xD2, 0xD1, 0x15, 0xC9, 0xD2, 0xD1 }));

            Assert.Empty(errors);
            Assert.Empty(sent);
            Assert.True(d.SscpLuMode);
            Assert.False(d.IsFormatted);
            Assert.Equal(0xC9, d.GetByte(0));
            Assert.Equal(0xD1, d.GetByte(2));
            Assert.Equal(0x00, d.GetByte(3));
            Assert.Equal(0x00, d.GetByte(79));
            Assert.Equal(0xC9, d.GetByte(80));

            // "the 3274 places the cursor in the position next to the last character".
            Assert.Equal(83, d.CursorAddress);
            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void AMessageEndingWithNewLineLeavesTheCursorAtTheNextLine()
        {
            var (d, _, _) = Create();

            Assert.True(d.ProcessSscpLuData(new byte[] { 0xC9, 0xD2, 0xD1, 0x15 }));

            Assert.Equal(80, d.CursorAddress);
        }

        [Fact]
        public void PastTheLastLineTheMessageWrapsToTheTop()
        {
            var (d, _, _) = Create();
            // 23 New Lines reach the last line, 80 characters fill it, two more wrap (and the
            // whole message stays under the 1,536-byte limit).
            var text = new byte[23 + 80 + 2];
            Array.Fill(text, (byte)0x15, 0, 23);
            Array.Fill(text, (byte)0xC1, 23, 80);
            text[^2] = 0xC2;
            text[^1] = 0xC3;

            Assert.True(d.ProcessSscpLuData(text));

            Assert.Equal(0xC2, d.GetByte(0));
            Assert.Equal(0xC3, d.GetByte(1));
            Assert.Equal(2, d.CursorAddress);
        }

        [Fact]
        public void EnterSendsTheTextFromTheInitialCursorAddressWithoutAidOrNulls()
        {
            var (d, sent, _) = Create();
            Assert.True(d.ProcessSscpLuData(new byte[] { 0xC9, 0xD2, 0xD1, 0x15 }));

            // LOGOFF typed at the initial cursor address (row 2), then two blanks skipped by the
            // cursor: those positions stay null and are left out.
            Assert.True(d.Type('L'));
            Assert.True(d.Type('O'));
            d.MoveCursor(84);
            Assert.True(d.Type('G'));

            Assert.True(d.Attention(Aid.Enter));

            // No AID, no cursor address, no SBA: the text alone (GA23-0061-2 p. 5-37).
            Assert.Equal(new byte[] { 0xD3, 0xD6, 0xC7 }, Assert.Single(sent));
            Assert.Equal(KeyboardLock.WaitingForHost, d.Lock);
        }

        [Fact]
        public void AnSscpReplyIsAtMost256Positions()
        {
            var (d, sent, _) = Create();
            Assert.True(d.ProcessSscpLuData(ReadOnlySpan<byte>.Empty));
            for (int i = 0; i < 300; i++)
            {
                d.Type('A');
            }

            Assert.True(d.Attention(Aid.Enter));

            Assert.Equal(256, Assert.Single(sent).Length);
        }

        [Fact]
        public void ClearClearsTheScreenAndSendsNothingAndOtherKeysAreMinusFunction()
        {
            var (d, sent, _) = Create();
            Assert.True(d.ProcessSscpLuData(Ikj));

            Assert.False(d.Attention(Aid.Pf(3)));
            Assert.Equal(KeyboardLock.MinusFunction, d.Lock);
            Assert.Empty(sent);

            d.Reset();
            Assert.True(d.Attention(Aid.Clear));
            Assert.Empty(sent);
            Assert.Equal(0x00, d.GetByte(0));
            Assert.Equal(0, d.CursorAddress);

            // The initial cursor address went back to 0 with it.
            d.Type('X');
            Assert.True(d.Attention(Aid.Enter));
            Assert.Equal(new byte[] { 0xE7 }, Assert.Single(sent));
        }

        [Fact]
        public void EnteringSscpModeClearsWhatTheLuSessionLeftOnTheScreen()
        {
            var (d, _, _) = Create();
            Assert.True(d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02, Order3270.StartField, 0x60, 0xC1 }));
            Assert.True(d.IsFormatted);

            Assert.True(d.ProcessSscpLuData(Ikj));

            Assert.False(d.IsFormatted);
            Assert.Equal(0xC9, d.GetByte(0));
            Assert.Equal(0x00, d.GetByte(3));
        }

        [Fact]
        public void AMessageOverTheOutboundLimitIsRefused()
        {
            var (d, _, errors) = Create();

            Assert.False(d.ProcessSscpLuData(new byte[1537]));

            Assert.Contains("1,536", Assert.Single(errors));
            Assert.False(d.SscpLuMode);
        }

        [Fact]
        public void NvtTextIsShownAsAsciiWithCarriageReturnAndLineFeed()
        {
            var (d, sent, errors) = Create();
            Assert.True(d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02, Order3270.StartField, 0x60, 0xC1 }));

            // "AB" CR LF "C": RFC 854 NVT text.
            Assert.True(d.ProcessNvtData(new byte[] { 0x41, 0x42, 0x0D, 0x0A, 0x43 }));

            Assert.Empty(errors);
            Assert.Empty(sent);
            Assert.True(d.NvtMode);

            // RFC 2355 9.1: the mode switch erased the screen first, so the field is gone.
            Assert.False(d.IsFormatted);
            Assert.Equal(0xC1, d.GetByte(0)); // 'A' in code page 037
            Assert.Equal(0xC2, d.GetByte(1));
            Assert.Equal(0xC3, d.GetByte(80));
            Assert.Equal(81, d.CursorAddress);
        }

        [Fact]
        public void EnterInNvtModeSendsTheTypedLineAsAsciiWithCrLf()
        {
            var (d, sent, _) = Create();
            Assert.True(d.ProcessNvtData(new byte[] { 0x3E, 0x20 })); // "> "

            d.Type('h');
            d.Type('i');
            Assert.True(d.Attention(Aid.Enter));

            // Only what follows the host's prompt goes, as ASCII, then CR LF.
            Assert.Equal(new byte[] { 0x68, 0x69, 0x0D, 0x0A }, Assert.Single(sent));
            Assert.Equal(80, d.CursorAddress);
            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void AnEscapeSequenceInNvtTextIsSaidOutLoudNotShown()
        {
            var (d, _, errors) = Create();

            Assert.False(d.ProcessNvtData(new byte[] { 0x1B, 0x5B, 0x48, 0x41 }));

            Assert.Contains("X'1B'", Assert.Single(errors));
        }

        [Fact]
        public void LeavingNvtModeErasesTheScreen()
        {
            var (d, _, _) = Create();
            Assert.True(d.ProcessNvtData(new byte[] { 0x41 }));

            d.LeaveNvtMode();

            Assert.False(d.NvtMode);
            Assert.Equal(0x00, d.GetByte(0));
        }

        [Fact]
        public void ABindTakesTheScreenBackFromTheSscp()
        {
            var (d, _, _) = Create();
            Assert.True(d.ProcessSscpLuData(Ikj));

            Assert.True(d.ApplyBind(Read(IbmModel2Bind)));

            Assert.False(d.SscpLuMode);
            Assert.Equal(0x00, d.GetByte(0));
        }
    }
}
