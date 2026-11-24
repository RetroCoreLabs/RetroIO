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
    /// Load Programmed Symbols, X'06' (GA23-0059-4 p. 5-18 to 5-33), built from the manual's own
    /// examples: Figure 5-3 (p. 5-28) and the three compressed bit strings of p. 5-31 to 5-33.
    /// </summary>
    public class ProgrammedSymbolsTests
    {
        // Figure 5-3 (p. 5-28), type 1: 18 bytes.
        private static readonly byte[] Figure53 =
        {
            0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0xFF, 0x00, 0x00, 0x00, 0x00,
        };

        // The same symbol as a 9 x 16 picture, read off Figure 5-3: column 0 is slices 1 and 2,
        // columns 1 to 8 are slices 3 to 18.
        private static readonly string[] Figure53Picture =
        {
            ".........",
            ".........",
            ".........",
            "#........",
            ".#.......",
            "..#......",
            "...#.....",
            "....#....",
            ".....#...",
            "......#..",
            ".......#.",
            "#########",
            ".........",
            ".........",
            ".........",
            ".........",
        };

        // The resulting bit strings, header included, as printed (p. 5-31, 5-32, 5-33).
        private const string Rule1 = "0110 0010 1000 1000 1110 0001 0100 0110 0100 1000 1010 1100 0010 1001 0100 1011 1111 1111 00";
        private const string Rule2 = "1011 0001 0001 1000 0000 0111 0000 1010 0011 0010 0100 0101 1000 0110 0001 0100 1010 0101 1111 1111 1110 0001 0000 000";
        private const string Rule3 = "1101 1000 1010 0010 1100 0001 0000 0011 1000 0101 0001 1001 0010 0010 1100 0011 0001 0000 1010 0101 0010 1111 1111 1110 1000 0100 0010 0000";

        private static (Display3270 Display, List<byte[]> Sent, List<string> Errors) Create()
        {
            var display = new Display3270(24, 80, 24, 80);
            var sent = new List<byte[]>();
            var errors = new List<string>();
            display.InboundRecord += record => sent.Add(record.ToArray());
            display.ProtocolError += errors.Add;
            return (display, sent, errors);
        }

        // A WSF holding one Load PS field: its header bytes from byte 3 on, then the data.
        private static byte[] Wsf(byte[] header, byte[] data)
        {
            int length = 3 + header.Length + data.Length;
            var b = new List<byte> { Command3270.WriteStructuredField, (byte)(length >> 8), (byte)length, StructuredFieldId.LoadProgrammedSymbols };
            b.AddRange(header);
            b.AddRange(data);
            return b.ToArray();
        }

        // A bit string as printed, spaces ignored, then 1 bits to a whole byte - at least 4 (p. 5-30).
        private static byte[] Bits(string printed)
        {
            var bits = new List<bool>();
            for (int i = 0; i < printed.Length; i++)
            {
                if (printed[i] != ' ')
                {
                    bits.Add(printed[i] == '1');
                }
            }

            bits.Add(true);
            bits.Add(true);
            bits.Add(true);
            bits.Add(true);
            while (bits.Count % 8 != 0)
            {
                bits.Add(true);
            }

            byte[] bytes = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++)
            {
                if (bits[i])
                {
                    bytes[i / 8] |= (byte)(0x80 >> (i % 8));
                }
            }

            return bytes;
        }

        private static string Bitless(string printed) => printed.Replace(" ", string.Empty, StringComparison.Ordinal);

        private static void AssertPicture(ProgrammedSymbolSet set, byte code, string[] picture)
        {
            for (int y = 0; y < picture.Length; y++)
            {
                var row = new char[picture[y].Length];
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = set.GetDot(code, x, y) != 0 ? '#' : '.';
                }

                Assert.Equal(picture[y], new string(row));
            }
        }

        [Fact]
        public void TheExampleBitStringsHaveThePrintedLengths()
        {
            // "compressed into 74 bits", "to 95 bits", "to 112 bits" (p. 5-31, 5-32, 5-33).
            Assert.Equal(74, Bitless(Rule1).Length);
            Assert.Equal(95, Bitless(Rule2).Length);
            Assert.Equal(112, Bitless(Rule3).Length);
        }

        [Fact]
        public void Type1LoadsFigure53()
        {
            var (d, _, errors) = Create();

            // Basic form: flags type 1, LCID X'42', CHAR X'C1', RWS X'02' (p. 5-19).
            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x02 }, Figure53));

            Assert.Empty(errors);
            ProgrammedSymbolSet? set = d.ProgrammedSymbols.Find(0x42);
            Assert.NotNull(set);
            Assert.Equal(0x02, set!.Rws);
            AssertPicture(set, 0xC1, Figure53Picture);

            // Basic form, all planes: every dot that is on has all three (p. 5-24).
            Assert.Equal(0x07, set.GetDot(0xC1, 0, 3));

            // Slots not loaded show a space (p. 5-23).
            Assert.Equal(0, set.GetDot(0xC2, 0, 11));
        }

        [Theory]
        [InlineData(Rule1)]
        [InlineData(Rule2)]
        [InlineData(Rule3)]
        public void EachPrintedCompressedStringDecodesToFigure53(string printed)
        {
            var (d, _, errors) = Create();

            d.ProcessOutbound(Wsf(new byte[] { 0x02, 0x42, 0xC1, 0x02 }, Bits(printed)));

            Assert.Empty(errors);
            AssertPicture(d.ProgrammedSymbols.Find(0x42)!, 0xC1, Figure53Picture);
        }

        [Fact]
        public void TheThreeStringsJoinedWithoutRegardToBytesLoadThreeSlots()
        {
            // "The compressed bit strings for all the symbols being defined are concatenated
            // without regard for the byte boundaries" (p. 5-27): 74 + 95 + 112 = 281 bits, then 7
            // terminator bits.
            var (d, _, errors) = Create();
            byte[] data = Bits(Rule1 + Rule2 + Rule3);
            Assert.Equal(36, data.Length);

            d.ProcessOutbound(Wsf(new byte[] { 0x02, 0x42, 0xC1, 0x02 }, data));

            Assert.Empty(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x42)!;
            AssertPicture(set, 0xC1, Figure53Picture);
            AssertPicture(set, 0xC2, Figure53Picture);
            AssertPicture(set, 0xC3, Figure53Picture);
            Assert.Equal(0, set.GetDot(0xC4, 0, 11));
        }

        [Fact]
        public void Rule2ReadWithTheOneBitHeaderOfPage529DoesNotGiveFigure53()
        {
            // The example of p. 5-32 starts B'10'. Read with p. 5-29's B'1' the next bit (0) would
            // be the first slice-pair's "matches" bit, and the shape comes out different. This
            // pins that the B'10' reading is the one built: the string as printed decodes to
            // Figure 5-3 (see EachPrintedCompressedStringDecodesToFigure53), and dropping its
            // second bit - the B'1' reading of the same symbol - does not.
            var (d, _, errors) = Create();
            string oneBitHeader = "1" + Bitless(Rule2).Substring(2);

            d.ProcessOutbound(Wsf(new byte[] { 0x02, 0x42, 0xC1, 0x02 }, Bits(oneBitHeader)));

            bool same = errors.Count == 0;
            if (same)
            {
                ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x42)!;
                for (int y = 0; y < 16 && same; y++)
                {
                    for (int x = 0; x < 9; x++)
                    {
                        if ((set.GetDot(0xC1, x, y) != 0) != (Figure53Picture[y][x] == '#'))
                        {
                            same = false;
                        }
                    }
                }
            }

            Assert.False(same);
        }

        [Fact]
        public void Rule4IsABlankSymbolOfFourBits()
        {
            // B'1110' and nothing else (p. 5-29). Load Figure 5-3, then load a blank over it.
            var (d, _, errors) = Create();
            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x02 }, Figure53));

            d.ProcessOutbound(Wsf(new byte[] { 0x02, 0x42, 0xC1, 0x02 }, Bits("1110")));

            Assert.Empty(errors);
            Assert.Equal(0, d.ProgrammedSymbols.Find(0x42)!.GetDot(0xC1, 0, 11));
        }

        [Fact]
        public void HeaderB1111IsRejected()
        {
            // "If all of the header bits contain anything other than the bit combinations shown
            // ... the data stream will be rejected" (p. 5-29). B'1111' then a 0 bit is not a
            // terminator, so it can only be a header.
            var (d, _, errors) = Create();

            d.ProcessOutbound(Wsf(new byte[] { 0x02, 0x42, 0xC1, 0x02 }, new byte[] { 0xF0 }));

            Assert.Single(errors);
        }

        [Fact]
        public void FewerThanFourTerminatorBitsIsRejected()
        {
            // Rule 1's 74 bits and a blank symbol (B'1110') are 78 bits. With 10 terminator bits
            // (88 bits, 11 bytes) it is taken; with only 2 (80 bits, 10 bytes) it is not: "There
            // must be at least 4 of these terminator 1 bits" (p. 5-30).
            var (d, _, errors) = Create();
            byte[] legal = Bits(Rule1 + "1110");
            Assert.Equal(11, legal.Length);
            d.ProcessOutbound(Wsf(new byte[] { 0x02, 0x42, 0xC1, 0x02 }, legal));
            Assert.Empty(errors);

            byte[] shortEnd = new byte[10];
            Array.Copy(legal, shortEnd, 10);
            d.ProcessOutbound(Wsf(new byte[] { 0x02, 0x43, 0xC1, 0x03 }, shortEnd));
            Assert.Single(errors);
        }

        [Fact]
        public void Type3LoadsRowsOfLwBitsInTheExtendedForm()
        {
            // Extended form: P LENGTH 4 (bytes 7 to 10), flags 0, LW 8, LH 12 (p. 5-20). Each
            // symbol is 8 x 12 = 96 bits, 12 bytes, a row per byte (p. 5-22). Loaded top left of
            // the 9 x 16 slot; the rest of the slot is background (p. 6-23).
            var (d, _, errors) = Create();
            byte[] rows = { 0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81, 0xFF, 0x00, 0x00, 0x01 };

            d.ProcessOutbound(Wsf(new byte[] { 0x83, 0x50, 0x41, 0x04, 0x04, 0x00, 0x08, 0x0C }, rows));

            Assert.Empty(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x50)!;
            Assert.Equal(0x04, set.Rws);
            Assert.NotEqual(0, set.GetDot(0x41, 0, 0));
            Assert.NotEqual(0, set.GetDot(0x41, 7, 0));
            Assert.Equal(0, set.GetDot(0x41, 1, 0));
            Assert.NotEqual(0, set.GetDot(0x41, 3, 3));
            Assert.NotEqual(0, set.GetDot(0x41, 5, 8));
            Assert.NotEqual(0, set.GetDot(0x41, 7, 11));
            Assert.Equal(0, set.GetDot(0x41, 8, 0));
            Assert.Equal(0, set.GetDot(0x41, 0, 12));
        }

        [Fact]
        public void Type5LoadsColumnsOfLhBits()
        {
            // LW 2, LH 4: two columns of 4 bits, 8 bits, one byte (p. 5-22). B'1000 0001': column
            // 0 has its top dot, column 1 its bottom dot.
            var (d, _, errors) = Create();

            d.ProcessOutbound(Wsf(new byte[] { 0x85, 0x50, 0x41, 0x02, 0x04, 0x00, 0x02, 0x04 }, new byte[] { 0x81 }));

            Assert.Empty(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x50)!;
            Assert.NotEqual(0, set.GetDot(0x41, 0, 0));
            Assert.Equal(0, set.GetDot(0x41, 0, 1));
            Assert.Equal(0, set.GetDot(0x41, 1, 0));
            Assert.NotEqual(0, set.GetDot(0x41, 1, 3));
        }

        [Fact]
        public void Type6CompressesColumnsAsSlices()
        {
            // Type 6 with LW 10 and LH 8: ten column slices of 8 bits, five slice-pairs - the
            // printer form of p. 5-26 ("10 bytes"). Rule 1, first pair X'8000' (column 0's top dot)
            // differs: 1, then element 1 "1 1000", elements 2 to 4 "0 0 0"; pairs 2 to 5 match: 0000.
            // The slot is made 10 wide so the symbol fits (p. 5-24).
            var (d, _, errors) = Create();
            d.CellWidth = 10;
            byte[] data = Bits("0" + "1" + "11000" + "0" + "0" + "0" + "0000");

            d.ProcessOutbound(Wsf(new byte[] { 0x86, 0x50, 0x41, 0x02, 0x04, 0x00, 0x0A, 0x08 }, data));

            Assert.Empty(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x50)!;
            Assert.Equal(10, set.SlotWidth);
            for (int x = 0; x < 10; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    Assert.Equal(x == 0 && y == 0, set.GetDot(0x41, x, y) != 0);
                }
            }
        }

        [Fact]
        public void Type4CompressesRowsAsSlices()
        {
            // Type 4 with LW 8 and LH 4: four row slices, two slice-pairs. Rule 1; pair 1 is
            // rows X'FF' and X'00': 1, then "1 1111" "1 1111" "0" "0"; pair 2 matches: 0.
            var (d, _, errors) = Create();
            byte[] data = Bits("0" + "1" + "11111" + "11111" + "0" + "0" + "0");

            d.ProcessOutbound(Wsf(new byte[] { 0x84, 0x50, 0x41, 0x02, 0x04, 0x00, 0x08, 0x04 }, data));

            Assert.Empty(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x50)!;
            for (int x = 0; x < 8; x++)
            {
                Assert.NotEqual(0, set.GetDot(0x41, x, 0));
                Assert.Equal(0, set.GetDot(0x41, x, 1));
            }
        }

        [Fact]
        public void ASymbolWiderThanTheSlotIsRejected()
        {
            // "If either LW or LH exceeds the size of the character slot ... this structured field
            // will be rejected" (p. 5-24). The slot is 9 x 16 (CellWidth, CellHeight).
            var (d, _, errors) = Create();

            d.ProcessOutbound(Wsf(new byte[] { 0x83, 0x50, 0x41, 0x02, 0x04, 0x00, 0x0A, 0x08 }, new byte[10]));

            Assert.Single(errors);
            Assert.Null(d.ProgrammedSymbols.Find(0x50));
        }

        [Fact]
        public void Type1WithLwOtherThan9IsRejected()
        {
            // "If data of types 1 or 2 has been specified, then only the values LW = 9 and LH = 16
            // are valid" (p. 5-24).
            var (d, _, errors) = Create();

            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x04, 0x00, 0x08, 0x10 }, Figure53));

            Assert.Single(errors);
            Assert.Null(d.ProgrammedSymbols.Find(0x42));
        }

        [Theory]
        [InlineData(0x00, 0x07)]
        [InlineData(0x01, 0x01)]
        [InlineData(0x02, 0x02)]
        [InlineData(0x04, 0x04)]
        [InlineData(0xF3, 0x03)]
        [InlineData(0xF6, 0x06)]
        public void ColorLoadsTheNamedPlanes(byte color, byte mask)
        {
            // p. 5-20, 5-24: one plane, all planes, or a colour value split into its primaries.
            var (d, _, errors) = Create();

            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x06, 0x00, 0x00, 0x00, 0x00, color }, Figure53));

            Assert.Empty(errors);
            Assert.Equal(mask, d.ProgrammedSymbols.Find(0x42)!.GetDot(0xC1, 0, 11));
        }

        [Fact]
        public void PlanesLoadedSeparatelyAreOred()
        {
            // "each plane may be loaded independently ... the color constituent parts are ORed for
            // each pel" (p. 5-24): blue, then red over it, gives both.
            var (d, _, errors) = Create();

            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x06, 0x00, 0x00, 0x00, 0x00, 0x01 }, Figure53));
            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x06, 0x00, 0x00, 0x00, 0x00, 0x02 }, Figure53));

            Assert.Empty(errors);
            Assert.Equal(0x03, d.ProgrammedSymbols.Find(0x42)!.GetDot(0xC1, 0, 11));
        }

        [Theory]
        [InlineData(0x03)]
        [InlineData(0x08)]
        [InlineData(0xF0)]
        public void OtherColorValuesAreRejected(byte color)
        {
            var (d, _, errors) = Create();

            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x06, 0x00, 0x00, 0x00, 0x00, color }, Figure53));

            Assert.Single(errors);
        }

        [Fact]
        public void ASinglePlaneSetTakesOnlyColorZero()
        {
            // This display's reading of p. 5-24 against p. 5-20, see ProgrammedSymbolStore.Load.
            var (d, _, errors) = Create();
            d.Color = false;

            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x06, 0x00, 0x00, 0x00, 0x00, 0x04 }, Figure53));
            Assert.Single(errors);

            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x02 }, Figure53));
            Assert.Single(errors);
            Assert.Equal(0x07, d.ProgrammedSymbols.Find(0x42)!.GetDot(0xC1, 0, 11));
        }

        [Fact]
        public void ClearBlanksEverySlotThenLoads()
        {
            // p. 5-21: "all slots in the specified RWS are cleared" before the load.
            var (d, _, errors) = Create();
            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x02 }, Figure53));

            d.ProcessOutbound(Wsf(new byte[] { 0x41, 0x42, 0xC2, 0x02 }, Figure53));

            Assert.Empty(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x42)!;
            Assert.Equal(0, set.GetDot(0xC1, 0, 11));
            Assert.NotEqual(0, set.GetDot(0xC2, 0, 11));
        }

        [Fact]
        public void WithoutClearOtherSlotsKeepWhatTheyHad()
        {
            // "Code points whose symbol definitions ... are not updated by this command are
            // unaltered (subject to the CLEAR parameter)" (p. 5-23).
            var (d, _, errors) = Create();
            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x02 }, Figure53));

            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC2, 0x02 }, Figure53));

            Assert.Empty(errors);
            Assert.NotEqual(0, d.ProgrammedSymbols.Find(0x42)!.GetDot(0xC1, 0, 11));
        }

        [Fact]
        public void ALoadPsWithNoDataChangesOnlyTheSettings()
        {
            // "If this structured field does not contain data, the operation executes anyway"
            // (p. 5-18): skip suppression set, the shapes kept.
            var (d, _, errors) = Create();
            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x02 }, Figure53));

            d.ProcessOutbound(Wsf(new byte[] { 0x22, 0x42, 0xC1, 0x02 }, Array.Empty<byte>()));

            Assert.Empty(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x42)!;
            Assert.True(set.SkipSuppress);
            Assert.NotEqual(0, set.GetDot(0xC1, 0, 11));
        }

        [Fact]
        public void TheLcidMovesToTheNewStoreAndTheOldOneIsFreed()
        {
            // MULTID = B'0': the LCID is released from the store that had it, which gets X'FF'
            // (p. 5-22).
            var (d, _, errors) = Create();
            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x02 }, Figure53));

            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x03 }, Figure53));

            Assert.Empty(errors);
            Assert.Equal(0x03, d.ProgrammedSymbols.Find(0x42)!.Rws);
            Assert.Equal(ProgrammedSymbolSet.FreeLcid, d.ProgrammedSymbols[0].Lcid);
        }

        [Fact]
        public void MultidOnBothLoadsKeepsBothStores()
        {
            // MULTID = B'1' releases the other store only "unless it was loaded with MULTID = B'1'"
            // (p. 5-20, 5-22).
            var (d, _, errors) = Create();
            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x02, 0x10 }, Figure53));

            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x03, 0x02, 0x10 }, Figure53));

            Assert.Empty(errors);
            Assert.Equal(0x42, d.ProgrammedSymbols[0].Lcid);
            Assert.Equal(0x42, d.ProgrammedSymbols[1].Lcid);
        }

        [Fact]
        public void LcidFfFreesTheStore()
        {
            var (d, _, errors) = Create();
            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x02 }, Figure53));

            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0xFF, 0xC1, 0x02 }, Array.Empty<byte>()));

            Assert.Empty(errors);
            Assert.Null(d.ProgrammedSymbols.Find(0x42));
            Assert.Null(d.ProgrammedSymbols.Find(0xFF));
        }

        [Theory]
        [InlineData(0x01, 0x30, 0xC1, 0x02)] // LCID below X'40'
        [InlineData(0x01, 0xF0, 0xC1, 0x02)] // LCID X'F0': reserved for Load PS (the base set's)
        [InlineData(0x01, 0x42, 0x40, 0x02)] // CHAR X'40'
        [InlineData(0x01, 0x42, 0xFF, 0x02)] // CHAR X'FF'
        [InlineData(0x01, 0x42, 0xC1, 0x01)] // RWS X'01', the Graphic Escape set, not loadable
        [InlineData(0x01, 0x42, 0xC1, 0x08)] // RWS past the six loadable sets
        [InlineData(0x00, 0x42, 0xC1, 0x02)] // type 0, reserved
        [InlineData(0x07, 0x42, 0xC1, 0x02)] // type 7, reserved
        [InlineData(0x08, 0x42, 0xC1, 0x02)] // type 8, vector, not built
        public void ABadHeaderIsRejectedAndChangesNothing(byte flags, byte lcid, byte first, byte rws)
        {
            var (d, _, errors) = Create();
            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x55, 0xC1, 0x02 }, Figure53));

            d.ProcessOutbound(Wsf(new byte[] { flags, lcid, first, rws }, Figure53));

            Assert.Single(errors);

            // "when an error condition is detected before the contents of the associated RWS are
            // altered, an error sense code is sent but the LCID is not changed" (p. 5-22).
            Assert.Equal(0x55, d.ProgrammedSymbols[0].Lcid);
            Assert.NotEqual(0, d.ProgrammedSymbols[0].GetDot(0xC1, 0, 11));
        }

        [Fact]
        public void DataCutInsideASymbolLoadsTheWholeOnesAndTakesTheLcid()
        {
            // "The data is exhausted. In this case the last complete cell definition in the data
            // is loaded. If there are any excess bits, they are ignored and a -RSP ... is
            // returned" (p. 5-22); "Where the error condition occurs during the update of the RWS,
            // the error condition is sent and the LCID is updated" (p. 5-22).
            var (d, _, errors) = Create();
            byte[] data = new byte[18 + 5];
            Array.Copy(Figure53, data, 18);

            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x02 }, data));

            Assert.Single(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x42)!;
            Assert.NotEqual(0, set.GetDot(0xC1, 0, 11));
        }

        [Fact]
        public void DataPastTheFeSlotIsRejectedAfterLoadingFe()
        {
            // "The slot corresponding to X'FE' is loaded. Excess data is ignored and a -RSP ... is
            // returned" (p. 5-22).
            var (d, _, errors) = Create();
            byte[] data = new byte[36];
            Array.Copy(Figure53, data, 18);
            Array.Copy(Figure53, 0, data, 18, 18);

            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xFE, 0x02 }, data));

            Assert.Single(errors);
            Assert.NotEqual(0, d.ProgrammedSymbols.Find(0x42)!.GetDot(0xFE, 0, 11));
        }

        [Fact]
        public void MoreSymbolsThanEcharAllowsIsRejectedWithTheSlotsUpToEcharLoaded()
        {
            // ECHAR X'C1' with CHAR X'C1': one symbol; two sent (p. 5-24). P LENGTH 8 reaches
            // byte 14.
            var (d, _, errors) = Create();
            byte[] data = new byte[36];
            Array.Copy(Figure53, data, 18);
            Array.Copy(Figure53, 0, data, 18, 18);

            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC1 }, data));

            Assert.Single(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x42)!;
            Assert.NotEqual(0, set.GetDot(0xC1, 0, 11));
            Assert.Equal(0, set.GetDot(0xC2, 0, 11));
        }

        [Fact]
        public void TheSymbolEnvelopeTableIsStoredPerCode()
        {
            // SDPP and USET set, NW 1, NH 1 (P LENGTH 10 reaches byte 16): one entry per symbol,
            // L X'06', X'01', a width pair and a height pair, then the Terminator X'02FF', then
            // the symbol (p. 5-25, 5-26).
            var (d, _, errors) = Create();
            var data = new List<byte> { 0x06, 0x01, 0x01, 0x02, 0x03, 0x04, 0x02, 0xFF };
            data.AddRange(Figure53);

            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x0A, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01 }, data.ToArray()));

            Assert.Empty(errors);
            ProgrammedSymbolSet set = d.ProgrammedSymbols.Find(0x42)!;
            Assert.True(set.UseEnvelopeTable);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, set.GetEnvelope(0xC1).ToArray());
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, set.GetEnvelope(0xC2).ToArray());
            AssertPicture(set, 0xC1, Figure53Picture);
        }

        [Fact]
        public void AnEnvelopePairWiderThanTheCellIsRejectedBeforeAnythingChanges()
        {
            // "The sum of the values of any pair must not exceed the width of the definition
            // cell" (p. 5-25): 5 + 5 > 9.
            var (d, _, errors) = Create();
            var data = new List<byte> { 0x06, 0x01, 0x05, 0x05, 0x00, 0x00, 0x02, 0xFF };
            data.AddRange(Figure53);

            d.ProcessOutbound(Wsf(new byte[] { 0x81, 0x42, 0xC1, 0x02, 0x0A, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01 }, data.ToArray()));

            Assert.Single(errors);
            Assert.Null(d.ProgrammedSymbols.Find(0x42));
        }

        [Fact]
        public void ARejectedLoadPsStopsTheFieldsAfterIt()
        {
            // "any sequence of Load PSs following the failing Load PS is not executed" (p. 5-22).
            var (d, _, errors) = Create();
            var b = new List<byte> { Command3270.WriteStructuredField, 0x00, 0x07, 0x06, 0x08, 0x42, 0xC1, 0x02 };
            b.AddRange(new byte[] { 0x00, 0x19, 0x06, 0x01, 0x43, 0xC1, 0x03 });
            b.AddRange(Figure53);

            d.ProcessOutbound(b.ToArray());

            Assert.Single(errors);
            Assert.Null(d.ProgrammedSymbols.Find(0x43));
        }

        [Fact]
        public void TheCharacterSetsReplyOffersLoadPsAndListsTheLoadableSets()
        {
            var (d, sent, errors) = Create();
            d.ProcessOutbound(Wsf(new byte[] { 0x01, 0x42, 0xC1, 0x03 }, Figure53));
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x01, 0xFF, 0x02 });

            Assert.Empty(errors);
            byte[] record = Assert.Single(sent);
            int pos = 1;
            byte[]? cs = null;
            while (pos < record.Length)
            {
                int length = (record[pos] << 8) | record[pos + 1];
                if (record[pos + 3] == QueryCode.CharacterSets)
                {
                    cs = record.AsSpan(pos, length).ToArray();
                }

                pos += length;
            }

            Assert.NotNull(cs);

            // Base (p. 6-21, 6-22): flags X'B2' ALT, LOADABLE, EXT, GF; PSCS; SDW 9, SDH 16; FORM
            // types 1 to 6; DL 7. Then 8 descriptors of 7 bytes: 13 + 56 = 69.
            Assert.Equal(new byte[] { 0x00, 0x45, 0x81, 0x85, 0xB2, 0x40, 0x09, 0x10, 0x7E, 0x00, 0x00, 0x00, 0x07 }, cs!.AsSpan(0, 13).ToArray());

            // Loadable descriptors (p. 6-24): SET 2 free, SET 3 with LCID X'42', LOAD and TRIPLE.
            Assert.Equal(new byte[] { 0x02, 0xC0, 0xFF, 0x00, 0x00, 0x00, 0x00 }, cs.AsSpan(27, 7).ToArray());
            Assert.Equal(new byte[] { 0x03, 0xC0, 0x42, 0x00, 0x00, 0x00, 0x00 }, cs.AsSpan(34, 7).ToArray());
            Assert.Equal(0x07, cs[62]);
        }

        [Fact]
        public void TheUsableAreaGivesRealDotDistancesBecauseLoadPsIsSupported()
        {
            // "X'FFFFFFFF' ... is not allowed if Load PS is supported" (p. 6-89): UNITS mm, Xr, Yr.
            var (d, sent, _) = Create();
            d.ProcessOutbound(new byte[] { Command3270.WriteStructuredField, 0x00, 0x05, 0x01, 0xFF, 0x02 });

            byte[] record = sent[0];
            int pos = 1;
            int first = (record[pos] << 8) | record[pos + 1];
            pos += first;
            Assert.Equal(QueryCode.UsableArea, record[pos + 3]);
            Assert.Equal(new byte[] { 0x01, 0x00, 0x0A, 0x02, 0xE5, 0x00, 0x02, 0x00, 0x6F }, record.AsSpan(pos + 10, 9).ToArray());
        }
    }
}
