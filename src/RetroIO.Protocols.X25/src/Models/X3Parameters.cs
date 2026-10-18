//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// X.3 PAD Parameters according to ITU-T X.3 standard
    /// Defines all 22 standard PAD parameters for Triple-X (X.3, X.28, X.29) services
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
            public const byte ESCAPE = 1;                    // PAD escape from data transfer mode
            /// <summary>
            /// X.3 parameter number 2 (Echo).
            /// </summary>
            public const byte ECHO = 2;                     // Echo
            /// <summary>
            /// X.3 parameter number 3 (Data forwarding signal).
            /// </summary>
            public const byte DATA_FORWARDING = 3;          // Data forwarding signal
            /// <summary>
            /// X.3 parameter number 4 (Idle timer delay).
            /// </summary>
            public const byte IDLE_TIMER = 4;               // Idle timer delay
            /// <summary>
            /// X.3 parameter number 5 (Ancillary device control).
            /// </summary>
            public const byte ANCILLARY_DEVICE_CONTROL = 5; // Ancillary device control
            /// <summary>
            /// X.3 parameter number 6 (Control of PAD service signals).
            /// </summary>
            public const byte CONTROL_PAD_SERVICE_SIGNALS = 6; // Control of PAD service signals
            /// <summary>
            /// X.3 parameter number 7 (Operation of the procedure for break).
            /// </summary>
            public const byte OPERATION_BREAKOUT_PROCEDURE = 7; // Operation of the procedure for break
            /// <summary>
            /// X.3 parameter number 8 (Discard output).
            /// </summary>
            public const byte DISCARD_OUTPUT = 8;           // Discard output
            /// <summary>
            /// X.3 parameter number 9 (Padding after carriage return).
            /// </summary>
            public const byte PADDING_AFTER_CR = 9;         // Padding after carriage return
            /// <summary>
            /// X.3 parameter number 10 (Line folding).
            /// </summary>
            public const byte LINE_FOLDING = 10;            // Line folding
            /// <summary>
            /// X.3 parameter number 11 (Binary speed (not used)).
            /// </summary>
            public const byte BINARY_SPEED = 11;            // Binary speed (not used)
            /// <summary>
            /// X.3 parameter number 12 (Flow control of the PAD).
            /// </summary>
            public const byte FLOW_CONTROL_X_ON_X_OFF = 12; // Flow control of the PAD
            /// <summary>
            /// X.3 parameter number 13 (Linefeed insertion after carriage return).
            /// </summary>
            public const byte LINE_FEED_INSERTION = 13;     // Linefeed insertion after carriage return
            /// <summary>
            /// X.3 parameter number 14 (Padding after line feed).
            /// </summary>
            public const byte PADDING_AFTER_LINE_FEED = 14; // Padding after line feed
            /// <summary>
            /// X.3 parameter number 15 (Editing).
            /// </summary>
            public const byte EDITING = 15;                 // Editing
            /// <summary>
            /// X.3 parameter number 16 (Character delete).
            /// </summary>
            public const byte CHARACTER_DELETE = 16;        // Character delete
            /// <summary>
            /// X.3 parameter number 17 (Line delete).
            /// </summary>
            public const byte LINE_DELETE = 17;             // Line delete
            /// <summary>
            /// X.3 parameter number 18 (Line display).
            /// </summary>
            public const byte LINE_DISPLAY = 18;            // Line display
            /// <summary>
            /// X.3 parameter number 19 (Editing PAD service signals).
            /// </summary>
            public const byte EDITING_PAD_SERVICE_SIGNALS = 19; // Editing PAD service signals
            /// <summary>
            /// X.3 parameter number 20 (Echo mask).
            /// </summary>
            public const byte ECHO_MASK = 20;               // Echo mask
            /// <summary>
            /// X.3 parameter number 21 (Parity treatment).
            /// </summary>
            public const byte PARITY_TREATMENT = 21;        // Parity treatment
            /// <summary>
            /// X.3 parameter number 22 (Page wait).
            /// </summary>
            public const byte PAGE_WAIT = 22;               // Page wait
        }

        /// <summary>
        /// Default X.3 parameter values according to ITU-T X.3
        /// </summary>
        public static class DefaultValue
        {
            /// <summary>
            /// Default value of the escape parameter: 1 (Enabled (DLE)).
            /// </summary>
            public const byte ESCAPE = 1;                    // Enabled (DLE)
            /// <summary>
            /// Default value of the echo parameter: 1 (Enabled).
            /// </summary>
            public const byte ECHO = 1;                     // Enabled
            /// <summary>
            /// Default value of the data forwarding parameter: 2 (Alphanumeric characters).
            /// </summary>
            public const byte DATA_FORWARDING = 2;          // Alphanumeric characters
            /// <summary>
            /// Default value of the idle timer parameter: 0 (Disabled).
            /// </summary>
            public const byte IDLE_TIMER = 0;               // Disabled
            /// <summary>
            /// Default value of the ancillary device control parameter: 0 (Disabled).
            /// </summary>
            public const byte ANCILLARY_DEVICE_CONTROL = 0; // Disabled
            /// <summary>
            /// Default value of the control pad service signals parameter: 1 (Enabled).
            /// </summary>
            public const byte CONTROL_PAD_SERVICE_SIGNALS = 1; // Enabled
            /// <summary>
            /// Default value of the operation breakout procedure parameter: 21 (BREAK forwarded).
            /// </summary>
            public const byte OPERATION_BREAKOUT_PROCEDURE = 21; // BREAK forwarded
            /// <summary>
            /// Default value of the discard output parameter: 1 (Enabled on receipt of normal data).
            /// </summary>
            public const byte DISCARD_OUTPUT = 1;           // Enabled on receipt of normal data
            /// <summary>
            /// Default value of the padding after cr parameter: 0 (No padding).
            /// </summary>
            public const byte PADDING_AFTER_CR = 0;         // No padding
            /// <summary>
            /// Default value of the line folding parameter: 0 (Disabled).
            /// </summary>
            public const byte LINE_FOLDING = 0;             // Disabled
            /// <summary>
            /// Default value of the binary speed parameter: 0 (Not used).
            /// </summary>
            public const byte BINARY_SPEED = 0;             // Not used
            /// <summary>
            /// Default value of the flow control x on x off parameter: 0 (Disabled).
            /// </summary>
            public const byte FLOW_CONTROL_X_ON_X_OFF = 0;  // Disabled
            /// <summary>
            /// Default value of the line feed insertion parameter: 0 (Disabled).
            /// </summary>
            public const byte LINE_FEED_INSERTION = 0;      // Disabled
            /// <summary>
            /// Default value of the padding after line feed parameter: 0 (No padding).
            /// </summary>
            public const byte PADDING_AFTER_LINE_FEED = 0;  // No padding
            /// <summary>
            /// Default value of the editing parameter: 1 (Enabled).
            /// </summary>
            public const byte EDITING = 1;                  // Enabled
            /// <summary>
            /// Default value of the character delete parameter: 127 (DEL character).
            /// </summary>
            public const byte CHARACTER_DELETE = 127;       // DEL character
            /// <summary>
            /// Default value of the line delete parameter: 24 (CAN character).
            /// </summary>
            public const byte LINE_DELETE = 24;             // CAN character
            /// <summary>
            /// Default value of the line display parameter: 18 (DC2 character).
            /// </summary>
            public const byte LINE_DISPLAY = 18;            // DC2 character
            /// <summary>
            /// Default value of the editing pad service signals parameter: 0 (Disabled).
            /// </summary>
            public const byte EDITING_PAD_SERVICE_SIGNALS = 0; // Disabled
            /// <summary>
            /// Default value of the echo mask parameter: 0 (All characters echoed).
            /// </summary>
            public const byte ECHO_MASK = 0;                // All characters echoed
            /// <summary>
            /// Default value of the parity treatment parameter: 0 (No parity checking).
            /// </summary>
            public const byte PARITY_TREATMENT = 0;         // No parity checking
            /// <summary>
            /// Default value of the page wait parameter: 0 (Disabled).
            /// </summary>
            public const byte PAGE_WAIT = 0;                // Disabled
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
            _parameters.Clear();
            _parameters[ParameterNumber.ESCAPE] = DefaultValue.ESCAPE;
            _parameters[ParameterNumber.ECHO] = DefaultValue.ECHO;
            _parameters[ParameterNumber.DATA_FORWARDING] = DefaultValue.DATA_FORWARDING;
            _parameters[ParameterNumber.IDLE_TIMER] = DefaultValue.IDLE_TIMER;
            _parameters[ParameterNumber.ANCILLARY_DEVICE_CONTROL] = DefaultValue.ANCILLARY_DEVICE_CONTROL;
            _parameters[ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS] = DefaultValue.CONTROL_PAD_SERVICE_SIGNALS;
            _parameters[ParameterNumber.OPERATION_BREAKOUT_PROCEDURE] = DefaultValue.OPERATION_BREAKOUT_PROCEDURE;
            _parameters[ParameterNumber.DISCARD_OUTPUT] = DefaultValue.DISCARD_OUTPUT;
            _parameters[ParameterNumber.PADDING_AFTER_CR] = DefaultValue.PADDING_AFTER_CR;
            _parameters[ParameterNumber.LINE_FOLDING] = DefaultValue.LINE_FOLDING;
            _parameters[ParameterNumber.BINARY_SPEED] = DefaultValue.BINARY_SPEED;
            _parameters[ParameterNumber.FLOW_CONTROL_X_ON_X_OFF] = DefaultValue.FLOW_CONTROL_X_ON_X_OFF;
            _parameters[ParameterNumber.LINE_FEED_INSERTION] = DefaultValue.LINE_FEED_INSERTION;
            _parameters[ParameterNumber.PADDING_AFTER_LINE_FEED] = DefaultValue.PADDING_AFTER_LINE_FEED;
            _parameters[ParameterNumber.EDITING] = DefaultValue.EDITING;
            _parameters[ParameterNumber.CHARACTER_DELETE] = DefaultValue.CHARACTER_DELETE;
            _parameters[ParameterNumber.LINE_DELETE] = DefaultValue.LINE_DELETE;
            _parameters[ParameterNumber.LINE_DISPLAY] = DefaultValue.LINE_DISPLAY;
            _parameters[ParameterNumber.EDITING_PAD_SERVICE_SIGNALS] = DefaultValue.EDITING_PAD_SERVICE_SIGNALS;
            _parameters[ParameterNumber.ECHO_MASK] = DefaultValue.ECHO_MASK;
            _parameters[ParameterNumber.PARITY_TREATMENT] = DefaultValue.PARITY_TREATMENT;
            _parameters[ParameterNumber.PAGE_WAIT] = DefaultValue.PAGE_WAIT;
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
            return parameterNumber switch
            {
                ParameterNumber.ESCAPE => value <= 1,
                ParameterNumber.ECHO => value <= 1,
                ParameterNumber.DATA_FORWARDING => value <= 126,
                ParameterNumber.IDLE_TIMER => value <= 255,
                ParameterNumber.ANCILLARY_DEVICE_CONTROL => value <= 1,
                ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS => value <= 1,
                ParameterNumber.OPERATION_BREAKOUT_PROCEDURE => value <= 127,
                ParameterNumber.DISCARD_OUTPUT => value <= 1,
                ParameterNumber.PADDING_AFTER_CR => value <= 7,
                ParameterNumber.LINE_FOLDING => value <= 255,
                ParameterNumber.BINARY_SPEED => true, // Not used
                ParameterNumber.FLOW_CONTROL_X_ON_X_OFF => value <= 1,
                ParameterNumber.LINE_FEED_INSERTION => value <= 7,
                ParameterNumber.PADDING_AFTER_LINE_FEED => value <= 7,
                ParameterNumber.EDITING => value <= 1,
                ParameterNumber.CHARACTER_DELETE => value <= 127,
                ParameterNumber.LINE_DELETE => value <= 127,
                ParameterNumber.LINE_DISPLAY => value <= 127,
                ParameterNumber.EDITING_PAD_SERVICE_SIGNALS => value <= 1,
                ParameterNumber.ECHO_MASK => true, // All values valid
                ParameterNumber.PARITY_TREATMENT => value <= 3,
                ParameterNumber.PAGE_WAIT => value <= 255,
                _ => false
            };
        }

        /// <summary>
        /// Get parameter description
        /// </summary>
        public static string GetParameterDescription(byte parameterNumber)
        {
            return parameterNumber switch
            {
                ParameterNumber.ESCAPE => "PAD escape from data transfer mode",
                ParameterNumber.ECHO => "Echo characters back to terminal",
                ParameterNumber.DATA_FORWARDING => "Data forwarding signal characters",
                ParameterNumber.IDLE_TIMER => "Idle timer delay (20ms units)",
                ParameterNumber.ANCILLARY_DEVICE_CONTROL => "Ancillary device control",
                ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS => "Control of PAD service signals",
                ParameterNumber.OPERATION_BREAKOUT_PROCEDURE => "Break handling procedure",
                ParameterNumber.DISCARD_OUTPUT => "Discard output control",
                ParameterNumber.PADDING_AFTER_CR => "Padding characters after CR",
                ParameterNumber.LINE_FOLDING => "Line folding column limit",
                ParameterNumber.BINARY_SPEED => "Binary speed (not used)",
                ParameterNumber.FLOW_CONTROL_X_ON_X_OFF => "XON/XOFF flow control",
                ParameterNumber.LINE_FEED_INSERTION => "Auto LF insertion after CR",
                ParameterNumber.PADDING_AFTER_LINE_FEED => "Padding after LF",
                ParameterNumber.EDITING => "Command line editing enable",
                ParameterNumber.CHARACTER_DELETE => "Character delete key",
                ParameterNumber.LINE_DELETE => "Line delete key", 
                ParameterNumber.LINE_DISPLAY => "Line display key",
                ParameterNumber.EDITING_PAD_SERVICE_SIGNALS => "Editing service signals",
                ParameterNumber.ECHO_MASK => "Character echo mask",
                ParameterNumber.PARITY_TREATMENT => "Parity checking mode",
                ParameterNumber.PAGE_WAIT => "Page wait line count",
                _ => "Unknown parameter"
            };
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