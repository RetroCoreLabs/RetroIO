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
    /// X.29 Error Codes used in Error messages according to ITU-T X.29 specification.
    /// These codes provide specific information about errors encountered during PAD operation.
    /// </summary>
    public enum X29ErrorCode : byte
    {
        /// <summary>
        /// Parameter Unavailable (0x01) - Parameter not available.
        /// The requested parameter is not supported by this PAD implementation
        /// or is not accessible in the current operational context.
        /// </summary>
        ParameterUnavailable = 0x01,

        /// <summary>
        /// Parameter Value Invalid (0x02) - Invalid parameter value.
        /// The parameter value provided is outside the valid range or
        /// is incompatible with the parameter type or current PAD configuration.
        /// </summary>
        ParameterValueInvalid = 0x02,

        /// <summary>
        /// Message Too Long (0x03) - Message exceeds limit.
        /// The X.29 control message exceeds the maximum allowed length
        /// for this PAD implementation or network configuration.
        /// </summary>
        MessageTooLong = 0x03,

        /// <summary>
        /// Procedure Error (0x04) - Protocol procedure error.
        /// An error in the X.29 protocol sequence has been detected,
        /// such as receiving a message in an inappropriate state.
        /// </summary>
        ProcedureError = 0x04,

        /// <summary>
        /// System Error (0x05) - PAD system error.
        /// An internal error has occurred within the PAD system
        /// that prevents normal operation or message processing.
        /// </summary>
        SystemError = 0x05
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
            return errorCode switch
            {
                X29ErrorCode.ParameterUnavailable => "Requested parameter not supported or accessible",
                X29ErrorCode.ParameterValueInvalid => "Parameter value outside valid range or incompatible",
                X29ErrorCode.MessageTooLong => "X.29 message exceeds maximum allowed length",
                X29ErrorCode.ProcedureError => "Protocol sequence error or inappropriate message state",
                X29ErrorCode.SystemError => "Internal PAD system error prevents normal operation",
                _ => "Unknown X.29 error code"
            };
        }
    }
}