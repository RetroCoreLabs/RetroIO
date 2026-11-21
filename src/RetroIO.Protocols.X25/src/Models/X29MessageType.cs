//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// X.29 PAD Control Message Types according to ITU-T X.29 specification.
    /// These message types control the operation of a Packet Assembler/Disassembler (PAD)
    /// and manage the interface between character-mode terminals and X.25 networks.
    /// </summary>
    public enum X29MessageType : byte
    {
        /// <summary>
        /// Parameter Indication (0x00) - Host → PAD parameter values.
        /// Used by the host to inform the PAD of parameter values that should be applied.
        /// Contains one or more parameter-value pairs that define PAD behavior.
        /// </summary>
        ParameterIndication = 0x00,

        /// <summary>
        /// Invitation to Clear (0x01) - Request to clear call.
        /// Sent by either the PAD or host to request termination of the X.25 virtual circuit.
        /// This is a polite way to request call clearing rather than forcing it.
        /// </summary>
        InvitationToClear = 0x01,

        /// <summary>
        /// Set Parameters (0x02) - Set PAD parameters.
        /// Used to modify the operational parameters of the PAD during an active session.
        /// Contains parameter-value pairs that override current PAD configuration.
        /// </summary>
        SetParameters = 0x02,

        /// <summary>
        /// Indication of Break (0x03) - Break signal received.
        /// Indicates that a BREAK signal has been received from the terminal.
        /// Used to signal urgent conditions or interrupt processing to the remote host.
        /// </summary>
        IndicationOfBreak = 0x03,

        /// <summary>
        /// Read Parameters (0x04) - Read PAD parameters.
        /// Requests the current values of specified PAD parameters.
        /// The PAD responds with a Parameter Indication message containing the requested values.
        /// </summary>
        ReadParameters = 0x04,

        /// <summary>
        /// Error (0x05) - Error indication.
        /// Indicates that an error has occurred in X.29 message processing.
        /// Contains an error code that specifies the type of error encountered.
        /// </summary>
        Error = 0x05,

        /// <summary>
        /// Set and Read Parameters (0x06) - Set and read parameters.
        /// Combines the functionality of Set Parameters and Read Parameters in one message.
        /// Allows setting some parameters while simultaneously reading others.
        /// </summary>
        SetAndReadParameters = 0x06
    }

    /// <summary>
    /// The error type of an X.29 error PAD message, octet 2: X.29 (12/97) Table 4.
    /// </summary>
    /// <remarks>
    /// Until 2026-11-21 this enum held five made-up codes (01 parameter unavailable, 02 parameter value invalid, 03
    /// message too long, 04 procedure error, 05 system error): 01, 03 and 05 do not exist in Table 4, and 02 and 04 mean
    /// other things there (RetroIO BUGS 45). A parameter that cannot be set or read is not an error PAD message at all:
    /// Table 1 answers it with a parameter indication whose reference has the error bit (4.4.5.2).
    /// </remarks>
    public enum X29ErrorCode : byte
    {
        /// <summary>
        /// Table 4 case a, 00000000: "Received PAD message contained less than eight bits". No octet 3 (Figure 3, Note).
        /// </summary>
        LessThanEightBits = 0x00,

        /// <summary>
        /// Table 4 case b, 00000010: "Unrecognized message code in received PAD message" (3.5.2 a).
        /// </summary>
        UnrecognizedMessageCode = 0x02,

        /// <summary>
        /// Table 4 case c, 00000100: "Parameter field format of received PAD message was incorrect or incompatible with
        /// message code" (3.5.2 b and c).
        /// </summary>
        ParameterFieldFormat = 0x04,

        /// <summary>
        /// Table 4 case d, 00000110: "Received PAD message did not contain an integral number of octets".
        /// </summary>
        NotIntegralOctets = 0x06,

        /// <summary>
        /// Table 4 case e, 00001000: "Received parameter indication PAD message was unsolicited" (3.5.2 d).
        /// </summary>
        UnsolicitedParameterIndication = 0x08,

        /// <summary>
        /// Table 4 case f, 00001010: "Received PAD message was too long" (3.5.2 e).
        /// </summary>
        TooLong = 0x0A,

        /// <summary>
        /// Table 4 case g, 00001100: "Unauthorized reselection PAD message", only from a PAD that does reselection (3.6).
        /// </summary>
        UnauthorizedReselection = 0x0C,
    }

    /// <summary>
    /// Extension methods for X29MessageType enum to provide additional functionality.
    /// </summary>
    public static class X29MessageTypeExtensions
    {
        /// <summary>
        /// Determines if the message type is sent from Host to PAD.
        /// </summary>
        /// <param name="messageType">
        /// The X.29 message type
        /// </param>
        /// <returns>
        /// True if this message is typically sent from Host to PAD
        /// </returns>
        public static bool IsHostToPadMessage(this X29MessageType messageType)
        {
            return messageType switch
            {
                X29MessageType.ParameterIndication => true,
                X29MessageType.SetParameters => true,
                X29MessageType.ReadParameters => true,
                X29MessageType.SetAndReadParameters => true,
                _ => false
            };
        }

        /// <summary>
        /// Determines if the message type is sent from PAD to Host.
        /// </summary>
        /// <param name="messageType">
        /// The X.29 message type
        /// </param>
        /// <returns>
        /// True if this message is typically sent from PAD to Host
        /// </returns>
        public static bool IsPadToHostMessage(this X29MessageType messageType)
        {
            return messageType switch
            {
                X29MessageType.InvitationToClear => true,
                X29MessageType.IndicationOfBreak => true,
                X29MessageType.Error => true,
                _ => false
            };
        }

        /// <summary>
        /// Determines if the message type can be sent in either direction.
        /// </summary>
        /// <param name="messageType">
        /// The X.29 message type
        /// </param>
        /// <returns>
        /// True if this message can be sent in either direction
        /// </returns>
        public static bool IsBidirectionalMessage(this X29MessageType messageType)
        {
            return messageType switch
            {
                X29MessageType.InvitationToClear => true,
                X29MessageType.Error => true,
                _ => false
            };
        }

        /// <summary>
        /// Gets a human-readable description of the message type.
        /// </summary>
        /// <param name="messageType">
        /// The X.29 message type
        /// </param>
        /// <returns>
        /// A descriptive string explaining the message type
        /// </returns>
        public static string GetDescription(this X29MessageType messageType)
        {
            return messageType switch
            {
                X29MessageType.ParameterIndication => "Host informs PAD of parameter values",
                X29MessageType.InvitationToClear => "Request to terminate X.25 virtual circuit",
                X29MessageType.SetParameters => "Modify PAD operational parameters",
                X29MessageType.IndicationOfBreak => "Terminal BREAK signal received",
                X29MessageType.ReadParameters => "Request current PAD parameter values",
                X29MessageType.Error => "X.29 message processing error occurred",
                X29MessageType.SetAndReadParameters => "Combined set and read parameter operation",
                _ => "Unknown X.29 message type"
            };
        }
    }

    /// <summary>
    /// Extension methods for X29ErrorCode enum to provide additional functionality.
    /// </summary>
    public static class X29ErrorCodeExtensions
    {
        /// <summary>
        /// Gets a human-readable description of the error code.
        /// </summary>
        /// <param name="errorCode">
        /// The X.29 error code
        /// </param>
        /// <returns>
        /// A descriptive string explaining the error
        /// </returns>
        public static string GetDescription(this X29ErrorCode errorCode)
        {
            // The words of X.29 (12/97) Table 4.
            return errorCode switch
            {
                X29ErrorCode.LessThanEightBits => "Received PAD message contained less than eight bits",
                X29ErrorCode.UnrecognizedMessageCode => "Unrecognized message code in received PAD message",
                X29ErrorCode.ParameterFieldFormat => "Parameter field format of received PAD message was incorrect or incompatible with message code",
                X29ErrorCode.NotIntegralOctets => "Received PAD message did not contain an integral number of octets",
                X29ErrorCode.UnsolicitedParameterIndication => "Received parameter indication PAD message was unsolicited",
                X29ErrorCode.TooLong => "Received PAD message was too long",
                X29ErrorCode.UnauthorizedReselection => "Unauthorized reselection PAD message",
                _ => "Unknown X.29 error code"
            };
        }
    }
}