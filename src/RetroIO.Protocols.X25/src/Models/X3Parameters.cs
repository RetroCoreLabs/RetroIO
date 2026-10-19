//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// X.3 PAD Parameters according to ITU-T X.3 standard
    /// Defines all 22 standard PAD parameters for Triple-X (X.3, X.28, X.29) services
    /// The parameter numbers, default values, valid values, descriptions and the two standard profiles
    /// all come from the X3 class, so the two classes cannot disagree.
    /// </summary>
    public class X3Parameters
    {
        private readonly Dictionary<byte, byte> _parameters = new Dictionary<byte, byte>();

        /// <summary>
        /// X.3 Parameter Numbers and their default values
        /// </summary>
        public static class ParameterNumber
        {
            /// <summary>
            /// X.3 parameter number 1 (PAD escape from data transfer mode).
            /// </summary>
            public const byte ESCAPE = X3.ParameterNumber.ESCAPE;
            /// <summary>
            /// X.3 parameter number 2 (Echo).
            /// </summary>
            public const byte ECHO = X3.ParameterNumber.ECHO;
            /// <summary>
            /// X.3 parameter number 3 (Data forwarding signal).
            /// </summary>
            public const byte DATA_FORWARDING = X3.ParameterNumber.DATA_FORWARDING;
            /// <summary>
            /// X.3 parameter number 4 (Idle timer delay).
            /// </summary>
            public const byte IDLE_TIMER = X3.ParameterNumber.IDLE_TIMER;
            /// <summary>
            /// X.3 parameter number 5 (Ancillary device control).
            /// </summary>
            public const byte ANCILLARY_DEVICE_CONTROL = X3.ParameterNumber.ANCILLARY_DEVICE_CONTROL;
            /// <summary>
            /// X.3 parameter number 6 (Control of PAD service signals).
            /// </summary>
            public const byte CONTROL_PAD_SERVICE_SIGNALS = X3.ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS;
            /// <summary>
            /// X.3 parameter number 7 (Operation of the procedure for break).
            /// </summary>
            public const byte OPERATION_BREAKOUT_PROCEDURE = X3.ParameterNumber.OPERATION_BREAKOUT_PROCEDURE;
            /// <summary>
            /// X.3 parameter number 8 (Discard output).
            /// </summary>
            public const byte DISCARD_OUTPUT = X3.ParameterNumber.DISCARD_OUTPUT;
            /// <summary>
            /// X.3 parameter number 9 (Padding after carriage return).
            /// </summary>
            public const byte PADDING_AFTER_CR = X3.ParameterNumber.PADDING_AFTER_CR;
            /// <summary>
            /// X.3 parameter number 10 (Line folding).
            /// </summary>
            public const byte LINE_FOLDING = X3.ParameterNumber.LINE_FOLDING;
            /// <summary>
            /// X.3 parameter number 11 (Binary speed (not used)).
            /// </summary>
            public const byte BINARY_SPEED = X3.ParameterNumber.BINARY_SPEED;
            /// <summary>
            /// X.3 parameter number 12 (Flow control of the PAD).
            /// </summary>
            public const byte FLOW_CONTROL_X_ON_X_OFF = X3.ParameterNumber.FLOW_CONTROL_X_ON_X_OFF;
            /// <summary>
            /// X.3 parameter number 13 (Linefeed insertion after carriage return).
            /// </summary>
            public const byte LINE_FEED_INSERTION = X3.ParameterNumber.LINE_FEED_INSERTION;
            /// <summary>
            /// X.3 parameter number 14 (Padding after line feed).
            /// </summary>
            public const byte PADDING_AFTER_LINE_FEED = X3.ParameterNumber.PADDING_AFTER_LINE_FEED;
            /// <summary>
            /// X.3 parameter number 15 (Editing).
            /// </summary>
            public const byte EDITING = X3.ParameterNumber.EDITING;
            /// <summary>
            /// X.3 parameter number 16 (Character delete).
            /// </summary>
            public const byte CHARACTER_DELETE = X3.ParameterNumber.CHARACTER_DELETE;
            /// <summary>
            /// X.3 parameter number 17 (Line delete).
            /// </summary>
            public const byte LINE_DELETE = X3.ParameterNumber.LINE_DELETE;
            /// <summary>
            /// X.3 parameter number 18 (Line display).
            /// </summary>
            public const byte LINE_DISPLAY = X3.ParameterNumber.LINE_DISPLAY;
            /// <summary>
            /// X.3 parameter number 19 (Editing PAD service signals).
            /// </summary>
            public const byte EDITING_PAD_SERVICE_SIGNALS = X3.ParameterNumber.EDITING_PAD_SERVICE_SIGNALS;
            /// <summary>
            /// X.3 parameter number 20 (Echo mask).
            /// </summary>
            public const byte ECHO_MASK = X3.ParameterNumber.ECHO_MASK;
            /// <summary>
            /// X.3 parameter number 21 (Parity treatment).
            /// </summary>
            public const byte PARITY_TREATMENT = X3.ParameterNumber.PARITY_TREATMENT;
            /// <summary>
            /// X.3 parameter number 22 (Page wait).
            /// </summary>
            public const byte PAGE_WAIT = X3.ParameterNumber.PAGE_WAIT;
        }

        /// <summary>
        /// Default X.3 parameter values according to ITU-T X.3
        /// </summary>
        public static class DefaultValue
        {
            /// <summary>
            /// Default value of the escape parameter: 1 (Enabled (DLE)).
            /// </summary>
            public const byte ESCAPE = X3.DefaultValue.ESCAPE;
            /// <summary>
            /// Default value of the echo parameter: 1 (Enabled).
            /// </summary>
            public const byte ECHO = X3.DefaultValue.ECHO;
            /// <summary>
            /// Default value of the data forwarding parameter: 126 (columns 0 and 1 and DEL, Table 1/X.28 simple profile). The value comes from X3.DefaultValue.
            /// </summary>
            public const byte DATA_FORWARDING = X3.DefaultValue.DATA_FORWARDING;
            /// <summary>
            /// Default value of the idle timer parameter: 0 (Disabled).
            /// </summary>
            public const byte IDLE_TIMER = X3.DefaultValue.IDLE_TIMER;
            /// <summary>
            /// Default value of the ancillary device control parameter: 1 (use of X-ON and X-OFF).
            /// </summary>
            public const byte ANCILLARY_DEVICE_CONTROL = X3.DefaultValue.ANCILLARY_DEVICE_CONTROL;
            /// <summary>
            /// Default value of the control pad service signals parameter: 1 (Enabled).
            /// </summary>
            public const byte CONTROL_PAD_SERVICE_SIGNALS = X3.DefaultValue.CONTROL_PAD_SERVICE_SIGNALS;
            /// <summary>
            /// Default value of the operation breakout procedure parameter: 2 (reset).
            /// </summary>
            public const byte OPERATION_BREAKOUT_PROCEDURE = X3.DefaultValue.OPERATION_BREAKOUT_PROCEDURE;
            /// <summary>
            /// Default value of the discard output parameter: 0 (normal data delivery).
            /// </summary>
            public const byte DISCARD_OUTPUT = X3.DefaultValue.DISCARD_OUTPUT;
            /// <summary>
            /// Default value of the padding after cr parameter: 0 (No padding).
            /// </summary>
            public const byte PADDING_AFTER_CR = X3.DefaultValue.PADDING_AFTER_CR;
            /// <summary>
            /// Default value of the line folding parameter: 0 (Disabled).
            /// </summary>
            public const byte LINE_FOLDING = X3.DefaultValue.LINE_FOLDING;
            /// <summary>
            /// Default value of the binary speed parameter: 0 (Not used).
            /// </summary>
            public const byte BINARY_SPEED = X3.DefaultValue.BINARY_SPEED;
            /// <summary>
            /// Default value of the flow control parameter: 1 (use of X-ON and X-OFF).
            /// </summary>
            public const byte FLOW_CONTROL_X_ON_X_OFF = X3.DefaultValue.FLOW_CONTROL_X_ON_X_OFF;
            /// <summary>
            /// Default value of the line feed insertion parameter: 0 (Disabled).
            /// </summary>
            public const byte LINE_FEED_INSERTION = X3.DefaultValue.LINE_FEED_INSERTION;
            /// <summary>
            /// Default value of the padding after line feed parameter: 0 (No padding).
            /// </summary>
            public const byte PADDING_AFTER_LINE_FEED = X3.DefaultValue.PADDING_AFTER_LINE_FEED;
            /// <summary>
            /// Default value of the editing parameter: 0 (no editing).
            /// </summary>
            public const byte EDITING = X3.DefaultValue.EDITING;
            /// <summary>
            /// Default value of the character delete parameter: 127 (DEL character).
            /// </summary>
            public const byte CHARACTER_DELETE = X3.DefaultValue.CHARACTER_DELETE;
            /// <summary>
            /// Default value of the line delete parameter: 24 (CAN character).
            /// </summary>
            public const byte LINE_DELETE = X3.DefaultValue.LINE_DELETE;
            /// <summary>
            /// Default value of the line display parameter: 18 (DC2 character).
            /// </summary>
            public const byte LINE_DISPLAY = X3.DefaultValue.LINE_DISPLAY;
            /// <summary>
            /// Default value of the editing pad service signals parameter: 1 (printing terminals).
            /// </summary>
            public const byte EDITING_PAD_SERVICE_SIGNALS = X3.DefaultValue.EDITING_PAD_SERVICE_SIGNALS;
            /// <summary>
            /// Default value of the echo mask parameter: 0 (All characters echoed).
            /// </summary>
            public const byte ECHO_MASK = X3.DefaultValue.ECHO_MASK;
            /// <summary>
            /// Default value of the parity treatment parameter: 0 (No parity checking).
            /// </summary>
            public const byte PARITY_TREATMENT = X3.DefaultValue.PARITY_TREATMENT;
            /// <summary>
            /// Default value of the page wait parameter: 0 (Disabled).
            /// </summary>
            public const byte PAGE_WAIT = X3.DefaultValue.PAGE_WAIT;
        }

        /// <summary>
        /// Initialize X.3 parameters with default values
        /// </summary>
        public X3Parameters()
        {
            SetDefaultValues();
        }

        /// <summary>
        /// Set all parameters to default values
        /// </summary>
        public void SetDefaultValues()
        {
            X3.FillDefaultValues(_parameters);
        }

        /// <summary>
        /// Set the transparent standard profile of Table 1/X.28 (ITU-T X.28, 12/97, page 9)
        /// </summary>
        public void SetTransparentProfile()
        {
            X3.FillTransparentProfile(_parameters);
        }

        /// <summary>
        /// Set the simple standard profile of Table 1/X.28 (ITU-T X.28, 12/97, page 9)
        /// </summary>
        public void SetSimpleProfile()
        {
            X3.FillSimpleProfile(_parameters);
        }

        /// <summary>
        /// Get parameter value
        /// </summary>
        public byte GetParameter(byte parameterNumber)
        {
            return _parameters.TryGetValue(parameterNumber, out byte value) ? value : (byte)0;
        }

        /// <summary>
        /// Set parameter value with validation
        /// </summary>
        public bool SetParameter(byte parameterNumber, byte value)
        {
            if (!IsValidParameter(parameterNumber, value))
                return false;

            _parameters[parameterNumber] = value;
            return true;
        }

        /// <summary>
        /// Validate parameter number and value according to X.3 standard
        /// </summary>
        public static bool IsValidParameter(byte parameterNumber, byte value)
        {
            return X3.IsValidParameter(parameterNumber, value);
        }

        /// <summary>
        /// Get parameter description
        /// </summary>
        public static string GetParameterDescription(byte parameterNumber)
        {
            return X3.GetParameterDescription(parameterNumber);
        }

        /// <summary>
        /// Get all parameters as dictionary
        /// </summary>
        public Dictionary<byte, byte> GetAllParameters()
        {
            return new Dictionary<byte, byte>(_parameters);
        }

        /// <summary>
        /// Clone parameters
        /// </summary>
        public X3Parameters Clone()
        {
            var clone = new X3Parameters();
            clone._parameters.Clear();
            foreach (var kvp in _parameters)
            {
                clone._parameters[kvp.Key] = kvp.Value;
            }
            return clone;
        }

        /// <summary>
        /// Get parameters string for display
        /// </summary>
        public override string ToString()
        {
            var parameters = new List<string>();
            foreach (var kvp in _parameters)
            {
                parameters.Add($"{kvp.Key}:{kvp.Value}");
            }
            return string.Join(",", parameters);
        }
    }

    /// <summary>
    /// X.29 PAD control messages according to ITU-T X.29
    /// </summary>
    public static class X29Messages
    {
        // Message types (Q-bit = 1)
        /// <summary>
        /// Message type code 0x02 for SET PARAMETERS.
        /// </summary>
        public const byte SET_PARAMETERS = 0x02;
        /// <summary>
        /// Message type code 0x04 for READ PARAMETERS.
        /// </summary>
        public const byte READ_PARAMETERS = 0x04;
        /// <summary>
        /// Message type code 0x06 for SET AND READ PARAMETERS.
        /// </summary>
        public const byte SET_AND_READ_PARAMETERS = 0x06;
        /// <summary>
        /// Message type code 0x00 for PARAMETER INDICATION.
        /// </summary>
        public const byte PARAMETER_INDICATION = 0x00;
        /// <summary>
        /// Message type code 0x01 for INVITATION TO CLEAR.
        /// </summary>
        public const byte INVITATION_TO_CLEAR = 0x01;
        /// <summary>
        /// Message type code 0x03 for INDICATION OF BREAK.
        /// </summary>
        public const byte INDICATION_OF_BREAK = 0x03;
        /// <summary>
        /// Message type code 0x05 for ERROR.
        /// </summary>
        public const byte ERROR = 0x05;
        
        /// <summary>
        /// Create X.29 SET PARAMETERS message
        /// </summary>
        public static byte[] CreateSetParameters(Dictionary<byte, byte> parameters)
        {
            var data = new List<byte> { SET_PARAMETERS };
            
            foreach (var param in parameters)
            {
                data.Add(param.Key);   // Parameter number
                data.Add(param.Value); // Parameter value
            }
            
            return data.ToArray();
        }

        /// <summary>
        /// Create X.29 READ PARAMETERS message
        /// </summary>
        public static byte[] CreateReadParameters(byte[] parameterNumbers)
        {
            var data = new List<byte> { READ_PARAMETERS };
            data.AddRange(parameterNumbers);
            return data.ToArray();
        }

        /// <summary>
        /// Create X.29 PARAMETER INDICATION message
        /// </summary>
        public static byte[] CreateParameterIndication(Dictionary<byte, byte> parameters)
        {
            var data = new List<byte> { PARAMETER_INDICATION };
            
            foreach (var param in parameters)
            {
                data.Add(param.Key);   // Parameter number
                data.Add(param.Value); // Parameter value
            }
            
            return data.ToArray();
        }

        /// <summary>
        /// Create X.29 INVITATION TO CLEAR message
        /// </summary>
        public static byte[] CreateInvitationToClear()
        {
            return new byte[] { INVITATION_TO_CLEAR };
        }

        /// <summary>
        /// Create X.29 INDICATION OF BREAK message
        /// </summary>
        public static byte[] CreateIndicationOfBreak()
        {
            return new byte[] { INDICATION_OF_BREAK };
        }

        /// <summary>
        /// Create X.29 ERROR message
        /// </summary>
        public static byte[] CreateError(byte errorType)
        {
            return new byte[] { ERROR, errorType };
        }

        /// <summary>
        /// Parse X.29 message from data
        /// </summary>
        public static X29Message ParseMessage(byte[] data)
        {
            if (data == null || data.Length < 1)
                return new X29Message { Type = 0xFF, IsValid = false };

            var message = new X29Message
            {
                Type = data[0],
                IsValid = true
            };

            switch (data[0])
            {
                case SET_PARAMETERS:
                case SET_AND_READ_PARAMETERS:
                case PARAMETER_INDICATION:
                    message.Parameters = ParseParameterPairs(data, 1);
                    break;
                    
                case READ_PARAMETERS:
                    message.ParameterNumbers = new List<byte>();
                    for (int i = 1; i < data.Length; i++)
                    {
                        message.ParameterNumbers.Add(data[i]);
                    }
                    break;
                    
                case ERROR:
                    if (data.Length >= 2)
                        message.ErrorType = data[1];
                    break;
            }

            return message;
        }

        /// <summary>
        /// Parse parameter number/value pairs
        /// </summary>
        private static Dictionary<byte, byte> ParseParameterPairs(byte[] data, int offset)
        {
            var parameters = new Dictionary<byte, byte>();
            
            for (int i = offset; i < data.Length - 1; i += 2)
            {
                byte paramNumber = data[i];
                byte paramValue = data[i + 1];
                parameters[paramNumber] = paramValue;
            }
            
            return parameters;
        }
    }

    /// <summary>
    /// X.29 message structure
    /// </summary>
    public class X29Message
    {
        /// <summary>
        /// Gets or sets the X.29 message type code.
        /// </summary>
        public byte Type { get; set; }
        /// <summary>
        /// Gets or sets whether the message was parsed as valid.
        /// </summary>
        public bool IsValid { get; set; }
        /// <summary>
        /// Gets or sets the parameter number and value pairs carried by the message.
        /// </summary>
        public Dictionary<byte, byte> Parameters { get; set; } = new Dictionary<byte, byte>();
        /// <summary>
        /// Gets or sets the parameter numbers listed in the message, in the order they appeared.
        /// </summary>
        public List<byte> ParameterNumbers { get; set; } = new List<byte>();
        /// <summary>
        /// Gets or sets the error type code carried by an error message.
        /// </summary>
        public byte ErrorType { get; set; }
        
        /// <summary>
        /// Returns the name of the X.29 message type.
        /// </summary>
        /// <returns>
        /// The message type in words, such as <c>SET PARAMETERS</c>; for an error message
        /// <c>ERROR</c> followed by the error type in brackets; for any other type <c>Unknown</c>
        /// followed by the type number in brackets.
        /// </returns>
        public override string ToString()
        {
            return Type switch
            {
                X29Messages.SET_PARAMETERS => "SET PARAMETERS",
                X29Messages.READ_PARAMETERS => "READ PARAMETERS",
                X29Messages.SET_AND_READ_PARAMETERS => "SET AND READ PARAMETERS",
                X29Messages.PARAMETER_INDICATION => "PARAMETER INDICATION",
                X29Messages.INVITATION_TO_CLEAR => "INVITATION TO CLEAR",
                X29Messages.INDICATION_OF_BREAK => "INDICATION OF BREAK",
                X29Messages.ERROR => $"ERROR ({ErrorType})",
                _ => $"Unknown ({Type})"
            };
        }
    }
}