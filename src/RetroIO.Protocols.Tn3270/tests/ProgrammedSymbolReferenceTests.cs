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
    /// The character set values SA, SFE and MF carry, checked against the sets the display has
    /// and the LCIDs Load Programmed Symbols has loaded (GA23-0059-4 p. 4-6, 4-16, 4-19, 5-22), and
    /// the rows whose skip is suppressed (p. 5-21).
    /// </summary>
    public class ProgrammedSymbolReferenceTests
    {
        // Figure 5-3 (p. 5-28), one type 1 symbol of 18 bytes.
        private static readonly byte[] Figure53 =
        {
            0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0xFF, 0x00, 0x00, 0x00, 0x00,
        };

        private static (Display3270 Display, List<string> Errors) Create()
        {
            var display = new Display3270(24, 80, 24, 80);
            var errors = new List<string>();
            display.ProtocolError += errors.Add;
            return (display, errors);
        }

        // A basic-form Load PS (p. 5-19): flags (type 1, plus SKIP X'20' when asked), LCID, CHAR
        // X'C1', RWS; then one symbol, or none.
        private static byte[] LoadPs(byte lcid, byte rws, bool skip = false, bool withSymbol = true)
        {
            var b = new List<byte> { 0x00, 0x00, StructuredFieldId.LoadProgrammedSymbols, (byte)(0x01 | (skip ? 0x20 : 0x00)), lcid, 0xC1, rws };
            if (withSymbol)
            {
                b.AddRange(Figure53);
            }

            b[0] = (byte)(b.Count >> 8);
            b[1] = (byte)b.Count;
            b.Insert(0, Command3270.WriteStructuredField);
            return b.ToArray();
        }

        // A Write with WCC 0 and the given orders and data.
        private static byte[] Write(params byte[] ordersAndData)
        {
            var record = new byte[2 + ordersAndData.Length];
            record[0] = Command3270.Write;
            Array.Copy(ordersAndData, 0, record, 2, ordersAndData.Length);
            return record;
        }

        [Fact]
        public void SaTakesTheDefaultTheBaseAndTheGraphicEscapeSet()
        {
            // X'00' is the default set (p. 4-19); X'F0' and X'F1' are the non-loadable sets the
            // Character Sets reply names (p. 6-26).
            var (d, errors) = Create();

            Assert.True(d.ProcessOutbound(Write(Order3270.SetAttribute, 0x43, 0x00, 0xC1, Order3270.SetAttribute, 0x43, 0xF0, 0xC1, Order3270.SetAttribute, 0x43, 0xF1, 0xC1)));

            Assert.Empty(errors);
            Assert.Equal(0xF1, d.GetCharacterCharacterSet(2));
        }

        [Fact]
        public void SaNamingAnLcidNoLoadPsHasLoadedIsRejectedAndTakenOnceLoaded()
        {
            // "Attribute values that are unknown or cannot be maintained and returned inbound by
            // an implementation must be rejected" (p. 4-6); X'40' to X'EF' are "Local ID for
            // loadable character sets" (p. 4-19).
            var (d, errors) = Create();

            Assert.False(d.ProcessOutbound(Write(Order3270.SetAttribute, 0x43, 0x43, 0xC1)));
            Assert.Single(errors);
            Assert.Equal(0x00, d.GetByte(0));

            Assert.True(d.ProcessOutbound(LoadPs(0x43, 0x02)));
            Assert.True(d.ProcessOutbound(Write(Order3270.SetAttribute, 0x43, 0x43, 0xC1)));
            Assert.Single(errors);
            Assert.Equal(0x43, d.GetCharacterCharacterSet(0));
        }

        [Fact]
        public void AReleasedLcidIsAnErrorInSfeAndMf()
        {
            // "The PS RWS number specified in this structured field is released from any
            // previously assigned LCID. Any subsequent reference to this released LCID will be an
            // error condition" (p. 5-22). RWS 2 gets LCID X'43', then LCID X'44'.
            var (d, errors) = Create();
            d.ProcessOutbound(LoadPs(0x43, 0x02));
            d.ProcessOutbound(LoadPs(0x44, 0x02, withSymbol: false));
            Assert.Empty(errors);

            Assert.False(d.ProcessOutbound(Write(Order3270.StartFieldExtended, 0x01, 0x43, 0x43)));
            Assert.Single(errors);

            Assert.True(d.ProcessOutbound(Write(Order3270.StartFieldExtended, 0x01, 0x43, 0x44)));
            Assert.Equal(0x44, d.GetFieldCharacterSet(0));

            // MF at the field attribute just made (p. 4-6).
            Assert.False(d.ProcessOutbound(Write(Order3270.SetBufferAddress, 0x00, 0x00, Order3270.ModifyField, 0x01, 0x43, 0x43)));
            Assert.Equal(2, errors.Count);
            Assert.Equal(0x44, d.GetFieldCharacterSet(0));
        }

        [Fact]
        public void ValuesTheDisplayDoesNotHaveOrThatAreReservedAreRejected()
        {
            // p. 4-19: X'F2' to X'F7' non-loadable sets this display does not list; X'F8' to
            // X'FE' two-byte sets; X'FF' "is invalid if sent as a value byte (sense code
            // X'1003')"; X'01' to X'3F' "All others reserved".
            byte[] values = { 0xF2, 0xF7, 0xF8, 0xFE, 0xFF, 0x01, 0x3F };
            for (int i = 0; i < values.Length; i++)
            {
                var (d, errors) = Create();
                Assert.False(d.ProcessOutbound(Write(Order3270.SetAttribute, 0x43, values[i], 0xC1)), "X'" + values[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "'");
                Assert.Single(errors);
            }
        }

        [Fact]
        public void TheCharacterOverridesTheFieldAndGraphicEscapeOverridesBoth()
        {
            // p. 4-19: "the character's ID overrides (for that character) the field's LCID";
            // p. 4-9 note 1: GE "always takes precedence over any other character set". A field
            // with set X'43' holding: a plain character, one with SA X'F0', and a GE character.
            var (d, errors) = Create();
            d.ProcessOutbound(LoadPs(0x43, 0x02));
            d.ProcessOutbound(Write(
                Order3270.StartFieldExtended, 0x01, 0x43, 0x43,
                0xC1,
                Order3270.SetAttribute, 0x43, 0xF0, 0xC2,
                Order3270.GraphicEscape, 0xC3));

            Assert.Empty(errors);
            Partition3270 p = d.ActivePartition;
            Assert.Equal(0x43, Display3270.GetCharacterSetAt(p, 1));
            Assert.Equal(0xF0, Display3270.GetCharacterSetAt(p, 2));
            Assert.Equal(Display3270.GraphicEscapeCharacterSetId, Display3270.GetCharacterSetAt(p, 3));
        }

        [Fact]
        public void ARowHoldingACharacterOfASkipSuppressingSetHasItsSkipSuppressed()
        {
            // "If skip suppression is on, any row in the usable area containing characters from
            // this character set will have the skip suppressed that would normally follow that
            // row" (p. 5-21). Row 2 holds an X'C1' from LCID X'43', loaded with SKIP.
            var (d, errors) = Create();
            d.ProcessOutbound(LoadPs(0x43, 0x02, skip: true));
            d.ProcessOutbound(Write(Order3270.SetBufferAddress, 0x00, 0xA5, Order3270.SetAttribute, 0x43, 0x43, 0xC1));

            Assert.Empty(errors);
            Assert.False(d.RowSuppressesSkip(1));
            Assert.True(d.RowSuppressesSkip(2));
            Assert.False(d.RowSuppressesSkip(3));
        }

        [Fact]
        public void WithoutSkipOrWithOnlyNullsNoRowIsSuppressed()
        {
            // "If the skip suppression flag is not set, no skip suppression is required" (p. 5-21).
            // A field in a SKIP set whose rows hold only nulls has no "characters from this
            // character set" in them (this display's reading).
            var (d, errors) = Create();
            d.ProcessOutbound(LoadPs(0x43, 0x02, skip: false));
            d.ProcessOutbound(LoadPs(0x44, 0x03, skip: true));
            d.ProcessOutbound(Write(
                Order3270.SetBufferAddress, 0x00, 0xA0, Order3270.SetAttribute, 0x43, 0x43, 0xC1,
                Order3270.SetBufferAddress, 0x01, 0x90, Order3270.StartFieldExtended, 0x01, 0x43, 0x44));

            Assert.Empty(errors);
            Assert.False(d.RowSuppressesSkip(2));
            Assert.False(d.RowSuppressesSkip(5));
            Assert.False(d.RowSuppressesSkip(6));

            // One character typed into the SKIP field's first row: that row only.
            d.ProcessOutbound(Write(Order3270.SetBufferAddress, 0x01, 0x91, 0xC1));
            Assert.True(d.RowSuppressesSkip(5));
            Assert.False(d.RowSuppressesSkip(6));
        }
    }
}
