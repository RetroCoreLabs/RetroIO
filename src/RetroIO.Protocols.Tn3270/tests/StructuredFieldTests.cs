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
    public class StructuredFieldTests
    {
        private static readonly byte[] Query = { Command3270.WriteStructuredField, 0x00, 0x05, 0x01, 0xFF, 0x02 };

        private static (Display3270 Display, List<byte[]> Sent, List<string> Errors) Create(int rows = 24, int columns = 80, int alternateRows = 24, int alternateColumns = 80)
        {
            var display = new Display3270(rows, columns, alternateRows, alternateColumns);
            var sent = new List<byte[]>();
            var errors = new List<string>();
            display.InboundRecord += record => sent.Add(record.ToArray());
            display.ProtocolError += errors.Add;
            return (display, sent, errors);
        }

        // Splits a query reply record (X'88' then structured fields) into its fields.
        private static List<byte[]> Fields(byte[] record)
        {
            Assert.Equal(Aid.StructuredField, record[0]);
            var fields = new List<byte[]>();
            int pos = 1;
            while (pos < record.Length)
            {
                int length = (record[pos] << 8) | record[pos + 1];
                Assert.True(length >= 4 && pos + length <= record.Length, "bad length " + length + " at " + pos);
                fields.Add(record.AsSpan(pos, length).ToArray());
                pos += length;
            }

            return fields;
        }

        private static byte[] Find(List<byte[]> fields, byte code)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i][2] == StructuredFieldId.QueryReply && fields[i][3] == code)
                {
                    return fields[i];
                }
            }

            throw new Xunit.Sdk.XunitException("No query reply X'" + code.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "'");
        }

        [Fact]
        public void QueryAnswersWithEveryReplyInCodeOrder()
        {
            var (d, sent, errors) = Create(24, 80, 32, 80);

            d.ProcessOutbound(Query);

            Assert.Empty(errors);
            List<byte[]> f = Fields(Assert.Single(sent));
            byte[] codes = new byte[f.Count];
            for (int i = 0; i < f.Count; i++)
            {
                codes[i] = f[i][3];
            }

            Assert.Equal(new byte[] { 0x80, 0x81, 0x85, 0x86, 0x87, 0x88, 0x8A, 0xA6 }, codes);

            // Summary lists the same codes (p. 6-81).
            Assert.Equal(new byte[] { 0x00, 0x0C, 0x81, 0x80, 0x80, 0x81, 0x85, 0x86, 0x87, 0x88, 0x8A, 0xA6 }, f[0]);

            // Usable Area: 12/14-bit, 80 x 32 (the larger screen), cells 9 x 16, buffer 2560.
            Assert.Equal(
                new byte[] { 0x00, 0x17, 0x81, 0x81, 0x01, 0x00, 0x00, 0x50, 0x00, 0x20, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x09, 0x10, 0x0A, 0x00 },
                f[1]);

            // Character Sets: base set F0 = 697/037, GE set F1 = 963/310.
            Assert.Equal(
                new byte[] { 0x00, 0x1B, 0x81, 0x85, 0x82, 0x00, 0x09, 0x10, 0x00, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0xF0, 0x02, 0xB9, 0x00, 0x25, 0x01, 0x00, 0xF1, 0x03, 0xC3, 0x01, 0x36 },
                f[2]);

            // Implicit Partition: default 80 x 24, alternate 80 x 32.
            Assert.Equal(new byte[] { 0x00, 0x11, 0x81, 0xA6, 0x00, 0x00, 0x0B, 0x01, 0x00, 0x00, 0x50, 0x00, 0x18, 0x00, 0x50, 0x00, 0x20 }, f[7]);

            // Field Validation: mandatory fill and mandatory entry, no trigger.
            Assert.Equal(new byte[] { 0x00, 0x05, 0x81, 0x8A, 0x06 }, f[6]);
        }

        [Fact]
        public void ColorReplyShowsEveryColourAsItselfOnlyOnAColourScreen()
        {
            var (d, sent, _) = Create();
            d.ProcessOutbound(Query);
            byte[] color = Find(Fields(sent[0]), QueryCode.Color);
            Assert.Equal(new byte[] { 0x00, 0x16, 0x81, 0x86, 0x00, 0x08, 0x00, 0xF4, 0xF1, 0xF1, 0xF2, 0xF2, 0xF3, 0xF3, 0xF4, 0xF4, 0xF5, 0xF5, 0xF6, 0xF6, 0xF7, 0xF7 }, color);

            d.Color = false;
            d.ProcessOutbound(Query);
            byte[] mono = Find(Fields(sent[1]), QueryCode.Color);
            Assert.Equal(0x00, mono[9]);
            Assert.Equal(0x00, mono[21]);
        }

        [Fact]
        public void TheChosenCodePageIsNamedInTheCharacterSetsReply()
        {
            var (d, sent, _) = Create();
            d.CodePage = CodePage.Cp1142;
            d.ProcessOutbound(Query);
            byte[] cs = Find(Fields(sent[0]), QueryCode.CharacterSets);

            // GCSGID 695 (X'02B7') and code page 1142 (X'0476').
            Assert.Equal(new byte[] { 0x02, 0xB7, 0x04, 0x76 }, cs.AsSpan(16, 4).ToArray());
        }

        [Fact]
        public void DdmIsOfferedOnlyWhileFileTransferIsOn()
        {
            var (d, sent, _) = Create();
            d.FileTransferLimit = 16384;
            d.ProcessOutbound(Query);
            List<byte[]> f = Fields(sent[0]);
            Assert.Equal(new byte[] { 0x00, 0x0C, 0x81, 0x95, 0x00, 0x00, 0x40, 0x00, 0x40, 0x00, 0x01, 0x01 }, Find(f, QueryCode.Ddm));
            Assert.Equal(9, f.Count);
        }

        [Fact]
        public void QueryListSendsOnlyWhatIsListedAndSupportedOnce()
        {
            var (d, sent, errors) = Create();

            // List: Color twice, DDM (not supported while file transfer is off), Reply Modes.
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x0A, 0x01, 0xFF, 0x03, 0x00, 0x86, 0x95, 0x86, 0x88 });

            Assert.Empty(errors);
            List<byte[]> f = Fields(sent[0]);
            Assert.Equal(2, f.Count);
            Assert.Equal(QueryCode.Color, f[0][3]);
            Assert.Equal(QueryCode.ReplyModes, f[1][3]);
        }

        [Fact]
        public void QueryListOfNothingSupportedGivesNull()
        {
            var (d, sent, _) = Create();

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x07, 0x01, 0xFF, 0x03, 0x00, 0x95 });

            Assert.Equal(new byte[] { 0x88, 0x00, 0x04, 0x81, 0xFF }, sent[0]);
        }

        [Fact]
        public void QueryListAllIgnoresTheList()
        {
            var (d, sent, _) = Create();

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x07, 0x01, 0xFF, 0x03, 0x80, 0x86 });

            Assert.Equal(8, Fields(sent[0]).Count);
        }

        [Fact]
        public void QueryToAPartitionOtherThanFFIsRejected()
        {
            var (d, sent, errors) = Create();

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x01, 0x00, 0x02 });

            Assert.Empty(sent);
            Assert.Single(errors);
        }

        [Fact]
        public void ReadPartitionThatIsNotLastIsRejectedBeforeAnythingIsApplied()
        {
            var (d, sent, errors) = Create();

            // Read Partition Query, then Erase/Reset.
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x01, 0xFF, 0x02, 0x00, 0x04, 0x03, 0x00 });

            Assert.Empty(sent);
            Assert.Single(errors);
        }

        [Fact]
        public void AnUnknownStructuredFieldIsReported()
        {
            var (d, _, errors) = Create();

            // Load Programmed Symbols, X'06'.
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x06, 0x00 });

            Assert.Single(errors);
        }

        [Fact]
        public void EraseResetPicksTheSizeAndOutbound3270DSEraseWriteKeepsIt()
        {
            var (d, _, errors) = Create(24, 80, 43, 80);

            // Erase/Reset with IPZ: alternate size.
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x03, 0x80 });
            Assert.Equal(43, d.Rows);

            // Outbound 3270DS Erase/Write: the size does not change (p. 5-40), data at 0.
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x07, 0x40, 0x00, Command3270.EraseWrite, 0x02, 0xC1 });
            Assert.Equal(43, d.Rows);
            Assert.Equal(0xC1, d.GetByte(0));

            // Erase/Reset without IPZ: default size.
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x04, 0x03, 0x00 });
            Assert.Equal(24, d.Rows);
            Assert.Empty(errors);
        }

        [Fact]
        public void Outbound3270DSToAnotherPartitionIsRejected()
        {
            var (d, _, errors) = Create();

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x07, 0x40, 0x01, Command3270.Write, 0x02, 0xC1 });

            Assert.Single(errors);
            Assert.Equal(0, d.GetByte(0));
        }

        [Fact]
        public void ExtendedFieldModeReadBufferSendsStartFieldExtended()
        {
            var (d, sent, errors) = Create();

            // Set Reply Mode: extended field.
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x09, 0x00, 0x01 });

            // EW, SFE with attribute protected and colour red, then "A".
            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02, Order3270.StartFieldExtended, 0x02, 0xC0, 0x20, 0x42, 0xF2, 0xC1 });
            d.ProcessOutbound(new byte[] { Command3270.ReadBuffer });

            Assert.Empty(errors);
            Assert.Equal(ReplyMode.ExtendedField, d.ReplyMode);
            byte[] r = sent[0];

            // AID, cursor, SFE count 2: C0 attribute (X'20' made printable as X'60'), 42 red.
            Assert.Equal(new byte[] { Order3270.StartFieldExtended, 0x02, 0xC0, 0x60, 0x42, 0xF2, 0xC1 }, r.AsSpan(3, 7).ToArray());
        }

        [Fact]
        public void CharacterModeSendsSetAttributeOnlyForListedTypesAndOnlyOnChange()
        {
            var (d, sent, errors) = Create();

            // Set Reply Mode: character, types foreground colour only.
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x06, 0x09, 0x00, 0x02, 0x42 });

            // Unformatted: SA red "AB", SA highlight reverse "C" (still red), SA default "D".
            d.ProcessOutbound(new byte[]
            {
                Command3270.EraseWrite, 0x02,
                Order3270.SetAttribute, 0x42, 0xF2, 0xC1, 0xC2,
                Order3270.SetAttribute, 0x41, 0xF2, 0xC3,
                Order3270.SetAttribute, 0x00, 0x00, 0xC4,
            });
            d.ProcessOutbound(new byte[] { Command3270.ReadModified });

            Assert.Empty(errors);
            byte[] r = sent[0];

            // Highlighting is not listed, so only the colour changes are sent.
            Assert.Equal(new byte[] { 0x28, 0x42, 0xF2, 0xC1, 0xC2, 0xC3, 0x28, 0x42, 0x00, 0xC4 }, r.AsSpan(3).ToArray());
        }

        [Fact]
        public void EraseWriteWithWccResetPutsTheReplyModeBackToField()
        {
            var (d, _, _) = Create();
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x09, 0x00, 0x01 });

            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02 });
            Assert.Equal(ReplyMode.ExtendedField, d.ReplyMode);

            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x42 });
            Assert.Equal(ReplyMode.Field, d.ReplyMode);
        }

        [Fact]
        public void TheClearKeyPutsTheReplyModeBackToField()
        {
            // GA23-0059-4 Table C-1, p. C-1: CLEAR Key LULU, Inbound Reply Mode = R.
            var (d, _, _) = Create();
            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02 });
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x09, 0x00, 0x01 });
            Assert.Equal(ReplyMode.ExtendedField, d.ReplyMode);

            Assert.True(d.Attention(Aid.Clear));

            Assert.Equal(ReplyMode.Field, d.ReplyMode);
        }

        [Fact]
        public void SetReplyModeRejectsAReservedModeAndANonCharacterType()
        {
            var (d, _, errors) = Create();

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x09, 0x00, 0x03 });
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x06, 0x09, 0x00, 0x02, 0xC0 });

            Assert.Equal(2, errors.Count);
            Assert.Equal(ReplyMode.Field, d.ReplyMode);
        }

        [Fact]
        public void ReadPartitionReadBufferOfPartitionZeroAnswersInPlain3270Format()
        {
            var (d, sent, errors) = Create();
            d.ProcessOutbound(new byte[] { Command3270.EraseWrite, 0x02, 0xC1 });

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x01, 0x00, Command3270.ReadBuffer });

            Assert.Empty(errors);
            Assert.Equal(Aid.None, sent[0][0]);
            Assert.Equal(0xC1, sent[0][3]);
        }

        [Fact]
        public void AFileTransferFieldIsRejectedWhileFileTransferIsOff()
        {
            var (d, sent, errors) = Create();

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0xD0, 0x41, 0x12 });

            Assert.Empty(sent);
            Assert.Single(errors);
        }

        [Fact]
        public void FileTransferFieldsOverTheLimitAreRejected()
        {
            var (d, _, errors) = Create();
            d.FileTransferLimit = 6;
            int fields = 0;
            d.FileTransferField += _ => fields++;

            // Two Close requests, 5 bytes each: 10 is more than 6.
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0xD0, 0x41, 0x12, 0x00, 0x05, 0xD0, 0x41, 0x12 });

            Assert.Equal(0, fields);
            Assert.Single(errors);
        }

        [Fact]
        public void TheBaseCharacterSetHasLocalIdF0()
        {
            // GA23-0059-4 p. 6-26: non-loadable 1-byte sets get X'F0' to X'F7'; X'F1' is the
            // Graphic Escape set, X'F0' the base one (decided 24 November 2026).
            var (d, sent, _) = Create();
            d.ProcessOutbound(Query);
            byte[] cs = Find(Fields(sent[0]), QueryCode.CharacterSets);

            // First descriptor: SET 0, flags 0, LCID (p. 6-24); second: SET 1, flags 0, LCID.
            Assert.Equal(new byte[] { 0x00, 0x00, 0xF0 }, cs.AsSpan(13, 3).ToArray());
            Assert.Equal(new byte[] { 0x01, 0x00, 0xF1 }, cs.AsSpan(20, 3).ToArray());
        }

        [Fact]
        public void UsableAreaForTwoSizesThatDoNotNestReportsTheDefaultAndAnAlternateUsableArea()
        {
            // The 3180 of GA23-0059-4 p. 6-93: 43 x 80 or 27 x 132, not at the same time. Base is
            // the default size, BUFFSZ the larger cell count (27 x 132 = 3564 = X'0DEC'), XMIN to
            // YMAX present as zeros because a self-defining parameter follows (p. 6-87), then the
            // Alternate Usable Area parameter (p. 6-94).
            var (d, sent, errors) = Create(43, 80, 27, 132);
            d.ProcessOutbound(Query);

            Assert.Empty(errors);
            byte[] ua = Find(Fields(sent[0]), QueryCode.UsableArea);
            Assert.Equal(
                new byte[]
                {
                    0x00, 0x2E, 0x81, 0x81,
                    0x01, 0x00,                                     // 12/14-bit, matrix, cells
                    0x00, 0x50, 0x00, 0x2B,                         // W 80, H 43
                    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                    0x09, 0x10,                                     // AW, AH
                    0x0D, 0xEC,                                     // BUFFSZ 3564
                    0x00, 0x00, 0x00, 0x00,                         // XMIN, YMIN, XMAX, YMAX
                    0x13, 0x02, 0x00, 0x01,                         // Alternate Usable Area, AUAID 1
                    0x00, 0x84, 0x00, 0x1B,                         // WAUA 132, HAUA 27
                    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                    0x09, 0x10,                                     // AWAUA, AHAUA
                },
                ua);
        }

        [Fact]
        public void UsableAreaForNestedSizesIsTheLargerOneWithNoParameter()
        {
            // 24 x 80 inside 27 x 132: the larger is reported, no self-defining parameter, the
            // reply stops after BUFFSZ (p. 6-87). 27 x 132 = 3564.
            var (d, sent, _) = Create(24, 80, 27, 132);
            d.ProcessOutbound(Query);
            byte[] ua = Find(Fields(sent[0]), QueryCode.UsableArea);
            Assert.Equal(
                new byte[] { 0x00, 0x17, 0x81, 0x81, 0x01, 0x00, 0x00, 0x84, 0x00, 0x1B, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x09, 0x10, 0x0D, 0xEC },
                ua);
        }

        // Erase/Write, WCC X'C3' (keyboard restore, reset MDT), then one character.
        private static readonly byte[] EraseWriteWithRestore = { Command3270.EraseWrite, 0xC3, 0xC1 };

        // Write, WCC X'C0' (no keyboard restore), then one character.
        private static readonly byte[] WriteWithoutRestore = { Command3270.Write, 0xC0, 0xC1 };

        [Fact]
        public void AQueryReplyLocksTheKeyboardAndTheResetKeyDoesNotUnlockIt()
        {
            // GA23-0059-4 p. 9-9 and 10-8: TWAIT is displayed; p. 3-20: no keystroking, and
            // Reset does not clear TWAIT.
            var (d, sent, _) = Create();
            d.ProcessOutbound(EraseWriteWithRestore);
            Assert.Equal(KeyboardLock.None, d.Lock);

            d.ProcessOutbound(Query);

            Assert.Single(sent);
            Assert.Equal(KeyboardLock.QueryReplySent, d.Lock);
            Assert.False(d.Type('A'));
            Assert.False(d.Attention(Aid.Enter));
            Assert.Single(sent);
            d.Reset();
            Assert.Equal(KeyboardLock.QueryReplySent, d.Lock);
            Assert.Equal("X Wait", StatusLine3270.Message(d, StatusLineConnection.Connected));
        }

        [Fact]
        public void AWriteWithoutKeyboardRestoreAcknowledgesTheQueryAndLeavesNoSystemLock()
        {
            // p. 3-23, 10-5: an EW, EWA or Write "with or without a WCC or data" acknowledges a
            // query. System Lock comes only from an enter action (p. 3-21).
            var (d, _, _) = Create();
            d.ProcessOutbound(EraseWriteWithRestore);
            d.ProcessOutbound(Query);
            Assert.Equal(KeyboardLock.QueryReplySent, d.Lock);

            d.ProcessOutbound(WriteWithoutRestore);

            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void AWriteStructuredFieldAcknowledgesTheQuery()
        {
            // p. 3-23, 10-5: a WSF acknowledges "regardless of an error being detected in the
            // accompanying structured fields". Set Reply Mode to partition 0, Field mode.
            var (d, _, _) = Create();
            d.ProcessOutbound(EraseWriteWithRestore);
            d.ProcessOutbound(Query);
            Assert.Equal(KeyboardLock.QueryReplySent, d.Lock);

            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x09, 0x00, 0x00 });

            Assert.Equal(KeyboardLock.None, d.Lock);
        }

        [Fact]
        public void AReadCommandDoesNotAcknowledgeTheQuery()
        {
            // p. 3-23, 10-5: "any outbound transmission except one with a read command".
            var (d, _, _) = Create();
            d.ProcessOutbound(EraseWriteWithRestore);
            d.ProcessOutbound(Query);
            Assert.Equal(KeyboardLock.QueryReplySent, d.Lock);

            d.ProcessOutbound(new byte[] { Command3270.ReadBuffer });

            Assert.Equal(KeyboardLock.QueryReplySent, d.Lock);
        }

        [Fact]
        public void AnEraseAllUnprotectedAcknowledgesTheQuery()
        {
            var (d, _, _) = Create();
            d.ProcessOutbound(EraseWriteWithRestore);
            d.ProcessOutbound(Query);
            Assert.Equal(KeyboardLock.QueryReplySent, d.Lock);

            d.ProcessOutbound(new byte[] { Command3270.EraseAllUnprotected });

            Assert.Equal(KeyboardLock.None, d.Lock);
        }
    }
}
