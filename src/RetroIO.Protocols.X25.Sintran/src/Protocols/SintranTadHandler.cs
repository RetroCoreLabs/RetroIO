//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

using RetroIO.Protocols.X25.Protocols;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// SINTRAN TAD (Terminal Access and Data) Protocol Handler
    /// Implements the server side of SINTRAN TAD protocol
    /// Handles terminal connections from SINTRAN PAD clients
    /// Based on SINTRAN NPL source code (RP-P2-TAD.NPL)
    /// </summary>
    public class SintranTadHandler : ISintranProtocolHandler
    {
        private readonly ILogger _logger;
        private SintranHandlerStats _stats;
        private SintranTadSession _session;
        private bool _initialized = false;

        /// <inheritdoc/>
        public string Protocol => "TAD";
        /// <inheritdoc/>
        public string Description => "SINTRAN Terminal Access and Data protocol";
        /// <inheritdoc/>
        public ushort LCN { get; private set; }

        /// <inheritdoc/>
        public event EventHandler<SintranResponseEventArgs>? ResponseReady;

        /// <summary>
        /// Creates the handler.
        /// </summary>
        /// <param name="logger">
        /// The logger to write to.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="logger"/> is null.
        /// </exception>
        public SintranTadHandler(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _stats = new SintranHandlerStats
            {
                Protocol = Protocol,
                Status = "Initializing"
            };
            _session = new SintranTadSession
            {
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };
        }

        /// <inheritdoc/>
        public void Initialize()
        {
            if (_initialized) return;

            _logger.LogInformation("SintranTadHandler: Initializing TAD protocol handler");
            _stats.IsActive = true;
            _stats.Status = "Ready";
            _initialized = true;
        }

        /// <inheritdoc/>
        public void OnChannelEstablished(ushort lcn)
        {
            LCN = lcn;
            _stats.LCN = lcn;
            _stats.IsActive = true;
            _stats.LastActivity = DateTime.UtcNow;
            _stats.Status = "Connected";

            _logger.LogInformation("SintranTadHandler: TAD channel established on LCN {LCN}", lcn);

            // Start connection handshake (INIBDR phase)
            StartConnectionHandshake();
        }

        /// <inheritdoc/>
        public void OnChannelClosed()
        {
            _stats.IsActive = false;
            _stats.Status = "Disconnected";
            _session.State = TadSessionState.Closed;
            _logger.LogInformation("SintranTadHandler: TAD channel closed on LCN {LCN}", LCN);
        }

        /// <inheritdoc/>
        public void HandleData(byte[] data)
        {
            if (data.Length == 0) return;

            _stats.MessagesReceived++;
            _stats.BytesReceived += data.Length;
            _stats.LastActivity = DateTime.UtcNow;
            _session.LastActivity = DateTime.UtcNow;

            try
            {
                _logger.LogInformation("SintranTadHandler: Received {Bytes} bytes on LCN {LCN}: {Data}",
                    data.Length, LCN, BitConverter.ToString(data));

                // Parse TAD message
                if (!SintranTadMessage.TryParse(data, out var message, out var error))
                {
                    _logger.LogError("SintranTadHandler: Failed to parse TAD message: {Error}", error);
                    SendErrorResponse("Message parse error");
                    return;
                }

                _logger.LogInformation("SintranTadHandler: Parsed TAD message: {Message}", message);

                // Process message based on type
                ProcessTadMessage(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SintranTadHandler: Error processing TAD data on LCN {LCN}", LCN);
                _stats.Status = $"Error: {ex.Message}";
                SendErrorResponse(ex.Message);
            }
        }

        /// <summary>
        /// Process incoming TAD message based on type
        /// </summary>
        private void ProcessTadMessage(SintranTadMessage message)
        {
            _session.MessagesReceived++;

            // Route message based on type
            if (message.MessageType == SintranTadMessageTypes.DUMM)
            {
                HandleDummyMessage(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.BDAT)
            {
                HandleBinaryData(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.RFI)
            {
                HandleReadyForInput(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.TMOD)
            {
                HandleTerminalMode(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.TTYP)
            {
                HandleTerminalType(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.OPSV)
            {
                HandleOptionSave(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.DESC)
            {
                HandleDescriptor(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.ESCA)
            {
                HandleEscapeAction(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.BMMX)
            {
                HandleBreakMatrix(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.ECKM)
            {
                HandleEchoMatrix(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.REJE)
            {
                HandleReject(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.RLOC)
            {
                HandleRelocate(message);
            }
            else if (message.MessageType == SintranTadMessageTypes.DCON)
            {
                HandleDisconnect(message);
            }
            else
            {
                _logger.LogWarning("SintranTadHandler: Unhandled message type: {MessageType}",
                    SintranTadMessageTypes.GetMessageTypeName(message.MessageType));
                // Some message types don't require responses
            }
        }

        /// <summary>
        /// Start connection handshake (INIBDR → INISND phases)
        /// </summary>
        private void StartConnectionHandshake()
        {
            _logger.LogInformation("SintranTadHandler: Starting connection handshake (INIBDR phase)");
            _session.State = TadSessionState.WaitingForInibdr;

            // INIBDR phase: Wait for client configuration
            // Send initial dummy message to establish communication
            var dummyMsg = SintranTadMessage.CreateDummy();
            SendTadMessage(dummyMsg);

            // INISND phase: Send our configuration
            SendServerConfiguration();

            // Send welcome message
            SendWelcomeMessage();

            _session.State = TadSessionState.Active;
            _stats.Status = "Active (Handshake Complete)";
        }

        /// <summary>
        /// Send server configuration messages
        /// </summary>
        private void SendServerConfiguration()
        {
            _logger.LogInformation("SintranTadHandler: Sending server configuration");

            // Send terminal type (7TTYP)
            var ttypMsg = SintranTadMessage.CreateTerminalType(0x01); // ASCII terminal
            SendTadMessage(ttypMsg);

            // Send terminal mode (7TMOD)
            var tmodMsg = SintranTadMessage.CreateTerminalMode(
                echoPlex: true,
                localEdit: true,
                pageMode: false,
                formFeed: false);
            SendTadMessage(tmodMsg);

            // Send descriptor (7DESC)
            var descMsg = SintranTadMessage.CreateDescriptor(
                lineWidth: 80,
                pageLength: 24,
                tabWidth: 8);
            SendTadMessage(descMsg);

            // Send option save (7OPSV)
            var opsvMsg = SintranTadMessage.CreateOptionSave(true);
            SendTadMessage(opsvMsg);

            // Send break matrix (7BMMX) - default strategy
            var bmmxMsg = SintranTadMessage.CreateBreakMatrix(_session.BreakModeMatrix);
            SendTadMessage(bmmxMsg);

            // Send echo matrix (7ECKM) - default strategy
            var eckmMsg = SintranTadMessage.CreateEchoMatrix(_session.EchoModeMatrix);
            SendTadMessage(eckmMsg);

            _logger.LogInformation("SintranTadHandler: Server configuration sent");
        }

        /// <summary>
        /// Handle 7DUMM (dummy) message
        /// </summary>
        private void HandleDummyMessage(SintranTadMessage message)
        {
            _logger.LogDebug("SintranTadHandler: Received dummy message");
            // Dummy messages require no response
        }

        /// <summary>
        /// Handle 7BDAT (binary data) message
        /// Enhanced with command processing and echo service
        /// </summary>
        private void HandleBinaryData(SintranTadMessage message)
        {
            if (message.Data.Length == 0)
            {
                _logger.LogWarning("SintranTadHandler: Received empty binary data message");
                return;
            }

            var text = message.GetDataAsString().Trim();
            _logger.LogInformation("SintranTadHandler: Received text on LCN {LCN}: '{Text}'", LCN, text);

            _session.BytesReceived += message.Data.Length;

            // Handle special commands (similar to LocalTadProtocol)
            switch (text.ToUpper())
            {
                case "HELP":
                    SendHelpMessage();
                    break;

                case "STATUS":
                    SendStatusMessage();
                    break;

                case "QUIT":
                case "EXIT":
                case "BYE":
                    SendGoodbyeMessage();
                    // Disconnect message will be sent
                    var disconnectMsg = SintranTadMessage.CreateDisconnect();
                    SendTadMessage(disconnectMsg);
                    break;

                case "INFO":
                    SendInfoMessage();
                    break;

                default:
                    // Enhanced echo service with message analysis
                    SendEchoResponse(text);
                    break;
            }

            // Send RFI (Ready For Input) after response
            var rfi = SintranTadMessage.CreateReadyForInput(0);
            SendTadMessage(rfi);
        }

        /// <summary>
        /// Handle 7RFI (Ready For Input) message
        /// </summary>
        private void HandleReadyForInput(SintranTadMessage message)
        {
            _logger.LogDebug("SintranTadHandler: Received RFI (Ready For Input)");

            if (message.Data.Length >= 2)
            {
                int byteCount = (message.Data[0] << 8) | message.Data[1];
                _logger.LogDebug("SintranTadHandler: Client ready for {Count} bytes", byteCount);
            }

            // Send any queued high-priority messages
            SendQueuedMessages(highPriority: true);

            // Send any queued normal-priority messages
            SendQueuedMessages(highPriority: false);
        }

        /// <summary>
        /// Handle 7TMOD (Terminal Mode) configuration
        /// </summary>
        private void HandleTerminalMode(SintranTadMessage message)
        {
            if (message.Data.Length < 1)
            {
                _logger.LogWarning("SintranTadHandler: Invalid TMOD message (too short)");
                return;
            }

            _session.EchoPlex = (message.Data[0] & 0x01) != 0;
            _session.LocalEdit = (message.Data[0] & 0x02) != 0;
            _session.PageMode = (message.Data[0] & 0x04) != 0;
            _session.FormFeed = (message.Data[0] & 0x08) != 0;

            _logger.LogInformation("SintranTadHandler: Terminal mode configured - Echo:{Echo} LocalEdit:{Edit} Page:{Page} FF:{FF}",
                _session.EchoPlex, _session.LocalEdit, _session.PageMode, _session.FormFeed);

            // Acknowledge configuration
            var ack = SintranTadMessage.CreateTerminalMode(
                _session.EchoPlex,
                _session.LocalEdit,
                _session.PageMode,
                _session.FormFeed);
            SendTadMessage(ack);
        }

        /// <summary>
        /// Handle 7TTYP (Terminal Type) configuration
        /// </summary>
        private void HandleTerminalType(SintranTadMessage message)
        {
            if (message.Data.Length < 1)
            {
                _logger.LogWarning("SintranTadHandler: Invalid TTYP message (too short)");
                return;
            }

            _session.TerminalType = message.Data[0];
            _logger.LogInformation("SintranTadHandler: Terminal type set to: 0x{Type:X2}", _session.TerminalType);

            // Acknowledge configuration
            var ack = SintranTadMessage.CreateTerminalType(_session.TerminalType);
            SendTadMessage(ack);
        }

        /// <summary>
        /// Handle 7OPSV (Option Save) configuration
        /// </summary>
        private void HandleOptionSave(SintranTadMessage message)
        {
            if (message.Data.Length < 1)
            {
                _logger.LogWarning("SintranTadHandler: Invalid OPSV message (too short)");
                return;
            }

            _session.OptionSave = message.Data[0] != 0;
            _logger.LogInformation("SintranTadHandler: Option save: {OptionSave}", _session.OptionSave);

            // Acknowledge configuration
            var ack = SintranTadMessage.CreateOptionSave(_session.OptionSave);
            SendTadMessage(ack);

            // OPSV is typically the last client configuration message
            // Send RFI to signal handshake completion and readiness for data
            _logger.LogInformation("SintranTadHandler: Client configuration complete, sending RFI");
            var rfi = SintranTadMessage.CreateReadyForInput(0);
            SendTadMessage(rfi);
        }

        /// <summary>
        /// Handle 7DESC (Descriptor) configuration
        /// </summary>
        private void HandleDescriptor(SintranTadMessage message)
        {
            if (message.Data.Length < 6)
            {
                _logger.LogWarning("SintranTadHandler: Invalid DESC message (too short)");
                return;
            }

            _session.LineWidth = (message.Data[0] << 8) | message.Data[1];
            _session.PageLength = (message.Data[2] << 8) | message.Data[3];
            _session.TabWidth = (message.Data[4] << 8) | message.Data[5];

            _logger.LogInformation("SintranTadHandler: Descriptor configured - Width:{Width} Height:{Height} Tab:{Tab}",
                _session.LineWidth, _session.PageLength, _session.TabWidth);

            // Acknowledge configuration
            var ack = SintranTadMessage.CreateDescriptor(_session.LineWidth, _session.PageLength, _session.TabWidth);
            SendTadMessage(ack);
        }

        /// <summary>
        /// Handle 7ESCA (Escape Action) message
        /// </summary>
        private void HandleEscapeAction(SintranTadMessage message)
        {
            _logger.LogInformation("SintranTadHandler: Escape action received");

            // Respond with escape acknowledgment
            var ack = SintranTadMessage.CreateEscapeAction();
            SendTadMessage(ack);
        }

        /// <summary>
        /// Handle 7BMMX (Break Matrix) configuration
        /// </summary>
        private void HandleBreakMatrix(SintranTadMessage message)
        {
            if (message.Data.Length != 20)
            {
                _logger.LogWarning("SintranTadHandler: Invalid BMMX message (expected 20 bytes, got {Count})", message.Data.Length);
                return;
            }

            Array.Copy(message.Data, _session.BreakModeMatrix, 20);
            _logger.LogInformation("SintranTadHandler: Break strategy matrix updated");

            // Acknowledge configuration
            var ack = SintranTadMessage.CreateBreakMatrix(_session.BreakModeMatrix);
            SendTadMessage(ack);
        }

        /// <summary>
        /// Handle 7ECKM (Echo Matrix) configuration
        /// </summary>
        private void HandleEchoMatrix(SintranTadMessage message)
        {
            if (message.Data.Length != 20)
            {
                _logger.LogWarning("SintranTadHandler: Invalid ECKM message (expected 20 bytes, got {Count})", message.Data.Length);
                return;
            }

            Array.Copy(message.Data, _session.EchoModeMatrix, 20);
            _logger.LogInformation("SintranTadHandler: Echo strategy matrix updated");

            // Acknowledge configuration
            var ack = SintranTadMessage.CreateEchoMatrix(_session.EchoModeMatrix);
            SendTadMessage(ack);
        }

        /// <summary>
        /// Handle 7REJE (Reject) message
        /// </summary>
        private void HandleReject(SintranTadMessage message)
        {
            _logger.LogWarning("SintranTadHandler: Client rejected last message");
            _stats.Status = "Client Reject";
        }

        /// <summary>
        /// Handle 7RLOC (Relocate) message
        /// </summary>
        private void HandleRelocate(SintranTadMessage message)
        {
            _logger.LogInformation("SintranTadHandler: Relocate request received");
            // Relocate typically means connection transfer - acknowledge and maintain state
            var ack = SintranTadMessage.CreateRelocate();
            SendTadMessage(ack);
        }

        /// <summary>
        /// Handle 7DCON (Disconnect) message
        /// </summary>
        private void HandleDisconnect(SintranTadMessage message)
        {
            _logger.LogInformation("SintranTadHandler: Client requested disconnect");
            _session.State = TadSessionState.Closing;
            _stats.Status = "Disconnecting";

            // Acknowledge disconnect
            var ack = SintranTadMessage.CreateDisconnect();
            SendTadMessage(ack);

            // Close channel (implementation depends on X25Layer integration)
            _session.State = TadSessionState.Closed;
        }

        /// <summary>
        /// Send welcome message on connection
        /// </summary>
        private void SendWelcomeMessage()
        {
            var welcomeText = "*** SINTRAN TAD Echo Service ***\r\n" +
                            "Connected to SINTRAN TAD Server\r\n" +
                            "Type 'HELP' for commands, 'QUIT' to exit\r\n" +
                            "All text will be echoed back to you\r\n" +
                            "TAD> ";

            SendTextResponse(welcomeText);
        }

        /// <summary>
        /// Send help message with available commands
        /// </summary>
        private void SendHelpMessage()
        {
            var helpText = "*** SINTRAN TAD Server Help ***\r\n" +
                          "Available commands:\r\n" +
                          "  HELP   - Show this help\r\n" +
                          "  STATUS - Show connection status\r\n" +
                          "  INFO   - Show SINTRAN TAD information\r\n" +
                          "  QUIT   - Disconnect\r\n" +
                          "\r\n" +
                          "Echo Service:\r\n" +
                          "  Any other text will be echoed back with analysis\r\n" +
                          "TAD> ";

            SendTextResponse(helpText);
        }

        /// <summary>
        /// Send connection status information
        /// </summary>
        private void SendStatusMessage()
        {
            var uptime = DateTime.UtcNow - _session.CreatedAt;
            var statusText = $"Connection Status:\r\n" +
                           $"  LCN: {LCN}\r\n" +
                           $"  Connected: {uptime.TotalMinutes:F1} minutes\r\n" +
                           $"  Messages Sent: {_session.MessagesSent}\r\n" +
                           $"  Messages Received: {_session.MessagesReceived}\r\n" +
                           $"  Bytes Sent: {_session.BytesSent}\r\n" +
                           $"  Bytes Received: {_session.BytesReceived}\r\n" +
                           $"  Echo service active\r\n" +
                           "TAD> ";

            SendTextResponse(statusText);
        }

        /// <summary>
        /// Send SINTRAN TAD protocol information
        /// </summary>
        private void SendInfoMessage()
        {
            var infoText = "*** SINTRAN TAD Protocol Information ***\r\n" +
                          "Protocol: SINTRAN Terminal Access and Data\r\n" +
                          "Based on: SINTRAN NPL (RP-P2-TAD.NPL)\r\n" +
                          "\r\n" +
                          "Message Types:\r\n" +
                          "  7DUMM - Dummy message\r\n" +
                          "  7BDAT - Binary data\r\n" +
                          "  7RFI  - Ready for input\r\n" +
                          "  7TMOD - Terminal mode\r\n" +
                          "  7TTYP - Terminal type\r\n" +
                          "  7DESC - Descriptor\r\n" +
                          "\r\n" +
                          "Features:\r\n" +
                          "  - Echo/Local edit modes\r\n" +
                          "  - Break/Echo strategy matrices\r\n" +
                          "  - Page mode support\r\n" +
                          "TAD> ";

            SendTextResponse(infoText);
        }

        /// <summary>
        /// Send goodbye message before disconnect
        /// </summary>
        private void SendGoodbyeMessage()
        {
            var goodbyeText = "Goodbye!\r\n" +
                            "Connection will be closed...\r\n";

            SendTextResponse(goodbyeText);
        }

        /// <summary>
        /// Send enhanced echo response with message analysis
        /// </summary>
        private void SendEchoResponse(string originalText)
        {
            var analysis = AnalyzeMessage(originalText);
            var echoText = $"Echo: {originalText}\r\n" +
                         $"Analysis: {analysis}\r\n" +
                         "TAD> ";

            SendTextResponse(echoText);
        }

        /// <summary>
        /// Analyze incoming message for educational purposes
        /// </summary>
        private string AnalyzeMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "Empty message";

            var length = message.Length;
            var hasNumbers = false;
            var hasLetters = false;
            var hasSpecial = false;

            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                if (char.IsDigit(c)) hasNumbers = true;
                if (char.IsLetter(c)) hasLetters = true;
                if (!char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c)) hasSpecial = true;
            }

            var analysis = $"{length} chars";
            if (hasNumbers) analysis += ", numeric";
            if (hasLetters) analysis += ", alphabetic";
            if (hasSpecial) analysis += ", special chars";

            return analysis;
        }

        /// <summary>
        /// Send text response to client (converts to BDAT message)
        /// </summary>
        private void SendTextResponse(string text)
        {
            var data = System.Text.Encoding.ASCII.GetBytes(text);
            var response = SintranTadMessage.CreateBinaryData(data);
            SendTadMessage(response);
        }

        /// <summary>
        /// Send TAD message to client
        /// </summary>
        private void SendTadMessage(SintranTadMessage message)
        {
            var data = message.ToBytes();

            _stats.MessagesSent++;
            _stats.BytesSent += data.Length;
            _session.MessagesSent++;
            _session.BytesSent += data.Length;
            _stats.LastActivity = DateTime.UtcNow;
            _session.LastActivity = DateTime.UtcNow;

            var eventArgs = new SintranResponseEventArgs
            {
                LCN = LCN,
                Data = data,
                Protocol = Protocol,
                Timestamp = DateTime.UtcNow
            };

            ResponseReady?.Invoke(this, eventArgs);

            _logger.LogDebug("SintranTadHandler: Sent TAD message on LCN {LCN}: {Message}", LCN, message);
        }

        /// <summary>
        /// Send queued messages to client
        /// </summary>
        private void SendQueuedMessages(bool highPriority)
        {
            var queue = highPriority ? _session.OutputQueueHigh : _session.OutputQueueNormal;

            while (queue.Count > 0)
            {
                var message = queue.Dequeue();
                SendTadMessage(message);
            }
        }

        /// <summary>
        /// Send error response to client
        /// </summary>
        private void SendErrorResponse(string errorMessage)
        {
            _logger.LogError("SintranTadHandler: Sending error response: {Error}", errorMessage);

            // Send error response (7ERRS message type)
            var errorMsg = SintranTadMessage.CreateError(errorMessage);
            SendTadMessage(errorMsg);
        }

        /// <inheritdoc/>
        public SintranHandlerStats GetStats()
        {
            return new SintranHandlerStats
            {
                Protocol = _stats.Protocol,
                LCN = _stats.LCN,
                MessagesReceived = _stats.MessagesReceived,
                MessagesSent = _stats.MessagesSent,
                BytesReceived = _stats.BytesReceived,
                BytesSent = _stats.BytesSent,
                LastActivity = _stats.LastActivity,
                IsActive = _stats.IsActive,
                Status = _stats.Status
            };
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!_initialized) return;

            _logger.LogDebug("SintranTadHandler: Disposing TAD handler for LCN {LCN}", LCN);
            _stats.IsActive = false;
            _stats.Status = "Disposed";
            _session.State = TadSessionState.Closed;
            _initialized = false;
        }
    }
}
