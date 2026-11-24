//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// The SNA character string (SCS) control codes a 3287 printer honours as an LU type 1
    /// device, as GA23-0061-2 lists them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The source: GA23-0061-2 (3274 Control Unit Description and Programmer's Guide, March 1985),
    /// chapter 2, "SCS Control Codes", p. 2-25 (the table, read from the page image: the text
    /// layer of the scan shifts the names one row against the codes), and the definitions on
    /// pp. 2-25 to 2-29. This is the only list of SCS codes held under docs\spec. The full SCS
    /// reference (IBM's SNA character string manual) is not held, so a code that is not in this
    /// list has no source here, and whoever reads the stream must refuse it out loud.
    /// </para>
    /// <para>
    /// SHF, SLD and SVF are two-byte codes, X'2B' then a second byte, and a count byte follows them
    /// that includes itself. TRN and VCS take one byte after the code (p. 2-29).
    /// </para>
    /// </remarks>
    public static class ScsControl
    {
        /// <summary>
        /// GE, Graphic Escape, X'08': the next byte is a character from the alternate set.
        /// </summary>
        public const byte GraphicEscape = 0x08;

        /// <summary>
        /// HT, Horizontal Tab, X'05': to the next tab stop, or a space when there is none.
        /// </summary>
        public const byte HorizontalTab = 0x05;

        /// <summary>
        /// VCS, Vertical Channel Select, X'04' and one channel byte: on a 3287 always a line feed.
        /// </summary>
        public const byte VerticalChannelSelect = 0x04;

        /// <summary>
        /// VT, Vertical Tab, X'0B': down to the next vertical tab stop, or a line feed when there is none.
        /// </summary>
        public const byte VerticalTab = 0x0B;

        /// <summary>
        /// FF, Forms Feed, X'0C': to the top and left margin of the next form.
        /// </summary>
        public const byte FormFeed = 0x0C;

        /// <summary>
        /// CR, Carriage Return, X'0D': to the left margin on the same line.
        /// </summary>
        public const byte CarriageReturn = 0x0D;

        /// <summary>
        /// ENP, Enable Presentation, X'14': no function on an LU type 1 device, accepted.
        /// </summary>
        public const byte EnablePresentation = 0x14;

        /// <summary>
        /// NL, New Line, X'15': carriage return and line feed.
        /// </summary>
        public const byte NewLine = 0x15;

        /// <summary>
        /// BS, Back Space, X'16': one position to the left, nothing at column 1.
        /// </summary>
        public const byte BackSpace = 0x16;

        /// <summary>
        /// IRS, Interchange-Record Separator, X'1E': a New Line on an LU-LU session.
        /// </summary>
        public const byte InterchangeRecordSeparator = 0x1E;

        /// <summary>
        /// INP, Inhibit Presentation, X'24': no function on an LU type 1 device, accepted.
        /// </summary>
        public const byte InhibitPresentation = 0x24;

        /// <summary>
        /// LF, Line Feed, X'25': down one line.
        /// </summary>
        public const byte LineFeed = 0x25;

        /// <summary>
        /// SA, Set Attribute, X'28', then an attribute type and a value (chapter 1).
        /// </summary>
        public const byte SetAttribute = 0x28;

        /// <summary>
        /// The first byte of SHF, SLD and SVF, X'2B'.
        /// </summary>
        public const byte FormatPrefix = 0x2B;

        /// <summary>
        /// The second byte of SHF, Set Horizontal Format, X'2BC1'.
        /// </summary>
        public const byte SetHorizontalFormat = 0xC1;

        /// <summary>
        /// The second byte of SVF, Set Vertical Format, X'2BC2'.
        /// </summary>
        public const byte SetVerticalFormat = 0xC2;

        /// <summary>
        /// The second byte of SLD, Set Line Density, X'2BC6'.
        /// </summary>
        public const byte SetLineDensity = 0xC6;

        /// <summary>
        /// BEL, Bell Function, X'2F'. GA23-0061-2 names it in the table and does not define it.
        /// </summary>
        public const byte Bell = 0x2F;

        /// <summary>
        /// TRN, Transparent, X'35', then a length byte and that many bytes printed without being
        /// scanned for controls.
        /// </summary>
        public const byte Transparent = 0x35;
    }

    /// <summary>
    /// The TN3270E function codes of a FUNCTIONS subnegotiation (RFC 2355 section 3).
    /// </summary>
    public static class Tn3270EFunction
    {
        /// <summary>
        /// BIND-IMAGE, X'00'.
        /// </summary>
        public const byte BindImage = 0x00;

        /// <summary>
        /// DATA-STREAM-CTL, X'01': a printer takes 3270 data (RFC 2355 section 10.2).
        /// </summary>
        public const byte DataStreamControl = 0x01;

        /// <summary>
        /// RESPONSES, X'02'.
        /// </summary>
        public const byte Responses = 0x02;

        /// <summary>
        /// SCS-CTL-CODES, X'03': a printer takes SCS data (RFC 2355 section 10.1).
        /// </summary>
        public const byte ScsControlCodes = 0x03;

        /// <summary>
        /// SYSREQ, X'04'.
        /// </summary>
        public const byte SysReq = 0x04;
    }
}
