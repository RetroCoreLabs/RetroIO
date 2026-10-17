//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// X.3 PAD Parameters protocol implementation according to ITU-T X.3 standard
    /// Manages all 22 standard PAD parameters for Triple-X (X.3, X.28, X.29) services
    /// Provides validation, profiles, and parameter manipulation functionality
    /// </summary>
    public class X3
    {
        private readonly ILogger? _logger;
        private readonly Dictionary<byte, byte> _parameters = new Dictionary<byte, byte>();
        
        /// <summary>
        /// Event fired when X.3 parameters are changed
        /// </summary>
        public event EventHandler<EventArgs>? ParametersChanged;
        
        /// <summary>
        /// X.3 Parameter Numbers according to ITU-T X.3 standard
        /// </summary>
        public static class ParameterNumber
        {
            public const byte ESCAPE = 1;                    // PAD escape from data transfer mode
            public const byte ECHO = 2;                     // Echo
            public const byte DATA_FORWARDING = 3;          // Data forwarding signal
            public const byte IDLE_TIMER = 4;               // Idle timer delay
            public const byte ANCILLARY_DEVICE_CONTROL = 5; // Ancillary device control
            public const byte CONTROL_PAD_SERVICE_SIGNALS = 6; // Control of PAD service signals
            public const byte OPERATION_BREAKOUT_PROCEDURE = 7; // Operation of the procedure for break
            public const byte DISCARD_OUTPUT = 8;           // Discard output
            public const byte PADDING_AFTER_CR = 9;         // Padding after carriage return
            public const byte LINE_FOLDING = 10;            // Line folding
            public const byte BINARY_SPEED = 11;            // Binary speed (not used)
            public const byte FORWARD_ON_N_CHARACTERS = 12; // Forward on N characters
            public const byte LINE_FEED_INSERTION = 13;     // Linefeed insertion after carriage return
            public const byte PADDING_AFTER_LINE_FEED = 14; // Padding after line feed
            public const byte EDITING = 15;                 // Editing
            public const byte CHARACTER_DELETE = 16;        // Character delete
            public const byte LINE_DELETE = 17;             // Line delete
            public const byte LINE_DISPLAY = 18;            // Line display
            public const byte EDITING_PAD_SERVICE_SIGNALS = 19; // Editing PAD service signals
            public const byte ECHO_MASK = 20;               // Echo mask
            public const byte PARITY_TREATMENT = 21;        // Parity treatment
            public const byte PAGE_WAIT = 22;               // Page wait
        }

        /// <summary>
        /// Default X.3 parameter values according to ITU-T X.3 standard
        /// </summary>
        public static class DefaultValue
        {
            public const byte ESCAPE = 1;                    // Enabled (DLE)
            public const byte ECHO = 1;                     // Enabled
            public const byte DATA_FORWARDING = 13;         // Carriage return (CR)
            public const byte IDLE_TIMER = 0;               // Disabled
            public const byte ANCILLARY_DEVICE_CONTROL = 0; // Disabled
            public const byte CONTROL_PAD_SERVICE_SIGNALS = 1; // Enabled
            public const byte OPERATION_BREAKOUT_PROCEDURE = 21; // BREAK forwarded
            public const byte DISCARD_OUTPUT = 1;           // Enabled on receipt of normal data
            public const byte PADDING_AFTER_CR = 0;         // No padding
            public const byte LINE_FOLDING = 0;             // Disabled
            public const byte BINARY_SPEED = 0;             // Not used
            public const byte FORWARD_ON_N_CHARACTERS = 0;  // Disabled (line mode)
            public const byte LINE_FEED_INSERTION = 0;      // Disabled
            public const byte PADDING_AFTER_LINE_FEED = 0;  // No padding
            public const byte EDITING = 1;                  // Enabled
            public const byte CHARACTER_DELETE = 127;       // DEL character
            public const byte LINE_DELETE = 24;             // CAN character
            public const byte LINE_DISPLAY = 18;            // DC2 character
            public const byte EDITING_PAD_SERVICE_SIGNALS = 0; // Disabled
            public const byte ECHO_MASK = 0;                // All characters echoed
            public const byte PARITY_TREATMENT = 0;         // No parity checking
            public const byte PAGE_WAIT = 0;                // Disabled
        }

        /// <summary>
        /// Standard PAD profiles
        /// </summary>
        public static class Profiles
        {
            public const string STANDARD = "STD";
            public const string TRANSPARENT = "TRANS";  
            public const string SIMPLE = "SIMPLE";
        }

        public X3(ILogger? logger = null)
        {
            _logger = logger;
            SetDefaultProfile();
        }

        /// <summary>
        /// Set all parameters to default values
        /// </summary>
        public void SetDefaultProfile()
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
            _parameters[ParameterNumber.FORWARD_ON_N_CHARACTERS] = DefaultValue.FORWARD_ON_N_CHARACTERS;
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
            
            _logger?.LogDebug("X.3: Set default parameter profile");
        }

        /// <summary>
        /// Set predefined parameter profile
        /// </summary>
        /// <param name="profileName">Profile name (STD, TRANS, SIMPLE)</param>
        public bool SetProfile(string profileName)
        {
            switch (profileName?.ToUpper())
            {
                case Profiles.STANDARD:
                    SetStandardProfile();
                    break;
                    
                case Profiles.TRANSPARENT:
                    SetTransparentProfile();
                    break;
                    
                case Profiles.SIMPLE:
                    SetSimpleProfile();
                    break;
                    
                default:
                    _logger?.LogWarning("X.3: Unknown profile: {ProfileName}", profileName);
                    return false;
            }
            
            _logger?.LogInformation("X.3: Applied profile {ProfileName}", profileName);
            return true;
        }

        /// <summary>
        /// Set standard terminal profile (default ITU-T X.3 values)
        /// </summary>
        public void SetStandardProfile()
        {
            SetDefaultProfile(); // Standard profile is the default
        }

        /// <summary>
        /// Set transparent profile (minimal PAD processing)
        /// </summary>
        public void SetTransparentProfile()
        {
            SetDefaultProfile();
            _parameters[ParameterNumber.ECHO] = 0;                // No echo
            _parameters[ParameterNumber.DATA_FORWARDING] = 0;     // No automatic forwarding
            _parameters[ParameterNumber.EDITING] = 0;             // No editing
            _parameters[ParameterNumber.LINE_FEED_INSERTION] = 0; // No LF insertion
            _parameters[ParameterNumber.PADDING_AFTER_CR] = 0;    // No padding
            _parameters[ParameterNumber.PADDING_AFTER_LINE_FEED] = 0; // No padding
            
            _logger?.LogDebug("X.3: Set transparent profile (minimal processing)");
        }

        /// <summary>
        /// Set simple terminal profile (basic functionality)
        /// </summary>
        public void SetSimpleProfile()
        {
            SetDefaultProfile();
            _parameters[ParameterNumber.ECHO] = 1;                // Echo enabled
            _parameters[ParameterNumber.DATA_FORWARDING] = 2;     // Forward on CR
            _parameters[ParameterNumber.EDITING] = 1;             // Basic editing
            _parameters[ParameterNumber.LINE_FEED_INSERTION] = 1; // Insert LF after CR
            
            _logger?.LogDebug("X.3: Set simple profile (basic terminal)");
        }

        /// <summary>
        /// Get parameter value
        /// </summary>
        /// <param name="parameterNumber">X.3 parameter number (1-22)</param>
        /// <returns>Parameter value or 0 if not found</returns>
        public byte GetParameter(byte parameterNumber)
        {
            if (_parameters.TryGetValue(parameterNumber, out byte value))
            {
                _logger?.LogTrace("X.3: Get parameter {ParamNum} = {Value}", parameterNumber, value);
                return value;
            }
            
            _logger?.LogWarning("X.3: Unknown parameter number: {ParamNum}", parameterNumber);
            return 0;
        }

        /// <summary>
        /// Set parameter value with validation
        /// </summary>
        /// <param name="parameterNumber">X.3 parameter number (1-22)</param>
        /// <param name="value">Parameter value</param>
        /// <returns>True if successfully set</returns>
        public bool SetParameter(byte parameterNumber, byte value)
        {
            if (!IsValidParameter(parameterNumber, value))
            {
                _logger?.LogWarning("X.3: Invalid parameter {ParamNum} = {Value}", parameterNumber, value);
                return false;
            }

            byte oldValue = GetParameter(parameterNumber);
            _parameters[parameterNumber] = value;
            
            _logger?.LogDebug("X.3: Set parameter {ParamNum}: {OldValue} → {NewValue}", 
                parameterNumber, oldValue, value);
                
            // Fire event to notify listeners that parameters have changed
            ParametersChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>
        /// Set multiple parameters at once
        /// </summary>
        /// <param name="parameters">Dictionary of parameter number/value pairs</param>
        /// <returns>Number of parameters successfully set</returns>
        public int SetParameters(Dictionary<byte, byte> parameters)
        {
            int successCount = 0;
            
            foreach (var kvp in parameters)
            {
                if (SetParameter(kvp.Key, kvp.Value))
                {
                    successCount++;
                }
            }
            
            _logger?.LogDebug("X.3: Set {SuccessCount}/{TotalCount} parameters", 
                successCount, parameters.Count);
            return successCount;
        }

        /// <summary>
        /// Validate parameter number and value according to X.3 standard
        /// </summary>
        /// <param name="parameterNumber">Parameter number to validate</param>
        /// <param name="value">Parameter value to validate</param>
        /// <returns>True if valid parameter/value combination</returns>
        public static bool IsValidParameter(byte parameterNumber, byte value)
        {
            return parameterNumber switch
            {
                ParameterNumber.ESCAPE => value <= 1,
                ParameterNumber.ECHO => value <= 1,
                ParameterNumber.DATA_FORWARDING => value <= 126,
                ParameterNumber.IDLE_TIMER => true, // All values 0-255 valid
                ParameterNumber.ANCILLARY_DEVICE_CONTROL => value <= 1,
                ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS => value <= 1,
                ParameterNumber.OPERATION_BREAKOUT_PROCEDURE => value <= 127,
                ParameterNumber.DISCARD_OUTPUT => value <= 1,
                ParameterNumber.PADDING_AFTER_CR => value <= 7,
                ParameterNumber.LINE_FOLDING => true, // All values 0-255 valid
                ParameterNumber.BINARY_SPEED => true, // Not used, all values accepted
                ParameterNumber.FORWARD_ON_N_CHARACTERS => value <= 255, // 0 = line mode, >0 = forward after N chars
                ParameterNumber.LINE_FEED_INSERTION => value <= 7,
                ParameterNumber.PADDING_AFTER_LINE_FEED => value <= 7,
                ParameterNumber.EDITING => value <= 1,
                ParameterNumber.CHARACTER_DELETE => value <= 127,
                ParameterNumber.LINE_DELETE => value <= 127,
                ParameterNumber.LINE_DISPLAY => value <= 127,
                ParameterNumber.EDITING_PAD_SERVICE_SIGNALS => value <= 1,
                ParameterNumber.ECHO_MASK => true, // All values valid
                ParameterNumber.PARITY_TREATMENT => value <= 3,
                ParameterNumber.PAGE_WAIT => true, // All values 0-255 valid
                _ => false
            };
        }

        /// <summary>
        /// Get parameter description for display/debugging
        /// </summary>
        /// <param name="parameterNumber">Parameter number</param>
        /// <returns>Human-readable parameter description</returns>
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
                ParameterNumber.FORWARD_ON_N_CHARACTERS => "Forward on N characters",
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
        /// Get all parameters as read-only dictionary
        /// </summary>
        /// <returns>Copy of current parameters</returns>
        public Dictionary<byte, byte> GetAllParameters()
        {
            return new Dictionary<byte, byte>(_parameters);
        }

        /// <summary>
        /// Get parameters for specific numbers
        /// </summary>
        /// <param name="parameterNumbers">List of parameter numbers to retrieve</param>
        /// <returns>Dictionary of requested parameters</returns>
        public Dictionary<byte, byte> GetParameters(IEnumerable<byte> parameterNumbers)
        {
            var result = new Dictionary<byte, byte>();
            
            foreach (byte paramNum in parameterNumbers)
            {
                if (_parameters.TryGetValue(paramNum, out byte value))
                {
                    result[paramNum] = value;
                }
            }
            
            return result;
        }

        /// <summary>
        /// Check if parameter affects character processing
        /// </summary>
        /// <param name="parameterNumber">Parameter number to check</param>
        /// <returns>True if parameter affects character handling</returns>
        public static bool IsCharacterProcessingParameter(byte parameterNumber)
        {
            return parameterNumber switch
            {
                ParameterNumber.ECHO or
                ParameterNumber.DATA_FORWARDING or
                ParameterNumber.EDITING or
                ParameterNumber.CHARACTER_DELETE or
                ParameterNumber.LINE_DELETE or
                ParameterNumber.LINE_DISPLAY or
                ParameterNumber.ECHO_MASK => true,
                _ => false
            };
        }

        /// <summary>
        /// Check if parameter affects data forwarding
        /// </summary>
        /// <param name="parameterNumber">Parameter number to check</param>
        /// <returns>True if parameter affects when data is forwarded</returns>
        public static bool IsForwardingParameter(byte parameterNumber)
        {
            return parameterNumber switch
            {
                ParameterNumber.DATA_FORWARDING or
                ParameterNumber.IDLE_TIMER => true,
                _ => false
            };
        }

        /// <summary>
        /// Create a copy of this parameter set
        /// </summary>
        /// <returns>New X3 instance with same parameters</returns>
        public X3 Clone()
        {
            var clone = new X3(_logger);
            clone._parameters.Clear();
            foreach (var kvp in _parameters)
            {
                clone._parameters[kvp.Key] = kvp.Value;
            }
            return clone;
        }

        /// <summary>
        /// Reset specific parameters to default values
        /// </summary>
        /// <param name="parameterNumbers">Parameters to reset</param>
        public void ResetParameters(IEnumerable<byte> parameterNumbers)
        {
            var defaults = new X3();
            int resetCount = 0;
            
            foreach (byte paramNum in parameterNumbers)
            {
                if (_parameters.ContainsKey(paramNum))
                {
                    _parameters[paramNum] = defaults.GetParameter(paramNum);
                    resetCount++;
                }
            }
            
            _logger?.LogDebug("X.3: Reset {Count} parameters to defaults", resetCount);
        }

        /// <summary>
        /// Get parameters summary for display
        /// </summary>
        /// <returns>Formatted parameter summary</returns>
        public override string ToString()
        {
            var defaults = new X3();
            var nonDefaultParams = new List<KeyValuePair<byte, byte>>();
            foreach (var kvp in _parameters)
            {
                if (kvp.Value != defaults.GetParameter(kvp.Key))
                {
                    nonDefaultParams.Add(kvp);
                }
            }

            if (nonDefaultParams.Count == 0)
                return "X.3 Parameters: All defaults";

            var paramStrings = new List<string>();
            for (int i = 0; i < nonDefaultParams.Count; i++)
            {
                paramStrings.Add($"{nonDefaultParams[i].Key}:{nonDefaultParams[i].Value}");
            }
            return $"X.3 Parameters: {string.Join(", ", paramStrings)}";
        }

        /// <summary>
        /// Process outgoing text according to X.3 parameters
        /// </summary>
        /// <param name="text">Text to process</param>
        /// <returns>Processed text</returns>
        public string ProcessOutgoingText(string text)
        {
            var processed = new StringBuilder(text.Length * 2);
            
            foreach (char c in text)
            {
                // Apply X.3 parameter processing
                if (c == '\r' && GetParameter(ParameterNumber.LINE_FEED_INSERTION) > 0)
                {
                    processed.Append(c);
                    processed.Append('\n'); // Add LF after CR
                }
                else if (c == '\r' && GetParameter(ParameterNumber.PADDING_AFTER_CR) > 0)
                {
                    processed.Append(c);
                    // Add padding characters
                    int padding = GetParameter(ParameterNumber.PADDING_AFTER_CR);
                    processed.Append(new string('\0', padding));
                }
                else if (c == '\n' && GetParameter(ParameterNumber.PADDING_AFTER_LINE_FEED) > 0)
                {
                    processed.Append(c);
                    // Add padding after LF
                    int padding = GetParameter(ParameterNumber.PADDING_AFTER_LINE_FEED);
                    processed.Append(new string('\0', padding));
                }
                else
                {
                    processed.Append(c);
                }
            }

            return processed.ToString();
        }

        /// <summary>
        /// Process incoming text according to X.3 parameters
        /// </summary>
        /// <param name="text">Text to process</param>
        /// <returns>Processed text</returns>
        public string ProcessIncomingText(string text)
        {
            if (GetParameter(ParameterNumber.ECHO) == 0)
            {
                // No echo processing needed
                return text;
            }

            // Apply echo mask and other processing
            var processed = new StringBuilder(text.Length);
            byte echoMask = GetParameter(ParameterNumber.ECHO_MASK);
            
            foreach (char c in text)
            {
                // Apply echo mask logic
                if (ShouldEchoCharacter(c, echoMask))
                {
                    processed.Append(c);
                }
            }

            return processed.ToString();
        }

        /// <summary>
        /// Check if character should be echoed based on echo mask
        /// </summary>
        /// <param name="c">Character to check</param>
        /// <param name="echoMask">Echo mask parameter</param>
        /// <returns>True if character should be echoed</returns>
        private bool ShouldEchoCharacter(char c, byte echoMask)
        {
            // Simplified echo mask logic - full implementation would check bit patterns
            return echoMask == 0 || (c >= 32 && c <= 126); // Printable characters
        }
    }
}