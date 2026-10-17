//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Link.Models
{
    /// <summary>
    /// HDLC (High-Level Data Link Control) special characters according to RFC 1662 and ISO/IEC 13239.
    /// These constants define the special bytes used in HDLC framing and byte-stuffing procedures.
    /// HDLC provides reliable data transmission over point-to-point and multipoint links.
    /// </summary>
    public enum HdlcSpecialByte : byte
    {
        /// <summary>
        /// HDLC Frame Delimiter (0x7E) - Flag sequence.
        /// Marks the beginning and end of HDLC frames.
        /// This character cannot appear in the data portion of a frame due to byte-stuffing.
        /// Also known as the Flag character in HDLC terminology.
        /// </summary>
        Flag = 0x7E,

        /// <summary>
        /// HDLC Escape Character (0x7D) - Control Escape.
        /// Used in byte-stuffing to escape special characters within frame data.
        /// When this character appears in data, it indicates the next byte is escaped.
        /// Also known as the Control Escape character in HDLC terminology.
        /// </summary>
        Escape = 0x7D,

        /// <summary>
        /// Escaped Flag (0x5E) - Flag after escape processing.
        /// When a 0x7E byte needs to appear in frame data, it is transmitted as 0x7D 0x5E.
        /// This is the result of XORing the Flag (0x7E) with the Escape Mask (0x20).
        /// </summary>
        EscapedFlag = 0x5E,

        /// <summary>
        /// Escaped Control Escape (0x5D) - Escape character after escape processing.
        /// When a 0x7D byte needs to appear in frame data, it is transmitted as 0x7D 0x5D.
        /// This is the result of XORing the Escape (0x7D) with the Escape Mask (0x20).
        /// </summary>
        EscapedEscape = 0x5D
    }

    /// <summary>
    /// HDLC Address Field Values according to ISO/IEC 13239 specification.
    /// These define the address field values used in HDLC frames for point-to-point links.
    /// </summary>
    public enum HdlcAddress : byte
    {
        /// <summary>
        /// DTE to DCE Address (0x01) - Data Terminal Equipment to Data Communication Equipment.
        /// Used when the DTE (terminal) is sending frames to the DCE (modem/network equipment).
        /// This is the standard address for frames originated by the terminal side.
        /// </summary>
        DteToDce = 0x01,

        /// <summary>
        /// DCE to DTE Address (0x03) - Data Communication Equipment to Data Terminal Equipment.
        /// Used when the DCE (modem/network equipment) is sending frames to the DTE (terminal).
        /// This is the standard address for frames originated by the network side.
        /// </summary>
        DceToDte = 0x03
    }

    /// <summary>
    /// HDLC processing constants and masks used in frame handling.
    /// </summary>
    public static class HdlcMasks
    {
        /// <summary>
        /// Escape Mask (0x20) - XOR mask for byte-stuffing.
        /// This mask is XORed with special characters during escape processing.
        /// Used to transform Flag (0x7E) to EscapedFlag (0x5E) and Escape (0x7D) to EscapedEscape (0x5D).
        /// </summary>
        public const byte EscapeMask = 0x20;

        /// <summary>
        /// Control Character Threshold (0x20) - ASCII control character boundary.
        /// Characters below this value (0x00-0x1F) are considered control characters.
        /// Some HDLC implementations optionally escape these characters per RFC 1662.
        /// </summary>
        public const byte ControlCharThreshold = 0x20;

        /// <summary>
        /// Information Frame Mask (0x01) - LSB mask for frame type detection.
        /// Information frames have LSB = 0, so (control & 0x01) == 0 indicates I-frame.
        /// </summary>
        public const byte InformationFrameMask = 0x01;

        /// <summary>
        /// Supervisory Frame Mask (0x03) - Two LSB mask for frame type detection.
        /// Supervisory frames have bits 1-0 = 01, so (control & 0x03) == 0x01 indicates S-frame.
        /// </summary>
        public const byte SupervisoryFrameMask = 0x03;

        /// <summary>
        /// Supervisory Frame Pattern (0x01) - Expected pattern for S-frame detection.
        /// Used with SupervisoryFrameMask to identify supervisory frames.
        /// </summary>
        public const byte SupervisoryFramePattern = 0x01;

        /// <summary>
        /// Supervisory Type Mask (0x0F) - Mask for extracting supervisory frame subtype.
        /// Used to extract the specific type of supervisory frame (RR, RNR, REJ, etc.).
        /// </summary>
        public const byte SupervisoryTypeMask = 0x0F;

        /// <summary>
        /// Unnumbered Frame P/F Mask (0xEF) - Mask to remove Poll/Final bit from U-frames.
        /// Used to extract the unnumbered frame type while ignoring the P/F bit.
        /// </summary>
        public const byte UnnumberedPFMask = 0xEF;

        /// <summary>
        /// Low Byte Mask (0xFF) - Mask for extracting low 8 bits.
        /// Commonly used in CRC calculations and multi-byte value handling.
        /// </summary>
        public const byte LowByteMask = 0xFF;

        /// <summary>
        /// High Byte Shift (8) - Bit shift for extracting high byte from 16-bit value.
        /// Used in CRC calculations: (fcs >> 8) & 0xFF gives high byte.
        /// </summary>
        public const int HighByteShift = 8;
    }

    /// <summary>
    /// HDLC CRC (Frame Check Sequence) constants for error detection.
    /// These values are used in the CRC-16 calculation for HDLC frame validation.
    /// </summary>
    public static class HdlcCrc
    {
        /// <summary>
        /// CRC-16 Initial Value (0xFFFF) - Starting value for CRC calculation.
        /// The CRC register is initialized to all 1s before processing frame data.
        /// This provides better error detection characteristics than starting with 0x0000.
        /// </summary>
        public const ushort InitialValue = 0xFFFF;

        /// <summary>
        /// CRC-16 Good Frame Value (0xF0B8) - Expected CRC after processing valid frame.
        /// When a complete valid HDLC frame (including its FCS) is processed through
        /// the CRC algorithm, the result should be this constant value.
        /// </summary>
        public const ushort GoodFrameValue = 0xF0B8;
    }

    /// <summary>
    /// Extension methods for HdlcSpecialByte enum to provide additional functionality.
    /// </summary>
    public static class HdlcSpecialByteExtensions
    {
        /// <summary>
        /// Determines if this byte requires escape processing in HDLC frames.
        /// </summary>
        /// <param name="specialByte">The HDLC special byte</param>
        /// <returns>True if this byte must be escaped when appearing in frame data</returns>
        public static bool RequiresEscaping(this HdlcSpecialByte specialByte)
        {
            return specialByte == HdlcSpecialByte.Flag || specialByte == HdlcSpecialByte.Escape;
        }

        /// <summary>
        /// Gets the escaped version of this special byte.
        /// </summary>
        /// <param name="specialByte">The HDLC special byte to escape</param>
        /// <returns>The escaped version, or null if escaping is not applicable</returns>
        public static HdlcSpecialByte? GetEscapedVersion(this HdlcSpecialByte specialByte)
        {
            return specialByte switch
            {
                HdlcSpecialByte.Flag => HdlcSpecialByte.EscapedFlag,
                HdlcSpecialByte.Escape => HdlcSpecialByte.EscapedEscape,
                _ => null
            };
        }

        /// <summary>
        /// Gets the original (unescaped) version of this escaped byte.
        /// </summary>
        /// <param name="specialByte">The escaped HDLC special byte</param>
        /// <returns>The original version, or null if unescaping is not applicable</returns>
        public static HdlcSpecialByte? GetUnescapedVersion(this HdlcSpecialByte specialByte)
        {
            return specialByte switch
            {
                HdlcSpecialByte.EscapedFlag => HdlcSpecialByte.Flag,
                HdlcSpecialByte.EscapedEscape => HdlcSpecialByte.Escape,
                _ => null
            };
        }

        /// <summary>
        /// Gets a human-readable description of the special byte.
        /// </summary>
        /// <param name="specialByte">The HDLC special byte</param>
        /// <returns>A descriptive string explaining the special byte's purpose</returns>
        public static string GetDescription(this HdlcSpecialByte specialByte)
        {
            return specialByte switch
            {
                HdlcSpecialByte.Flag => "Frame delimiter marking beginning and end of HDLC frames",
                HdlcSpecialByte.Escape => "Control escape character for byte-stuffing special characters",
                HdlcSpecialByte.EscapedFlag => "Flag character after escape processing (0x7D 0x5E sequence)",
                HdlcSpecialByte.EscapedEscape => "Escape character after escape processing (0x7D 0x5D sequence)",
                _ => "Unknown HDLC special byte"
            };
        }
    }

    /// <summary>
    /// Extension methods for HdlcAddress enum to provide additional functionality.
    /// </summary>
    public static class HdlcAddressExtensions
    {
        /// <summary>
        /// Determines if this is a valid HDLC address for point-to-point links.
        /// </summary>
        /// <param name="address">The HDLC address value</param>
        /// <returns>True if this is a valid point-to-point address</returns>
        public static bool IsValidPointToPointAddress(this HdlcAddress address)
        {
            return address == HdlcAddress.DteToDce || address == HdlcAddress.DceToDte;
        }

        /// <summary>
        /// Gets the opposite address for response frames.
        /// </summary>
        /// <param name="address">The original HDLC address</param>
        /// <returns>The address to use in response frames</returns>
        public static HdlcAddress GetResponseAddress(this HdlcAddress address)
        {
            return address switch
            {
                HdlcAddress.DteToDce => HdlcAddress.DceToDte,
                HdlcAddress.DceToDte => HdlcAddress.DteToDce,
                _ => address
            };
        }

        /// <summary>
        /// Determines if this address indicates DTE origination.
        /// </summary>
        /// <param name="address">The HDLC address value</param>
        /// <returns>True if this frame was originated by a DTE</returns>
        public static bool IsDteOriginated(this HdlcAddress address)
        {
            return address == HdlcAddress.DteToDce;
        }

        /// <summary>
        /// Determines if this address indicates DCE origination.
        /// </summary>
        /// <param name="address">The HDLC address value</param>
        /// <returns>True if this frame was originated by a DCE</returns>
        public static bool IsDceOriginated(this HdlcAddress address)
        {
            return address == HdlcAddress.DceToDte;
        }

        /// <summary>
        /// Gets a human-readable description of the address.
        /// </summary>
        /// <param name="address">The HDLC address</param>
        /// <returns>A descriptive string explaining the address meaning</returns>
        public static string GetDescription(this HdlcAddress address)
        {
            return address switch
            {
                HdlcAddress.DteToDce => "Frame from Data Terminal Equipment to Data Communication Equipment",
                HdlcAddress.DceToDte => "Frame from Data Communication Equipment to Data Terminal Equipment",
                _ => "Unknown HDLC address"
            };
        }
    }
}