//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// X.29 PAD/Host Protocol implementation according to ITU-T X.29 standard
    /// Handles control information exchange between PAD facility and packet mode DTE/host
    /// Manages parameter control messages, break indications, and error handling
    /// </summary>
    public class X29
    {
        private readonly ILogger? _logger;
        

        /// <summary>
        /// Creates the X.29 helper.
        /// </summary>
        /// <param name="logger">
        /// Optional diagnostics logger; may be null.
        /// </param>
        public X29(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Create X.29 SET PARAMETERS message
        /// </summary>
        /// <param name="parameters">
        /// Parameters to set (parameter number → value)
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateSetParameters(Dictionary<byte, byte> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                throw new ArgumentException("Parameters cannot be null or empty");
            }

            var message = new List<byte> { (byte)X29MessageType.SetParameters };

            // Sort by key
            var sortedKeys = new List<byte>(parameters.Keys);
            sortedKeys.Sort();

            for (int i = 0; i < sortedKeys.Count; i++)
            {
                byte key = sortedKeys[i];
                byte value = parameters[key];
                // Validate parameter before adding
                if (X3.IsValidParameter(key, value))
                {
                    message.Add(key);   // Parameter number
                    message.Add(value); // Parameter value
                }
                else
                {
                    _logger?.LogWarning("X.29: Invalid parameter {ParamNum}={Value} excluded from SET message",
                        key, value);
                }
            }

            var result = message.ToArray();
            _logger?.LogDebug("X.29: Created SET PARAMETERS message with {Count} parameters ({Length} bytes)",
                parameters.Count, result.Length);

            return result;
        }

        /// <summary>
        /// Creates an X.29 read PAD message for the given parameters; none reads them all.
        /// </summary>
        /// <remarks>
        /// Each reference goes with a value of zero: X.29 (12/97) 4.4.5.3, "Value fields in read PAD messages are coded
        /// as all binary 0s". The references are sorted (4.4.5.1 allows any order; sorted keeps the message the same for
        /// the same set). Until 2026-11-21 they were sent bare (04 02 03), which a PAD reads as parameter 2 with the
        /// value 3 ignored (RetroIO BUGS 45).
        /// </remarks>
        /// <param name="parameterNumbers">
        /// The X.3 references to read; empty or null reads every parameter.
        /// </param>
        /// <returns>
        /// The PAD message, to send with Q = 1.
        /// </returns>
        public byte[] CreateReadParameters(params byte[] parameterNumbers)
        {
            byte[] sorted = parameterNumbers == null ? Array.Empty<byte>() : (byte[])parameterNumbers.Clone();
            Array.Sort(sorted);
            byte[] result = X29ParameterField.BuildRead((byte)X29MessageType.ReadParameters, sorted);
            _logger?.LogDebug("X.29: Created READ PARAMETERS message for {Count} parameters",
                parameterNumbers?.Length ?? 0);

            return result;
        }

        /// <summary>
        /// Create X.29 SET AND READ PARAMETERS message
        /// </summary>
        /// <param name="parameters">
        /// Parameters to set
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateSetAndReadParameters(Dictionary<byte, byte> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                throw new ArgumentException("Parameters cannot be null or empty");
            }

            var message = new List<byte> { (byte)X29MessageType.SetAndReadParameters };

            // Sort by key
            var sortedKeys = new List<byte>(parameters.Keys);
            sortedKeys.Sort();

            for (int i = 0; i < sortedKeys.Count; i++)
            {
                byte key = sortedKeys[i];
                byte value = parameters[key];
                if (X3.IsValidParameter(key, value))
                {
                    message.Add(key);
                    message.Add(value);
                }
            }

            var result = message.ToArray();
            _logger?.LogDebug("X.29: Created SET AND READ PARAMETERS message with {Count} parameters",
                parameters.Count);

            return result;
        }

        /// <summary>
        /// Create X.29 PARAMETER INDICATION message
        /// </summary>
        /// <param name="parameters">
        /// Current parameter values to indicate
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateParameterIndication(Dictionary<byte, byte> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                throw new ArgumentException("Parameters cannot be null or empty");
            }

            var message = new List<byte> { (byte)X29MessageType.ParameterIndication };

            // Sort by key
            var sortedKeys = new List<byte>(parameters.Keys);
            sortedKeys.Sort();

            for (int i = 0; i < sortedKeys.Count; i++)
            {
                byte key = sortedKeys[i];
                message.Add(key);
                message.Add(parameters[key]);
            }

            var result = message.ToArray();
            _logger?.LogDebug("X.29: Created PARAMETER INDICATION message with {Count} parameters",
                parameters.Count);

            return result;
        }

        /// <summary>
        /// Create X.29 INVITATION TO CLEAR message
        /// </summary>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateInvitationToClear()
        {
            _logger?.LogDebug("X.29: Created INVITATION TO CLEAR message");
            return new byte[] { (byte)X29MessageType.InvitationToClear };
        }

        /// <summary>
        /// Create X.29 INDICATION OF BREAK message
        /// </summary>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateIndicationOfBreak()
        {
            _logger?.LogDebug("X.29: Created INDICATION OF BREAK message");
            return new byte[] { (byte)X29MessageType.IndicationOfBreak };
        }

        /// <summary>
        /// Create X.29 ERROR message
        /// </summary>
        /// <param name="errorType">
        /// Type of error
        /// </param>
        /// <param name="errorData">
        /// Additional error data (optional)
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateError(byte errorType, byte[]? errorData = null)
        {
            var message = new List<byte> { (byte)X29MessageType.Error, errorType };

            if (errorData != null && errorData.Length > 0)
            {
                for (int i = 0; i < errorData.Length; i++)
                {
                    message.Add(errorData[i]);
                }
            }

            var result = message.ToArray();
            _logger?.LogDebug("X.29: Created ERROR message type {ErrorType} ({Length} bytes)",
                errorType, result.Length);

            return result;
        }

        /// <summary>
        /// Parse X.29 message from received data
        /// </summary>
        /// <param name="data">
        /// Raw message data
        /// </param>
        /// <returns>
        /// Parsed X.29 message
        /// </returns>
        public X29Message ParseMessage(byte[] data)
        {
            if (data == null || data.Length < 1)
            {
                _logger?.LogWarning("X.29: Invalid message data - null or empty");
                return new X29Message { Type = 0xFF, IsValid = false, ErrorMessage = "Null or empty data" };
            }

            var message = new X29Message
            {
                Type = data[0],
                IsValid = true,
                RawData = data
            };

            try
            {
                switch (data[0])
                {
                    case (byte)X29MessageType.SetParameters:
                    case (byte)X29MessageType.SetAndReadParameters:
                    case (byte)X29MessageType.ReadParameters:
                    case (byte)X29MessageType.ParameterIndication:
                        // The shared reader of the parameter field (X29ParameterField, X.29 4.4.5): a read is pairs
                        // with zero values (4.4.5.3); only an indication has the error bit (4.4.5.2); nothing is dropped,
                        // what to do with a value is the receiver's business (AnswerHostPadMessage for the PAD). Until
                        // 2026-11-21 a read was taken as bare references and a pair whose value looked invalid was
                        // dropped (RetroIO BUGS 45).
                        X29ParameterField.Read(data, 1, data[0] == (byte)X29MessageType.ParameterIndication,
                            message.ParameterNumbers, message.Parameters, message.ParametersInError);
                        _logger?.LogTrace("X.29: Parsed {MessageType} with {Count} parameters",
                            GetMessageTypeName(data[0]), message.ParameterNumbers.Count);
                        break;
                        
                    case (byte)X29MessageType.InvitationToClear:
                    case (byte)X29MessageType.IndicationOfBreak:
                        // These messages have no additional data
                        _logger?.LogTrace("X.29: Parsed {MessageType}", GetMessageTypeName(data[0]));
                        break;
                        
                    case (byte)X29MessageType.Error:
                        if (data.Length >= 2)
                        {
                            message.ErrorType = data[1];
                            if (data.Length > 2)
                            {
                                message.ErrorData = new byte[data.Length - 2];
                                Array.Copy(data, 2, message.ErrorData, 0, data.Length - 2);
                            }
                        }
                        _logger?.LogTrace("X.29: Parsed ERROR message type {ErrorType}",
                            message.ErrorType);
                        break;
                        
                    default:
                        message.IsValid = false;
                        message.ErrorMessage = $"Unknown message type: 0x{data[0]:X2}";
                        _logger?.LogWarning("X.29: Unknown message type: 0x{MessageType:X2}", data[0]);
                        break;
                }
            }
            catch (Exception ex)
            {
                message.IsValid = false;
                message.ErrorMessage = ex.Message;
                _logger?.LogError(ex, "X.29: Error parsing message type 0x{MessageType:X2}", data[0]);
            }

            return message;
        }

        /// <summary>
        /// Get human-readable message type name
        /// </summary>
        /// <param name="messageType">
        /// Message type byte
        /// </param>
        /// <returns>
        /// Message type name
        /// </returns>
        public static string GetMessageTypeName(byte messageType)
        {
            return messageType switch
            {
                (byte)X29MessageType.ParameterIndication => "PARAMETER INDICATION",
                (byte)X29MessageType.InvitationToClear => "INVITATION TO CLEAR",
                (byte)X29MessageType.SetParameters => "SET PARAMETERS",
                (byte)X29MessageType.IndicationOfBreak => "INDICATION OF BREAK",
                (byte)X29MessageType.ReadParameters => "READ PARAMETERS",
                (byte)X29MessageType.Error => "ERROR",
                (byte)X29MessageType.SetAndReadParameters => "SET AND READ PARAMETERS",
                _ => $"Unknown (0x{messageType:X2})"
            };
        }

        /// <summary>
        /// Get human-readable error type name
        /// </summary>
        /// <param name="errorType">
        /// Error type byte
        /// </param>
        /// <returns>
        /// Error type name
        /// </returns>
        public static string GetErrorTypeName(byte errorType)
        {
            // The words of X.29 Table 4, from the one list (X29ErrorCodeExtensions.GetDescription). Until 2026-11-21
            // this named five codes that are not in Table 4 (RetroIO BUGS 45).
            if (Enum.IsDefined(typeof(X29ErrorCode), errorType))
            {
                return ((X29ErrorCode)errorType).GetDescription();
            }

            return $"Unknown error (0x{errorType:X2})";
        }

        /// <summary>
        /// Validate X.29 message format and content
        /// </summary>
        /// <param name="data">
        /// Message data to validate
        /// </param>
        /// <returns>
        /// Validation result
        /// </returns>
        public X29ValidationResult ValidateMessage(byte[] data)
        {
            var result = new X29ValidationResult { IsValid = true };
            
            if (data == null || data.Length < 1)
            {
                result.IsValid = false;
                result.ErrorMessage = "Message data is null or empty";
                return result;
            }
            
            byte messageType = data[0];
            
            // Check message type validity
            if (!IsValidMessageType(messageType))
            {
                result.IsValid = false;
                result.ErrorMessage = $"Invalid message type: 0x{messageType:X2}";
                return result;
            }
            
            // Check message length constraints
            switch (messageType)
            {
                case (byte)X29MessageType.SetParameters:
                case (byte)X29MessageType.SetAndReadParameters:
                case (byte)X29MessageType.ParameterIndication:
                    if ((data.Length - 1) % 2 != 0)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Parameter message must have even number of parameter bytes";
                        return result;
                    }
                    if (data.Length > 45) // Max 22 parameters * 2 + 1 message type
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Parameter message too long";
                        return result;
                    }
                    break;
                    
                case (byte)X29MessageType.InvitationToClear:
                case (byte)X29MessageType.IndicationOfBreak:
                    if (data.Length != 1)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = $"{GetMessageTypeName(messageType)} must be exactly 1 byte";
                        return result;
                    }
                    break;
                    
                case (byte)X29MessageType.Error:
                    if (data.Length < 2)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "ERROR message must include error type";
                        return result;
                    }
                    break;
            }
            
            result.MessageType = messageType;
            result.MessageLength = data.Length;
            
            return result;
        }

        /// <summary>
        /// Check if message type is valid
        /// </summary>
        /// <param name="messageType">
        /// Message type to check
        /// </param>
        /// <returns>
        /// True if valid message type
        /// </returns>
        private bool IsValidMessageType(byte messageType)
        {
            return messageType switch
            {
                (byte)X29MessageType.ParameterIndication or
                (byte)X29MessageType.InvitationToClear or
                (byte)X29MessageType.SetParameters or
                (byte)X29MessageType.IndicationOfBreak or
                (byte)X29MessageType.ReadParameters or
                (byte)X29MessageType.Error or
                (byte)X29MessageType.SetAndReadParameters => true,
                _ => false
            };
        }

        /// <summary>
        /// The codes of octet 2 of an error PAD message, X.29 (12/97) Table 4, as byte constants.
        /// </summary>
        /// <remarks>
        /// Each takes its value from <see cref="X29ErrorCode"/>, so the table is written down once. Until 2026-11-21
        /// the enum held made-up codes and this class was the only correct copy (RetroIO BUGS 45).
        /// </remarks>
        public static class ErrorType
        {
            /// <summary>Table 4 case a: "Received PAD message contained less than eight bits", 00000000.</summary>
            public const byte LessThanEightBits = (byte)X29ErrorCode.LessThanEightBits;

            /// <summary>Table 4 case b: "Unrecognized message code in received PAD message", 00000010.</summary>
            public const byte UnrecognizedMessageCode = (byte)X29ErrorCode.UnrecognizedMessageCode;

            /// <summary>Table 4 case c: "Parameter field format of received PAD message was incorrect or incompatible with
            /// message code", 00000100.</summary>
            public const byte ParameterFieldFormat = (byte)X29ErrorCode.ParameterFieldFormat;

            /// <summary>Table 4 case d: "Received PAD message did not contain an integral number of octets", 00000110.</summary>
            public const byte NotIntegralOctets = (byte)X29ErrorCode.NotIntegralOctets;

            /// <summary>Table 4 case e: "Received parameter indication PAD message was unsolicited", 00001000.</summary>
            public const byte UnsolicitedParameterIndication = (byte)X29ErrorCode.UnsolicitedParameterIndication;

            /// <summary>Table 4 case f: "Received PAD message was too long", 00001010.</summary>
            public const byte TooLong = (byte)X29ErrorCode.TooLong;

            /// <summary>
            /// Table 4 case g: "Unauthorized reselection PAD message", 00001100.
            /// </summary>
            public const byte UnauthorizedReselection = (byte)X29ErrorCode.UnauthorizedReselection;
        }

        /// <summary>
        /// The codes of the value field of a parameter field whose reference has the error bit set, X.29 Table 3.
        /// </summary>
        public static class ParameterErrorReason
        {
            /// <summary>"No additional information", 0 (the only mandatory value).</summary>
            public const byte NoInformation = 0;

            /// <summary>"The parameter reference does not exist or has not been implemented in the PAD", 1.</summary>
            public const byte NoSuchReference = 1;

            /// <summary>"The parameter value is invalid or has not been implemented in the PAD", 2.</summary>
            public const byte InvalidValue = 2;

            /// <summary>"The parameter is read-only", 4.</summary>
            public const byte ReadOnly = 4;
        }

        /// <summary>
        /// The PAD's side of X.29 3.1 and 3.5: a PAD message from the host (Q = 1) is applied to the PAD's own X.3
        /// parameters, and the answer the PAD must send, if any, is returned.
        /// </summary>
        /// <param name="data">The user data of the complete packet sequence with Q = 1.</param>
        /// <param name="x3">The PAD's own parameters.</param>
        /// <param name="initialProfile">Puts the parameters back to the PAD's initial profile (X.29 Table 1, a set or a set
        /// and read with no parameter field).</param>
        /// <returns>The PAD message to send back with Q = 1, or null when X.29 says nothing is sent.</returns>
        /// <remarks>
        /// <para>
        /// X.29 (12/97) Table 1, line for line: a set with no parameter field resets every parameter to the initial
        /// profile and is not answered; a set with parameters sets them and is answered only when some could not be set,
        /// with "List of these invalid parameters"; a set and read is answered with all the parameters it named, "with
        /// their new current values" (or all of them, after the reset, when it named none); a read with no field is
        /// answered with "all implemented Recommendation X.3 parameters with their current values", and with the named
        /// ones otherwise. "If any of the parameters contain an error, then the error bit is set and the value field is
        /// coded as described in Table 3" (Table 1, Note).
        /// </para>
        /// <para>
        /// The parameter field (4.4.5): reference octet then value octet; "In PAD messages received by the PAD, bit 8 of
        /// each octet will be ignored" (4.4.5.2); reference 127 means the reference goes on in the next octet as "the
        /// parameter reference of Recommendation X.3 minus 127" (4.4.5.1); reference 0 with value 0 is the parameter
        /// separator, after which come national parameters (4.4.5.4), of which this PAD has none. A read carries the same
        /// pairs, "Value fields in read PAD messages are coded as all binary 0s" (4.4.5.3).
        /// </para>
        /// <para>
        /// Errors that make the whole message invalid get an error PAD message (3.5.2, 4.4.6): octet 1 0x05, octet 2 the
        /// Table 4 code, octet 3 "the message code of the received PAD message" (4.4.6.2).
        /// </para>
        /// <para>
        /// Invitation to clear and indication of break are not answered here (3.2, 3.3); the caller acts on them.
        /// Reselection (codes 0111, 1000) is not implemented by this PAD, so it is invalid here (3.6 Note 2) and answered
        /// with an error PAD message, unrecognized message code (Table 4 b).
        /// </para>
        /// </remarks>
        public byte[]? AnswerHostPadMessage(byte[] data, X3 x3, Action initialProfile)
        {
            if (data == null || data.Length == 0)
            {
                // Table 4 case a: less than eight bits. An empty Q = 1 packet holds no octet at all; octet 3 does not
                // occur for this error type (Figure 3, Note).
                return new byte[] { (byte)X29MessageType.Error, ErrorType.LessThanEightBits };
            }

            byte code = data[0];
            switch (code)
            {
                case (byte)X29MessageType.SetParameters:
                case (byte)X29MessageType.ReadParameters:
                case (byte)X29MessageType.SetAndReadParameters:
                    break;

                case (byte)X29MessageType.ParameterIndication:
                    // 3.5.2 d: a PAD never asks the host for its parameters, so every indication it gets is unsolicited.
                    return new byte[] { (byte)X29MessageType.Error, ErrorType.UnsolicitedParameterIndication, code };

                case (byte)X29MessageType.InvitationToClear:
                case (byte)X29MessageType.IndicationOfBreak:
                    return null;

                case 0x07: // reselection
                case 0x08: // reselection with TOA/NPI
                    // X.29 3.6 Note 2: "PADs which do not implement this feature will consider reselection and
                    // reselection with TOA/NPI PAD messages as invalid", and 3.5.2 a) answers an invalid message code
                    // with an error PAD message: Table 4 case b (00000010), octet 3 the code. Not case g (unauthorized
                    // reselection): that is for a PAD that does reselect and refuses this one (3.6 a to c). Until
                    // 2026-11-21 nothing was sent (RetroIO BUGS 46).
                    return new byte[] { (byte)X29MessageType.Error, ErrorType.UnrecognizedMessageCode, code };

                default:
                    // 3.5.2 a: an unrecognizable message code.
                    return new byte[] { (byte)X29MessageType.Error, ErrorType.UnrecognizedMessageCode, code };
            }

            bool set = code != (byte)X29MessageType.ReadParameters;
            bool read = code != (byte)X29MessageType.SetParameters;

            if (data.Length == 1)
            {
                if (set)
                {
                    initialProfile();
                }

                if (!read)
                {
                    return null;
                }

                List<byte> all = new List<byte>(1 + 44) { (byte)X29MessageType.ParameterIndication };
                for (byte reference = 1; reference <= 22; reference++)
                {
                    all.Add(reference);
                    all.Add(x3.GetParameter(reference));
                }

                return all.ToArray();
            }

            // First pass: the field must be whole parameter fields, or nothing is done (3.5.2 c).
            int at = 1;
            while (at < data.Length)
            {
                int reference = data[at] & 0x7F;
                at++;
                if (reference == 127)
                {
                    if (at >= data.Length)
                    {
                        return new byte[] { (byte)X29MessageType.Error, ErrorType.ParameterFieldFormat, code };
                    }

                    at++;
                }

                if (at >= data.Length)
                {
                    return new byte[] { (byte)X29MessageType.Error, ErrorType.ParameterFieldFormat, code };
                }

                at++;
            }

            // Second pass: act on each parameter field in the order received, and build the answer.
            List<byte> answer = new List<byte>(data.Length) { (byte)X29MessageType.ParameterIndication };
            bool anyError = false;
            bool afterSeparator = false;
            at = 1;
            while (at < data.Length)
            {
                int reference = data[at] & 0x7F;
                at++;
                int extension = -1;
                if (reference == 127)
                {
                    extension = data[at] & 0x7F;
                    at++;
                }

                // The value keeps bit 8: "binary coded in bits 8 to 1" (4.4.5.3), and X.3 has values above 127 (parameter
                // 4 up to 255, 16 = 128 to 130, 20 = 128). 4.4.5.2's "bit 8 of each octet will be ignored" is read as the
                // reference octets, the only octets where bit 8 has a meaning of its own (the error bit).
                byte value = data[at];
                at++;

                if (extension < 0 && reference == 0 && value == 0)
                {
                    // The parameter separator (4.4.5.4): national parameters follow. It is not a parameter; a read and a
                    // set and read list it back so the host can tell the two groups apart in the answer.
                    afterSeparator = true;
                    if (read)
                    {
                        answer.Add(0);
                        answer.Add(0);
                    }

                    continue;
                }

                byte reason;
                bool valid;
                if (afterSeparator || extension >= 0 || reference < 1 || reference > 22)
                {
                    // This PAD has parameters 1 to 22 and no national ones.
                    valid = false;
                    reason = ParameterErrorReason.NoSuchReference;
                }
                else if (set && reference == X3.ParameterNumber.BINARY_SPEED)
                {
                    // X.3 3.11: "This parameter is a read-only parameter and cannot be changed by either of the DTEs".
                    valid = false;
                    reason = ParameterErrorReason.ReadOnly;
                }
                else if (set && !x3.SetParameter((byte)reference, value))
                {
                    valid = false;
                    reason = ParameterErrorReason.InvalidValue;
                }
                else
                {
                    valid = true;
                    reason = ParameterErrorReason.NoInformation;
                }

                if (!valid)
                {
                    anyError = true;
                    _logger?.LogDebug("X.29: PAD refuses parameter {Reference} = {Value} in message 0x{Code:X2}, reason {Reason}", reference, value, code, reason);
                }

                if (read || !valid)
                {
                    // A reference that was extended is answered extended, with the error bit on the first octet.
                    if (extension >= 0)
                    {
                        answer.Add(0x80 | 127);
                        answer.Add((byte)extension);
                    }
                    else
                    {
                        answer.Add((byte)(valid ? reference : 0x80 | reference));
                    }

                    answer.Add(valid ? x3.GetParameter((byte)reference) : reason);
                }
            }

            if (!read && !anyError)
            {
                return null;
            }

            return answer.ToArray();
        }

        #region New Enum-Based Methods (Preferred API)

        /// <summary>
        /// Create X.29 message using the typed enum (preferred method).
        /// This is the recommended way to create X.29 messages with better type safety.
        /// </summary>
        /// <param name="messageType">
        /// X.29 message type from enum
        /// </param>
        /// <param name="parameters">
        /// Parameters for parameter-related messages
        /// </param>
        /// <param name="parameterNumbers">
        /// Parameter numbers for read operations
        /// </param>
        /// <param name="errorCode">
        /// The X.29 Table 4 error type; required for an error message.
        /// </param>
        /// <param name="errorData">
        /// Additional error data
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateMessage(X29MessageType messageType, 
                                   Dictionary<byte, byte>? parameters = null,
                                   byte[]? parameterNumbers = null,
                                   X29ErrorCode? errorCode = null,
                                   byte[]? errorData = null)
        {
            _logger?.LogDebug("X.29: Creating message type {MessageType}", messageType);
            
            return messageType switch
            {
                X29MessageType.SetParameters => CreateSetParametersEnum(parameters ?? new Dictionary<byte, byte>()),
                X29MessageType.ReadParameters => CreateReadParametersEnum(parameterNumbers ?? Array.Empty<byte>()),
                X29MessageType.ParameterIndication => CreateParameterIndicationEnum(parameters ?? new Dictionary<byte, byte>()),
                X29MessageType.InvitationToClear => CreateInvitationToClearEnum(),
                X29MessageType.IndicationOfBreak => CreateIndicationOfBreakEnum(),
                // X.29 Table 4 has no general error code; until 2026-11-21 a missing code became the made-up 05
                // "system error" (RetroIO BUGS 45). The caller must name the Table 4 case.
                X29MessageType.Error => CreateErrorEnum(errorCode ?? throw new ArgumentNullException(nameof(errorCode), "an error PAD message needs its X.29 Table 4 error type"), errorData),
                X29MessageType.SetAndReadParameters => CreateSetAndReadParametersEnum(parameters ?? new Dictionary<byte, byte>(), parameterNumbers ?? Array.Empty<byte>()),
                _ => throw new ArgumentException($"Unknown X.29 message type: {messageType}")
            };
        }

        /// <summary>
        /// Create X.29 SET PARAMETERS message using enum (preferred method).
        /// </summary>
        /// <param name="parameters">
        /// Parameters to set (parameter number → value)
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateSetParametersEnum(Dictionary<byte, byte> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                throw new ArgumentException("Parameters cannot be null or empty");
            }

            var message = new List<byte> { (byte)X29MessageType.SetParameters };

            // Sort by key
            var sortedKeys = new List<byte>(parameters.Keys);
            sortedKeys.Sort();

            for (int i = 0; i < sortedKeys.Count; i++)
            {
                byte key = sortedKeys[i];
                byte value = parameters[key];
                if (X3.IsValidParameter(key, value))
                {
                    message.Add(key);
                    message.Add(value);
                }
                else
                {
                    _logger?.LogWarning("X.29: Invalid parameter {ParamNum}={Value} excluded from SET message",
                        key, value);
                }
            }

            var result = message.ToArray();
            _logger?.LogDebug("X.29: Created SET PARAMETERS message with {Count} parameters ({Length} bytes)",
                parameters.Count, result.Length);

            return result;
        }

        /// <summary>
        /// Create X.29 READ PARAMETERS message using enum (preferred method).
        /// </summary>
        /// <param name="parameterNumbers">
        /// Parameter numbers to read (empty for all)
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateReadParametersEnum(params byte[] parameterNumbers)
        {
            // The same message as CreateReadParameters; one builder, so the two cannot differ again (until 2026-11-21
            // both sent bare references, RetroIO BUGS 45).
            return CreateReadParameters(parameterNumbers);
        }

        /// <summary>
        /// Create X.29 PARAMETER INDICATION message using enum (preferred method).
        /// </summary>
        /// <param name="parameters">
        /// Parameters to indicate (parameter number → value)
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateParameterIndicationEnum(Dictionary<byte, byte> parameters)
        {
            var message = new List<byte> { (byte)X29MessageType.ParameterIndication };

            // Sort by key
            var sortedKeys = new List<byte>(parameters.Keys);
            sortedKeys.Sort();

            for (int i = 0; i < sortedKeys.Count; i++)
            {
                byte key = sortedKeys[i];
                message.Add(key);
                message.Add(parameters[key]);
            }

            var result = message.ToArray();
            _logger?.LogDebug("X.29: Created PARAMETER INDICATION message with {Count} parameters ({Length} bytes)",
                parameters.Count, result.Length);

            return result;
        }

        /// <summary>
        /// Create X.29 INVITATION TO CLEAR message using enum (preferred method).
        /// </summary>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateInvitationToClearEnum()
        {
            var result = new byte[] { (byte)X29MessageType.InvitationToClear };
            _logger?.LogDebug("X.29: Created INVITATION TO CLEAR message");
            return result;
        }

        /// <summary>
        /// Create X.29 INDICATION OF BREAK message using enum (preferred method).
        /// </summary>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateIndicationOfBreakEnum()
        {
            var result = new byte[] { (byte)X29MessageType.IndicationOfBreak };
            _logger?.LogDebug("X.29: Created INDICATION OF BREAK message");
            return result;
        }

        /// <summary>
        /// Create X.29 ERROR message using enum (preferred method).
        /// </summary>
        /// <param name="errorCode">
        /// X.29 error code
        /// </param>
        /// <param name="errorData">
        /// Optional additional error data
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateErrorEnum(X29ErrorCode errorCode, byte[]? errorData = null)
        {
            var message = new List<byte> { (byte)X29MessageType.Error, (byte)errorCode };

            if (errorData != null && errorData.Length > 0)
            {
                for (int i = 0; i < errorData.Length; i++)
                {
                    message.Add(errorData[i]);
                }
            }

            var result = message.ToArray();
            _logger?.LogDebug("X.29: Created ERROR message with code {ErrorCode} ({Length} bytes)",
                errorCode, result.Length);

            return result;
        }

        /// <summary>
        /// Create X.29 SET AND READ PARAMETERS message using enum (preferred method).
        /// </summary>
        /// <param name="parametersToSet">
        /// Parameters to set
        /// </param>
        /// <param name="parameterNumbersToRead">
        /// Parameter numbers to read
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateSetAndReadParametersEnum(Dictionary<byte, byte> parametersToSet, byte[] parameterNumbersToRead)
        {
            var message = new List<byte> { (byte)X29MessageType.SetAndReadParameters };

            // Add parameters to set (sorted by key)
            var sortedKeys = new List<byte>(parametersToSet.Keys);
            sortedKeys.Sort();

            for (int i = 0; i < sortedKeys.Count; i++)
            {
                byte key = sortedKeys[i];
                byte value = parametersToSet[key];
                if (X3.IsValidParameter(key, value))
                {
                    message.Add(key);
                    message.Add(value);
                }
            }

            // Add separator (if needed by implementation)
            // Some implementations may require a separator between set and read sections

            // Add parameter numbers to read
            if (parameterNumbersToRead != null && parameterNumbersToRead.Length > 0)
            {
                var sorted = new byte[parameterNumbersToRead.Length];
                Array.Copy(parameterNumbersToRead, sorted, parameterNumbersToRead.Length);
                Array.Sort(sorted);
                for (int i = 0; i < sorted.Length; i++)
                {
                    message.Add(sorted[i]);
                }
            }

            var result = message.ToArray();
            _logger?.LogDebug("X.29: Created SET AND READ PARAMETERS message with {SetCount} set, {ReadCount} read ({Length} bytes)",
                parametersToSet.Count, parameterNumbersToRead?.Length ?? 0, result.Length);

            return result;
        }

        #endregion

        /// <summary>
        /// Create PAD Call User Data according to X.25 specification
        /// PAD protocol identification: CUD = 0x01000000 (4 bytes)
        /// </summary>
        /// <returns>
        /// PAD protocol identifier bytes
        /// </returns>
        public byte[] CreatePadCallUserData()
        {
            // X.29 clause 4.2 (call user data format), 4.2.1 (protocol identifier format): the call user data starts
            // with four octets, the protocol identifier. Octets 2 to 4 are all zero (verified against the 1992 text of
            // Recommendation X.29, clause 4.2.1). The first octet 01 (the value that names the PAD) could not be
            // read in that text; it is from measured behaviour: a Cisco router's PAD refuses a call without
            // 01 00 00 00 and accepts it with. Not verified against the standard text.
            // Every PAD call places this automatically (PadClient.Connect); no option switches it on.
            _logger?.LogDebug("X.29: Created PAD Call User Data");
            return new byte[] { 0x01, 0x00, 0x00, 0x00 };
        }
    }

    /// <summary>
    /// X.29 message structure with support for both legacy byte-based and new enum-based APIs
    /// </summary>
    public class X29Message
    {
        /// <summary>
        /// Message type as byte (legacy API - for backward compatibility)
        /// </summary>
        public byte Type { get; set; }
        
        /// <summary>
        /// Message type using strongly-typed enum (preferred API)
        /// Automatically synchronizes with the Type byte property
        /// </summary>
        public X29MessageType MessageType
        {
            get => (X29MessageType)Type;
            set => Type = (byte)value;
        }
        
        /// <summary>
        /// Gets or sets whether the message was parsed as valid. The default is true.
        /// </summary>
        public bool IsValid { get; set; } = true;
        /// <summary>
        /// Gets or sets the error text when the message is not valid, or null.
        /// </summary>
        public string? ErrorMessage { get; set; }
        /// <summary>
        /// Gets or sets the raw message bytes, or null.
        /// </summary>
        public byte[]? RawData { get; set; }
        
        // Parameter-related messages
        /// <summary>
        /// Gets or sets the parameter number and value pairs carried by the message.
        /// </summary>
        public Dictionary<byte, byte> Parameters { get; set; } = new Dictionary<byte, byte>();

        /// <summary>
        /// Gets or sets the parameters a parameter indication reports in error, reference to reason.
        /// </summary>
        /// <remarks>
        /// X.29 (12/97) 4.4.5.2 and 4.4.5.3: bit 8 of a reference in a parameter indication is the error bit, and the
        /// value is then the reason (Table 3), not a value. Such a parameter is here and not in
        /// <see cref="Parameters"/>.
        /// </remarks>
        public Dictionary<byte, byte> ParametersInError { get; set; } = new Dictionary<byte, byte>();

        /// <summary>
        /// Gets or sets the parameter references in the message, in the order they appeared.
        /// </summary>
        public List<byte> ParameterNumbers { get; set; } = new List<byte>();
        
        // Error messages
        /// <summary>
        /// Error type as byte (legacy API - for backward compatibility)
        /// </summary>
        public byte ErrorType { get; set; }
        
        /// <summary>
        /// Error code using strongly-typed enum (preferred API)
        /// Automatically synchronizes with the ErrorType byte property
        /// </summary>
        public X29ErrorCode ErrorCode
        {
            get => (X29ErrorCode)ErrorType;
            set => ErrorType = (byte)value;
        }
        
        /// <summary>
        /// Gets or sets the extra bytes after the error type in an error message, or null.
        /// </summary>
        public byte[]? ErrorData { get; set; }
        
        /// <summary>
        /// Returns a one-line description of the X.29 message.
        /// </summary>
        /// <returns>
        /// For an invalid message, <c>Invalid X.29 Message:</c> followed by the error message.
        /// Otherwise the message type name, followed in brackets by:
        ///  - the number of parameters, for set, set-and-read and parameter indication messages.
        ///  - the number of parameters requested, for read messages.
        ///  - the error type name, for error messages.
        /// Other message types give the type name alone.
        /// </returns>
        public override string ToString()
        {
            if (!IsValid)
                return $"Invalid X.29 Message: {ErrorMessage}";
                
            string typeName = X29.GetMessageTypeName(Type);
            
            return Type switch
            {
                (byte)X29MessageType.SetParameters or 
                (byte)X29MessageType.ParameterIndication or
                (byte)X29MessageType.SetAndReadParameters => 
                    $"{typeName} ({Parameters.Count} parameters)",
                    
                (byte)X29MessageType.ReadParameters => 
                    $"{typeName} ({ParameterNumbers.Count} requested)",
                    
                (byte)X29MessageType.Error => 
                    $"{typeName} ({X29.GetErrorTypeName(ErrorType)})",
                    
                _ => typeName
            };
        }
    }

    /// <summary>
    /// X.29 message validation result
    /// </summary>
    public class X29ValidationResult
    {
        /// <summary>
        /// Gets or sets whether the message passed validation.
        /// </summary>
        public bool IsValid { get; set; }
        /// <summary>
        /// Gets or sets the error text when validation failed, or null.
        /// </summary>
        public string? ErrorMessage { get; set; }
        /// <summary>
        /// Gets or sets the message type byte.
        /// </summary>
        public byte MessageType { get; set; }
        /// <summary>
        /// Gets or sets the length of the validated data in bytes.
        /// </summary>
        public int MessageLength { get; set; }
    }
}