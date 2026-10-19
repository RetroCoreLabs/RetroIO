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
            /// X.3 parameter number 12 (Flow control of the PAD by the start-stop mode DTE).
            /// Reference 12 in X.3 section 3.12: 0 means no use of X-ON and X-OFF, 1 means use of X-ON and X-OFF.
            /// </summary>
            public const byte FLOW_CONTROL_X_ON_X_OFF = 12; // Flow control of the PAD by the start-stop mode DTE
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
        /// Default X.3 parameter values according to ITU-T X.3 standard
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
            /// Default value of the data forwarding parameter: 126 (all characters in columns 0 and 1 of
            /// International Alphabet No. 5 and DEL, the flags 2 + 4 + 8 + 16 + 32 + 64).
            /// X.3 does not name defaults. Every default here is the simple standard profile of
            /// Table 1/X.28 (ITU-T X.28, 12/97, page 9), which sets parameter 3 to 126.
            /// </summary>
            public const byte DATA_FORWARDING = 126;        // Simple standard profile, Table 1/X.28
            /// <summary>
            /// Default value of the idle timer parameter: 0 (Disabled).
            /// </summary>
            public const byte IDLE_TIMER = 0;               // Disabled
            /// <summary>
            /// Default value of the ancillary device control parameter: 1 (use of X-ON and X-OFF).
            /// Simple standard profile, Table 1/X.28.
            /// </summary>
            public const byte ANCILLARY_DEVICE_CONTROL = 1; // Use of X-ON and X-OFF
            /// <summary>
            /// Default value of the control pad service signals parameter: 1 (Enabled).
            /// </summary>
            public const byte CONTROL_PAD_SERVICE_SIGNALS = 1; // Enabled
            /// <summary>
            /// Default value of the operation breakout procedure parameter: 2 (reset).
            /// Simple standard profile, Table 1/X.28.
            /// </summary>
            public const byte OPERATION_BREAKOUT_PROCEDURE = 2; // Reset
            /// <summary>
            /// Default value of the discard output parameter: 0 (normal data delivery).
            /// Simple standard profile, Table 1/X.28.
            /// </summary>
            public const byte DISCARD_OUTPUT = 0;           // Normal data delivery
            /// <summary>
            /// Default value of the padding after cr parameter: 0 (No padding).
            /// </summary>
            public const byte PADDING_AFTER_CR = 0;         // No padding
            /// <summary>
            /// Default value of the line folding parameter: 0 (Disabled).
            /// </summary>
            public const byte LINE_FOLDING = 0;             // Disabled
            /// <summary>
            /// Default value of the binary speed parameter: 0.
            /// Parameter 11 is read only (the speed of the terminal, known to the PAD) and Table 1/X.28
            /// says only "indicate speed of DTE", with no number. The PAD that owns the line has to set the
            /// real speed; 0 is kept as the table-less starting value (X.3 3.11 reads 0 as 110 bit/s).
            /// </summary>
            public const byte BINARY_SPEED = 0;             // Not used
            /// <summary>
            /// Default value of the flow control parameter: 1 (use of X-ON and X-OFF).
            /// Simple standard profile, Table 1/X.28.
            /// </summary>
            public const byte FLOW_CONTROL_X_ON_X_OFF = 1;  // Use of X-ON and X-OFF
            /// <summary>
            /// Default value of the line feed insertion parameter: 0 (Disabled).
            /// </summary>
            public const byte LINE_FEED_INSERTION = 0;      // Disabled
            /// <summary>
            /// Default value of the padding after line feed parameter: 0 (No padding).
            /// </summary>
            public const byte PADDING_AFTER_LINE_FEED = 0;  // No padding
            /// <summary>
            /// Default value of the editing parameter: 0 (no editing in the data transfer state).
            /// Simple standard profile, Table 1/X.28.
            /// </summary>
            public const byte EDITING = 0;                  // No editing in data transfer state
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
            /// Default value of the editing pad service signals parameter: 1 (printing terminals).
            /// Simple standard profile, Table 1/X.28.
            /// </summary>
            public const byte EDITING_PAD_SERVICE_SIGNALS = 1; // Printing terminals
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
        /// The flags that make up the value of parameter 3 (selection of data forwarding character),
        /// X.3 section 3.3 reference 3. The value is the sum of the flags wanted.
        /// </summary>
        public static class DataForwardingFlag
        {
            /// <summary>
            /// Flag 1: alphanumeric characters (A to Z, a to z, 0 to 9).
            /// </summary>
            public const byte ALPHANUMERIC = 1;
            /// <summary>
            /// Flag 2: the character CR.
            /// </summary>
            public const byte CARRIAGE_RETURN = 2;
            /// <summary>
            /// Flag 4: the characters ESC, BEL, ENQ and ACK.
            /// </summary>
            public const byte ESC_BEL_ENQ_ACK = 4;
            /// <summary>
            /// Flag 8: the characters DEL, CAN and DC2.
            /// </summary>
            public const byte DEL_CAN_DC2 = 8;
            /// <summary>
            /// Flag 16: the characters ETX and EOT.
            /// </summary>
            public const byte ETX_EOT = 16;
            /// <summary>
            /// Flag 32: the characters HT, LF, VT and FF.
            /// </summary>
            public const byte HT_LF_VT_FF = 32;
            /// <summary>
            /// Flag 64: all other characters in columns 0 and 1 of International Alphabet No. 5.
            /// </summary>
            public const byte OTHER_COLUMNS_0_1 = 64;
            /// <summary>
            /// Every flag except alphanumerics: all of columns 0 and 1 and DEL (value 126).
            /// This is the value the simple standard profile of Table 1/X.28 sets.
            /// </summary>
            public const byte ALL_CONTROL = 126;
            /// <summary>
            /// Every flag: alphanumerics (1) and all of 2 to 64, the sum 127.
            /// Space and the symbols of columns 2 to 7 of International Alphabet No. 5 are still in no flag.
            /// </summary>
            public const byte ALL_FLAGS = 127;
        }

        /// <summary>
        /// Standard PAD profiles
        /// </summary>
        public static class Profiles
        {
            /// <summary>
            /// Name of the standard profile.
            /// </summary>
            public const string STANDARD = "STD";
            /// <summary>
            /// Name of the transparent profile.
            /// </summary>
            public const string TRANSPARENT = "TRANS";  
            /// <summary>
            /// Name of the simple profile.
            /// </summary>
            public const string SIMPLE = "SIMPLE";
        }

        /// <summary>
        /// Creates the X.3 parameter set.
        /// </summary>
        /// <param name="logger">
        /// Optional diagnostics logger; may be null.
        /// </param>
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
            FillDefaultValues(_parameters);

            _logger?.LogDebug("X.3: Set default parameter profile");
        }

        /// <summary>
        /// Set predefined parameter profile
        /// </summary>
        /// <param name="profileName">
        /// Profile name (STD, TRANS, SIMPLE)
        /// </param>
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
        /// Set the transparent standard profile of Table 1/X.28 (ITU-T X.28, 12/97, page 9).
        /// </summary>
        public void SetTransparentProfile()
        {
            FillTransparentProfile(_parameters);

            _logger?.LogDebug("X.3: Set transparent standard profile (Table 1/X.28)");
        }

        /// <summary>
        /// Set the simple standard profile of Table 1/X.28 (ITU-T X.28, 12/97, page 9).
        /// </summary>
        public void SetSimpleProfile()
        {
            FillSimpleProfile(_parameters);

            _logger?.LogDebug("X.3: Set simple standard profile (Table 1/X.28)");
        }

        /// <summary>
        /// Clear the table and fill it with the default values of parameters 1 to 22.
        /// Both X3 and X3Parameters use this one table, so they cannot disagree.
        /// </summary>
        /// <param name="parameters">
        /// The table to clear and fill.
        /// </param>
        public static void FillDefaultValues(Dictionary<byte, byte> parameters)
        {
            parameters.Clear();
            parameters[ParameterNumber.ESCAPE] = DefaultValue.ESCAPE;
            parameters[ParameterNumber.ECHO] = DefaultValue.ECHO;
            parameters[ParameterNumber.DATA_FORWARDING] = DefaultValue.DATA_FORWARDING;
            parameters[ParameterNumber.IDLE_TIMER] = DefaultValue.IDLE_TIMER;
            parameters[ParameterNumber.ANCILLARY_DEVICE_CONTROL] = DefaultValue.ANCILLARY_DEVICE_CONTROL;
            parameters[ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS] = DefaultValue.CONTROL_PAD_SERVICE_SIGNALS;
            parameters[ParameterNumber.OPERATION_BREAKOUT_PROCEDURE] = DefaultValue.OPERATION_BREAKOUT_PROCEDURE;
            parameters[ParameterNumber.DISCARD_OUTPUT] = DefaultValue.DISCARD_OUTPUT;
            parameters[ParameterNumber.PADDING_AFTER_CR] = DefaultValue.PADDING_AFTER_CR;
            parameters[ParameterNumber.LINE_FOLDING] = DefaultValue.LINE_FOLDING;
            parameters[ParameterNumber.BINARY_SPEED] = DefaultValue.BINARY_SPEED;
            parameters[ParameterNumber.FLOW_CONTROL_X_ON_X_OFF] = DefaultValue.FLOW_CONTROL_X_ON_X_OFF;
            parameters[ParameterNumber.LINE_FEED_INSERTION] = DefaultValue.LINE_FEED_INSERTION;
            parameters[ParameterNumber.PADDING_AFTER_LINE_FEED] = DefaultValue.PADDING_AFTER_LINE_FEED;
            parameters[ParameterNumber.EDITING] = DefaultValue.EDITING;
            parameters[ParameterNumber.CHARACTER_DELETE] = DefaultValue.CHARACTER_DELETE;
            parameters[ParameterNumber.LINE_DELETE] = DefaultValue.LINE_DELETE;
            parameters[ParameterNumber.LINE_DISPLAY] = DefaultValue.LINE_DISPLAY;
            parameters[ParameterNumber.EDITING_PAD_SERVICE_SIGNALS] = DefaultValue.EDITING_PAD_SERVICE_SIGNALS;
            parameters[ParameterNumber.ECHO_MASK] = DefaultValue.ECHO_MASK;
            parameters[ParameterNumber.PARITY_TREATMENT] = DefaultValue.PARITY_TREATMENT;
            parameters[ParameterNumber.PAGE_WAIT] = DefaultValue.PAGE_WAIT;
        }

        /// <summary>
        /// Clear the table and fill it with the transparent standard profile of Table 1/X.28
        /// (ITU-T X.28, 12/97, page 9), parameters 1 to 22.
        /// Parameter 11 is read only (the speed of the terminal, known to the PAD); the table gives no
        /// number for it, so the default 0 is kept.
        /// Parameters 23 to 29 are 0 in both profiles; this class does not carry them.
        /// </summary>
        /// <param name="parameters">
        /// The table to clear and fill.
        /// </param>
        public static void FillTransparentProfile(Dictionary<byte, byte> parameters)
        {
            FillDefaultValues(parameters);
            parameters[ParameterNumber.ESCAPE] = 0;                       // 1: PAD recall not possible
            parameters[ParameterNumber.ECHO] = 0;                         // 2: no echo
            parameters[ParameterNumber.DATA_FORWARDING] = 0;              // 3: no data forwarding signal
            parameters[ParameterNumber.IDLE_TIMER] = 20;                  // 4: one second (20 twentieths)
            parameters[ParameterNumber.ANCILLARY_DEVICE_CONTROL] = 0;     // 5: no use of X-ON and X-OFF
            parameters[ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS] = 0;  // 6: no service signals sent
            parameters[ParameterNumber.OPERATION_BREAKOUT_PROCEDURE] = 2; // 7: reset
            parameters[ParameterNumber.DISCARD_OUTPUT] = 0;               // 8: normal data delivery
            parameters[ParameterNumber.PADDING_AFTER_CR] = 0;             // 9: no padding after CR
            parameters[ParameterNumber.LINE_FOLDING] = 0;                 // 10: no line folding
            parameters[ParameterNumber.FLOW_CONTROL_X_ON_X_OFF] = 0;      // 12: no use of X-ON and X-OFF
            parameters[ParameterNumber.LINE_FEED_INSERTION] = 0;          // 13: no linefeed insertion
            parameters[ParameterNumber.PADDING_AFTER_LINE_FEED] = 0;      // 14: no linefeed padding
            parameters[ParameterNumber.EDITING] = 0;                      // 15: no editing
            parameters[ParameterNumber.CHARACTER_DELETE] = 127;           // 16: DEL
            parameters[ParameterNumber.LINE_DELETE] = 24;                 // 17: CAN
            parameters[ParameterNumber.LINE_DISPLAY] = 18;                // 18: DC2
            parameters[ParameterNumber.EDITING_PAD_SERVICE_SIGNALS] = 1;  // 19: printing terminals
            parameters[ParameterNumber.ECHO_MASK] = 0;                    // 20: echo all characters
            parameters[ParameterNumber.PARITY_TREATMENT] = 0;             // 21: no parity detection or generation
            parameters[ParameterNumber.PAGE_WAIT] = 0;                    // 22: page wait disabled
        }

        /// <summary>
        /// Clear the table and fill it with the simple standard profile of Table 1/X.28
        /// (ITU-T X.28, 12/97, page 9), parameters 1 to 22.
        /// Parameter 11 is read only (the speed of the terminal, known to the PAD); the table gives no
        /// number for it, so the default 0 is kept.
        /// Parameters 23 to 29 are 0 in both profiles; this class does not carry them.
        /// </summary>
        /// <param name="parameters">
        /// The table to clear and fill.
        /// </param>
        public static void FillSimpleProfile(Dictionary<byte, byte> parameters)
        {
            FillTransparentProfile(parameters); // the two profiles agree on every parameter not set below
            parameters[ParameterNumber.ESCAPE] = 1;                       // 1: PAD recall possible (DLE)
            parameters[ParameterNumber.ECHO] = 1;                         // 2: echo
            parameters[ParameterNumber.DATA_FORWARDING] = DataForwardingFlag.ALL_CONTROL; // 3: columns 0 and 1 and DEL (126)
            parameters[ParameterNumber.IDLE_TIMER] = 0;                   // 4: no time-out
            parameters[ParameterNumber.ANCILLARY_DEVICE_CONTROL] = 1;     // 5: use X-ON and X-OFF
            parameters[ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS] = 1;  // 6: service signals are sent
            parameters[ParameterNumber.FLOW_CONTROL_X_ON_X_OFF] = 1;      // 12: use X-ON and X-OFF
        }

        /// <summary>
        /// Get parameter value
        /// </summary>
        /// <param name="parameterNumber">
        /// X.3 parameter number (1-22)
        /// </param>
        /// <returns>
        /// Parameter value or 0 if not found
        /// </returns>
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
        /// <param name="parameterNumber">
        /// X.3 parameter number (1-22)
        /// </param>
        /// <param name="value">
        /// Parameter value
        /// </param>
        /// <returns>
        /// True if successfully set
        /// </returns>
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
        /// <param name="parameters">
        /// Dictionary of parameter number/value pairs
        /// </param>
        /// <returns>
        /// Number of parameters successfully set
        /// </returns>
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
        /// <param name="parameterNumber">
        /// Parameter number to validate
        /// </param>
        /// <param name="value">
        /// Parameter value to validate
        /// </param>
        /// <returns>
        /// True if valid parameter/value combination
        /// </returns>
        public static bool IsValidParameter(byte parameterNumber, byte value)
        {
            return parameterNumber switch
            {
                // 3.1: 0 not possible, 1 DLE, 32 to 126 a graphic character
                ParameterNumber.ESCAPE => value <= 1 || (value >= 32 && value <= 126),
                // 3.2: 0 no echo, 1 echo, 2 echo all except the forwarding sequence, 32 to 126 scrambled echo character
                ParameterNumber.ECHO => value <= 2 || (value >= 32 && value <= 126),
                ParameterNumber.DATA_FORWARDING => value <= 127, // 3.3: sum of the seven flags
                ParameterNumber.IDLE_TIMER => true, // 3.4: any number 0 to 255
                ParameterNumber.ANCILLARY_DEVICE_CONTROL => value <= 2, // 3.5: 0, 1 or 2
                // 3.6: flags 1, 2, 4, network values 8 to 15, and 16 to 240 in steps of 16 combined with them: 0 to 255
                ParameterNumber.CONTROL_PAD_SERVICE_SIGNALS => true,
                ParameterNumber.OPERATION_BREAKOUT_PROCEDURE => value <= 31, // 3.7: sum of the flags 1, 2, 4, 8, 16
                ParameterNumber.DISCARD_OUTPUT => value <= 1, // 3.8: 0 or 1
                ParameterNumber.PADDING_AFTER_CR => true, // 3.9: any number 0 to 255
                ParameterNumber.LINE_FOLDING => true, // 3.10: 0 to 255
                ParameterNumber.BINARY_SPEED => value <= 19, // 3.11: speed values 0 to 19 (read only for the DTEs)
                ParameterNumber.FLOW_CONTROL_X_ON_X_OFF => value <= 1, // 3.12: 0 or 1
                ParameterNumber.LINE_FEED_INSERTION => value <= 7, // 3.13: sum of the flags 1, 2, 4
                ParameterNumber.PADDING_AFTER_LINE_FEED => true, // 3.14: any number 0 to 255
                ParameterNumber.EDITING => value <= 1, // 3.15: 0 or 1
                ParameterNumber.CHARACTER_DELETE => value <= 130, // 3.16: a character 0 to 127, or 128 to 130 for sequences
                ParameterNumber.LINE_DELETE => value <= 127, // 3.17
                ParameterNumber.LINE_DISPLAY => value <= 127, // 3.18
                // 3.19: 0, 1, 2, 8, or a character 32 to 126
                ParameterNumber.EDITING_PAD_SERVICE_SIGNALS => value <= 2 || value == 8 || (value >= 32 && value <= 126),
                ParameterNumber.ECHO_MASK => true, // 3.20: sum of eight flags, 0 to 255
                ParameterNumber.PARITY_TREATMENT => value <= 7, // 3.21: sum of the flags 1, 2, 4
                ParameterNumber.PAGE_WAIT => true, // 3.22: 0 to 255
                _ => false
            };
        }

        /// <summary>
        /// Get parameter description for display/debugging
        /// </summary>
        /// <param name="parameterNumber">
        /// Parameter number
        /// </param>
        /// <returns>
        /// Human-readable parameter description
        /// </returns>
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
                ParameterNumber.FLOW_CONTROL_X_ON_X_OFF => "Flow control of the PAD (X-ON/X-OFF)",
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
        /// <returns>
        /// Copy of current parameters
        /// </returns>
        public Dictionary<byte, byte> GetAllParameters()
        {
            return new Dictionary<byte, byte>(_parameters);
        }

        /// <summary>
        /// Get parameters for specific numbers
        /// </summary>
        /// <param name="parameterNumbers">
        /// List of parameter numbers to retrieve
        /// </param>
        /// <returns>
        /// Dictionary of requested parameters
        /// </returns>
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
        /// <param name="parameterNumber">
        /// Parameter number to check
        /// </param>
        /// <returns>
        /// True if parameter affects character handling
        /// </returns>
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
        /// <param name="parameterNumber">
        /// Parameter number to check
        /// </param>
        /// <returns>
        /// True if parameter affects when data is forwarded
        /// </returns>
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
        /// <returns>
        /// New X3 instance with same parameters
        /// </returns>
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
        /// <param name="parameterNumbers">
        /// Parameters to reset
        /// </param>
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
        /// <returns>
        /// Formatted parameter summary
        /// </returns>
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
        /// <param name="text">
        /// Text to process
        /// </param>
        /// <returns>
        /// Processed text
        /// </returns>
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
        /// <param name="text">
        /// Text to process
        /// </param>
        /// <returns>
        /// Processed text
        /// </returns>
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
        /// <param name="c">
        /// Character to check
        /// </param>
        /// <param name="echoMask">
        /// Echo mask parameter
        /// </param>
        /// <returns>
        /// True if character should be echoed
        /// </returns>
        private bool ShouldEchoCharacter(char c, byte echoMask)
        {
            // Simplified echo mask logic - full implementation would check bit patterns
            return echoMask == 0 || (c >= 32 && c <= 126); // Printable characters
        }
    }
}