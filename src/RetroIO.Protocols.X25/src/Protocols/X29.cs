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
        /// Create X.29 READ PARAMETERS message  
        /// </summary>
        /// <param name="parameterNumbers">
        /// Parameter numbers to read (empty for all)
        /// </param>
        /// <returns>
        /// X.29 message bytes
        /// </returns>
        public byte[] CreateReadParameters(params byte[] parameterNumbers)
        {
            var message = new List<byte> { (byte)X29MessageType.ReadParameters };

            if (parameterNumbers != null && parameterNumbers.Length > 0)
            {
                var sorted = new byte[parameterNumbers.Length];
                Array.Copy(parameterNumbers, sorted, parameterNumbers.Length);
                Array.Sort(sorted);
                for (int i = 0; i < sorted.Length; i++)
                {
                    message.Add(sorted[i]);
                }
            }
            // If no parameters specified, request all (empty parameter list)

            var result = message.ToArray();
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
                    case (byte)X29MessageType.ParameterIndication:
                        message.Parameters = ParseParameterPairs(data, 1);
                        _logger?.LogTrace("X.29: Parsed {MessageType} with {Count} parameters", 
                            GetMessageTypeName(data[0]), message.Parameters.Count);
                        break;
                        
                    case (byte)X29MessageType.ReadParameters:
                        message.ParameterNumbers = ParseParameterNumbers(data, 1);
                        _logger?.LogTrace("X.29: Parsed READ PARAMETERS for {Count} parameters", 
                            message.ParameterNumbers.Count);
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
        /// Parse parameter number/value pairs from message data
        /// </summary>
        /// <param name="data">
        /// Message data
        /// </param>
        /// <param name="offset">
        /// Starting offset in data
        /// </param>
        /// <returns>
        /// Dictionary of parameter number → value
        /// </returns>
        private Dictionary<byte, byte> ParseParameterPairs(byte[] data, int offset)
        {
            var parameters = new Dictionary<byte, byte>();
            
            for (int i = offset; i < data.Length - 1; i += 2)
            {
                byte paramNumber = data[i];
                byte paramValue = data[i + 1];
                
                // Validate parameter
                if (X3.IsValidParameter(paramNumber, paramValue))
                {
                    parameters[paramNumber] = paramValue;
                }
                else
                {
                    _logger?.LogWarning("X.29: Invalid parameter {ParamNum}={Value} in message", 
                        paramNumber, paramValue);
                }
            }
            
            return parameters;
        }

        /// <summary>
        /// Parse parameter numbers from READ PARAMETERS message
        /// </summary>
        /// <param name="data">
        /// Message data
        /// </param>
        /// <param name="offset">
        /// Starting offset in data
        /// </param>
        /// <returns>
        /// List of parameter numbers
        /// </returns>
        private List<byte> ParseParameterNumbers(byte[] data, int offset)
        {
            var parameters = new List<byte>();
            
            for (int i = offset; i < data.Length; i++)
            {
                byte paramNumber = data[i];
                
                // Validate parameter number exists
                if (paramNumber >= 1 && paramNumber <= 22)
                {
                    parameters.Add(paramNumber);
                }
                else
                {
                    _logger?.LogWarning("X.29: Invalid parameter number {ParamNum} in READ message", 
                        paramNumber);
                }
            }
            
            return parameters;
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
            return errorType switch
            {
                (byte)X29ErrorCode.ParameterUnavailable => "Parameter unavailable",
                (byte)X29ErrorCode.ParameterValueInvalid => "Parameter value invalid",
                (byte)X29ErrorCode.MessageTooLong => "Message too long",
                (byte)X29ErrorCode.ProcedureError => "Procedure error",
                (byte)X29ErrorCode.SystemError => "System error",
                _ => $"Unknown error (0x{errorType:X2})"
            };
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
        /// Create error response for invalid parameter
        /// </summary>
        /// <param name="parameterNumber">
        /// Invalid parameter number
        /// </param>
        /// <param name="parameterValue">
        /// Invalid parameter value
        /// </param>
        /// <returns>
        /// X.29 ERROR message
        /// </returns>
        public byte[] CreateParameterError(byte parameterNumber, byte parameterValue)
        {
            return CreateError((byte)X29ErrorCode.ParameterValueInvalid, new byte[] { parameterNumber, parameterValue });
        }

        /// <summary>
        /// Process X.29 control message and generate appropriate response
        /// </summary>
        /// <param name="message">
        /// Parsed X.29 message
        /// </param>
        /// <param name="x3Parameters">
        /// Current X.3 parameters
        /// </param>
        /// <returns>
        /// Response message or null if no response needed
        /// </returns>
        public byte[]? ProcessControlMessage(X29Message message, X3 x3Parameters)
        {
            if (!message.IsValid)
            {
                return CreateError((byte)X29ErrorCode.ProcedureError);
            }

            switch (message.Type)
            {
                case (byte)X29MessageType.SetParameters:
                    return ProcessSetParameters(message, x3Parameters);
                    
                case (byte)X29MessageType.ReadParameters:
                    return ProcessReadParameters(message, x3Parameters);
                    
                case (byte)X29MessageType.SetAndReadParameters:
                    ProcessSetParameters(message, x3Parameters);
                    return ProcessReadParameters(message, x3Parameters);
                    
                case (byte)X29MessageType.InvitationToClear:
                case (byte)X29MessageType.IndicationOfBreak:
                    // These messages typically don't require responses
                    return null;
                    
                default:
                    return CreateError((byte)X29ErrorCode.ProcedureError);
            }
        }

        /// <summary>
        /// Process SET PARAMETERS message
        /// </summary>
        private byte[]? ProcessSetParameters(X29Message message, X3 x3Parameters)
        {
            var errors = new List<byte>();

            foreach (var param in message.Parameters)
            {
                if (!x3Parameters.SetParameter(param.Key, param.Value))
                {
                    errors.Add(param.Key);
                    errors.Add(param.Value);
                }
            }

            if (errors.Count > 0)
            {
                return CreateError((byte)X29ErrorCode.ParameterValueInvalid, errors.ToArray());
            }

            return null; // Success, no response needed
        }

        /// <summary>
        /// Process READ PARAMETERS message  
        /// </summary>
        private byte[] ProcessReadParameters(X29Message message, X3 x3Parameters)
        {
            Dictionary<byte, byte> responseParams;

            if (message.ParameterNumbers.Count > 0)
            {
                // Read specific parameters
                responseParams = x3Parameters.GetParameters(message.ParameterNumbers);
            }
            else
            {
                // Read all parameters
                responseParams = x3Parameters.GetAllParameters();
            }

            return CreateParameterIndication(responseParams);
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
        /// Error code for error messages
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
                X29MessageType.Error => CreateErrorEnum(errorCode ?? X29ErrorCode.SystemError, errorData),
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
            var message = new List<byte> { (byte)X29MessageType.ReadParameters };

            if (parameterNumbers != null && parameterNumbers.Length > 0)
            {
                var sorted = new byte[parameterNumbers.Length];
                Array.Copy(parameterNumbers, sorted, parameterNumbers.Length);
                Array.Sort(sorted);
                for (int i = 0; i < sorted.Length; i++)
                {
                    message.Add(sorted[i]);
                }
            }

            var result = message.ToArray();
            _logger?.LogDebug("X.29: Created READ PARAMETERS message for {Count} parameters ({Length} bytes)",
                parameterNumbers?.Length ?? 0, result.Length);

            return result;
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
            // According to X.25 spec: PAD CUD = 0x01000000 (X.29 PAD identifier)
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
        /// Gets or sets the parameter numbers listed in the message.
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
        
        /// <inheritdoc/>
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