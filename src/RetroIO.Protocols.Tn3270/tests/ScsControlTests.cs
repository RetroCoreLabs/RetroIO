//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    /// <summary>
    /// The SCS codes against the table of GA23-0061-2 p. 2-25, copied row by row from the page
    /// image (the scan's text layer shifts the names one row, so it is not the reference), and the
    /// TN3270E function codes against RFC 2355 section 3.
    /// </summary>
    public class ScsControlTests
    {
        [Fact]
        public void TheScsCodesAreThoseOfTheTable()
        {
            Assert.Equal(0x16, ScsControl.BackSpace);
            Assert.Equal(0x2F, ScsControl.Bell);
            Assert.Equal(0x0D, ScsControl.CarriageReturn);
            Assert.Equal(0x14, ScsControl.EnablePresentation);
            Assert.Equal(0x0C, ScsControl.FormFeed);
            Assert.Equal(0x08, ScsControl.GraphicEscape);
            Assert.Equal(0x05, ScsControl.HorizontalTab);
            Assert.Equal(0x24, ScsControl.InhibitPresentation);
            Assert.Equal(0x1E, ScsControl.InterchangeRecordSeparator);
            Assert.Equal(0x25, ScsControl.LineFeed);
            Assert.Equal(0x15, ScsControl.NewLine);
            Assert.Equal(0x28, ScsControl.SetAttribute);
            Assert.Equal(0x2B, ScsControl.FormatPrefix);
            Assert.Equal(0xC1, ScsControl.SetHorizontalFormat);
            Assert.Equal(0xC6, ScsControl.SetLineDensity);
            Assert.Equal(0xC2, ScsControl.SetVerticalFormat);
            Assert.Equal(0x35, ScsControl.Transparent);
            Assert.Equal(0x04, ScsControl.VerticalChannelSelect);
            Assert.Equal(0x0B, ScsControl.VerticalTab);
        }

        [Fact]
        public void TheTn3270EFunctionCodesAreThoseOfRfc2355()
        {
            Assert.Equal(0x00, Tn3270EFunction.BindImage);
            Assert.Equal(0x01, Tn3270EFunction.DataStreamControl);
            Assert.Equal(0x02, Tn3270EFunction.Responses);
            Assert.Equal(0x03, Tn3270EFunction.ScsControlCodes);
            Assert.Equal(0x04, Tn3270EFunction.SysReq);
        }
    }
}
