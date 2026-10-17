//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// X.28 DTE/PAD Interface protocol implementation according to ITU-T X.28 standard
    /// Handles the interface between start-stop mode DTE (terminal) and PAD facility
    /// Manages command mode, data transfer mode, and character processing
    /// </summary>
    public class X28
    {
        private readonly ILogger? _logger;
        private readonly X3 _x3Parameters;
        private readonly StringBuilder _commandBuffer = new StringBuilder();
        private readonly StringBuilder _inputBuffer = new StringBuilder();
        
        /// <summary>
        /// PAD operating modes
        /// </summary>
        public enum PadMode
        {
            /// <summary>
            /// Command mode: the PAD accepts PAD commands.
            /// </summary>
            Command,        // Command mode - accepting PAD commands
            /// <summary>
            /// Data transfer mode: data passes through unchanged.
            /// </summary>
            DataTransfer,   // Data transfer mode - transparent data flow
            /// <summary>
            /// Escape mode: a temporary return to command mode.
            /// </summary>
            Escape         // Escape mode - temporary return to command mode
        }

        /// <summary>
        /// Data forwarding trigger conditions
        /// </summary>
        [Flags]
        public enum ForwardingCondition
        {
            /// <summary>
            /// No forwarding condition.
            /// </summary>
            None = 0,
            /// <summary>
            /// Data is forwarded when the idle timer expires.
            /// </summary>
            Timer = 1,                      // Idle timer expiry
            /// <summary>
            /// Data is forwarded when a carriage return is received.
            /// </summary>
            CarriageReturn = 2,             // CR character received
            /// <summary>
            /// Data is forwarded when another control character is received.
            /// </summary>
            ControlCharacter = 4,           // Other control characters
            /// <summary>
            /// Data is forwarded when the input buffer is full.
            /// </summary>
            BufferFull = 8,                 // Input buffer full
            /// <summary>
            /// Data is forwarded on an explicit forward request.
            /// </summary>
            ManualForward = 16,             // Explicit forward request
            /// <summary>
            /// Every character is forwarded immediately.
            /// </summary>
            CharacterByCharacter = 32,      // Every character forwarded immediately
            /// <summary>
            /// Data is forwarded when a specific printable character is received.
            /// </summary>
            SpecificCharacter = 64,         // Specific printable character
            /// <summary>
            /// Data is forwarded when an alphanumeric character is received (parameter 3 = 127).
            /// </summary>
            AlphanumericCharacter = 128     // Alphanumeric character (parameter 3 = 127)
        }

        /// <summary>
        /// Standard PAD commands according to X.28
        /// </summary>
        public static class Commands
        {
            /// <summary>
            /// Command text "C": initiate call.
            /// </summary>
            public const string CALL = "C";         // Initiate call
            /// <summary>
            /// Command text "CLR": clear call.
            /// </summary>
            public const string CLEAR = "CLR";      // Clear call
            /// <summary>
            /// Command text "STAT": show status.
            /// </summary>
            public const string STATUS = "STAT";    // Show status
            /// <summary>
            /// Command text "INT": send interrupt.
            /// </summary>
            public const string INTERRUPT = "INT";  // Send interrupt
            /// <summary>
            /// Command text "RESET": reset virtual circuit.
            /// </summary>
            public const string RESET = "RESET";    // Reset virtual circuit
            /// <summary>
            /// Command text "SET": set x.3 parameters.
            /// </summary>
            public const string SET = "SET";        // Set X.3 parameters
            /// <summary>
            /// Command text "PAR": read x.3 parameters.
            /// </summary>
            public const string PARAMETER = "PAR";  // Read X.3 parameters
            /// <summary>
            /// Command text "PROF": set parameter profile.
            /// </summary>
            public const string PROFILE = "PROF";   // Set parameter profile
            /// <summary>
            /// Command text "DATA": return to data transfer mode.
            /// </summary>
            public const string DATA = "DATA";      // Return to data transfer mode
        }

        // Events for terminal interface - PROPER ARCHITECTURE
        /// <summary>
        /// Raised when a PAD command line has been parsed.
        /// </summary>
        public event EventHandler<X28CommandEventArgs>? CommandReceived;
        /// <summary>
        /// Raised when buffered data is forwarded.
        /// </summary>
        public event EventHandler<X28DataEventArgs>? DataForwarded;
        /// <summary>
        /// Raised when the PAD mode changes.
        /// </summary>
        public event EventHandler<X28ModeChangedEventArgs>? ModeChanged;
#pragma warning disable CS0067 // Event for future character-level processing hooks
        /// <summary>
        /// Raised for character-level processing; nothing in this class raises it.
        /// </summary>
        public event EventHandler<X28CharacterEventArgs>? CharacterProcessed;
#pragma warning restore CS0067
        
        // NEW PROPER EVENTS - Replace string returns
        /// <summary>
        /// Raised when a character is to be echoed to the terminal.
        /// </summary>
        public event EventHandler<CharacterEchoEventArgs>? CharacterEcho;
        /// <summary>
        /// Raised when a command produces response text.
        /// </summary>
        public event EventHandler<CommandResponseEventArgs>? CommandResponse;
#pragma warning disable CS0067 // Event for future status update hooks
        /// <summary>
        /// Raised for status updates; nothing in this class raises it.
        /// </summary>
        public event EventHandler<StatusUpdateEventArgs>? StatusUpdate;
#pragma warning restore CS0067
        /// <summary>
        /// Raised when an error message is produced.
        /// </summary>
        public event EventHandler<ErrorMessageEventArgs>? ErrorMessage;

        private PadMode _currentMode = PadMode.Command;
        private DateTime _lastCharacterTime = DateTime.Now;
        private System.Threading.Timer? _idleTimer;

        /// <summary>
        /// Creates the X.28 interface in command mode.
        /// </summary>
        /// <param name="x3Parameters">
        /// The X.3 parameters that control the PAD behavior.
        /// </param>
        /// <param name="logger">
        /// Optional diagnostics logger; may be null.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="x3Parameters"/> is null.
        /// </exception>
        public X28(X3 x3Parameters, ILogger? logger = null)
        {
            _x3Parameters = x3Parameters ?? throw new ArgumentNullException(nameof(x3Parameters));
            _logger = logger;
            
            _logger?.LogDebug("X.28: Terminal interface initialized in command mode");
        }

        /// <summary>
        /// Current PAD operating mode
        /// </summary>
        public PadMode CurrentMode => _currentMode;

        /// <summary>
        /// Switch to data transfer mode (called when X.25 connection is established)
        /// </summary>
        public void SwitchToDataTransferMode()
        {
            _logger?.LogDebug("🎯 X.28: SwitchToDataTransferMode() called!");
            SwitchMode(PadMode.DataTransfer);
        }

        /// <summary>
        /// Switch to command mode (called when X.25 connection is cleared)
        /// </summary>
        public void SwitchToCommandMode()
        {
            SwitchMode(PadMode.Command);
        }

        /// <summary>
        /// Process incoming character from terminal
        /// </summary>
        /// <param name="character">
        /// Character received from terminal
        /// </param>
        /// <returns>
        /// True if character was processed successfully
        /// </returns>
        public bool ProcessCharacter(char character)
        {
            _lastCharacterTime = DateTime.Now;
            
            try
            {
                return _currentMode switch
                {
                    PadMode.Command => ProcessCommandModeCharacter(character),
                    PadMode.DataTransfer => ProcessDataTransferCharacter(character),
                    PadMode.Escape => ProcessEscapeModeCharacter(character),
                    _ => false
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "X.28: Error processing character {Char} in mode {Mode}", 
                    character, _currentMode);
                ErrorMessage?.Invoke(this, new ErrorMessageEventArgs($"Error processing character: {ex.Message}"));
                return false;
            }
        }

        /// <summary>
        /// Process character in command mode
        /// </summary>
        private bool ProcessCommandModeCharacter(char c)
        {
            // Handle special characters
            if (c == '\n')
            {
                // Fire immediate newline echo
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs('\r', EchoType.Normal));
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs('\n', EchoType.Normal));
                
                // Process the command
                ProcessCommandLine();
                return true;
            }
            
            if (c == '\b' || c == 0x7F) // Backspace or DEL
            {
                return HandleCommandBackspace();
            }
            
            // Add to command buffer
            _commandBuffer.Append(c);
            
            // Echo character (always echo in command mode)
            CharacterEcho?.Invoke(this, new CharacterEchoEventArgs(c, EchoType.Normal));
            return true;
        }

        /// <summary>
        /// Process complete command line
        /// </summary>
        private void ProcessCommandLine()
        {
            string commandLine = _commandBuffer.ToString().Trim().ToUpper();
            _commandBuffer.Clear();
            
            if (string.IsNullOrEmpty(commandLine))
                return;
                
            _logger?.LogDebug("X.28: Processing command: {Command}", commandLine);
            
            var command = ParseCommand(commandLine);
            
            // Process command and fire response event immediately
            var result = ProcessPadCommand(command);
            
            // Fire command event AFTER response is sent
            // This allows the response to be displayed before any connection events
            CommandReceived?.Invoke(this, new X28CommandEventArgs(command));
        }

        /// <summary>
        /// Parse command line into command structure
        /// </summary>
        private X28Command ParseCommand(string commandLine)
        {
            var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var arguments = new List<string>();
            for (int i = 1; i < parts.Length; i++)
            {
                arguments.Add(parts[i]);
            }
            var command = new X28Command
            {
                Verb = parts[0],
                Arguments = arguments,
                OriginalLine = commandLine
            };
            
            // Parse common command patterns and handle user variations
            switch (command.Verb)
            {
                case "C":
                case "CALL":
                    command.Verb = Commands.CALL;
                    if (command.Arguments.Count > 0)
                        command.Address = command.Arguments[0];
                    break;
                    
                case "CLR":
                case "CLEAR":
                    command.Verb = Commands.CLEAR;
                    break;
                    
                case "STAT":
                case "STATUS":
                    command.Verb = Commands.STATUS;
                    break;
                    
                case Commands.SET:
                    ParseSetCommand(command);
                    break;
                    
                case "PAR":
                case "PAR?":
                case "PARAMETER":
                    command.Verb = Commands.PARAMETER;
                    break;
                    
                case Commands.PROFILE:
                    command.Verb = Commands.PROFILE;
                    if (command.Arguments.Count > 0)
                        command.ProfileName = command.Arguments[0];
                    break;
                    
                case "DATA":
                    command.Verb = Commands.DATA;
                    break;
            }
            
            return command;
        }

        /// <summary>
        /// Parse SET command parameters
        /// </summary>
        private void ParseSetCommand(X28Command command)
        {
            command.Parameters = new Dictionary<byte, byte>();
            
            foreach (string arg in command.Arguments)
            {
                var match = Regex.Match(arg, @"^(\d+):(\d+)$");
                if (match.Success)
                {
                    if (byte.TryParse(match.Groups[1].Value, out byte paramNum) &&
                        byte.TryParse(match.Groups[2].Value, out byte paramValue))
                    {
                        command.Parameters[paramNum] = paramValue;
                    }
                }
            }
        }

        /// <summary>
        /// Process PAD command and return response
        /// Only processes commands when in Command mode (not DataTransfer mode)
        /// </summary>
        private CommandResult ProcessPadCommand(X28Command command)
        {
            return command.Verb switch
            {
                Commands.CALL => HandleCallCommand(command),
                Commands.CLEAR => HandleClearCommand(command),
                Commands.STATUS => HandleStatusCommand(command),
                Commands.SET => HandleSetCommand(command),
                Commands.PARAMETER => HandleParameterCommand(command),
                Commands.PROFILE => HandleProfileCommand(command),
                Commands.RESET => HandleResetCommand(command),
                Commands.INTERRUPT => HandleInterruptCommand(command),
                Commands.DATA => HandleDataCommand(command),
                _ => HandleInvalidCommand(command.Verb)
            };
        }

        /// <summary>
        /// Handle CALL command
        /// </summary>
        private CommandResult HandleCallCommand(X28Command command)
        {
            if (string.IsNullOrEmpty(command.Address))
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("Address required\r\n", ResponseType.Error));
                return CommandResult.InvalidParameters;
            }
                
            // Validate address format
            var x121 = new X121(_logger);
            if (!x121.IsValidAddress(command.Address))
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("Invalid address format\r\n", ResponseType.Error));
                return CommandResult.InvalidParameters;
            }
                
            // DON'T switch mode here - let the connection success/failure determine the mode
            // Mode will be switched by PadClient.HandleCallAccepted() when connection is actually established
            
            // Fire immediate "COM" response - this is critical for proper timing
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("COM\r\n", ResponseType.Success, isImmediate: true));
            
            _logger?.LogInformation("X.28: Call request to {Address}", command.Address);
            return CommandResult.Success;
        }

        /// <summary>
        /// Handle parameter-related commands
        /// </summary>
        private CommandResult HandleSetCommand(X28Command command)
        {
            if (command.Parameters == null || command.Parameters.Count == 0)
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("Parameters required (format: SET param:value)\r\n", ResponseType.Error));
                return CommandResult.InvalidParameters;
            }
                
            var results = new List<string>();
            var hasErrors = false;
            foreach (var param in command.Parameters)
            {
                if (_x3Parameters.SetParameter(param.Key, param.Value))
                {
                    results.Add($"{param.Key}:{param.Value}");
                }
                else
                {
                    results.Add($"{param.Key}:ERR");
                    hasErrors = true;
                }
            }
            
            var response = string.Join(" ", results) + "\r\n";
            var responseType = hasErrors ? ResponseType.Error : ResponseType.Success;
            CommandResponse?.Invoke(this, new CommandResponseEventArgs(response, responseType));
            return hasErrors ? CommandResult.InvalidParameters : CommandResult.Success;
        }

        private CommandResult HandleParameterCommand(X28Command command)
        {
            if (command.Arguments.Count > 0)
            {
                // Read specific parameters
                var results = new List<string>();
                foreach (string arg in command.Arguments)
                {
                    if (byte.TryParse(arg, out byte paramNum))
                    {
                        byte value = _x3Parameters.GetParameter(paramNum);
                        results.Add($"{paramNum}:{value}");
                    }
                }
                var response = string.Join(" ", results) + "\r\n";
                CommandResponse?.Invoke(this, new CommandResponseEventArgs(response, ResponseType.Success));
                return CommandResult.Success;
            }
            else
            {
                // Read all parameters
                var allParams = _x3Parameters.GetAllParameters();
                var paramStrings = new List<string>();
                foreach (var kvp in allParams)
                {
                    paramStrings.Add($"{kvp.Key}:{kvp.Value}");
                }
                var response = string.Join(" ", paramStrings) + "\r\n";
                CommandResponse?.Invoke(this, new CommandResponseEventArgs(response, ResponseType.Success));
                return CommandResult.Success;
            }
        }

        private CommandResult HandleProfileCommand(X28Command command)
        {
            if (string.IsNullOrEmpty(command.ProfileName))
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("Profile name required\r\n", ResponseType.Error));
                return CommandResult.InvalidParameters;
            }
                
            if (_x3Parameters.SetProfile(command.ProfileName))
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs($"{command.ProfileName} SET\r\n", ResponseType.Success));
                return CommandResult.Success;
            }
            else
            {
                CommandResponse?.Invoke(this, new CommandResponseEventArgs($"Unknown profile: {command.ProfileName}\r\n", ResponseType.Error));
                return CommandResult.InvalidParameters;
            }
        }

        /// <summary>
        /// Handle invalid commands
        /// </summary>
        private CommandResult HandleInvalidCommand(string verb)
        {
            CommandResponse?.Invoke(this, new CommandResponseEventArgs($"Invalid command: {verb}\r\n", ResponseType.Error));
            return CommandResult.InvalidCommand;
        }

        private CommandResult HandleStatusCommand(X28Command command)
        {
            var statusMessage = $"Mode: {_currentMode}, Parameters: {_x3Parameters}\r\n";
            CommandResponse?.Invoke(this, new CommandResponseEventArgs(statusMessage, ResponseType.Success));
            return CommandResult.Success;
        }

        private CommandResult HandleClearCommand(X28Command command)
        {
            SwitchMode(PadMode.Command);
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("CLEARED\r\n", ResponseType.Success));
            return CommandResult.Success;
        }

        private CommandResult HandleResetCommand(X28Command command)
        {
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("RESET\r\n", ResponseType.Success));
            return CommandResult.Success;
        }

        private CommandResult HandleInterruptCommand(X28Command command)
        {
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("INT\r\n", ResponseType.Success));
            return CommandResult.Success;
        }

        /// <summary>
        /// Handle DATA command - return to data transfer mode when connection is active
        /// </summary>
        private CommandResult HandleDataCommand(X28Command command)
        {
            if (_currentMode == PadMode.Command)
            {
                SwitchMode(PadMode.DataTransfer);
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("*** Resuming Data Transfer Mode ***\r\n", ResponseType.Success));
                return CommandResult.Success;
            }
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("Already in data transfer mode\r\n", ResponseType.Error));
            return CommandResult.NotAllowedInCurrentMode;
        }

        /// <summary>
        /// Handle backspace in command mode
        /// </summary>
        private bool HandleCommandBackspace()
        {
            if (_commandBuffer.Length > 0)
            {
                _commandBuffer.Length--;
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs('\b', EchoType.Backspace));
                return true;
            }
            return false;
        }

        /// <summary>
        /// Process character in data transfer mode
        /// </summary>
        private bool ProcessDataTransferCharacter(char c)
        {
            _logger?.LogDebug("X.28: Processing data transfer character '{Char}' (0x{Code:X2})", c, (int)c);
            
            // Check for escape to command mode
            if (IsEscapeCharacter(c))
            {
                return HandleEscapeCharacter();
            }
            
            // Check for editing characters if editing is enabled
            if (_x3Parameters.GetParameter(X3.ParameterNumber.EDITING) == 1)
            {
                if (c == _x3Parameters.GetParameter(X3.ParameterNumber.CHARACTER_DELETE))
                {
                    return HandleCharacterDelete();
                }
                
                if (c == _x3Parameters.GetParameter(X3.ParameterNumber.LINE_DELETE))
                {
                    return HandleLineDelete();
                }
                
                if (c == _x3Parameters.GetParameter(X3.ParameterNumber.LINE_DISPLAY))
                {
                    return HandleLineDisplay();
                }
            }
            
            // Add character to input buffer
            _inputBuffer.Append(c);
            _logger?.LogDebug("X.28: Added char to buffer, buffer length now: {Length}", _inputBuffer.Length);
            
            // Check if data should be forwarded
            var forwardingCondition = CheckForwardingCondition(c);
            _logger?.LogDebug("X.28: Forwarding condition for char '{Char}': {Condition}", c, forwardingCondition);
            
            if (forwardingCondition != ForwardingCondition.None)
            {
                _logger?.LogDebug("X.28: Forwarding buffered data due to condition: {Condition}", forwardingCondition);
                ForwardBufferedData(forwardingCondition);
            }
            
            // Restart idle timer
            RestartIdleTimer();
            
            // Fire echo event if enabled
            bool shouldEcho = ShouldEchoCharacter(c);
            _logger?.LogDebug("X.28: Should echo character '{Char}': {ShouldEcho}", c, shouldEcho);
            if (shouldEcho)
            {
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs(c, EchoType.Normal));
            }
            return true;
        }

        /// <summary>
        /// Check if character should trigger data forwarding per ITU-T X.3 specification
        /// 
        /// ITU-T X.3 PAD Parameters - Parameter 3 (DATA_FORWARDING):
        /// CCITT/ITU-T Recommendation X.3 defines forwarding trigger values:
        /// 
        /// Value | Meaning
        /// ------|--------------------------------------------------
        /// 0     | No automatic forwarding (buffer until explicit signal)
        /// 1     | Character-by-character forwarding (immediate)
        /// 2-31  | Forward on specific control characters:
        ///       |   2 = STX, 13 = CR (Carriage Return), etc.
        ///       |   Default: 13 (CR, 0x0D) per X.28 Section 4.1.2
        /// 32-126| Forward on specified printable character
        /// 127   | Forward on all alphanumeric characters
        /// 
        /// IMPORTANT: ITU-T X.28 specifies CR (0x0D) as standard line terminator,
        /// not LF (0x0A). This maintains compatibility with historical X.25 networks.
        /// </summary>
        private ForwardingCondition CheckForwardingCondition(char c)
        {
            byte forwarding = _x3Parameters.GetParameter(X3.ParameterNumber.DATA_FORWARDING);
            
            // No forwarding
            if (forwarding == 0)
                return ForwardingCondition.None;
                
            // Character-by-character forwarding
            if (forwarding == 1)
            {
                return ForwardingCondition.CharacterByCharacter;
            }
            
            // Specific character forwarding (2-31 are control characters)
            if (forwarding >= 2 && forwarding <= 31)
            {
                if ((byte)c == forwarding)
                {
                    return c == '\r' ? ForwardingCondition.CarriageReturn : ForwardingCondition.ControlCharacter;
                }
            }
            
            // Specific printable character forwarding (32-126)
            else if (forwarding >= 32 && forwarding <= 126)
            {
                if ((byte)c == forwarding)
                {
                    return ForwardingCondition.SpecificCharacter;
                }
            }
            
            // Forward on all alphanumeric (127)
            else if (forwarding == 127)
            {
                if (char.IsLetterOrDigit(c))
                {
                    return ForwardingCondition.AlphanumericCharacter;
                }
            }
            
            // Check buffer full condition
            if (_inputBuffer.Length >= 128) // Typical PAD buffer limit
            {
                return ForwardingCondition.BufferFull;
            }
            
            return ForwardingCondition.None;
        }

        /// <summary>
        /// Forward buffered data to network
        /// </summary>
        private void ForwardBufferedData(ForwardingCondition condition)
        {
            if (_inputBuffer.Length == 0)
                return;
                
            string data = _inputBuffer.ToString();
            _inputBuffer.Clear();
            
            _logger?.LogTrace("X.28: Forwarding {Length} characters (condition: {Condition})", 
                data.Length, condition);
            
            // Fire data forwarded event
            DataForwarded?.Invoke(this, new X28DataEventArgs(data, condition));
        }

        /// <summary>
        /// Check if character should be echoed
        /// </summary>
        private bool ShouldEchoCharacter(char c)
        {
            if (_x3Parameters.GetParameter(X3.ParameterNumber.ECHO) == 0)
                return false;
                
            // Check echo mask
            byte echoMask = _x3Parameters.GetParameter(X3.ParameterNumber.ECHO_MASK);
            if (echoMask != 0)
            {
                // Apply echo mask logic (implementation specific)
                return true; // Simplified - echo all for now
            }
            
            return true;
        }

        /// <summary>
        /// Handle character editing functions
        /// </summary>
        private bool HandleCharacterDelete()
        {
            if (_inputBuffer.Length > 0)
            {
                _inputBuffer.Length--;
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs('\b', EchoType.Delete));
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs(' ', EchoType.Delete));
                CharacterEcho?.Invoke(this, new CharacterEchoEventArgs('\b', EchoType.Delete));
                return true;
            }
            return false;
        }

        private bool HandleLineDelete()
        {
            if (_inputBuffer.Length > 0)
            {
                int lineStart = _inputBuffer.ToString().LastIndexOf('\n') + 1;
                _inputBuffer.Length = lineStart;
                CommandResponse?.Invoke(this, new CommandResponseEventArgs("XXX\r\n", ResponseType.Success)); // Line delete indication
                return true;
            }
            return false;
        }

        private bool HandleLineDisplay()
        {
            var lineContent = _inputBuffer.ToString() + "\r\n";
            CommandResponse?.Invoke(this, new CommandResponseEventArgs(lineContent, ResponseType.Success));
            return true;
        }

        /// <summary>
        /// Check if character is the escape character for returning to command mode
        /// X.3 Parameter 1: 0 = no escape, 1 = escape enabled (DLE character 0x10)
        /// </summary>
        private bool IsEscapeCharacter(char c)
        {
            byte escapeParam = _x3Parameters.GetParameter(X3.ParameterNumber.ESCAPE);
            return escapeParam == 1 && c == 0x10; // DLE (Data Link Escape)
        }

        /// <summary>
        /// Handle escape character processing - temporarily return to command mode
        /// Connection remains active but user can issue PAD commands
        /// </summary>
        private bool HandleEscapeCharacter()
        {
            _logger?.LogDebug("X.28: PAD escape character detected, switching to command mode (connection maintained)");
            SwitchMode(PadMode.Command);
            CommandResponse?.Invoke(this, new CommandResponseEventArgs("\r\n*** PAD Command Mode (connection maintained) ***\r\nCMD: ", ResponseType.Success));
            return true;
        }

        /// <summary>
        /// Process character in escape mode (not implemented in basic X.28)
        /// </summary>
        private bool ProcessEscapeModeCharacter(char c)
        {
            // Return to data transfer mode
            SwitchMode(PadMode.DataTransfer);
            return ProcessDataTransferCharacter(c);
        }

        /// <summary>
        /// Switch PAD operating mode
        /// </summary>
        private void SwitchMode(PadMode newMode)
        {
            if (_currentMode != newMode)
            {
                var oldMode = _currentMode;
                _currentMode = newMode;
                
                _logger?.LogDebug("🚨 X.28: MODE CHANGED: {OldMode} → {NewMode} 🚨", oldMode, newMode);
                
                // Clean up based on mode transition
                if (newMode == PadMode.Command)
                {
                    StopIdleTimer();
                    if (_inputBuffer.Length > 0)
                    {
                        _logger?.LogDebug("X.28: Forwarding {Length} buffered characters before switching to command mode", _inputBuffer.Length);
                        ForwardBufferedData(ForwardingCondition.ManualForward);
                    }
                }
                else if (newMode == PadMode.DataTransfer)
                {
                    _logger?.LogDebug("X.28: Switched to data transfer mode - clearing command buffer and starting idle timer");
                    _commandBuffer.Clear();
                    RestartIdleTimer();
                }
                
                // Fire mode changed event
                ModeChanged?.Invoke(this, new X28ModeChangedEventArgs(oldMode, newMode));
            }
            else
            {
                _logger?.LogDebug("X.28: Mode switch requested to {Mode} but already in that mode", newMode);
            }
        }

        /// <summary>
        /// Restart idle timer based on X.3 parameter 4
        /// </summary>
        private void RestartIdleTimer()
        {
            StopIdleTimer();
            
            byte idleTimeout = _x3Parameters.GetParameter(X3.ParameterNumber.IDLE_TIMER);
            if (idleTimeout > 0 && _currentMode == PadMode.DataTransfer)
            {
                int timeoutMs = idleTimeout * 50; // 50ms units (1/20 second)
                _idleTimer = new System.Threading.Timer(OnIdleTimeout, null, timeoutMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Stop idle timer
        /// </summary>
        private void StopIdleTimer()
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        /// <summary>
        /// Handle idle timer expiry
        /// </summary>
        private void OnIdleTimeout(object? state)
        {
            try
            {
                if (_inputBuffer.Length > 0)
                {
                    ForwardBufferedData(ForwardingCondition.Timer);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "X.28: Error in idle timeout handler");
            }
        }

        /// <summary>
        /// Force forward any buffered data
        /// </summary>
        public void ForceForwardData()
        {
            if (_inputBuffer.Length > 0)
            {
                ForwardBufferedData(ForwardingCondition.ManualForward);
            }
        }

        /// <summary>
        /// Get current input buffer content
        /// </summary>
        public string GetBufferedInput()
        {
            return _inputBuffer.ToString();
        }

        /// <summary>
        /// Clear input buffer
        /// </summary>
        public void ClearInputBuffer()
        {
            _inputBuffer.Clear();
            _logger?.LogTrace("X.28: Input buffer cleared");
        }

        /// <summary>
        /// Process PAD command and return response
        /// </summary>
        /// <param name="command">
        /// Command string to process
        /// </param>
        /// <returns>
        /// Response string
        /// </returns>
        public CommandResult ProcessCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
            {
                ErrorMessage?.Invoke(this, new ErrorMessageEventArgs("Empty command"));
                return CommandResult.InvalidCommand;
            }

            var cmd = command.Trim().ToUpperInvariant();
            
            // Common X.28 commands
            switch (cmd)
            {
                case "STAT":
                    CommandResponse?.Invoke(this, new CommandResponseEventArgs("CONNECTED\r\n", ResponseType.Success));
                    return CommandResult.Success;
                case "CLR":
                    return CommandResult.Success; // Clear - handled by disconnect
                case "RESET":
                    CommandResponse?.Invoke(this, new CommandResponseEventArgs("\r\n", ResponseType.Success));
                    return CommandResult.Success;
                default:
                    CommandResponse?.Invoke(this, new CommandResponseEventArgs(command + "\r\n", ResponseType.Success)); // Pass through unknown commands
                    return CommandResult.Success;
            }
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            StopIdleTimer();
            _commandBuffer.Clear();
            _inputBuffer.Clear();
        }
    }

    /// <summary>
    /// X.28 command structure
    /// </summary>
    public class X28Command
    {
        /// <summary>
        /// Gets or sets the command verb.
        /// </summary>
        public string Verb { get; set; } = "";
        /// <summary>
        /// Gets or sets the command arguments.
        /// </summary>
        public List<string> Arguments { get; set; } = new List<string>();
        /// <summary>
        /// Gets or sets the command line as typed.
        /// </summary>
        public string OriginalLine { get; set; } = "";
        /// <summary>
        /// Gets or sets the called address of a call command, or null.
        /// </summary>
        public string? Address { get; set; }
        /// <summary>
        /// Gets or sets the profile name of a profile command, or null.
        /// </summary>
        public string? ProfileName { get; set; }
        /// <summary>
        /// Gets or sets the parameter number and value pairs of a command, or null.
        /// </summary>
        public Dictionary<byte, byte>? Parameters { get; set; }
    }

    /// <summary>
    /// Event arguments for X.28 interface events
    /// </summary>
    public class X28CommandEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the parsed command.
        /// </summary>
        public X28Command Command { get; }
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="command">
        /// The parsed command.
        /// </param>
        public X28CommandEventArgs(X28Command command) => Command = command;
    }

    /// <summary>
    /// Event arguments for forwarded data.
    /// </summary>
    public class X28DataEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the forwarded data.
        /// </summary>
        public string Data { get; }
        /// <summary>
        /// Gets the condition that triggered the forwarding.
        /// </summary>
        public X28.ForwardingCondition Condition { get; }
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="data">
        /// The forwarded data.
        /// </param>
        /// <param name="condition">
        /// The condition that triggered the forwarding.
        /// </param>
        public X28DataEventArgs(string data, X28.ForwardingCondition condition)
        {
            Data = data;
            Condition = condition;
        }
    }

    /// <summary>
    /// Event arguments for a PAD mode change.
    /// </summary>
    public class X28ModeChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the mode before the change.
        /// </summary>
        public X28.PadMode OldMode { get; }
        /// <summary>
        /// Gets the mode after the change.
        /// </summary>
        public X28.PadMode NewMode { get; }
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="oldMode">
        /// The mode before the change.
        /// </param>
        /// <param name="newMode">
        /// The mode after the change.
        /// </param>
        public X28ModeChangedEventArgs(X28.PadMode oldMode, X28.PadMode newMode)
        {
            OldMode = oldMode;
            NewMode = newMode;
        }
    }

    /// <summary>
    /// Event arguments for character-level processing.
    /// </summary>
    public class X28CharacterEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the character.
        /// </summary>
        public char Character { get; }
        /// <summary>
        /// Gets the response text, or null when there is none.
        /// </summary>
        public string? Response { get; }
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="character">
        /// The character.
        /// </param>
        /// <param name="response">
        /// The response text, or null.
        /// </param>
        public X28CharacterEventArgs(char character, string? response)
        {
            Character = character;
            Response = response;
        }
    }

    /// <summary>
    /// Command processing results - PROPER ARCHITECTURE
    /// </summary>
    public enum CommandResult 
    {
        /// <summary>
        /// The command succeeded.
        /// </summary>
        Success,
        /// <summary>
        /// The command was not recognized.
        /// </summary>
        InvalidCommand,
        /// <summary>
        /// The command parameters were not valid.
        /// </summary>
        InvalidParameters, 
        /// <summary>
        /// The command is not allowed in the current mode.
        /// </summary>
        NotAllowedInCurrentMode,
        /// <summary>
        /// The command needs a connection.
        /// </summary>
        ConnectionRequired,
        /// <summary>
        /// The command failed.
        /// </summary>
        Error
    }

    /// <summary>
    /// Character echo types
    /// </summary>
    public enum EchoType
    {
        /// <summary>
        /// A normal character.
        /// </summary>
        Normal,
        /// <summary>
        /// A backspace.
        /// </summary>
        Backspace,
        /// <summary>
        /// A delete.
        /// </summary>
        Delete,
        /// <summary>
        /// A carriage return.
        /// </summary>
        CarriageReturn,
        /// <summary>
        /// A line feed.
        /// </summary>
        LineFeed
    }

    /// <summary>
    /// Response types for command responses
    /// </summary>
    public enum ResponseType
    {
        /// <summary>
        /// Response to a successful command.
        /// </summary>
        Success,
        /// <summary>
        /// Response to a failed command.
        /// </summary>
        Error,
        /// <summary>
        /// Status text.
        /// </summary>
        Status,
        /// <summary>
        /// A response that is shown immediately, such as COM.
        /// </summary>
        Immediate,  // For immediate responses like "COM"
        /// <summary>
        /// A prompt.
        /// </summary>
        Prompt
    }

    /// <summary>
    /// Event args for character echo - PROPER ARCHITECTURE
    /// </summary>
    public class CharacterEchoEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the echoed character.
        /// </summary>
        public char Character { get; }
        /// <summary>
        /// Gets the kind of echo.
        /// </summary>
        public EchoType Type { get; }
        
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="character">
        /// The echoed character.
        /// </param>
        /// <param name="type">
        /// The kind of echo.
        /// </param>
        public CharacterEchoEventArgs(char character, EchoType type = EchoType.Normal)
        {
            Character = character;
            Type = type;
        }
    }

    /// <summary>
    /// Event args for command responses - PROPER ARCHITECTURE
    /// </summary>
    public class CommandResponseEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the response text.
        /// </summary>
        public string Response { get; }
        /// <summary>
        /// Gets whether the response is shown immediately.
        /// </summary>
        public bool IsImmediate { get; }
        /// <summary>
        /// Gets the response type.
        /// </summary>
        public ResponseType Type { get; }
        
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="response">
        /// The response text.
        /// </param>
        /// <param name="type">
        /// The response type.
        /// </param>
        /// <param name="isImmediate">
        /// Whether the response is shown immediately.
        /// </param>
        public CommandResponseEventArgs(string response, ResponseType type = ResponseType.Success, bool isImmediate = false)
        {
            Response = response;
            Type = type;
            IsImmediate = isImmediate;
        }
    }

    /// <summary>
    /// Event args for status updates - PROPER ARCHITECTURE
    /// </summary>
    public class StatusUpdateEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the status text.
        /// </summary>
        public string Status { get; }
        
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="status">
        /// The status text.
        /// </param>
        public StatusUpdateEventArgs(string status)
        {
            Status = status;
        }
    }

    /// <summary>
    /// Event args for error messages - PROPER ARCHITECTURE
    /// </summary>
    public class ErrorMessageEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the error message text.
        /// </summary>
        public string ErrorMessage { get; }
        
        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="errorMessage">
        /// The error message text.
        /// </param>
        public ErrorMessageEventArgs(string errorMessage)
        {
            ErrorMessage = errorMessage;
        }
    }
}