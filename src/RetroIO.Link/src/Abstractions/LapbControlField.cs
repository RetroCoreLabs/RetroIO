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
    /// LAPB Control Field Values according to ITU-T LAPB (Link Access Procedure, Balanced) specification.
    /// LAPB is the data link layer protocol used in X.25 networks, providing reliable frame delivery
    /// and flow control over point-to-point links.
    /// This enum provides the specific control field values for different LAPB frame types.
    /// </summary>
    public enum LapbControlField : byte
    {
        // Information Frames (I-frames) - LSB = 0
        // Note: I-frames have variable control field values based on N(S) and N(R) sequence numbers
        // This enum provides the base identifier for I-frames

        /// <summary>
        /// Information Frame base identifier (0x00).
        /// I-frames carry user data and have the format: N(R) | P/F | N(S) | 0
        /// where N(S) = send sequence number, N(R) = receive sequence number, P/F = poll/final bit.
        /// Used for normal data transfer with acknowledgment and flow control.
        /// </summary>
        InformationFrame = 0x00,

        // Supervisory Frames (S-frames) - bits 1-0 = 01
        // These frames provide flow control and error recovery functions

        /// <summary>
        /// Receive Ready (RR) base identifier (0x01).
        /// Indicates readiness to receive information frames and acknowledges previously received I-frames.
        /// Format: N(R) | P/F | 0 | 0 | 0 | 1
        /// where N(R) indicates the next expected I-frame sequence number.
        /// </summary>
        ReceiveReady = 0x01,

        /// <summary>
        /// Receive Not Ready (RNR) base identifier (0x05).
        /// Indicates temporary inability to receive information frames due to busy condition.
        /// Format: N(R) | P/F | 0 | 1 | 0 | 1
        /// Acknowledges I-frames up to N(R)-1 but requests no further I-frames until RR is sent.
        /// </summary>
        ReceiveNotReady = 0x05,

        /// <summary>
        /// Reject (REJ) base identifier (0x09).
        /// Requests retransmission of I-frames starting from N(R).
        /// Format: N(R) | P/F | 1 | 0 | 0 | 1
        /// Used for error recovery when an I-frame is received out of sequence.
        /// </summary>
        Reject = 0x09,

        // Unnumbered Frames (U-frames) - bits 1-0 = 11
        // These frames control the link establishment, disconnection, and error reporting

        /// <summary>
        /// Set Asynchronous Balanced Mode (SABM) command (0x2F).
        /// Requests establishment of data link in balanced mode (both stations are primaries).
        /// Used to initialize or reset the data link connection.
        /// Response: UA (Unnumbered Acknowledgment) or DM (Disconnected Mode).
        /// </summary>
        SetAsynchronousBalancedMode = 0x2F,

        /// <summary>
        /// Set Asynchronous Balanced Mode Extended (SABME) command (0x6F).
        /// Similar to SABM but supports extended sequence numbering (modulo 128 vs modulo 8).
        /// Used in high-speed links where larger sequence number space is needed.
        /// Response: UA (Unnumbered Acknowledgment) or DM (Disconnected Mode).
        /// </summary>
        SetAsynchronousBalancedModeExtended = 0x6F,

        /// <summary>
        /// Disconnect (DISC) command (0x43).
        /// Requests termination of the data link connection.
        /// Sent when the station wishes to disconnect from the remote station.
        /// Response: UA (Unnumbered Acknowledgment) or DM (Disconnected Mode).
        /// </summary>
        Disconnect = 0x43,

        /// <summary>
        /// Unnumbered Acknowledgment (UA) response (0x63).
        /// Acknowledges SABM, SABME, or DISC commands.
        /// Indicates acceptance of the command and successful mode change.
        /// Used to confirm link establishment or disconnection.
        /// </summary>
        UnnumberedAcknowledgment = 0x63,

        /// <summary>
        /// Disconnected Mode (DM) response (0x0F).
        /// Indicates that the station is in disconnected mode and cannot accept I-frames.
        /// Sent in response to SABM, SABME, DISC, or any frame received while disconnected.
        /// Also sent when the station cannot comply with the requested mode.
        /// </summary>
        DisconnectedMode = 0x0F,

        /// <summary>
        /// Frame Reject (FRMR) response (0x87).
        /// Indicates that an invalid or unacceptable frame has been received.
        /// Contains information field with details about the rejected frame.
        /// Causes both stations to enter the frame reject condition requiring reset.
        /// </summary>
        FrameReject = 0x87,

        // Additional U-frame types (less commonly used)

        /// <summary>
        /// Set Normal Response Mode (SNRM) command (0x83).
        /// Legacy command for establishing unbalanced mode (not used in LAPB).
        /// LAPB uses balanced mode (SABM) instead of normal response mode.
        /// </summary>
        SetNormalResponseMode = 0x83,

        /// <summary>
        /// Set Asynchronous Response Mode (SARM) command (0x0C).
        /// Legacy command for asynchronous unbalanced mode (not used in LAPB).
        /// LAPB uses balanced mode (SABM) instead.
        /// </summary>
        SetAsynchronousResponseMode = 0x0C,

        /// <summary>
        /// Set Normal Response Mode Extended (SNRME) command (0x9E).
        /// Legacy command for extended unbalanced mode (not used in LAPB).
        /// LAPB uses balanced mode (SABME) instead.
        /// </summary>
        SetNormalResponseModeExtended = 0x9E,

        /// <summary>
        /// Set Asynchronous Response Mode Extended (SARME) command (0x8C).
        /// Legacy command for extended asynchronous unbalanced mode (not used in LAPB).
        /// LAPB uses balanced mode (SABME) instead.
        /// </summary>
        SetAsynchronousResponseModeExtended = 0x8C,

        /// <summary>
        /// Unnumbered Information (UI) frame (0x03).
        /// Carries unnumbered information without acknowledgment or flow control.
        /// Not commonly used in LAPB as it operates in balanced mode with numbered frames.
        /// </summary>
        UnnumberedInformation = 0x03,

        /// <summary>
        /// Exchange Identification (XID) frame (0xAF).
        /// Used to exchange station identification and characteristics.
        /// Can be used for negotiating parameters like modulo, window size, etc.
        /// Response: XID frame with station's identification information.
        /// </summary>
        ExchangeIdentification = 0xAF,

        /// <summary>
        /// Test (TEST) frame (0xE3).
        /// Used to test the data link by sending test information.
        /// The receiving station returns the same information in a TEST response.
        /// Used for link testing and diagnostics.
        /// </summary>
        Test = 0xE3
    }

    /// <summary>
    /// LAPB Frame Categories for classification of frame types.
    /// </summary>
    public enum LapbFrameCategory
    {
        /// <summary>
        /// Information frames - carry user data with sequence numbers
        /// </summary>
        Information,

        /// <summary>
        /// Supervisory frames - provide flow control and acknowledgment
        /// </summary>
        Supervisory,

        /// <summary>
        /// Unnumbered frames - control link establishment and disconnection
        /// </summary>
        Unnumbered,

        /// <summary>
        /// Unknown or invalid frame type
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Extension methods for LapbControlField enum to provide additional functionality.
    /// </summary>
    public static class LapbControlFieldExtensions
    {
        /// <summary>
        /// Determines the category of the LAPB control field value.
        /// </summary>
        /// <param name="controlField">
        /// The LAPB control field value
        /// </param>
        /// <returns>
        /// The frame category (Information, Supervisory, or Unnumbered)
        /// </returns>
        public static LapbFrameCategory GetCategory(this LapbControlField controlField)
        {
            byte value = (byte)controlField;
            
            // Information frames: LSB = 0
            if ((value & 0x01) == 0)
                return LapbFrameCategory.Information;
            
            // Supervisory frames: bits 1-0 = 01
            if ((value & 0x03) == 0x01)
                return LapbFrameCategory.Supervisory;
            
            // Unnumbered frames: bits 1-0 = 11
            if ((value & 0x03) == 0x03)
                return LapbFrameCategory.Unnumbered;
            
            return LapbFrameCategory.Unknown;
        }

        /// <summary>
        /// Determines if the control field represents a command frame.
        /// </summary>
        /// <param name="controlField">
        /// The LAPB control field value
        /// </param>
        /// <returns>
        /// True if this is typically a command frame
        /// </returns>
        public static bool IsCommand(this LapbControlField controlField)
        {
            return controlField switch
            {
                LapbControlField.SetAsynchronousBalancedMode => true,
                LapbControlField.SetAsynchronousBalancedModeExtended => true,
                LapbControlField.Disconnect => true,
                LapbControlField.SetNormalResponseMode => true,
                LapbControlField.SetAsynchronousResponseMode => true,
                LapbControlField.SetNormalResponseModeExtended => true,
                LapbControlField.SetAsynchronousResponseModeExtended => true,
                LapbControlField.ExchangeIdentification => true,
                LapbControlField.Test => true,
                _ => false
            };
        }

        /// <summary>
        /// Determines if the control field represents a response frame.
        /// </summary>
        /// <param name="controlField">
        /// The LAPB control field value
        /// </param>
        /// <returns>
        /// True if this is typically a response frame
        /// </returns>
        public static bool IsResponse(this LapbControlField controlField)
        {
            return controlField switch
            {
                LapbControlField.UnnumberedAcknowledgment => true,
                LapbControlField.DisconnectedMode => true,
                LapbControlField.FrameReject => true,
                _ => false
            };
        }

        /// <summary>
        /// Determines if the control field requires a response.
        /// </summary>
        /// <param name="controlField">
        /// The LAPB control field value
        /// </param>
        /// <returns>
        /// True if this control field typically requires a response
        /// </returns>
        public static bool RequiresResponse(this LapbControlField controlField)
        {
            return controlField switch
            {
                LapbControlField.SetAsynchronousBalancedMode => true,
                LapbControlField.SetAsynchronousBalancedModeExtended => true,
                LapbControlField.Disconnect => true,
                LapbControlField.InformationFrame => true, // Requires acknowledgment
                LapbControlField.ExchangeIdentification => true,
                LapbControlField.Test => true,
                _ => false
            };
        }

        /// <summary>
        /// Gets a human-readable description of the control field value.
        /// </summary>
        /// <param name="controlField">
        /// The LAPB control field value
        /// </param>
        /// <returns>
        /// A descriptive string explaining the control field
        /// </returns>
        public static string GetDescription(this LapbControlField controlField)
        {
            return controlField switch
            {
                LapbControlField.InformationFrame => "Information frame carrying user data with sequence numbers",
                LapbControlField.ReceiveReady => "Ready to receive, acknowledges previous frames",
                LapbControlField.ReceiveNotReady => "Temporarily unable to receive due to busy condition",
                LapbControlField.Reject => "Request retransmission starting from N(R)",
                LapbControlField.SetAsynchronousBalancedMode => "Establish balanced mode connection (modulo 8)",
                LapbControlField.SetAsynchronousBalancedModeExtended => "Establish balanced mode connection (modulo 128)",
                LapbControlField.Disconnect => "Request termination of data link connection",
                LapbControlField.UnnumberedAcknowledgment => "Acknowledge command and confirm mode change",
                LapbControlField.DisconnectedMode => "Station is disconnected and cannot accept I-frames",
                LapbControlField.FrameReject => "Invalid or unacceptable frame received",
                LapbControlField.SetNormalResponseMode => "Legacy unbalanced mode command (not used in LAPB)",
                LapbControlField.SetAsynchronousResponseMode => "Legacy asynchronous unbalanced mode (not used in LAPB)",
                LapbControlField.SetNormalResponseModeExtended => "Legacy extended unbalanced mode (not used in LAPB)",
                LapbControlField.SetAsynchronousResponseModeExtended => "Legacy extended async unbalanced mode (not used in LAPB)",
                LapbControlField.UnnumberedInformation => "Unnumbered information without acknowledgment",
                LapbControlField.ExchangeIdentification => "Exchange station identification and parameters",
                LapbControlField.Test => "Test data link with loopback information",
                _ => "Unknown LAPB control field value"
            };
        }

        /// <summary>
        /// Gets the typical response control field for a command frame.
        /// </summary>
        /// <param name="controlField">
        /// The LAPB command control field value
        /// </param>
        /// <returns>
        /// The expected response control field value, or null if no specific response
        /// </returns>
        public static LapbControlField? GetExpectedResponse(this LapbControlField controlField)
        {
            return controlField switch
            {
                LapbControlField.SetAsynchronousBalancedMode => LapbControlField.UnnumberedAcknowledgment,
                LapbControlField.SetAsynchronousBalancedModeExtended => LapbControlField.UnnumberedAcknowledgment,
                LapbControlField.Disconnect => LapbControlField.UnnumberedAcknowledgment,
                LapbControlField.ExchangeIdentification => LapbControlField.ExchangeIdentification,
                LapbControlField.Test => LapbControlField.Test,
                _ => null
            };
        }
    }
}