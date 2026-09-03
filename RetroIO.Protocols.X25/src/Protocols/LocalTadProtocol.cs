using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// Local TAD Protocol Handler for loopback testing
    /// Manages local X.25 connections between PAD clients and TAD servers
    /// Uses event-driven architecture for clean service layer separation
    /// </summary>
    public class LocalTadProtocol
    {
        private readonly ILogger _logger;
        private readonly Dictionary<ushort, LocalTadSession> _sessions = new Dictionary<ushort, LocalTadSession>();
        private readonly object _sessionsLock = new object();
        private readonly string _localMachineId;

        /// <summary>
        /// Event raised when LocalTadProtocol generates an X.25 packet for a PAD client
        /// </summary>
        public event EventHandler<X25PacketEventArgs>? PacketGenerated;

        public LocalTadProtocol(ILogger logger, string localMachineId)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _localMachineId = localMachineId ?? throw new ArgumentNullException(nameof(localMachineId), "LocalMachineId is required from centralized configuration");
        }

        /// <summary>
        /// Get the local machine ID configured for this TAD server
        /// </summary>
        public string LocalMachineId => _localMachineId;

        /// <summary>
        /// Handle outgoing call request to local TAD
        /// </summary>
        public bool HandleOutgoingCall(ushort lcn, string calledAddress, string callingAddress, byte[]? userData)
        {
            if (calledAddress != _localMachineId)
                return false; // Not for local TAD

            try
            {
                _logger.LogInformation("LocalTAD: Processing call to {CalledAddress} on LCN {LCN}", calledAddress, lcn);

                // Create local session
                var session = new LocalTadSession
                {
                    LCN = lcn,
                    CalledAddress = calledAddress,
                    CallingAddress = callingAddress,
                    StartTime = DateTime.UtcNow
                };
                
                // Initialize original parameter values from current X.3 defaults
                session.OriginalEcho = session.X3Parameters.GetParameter(2);
                session.OriginalForwarding = session.X3Parameters.GetParameter(3);
                session.OriginalEditing = session.X3Parameters.GetParameter(15);

                lock (_sessionsLock)
                {
                    _sessions[lcn] = session;
                }

                // Send call accepted - needed for internal PAD-to-TAD path
                // (For XOT inbound calls, the routing layer also sends Call Accepted,
                // but the C code's X.25 layer ignores duplicates)
                SendCallAccepted(session);

                // Send X.29 SET to configure remote PAD for character-at-a-time mode:
                //   Param 2  = 0: Echo OFF (TAD handles echo with proper BS+SPACE+BS)
                //   Param 3  = 126: Forward on all printable + control chars
                //   Param 4  = 1: Idle timer = 50ms (fast char-by-char forwarding)
                //   Param 14 = 0: Editing off (TAD handles editing)
                SendX29Message(session, new byte[] {
                    0x02,        // X.29 SET message type
                    0x02, 0x00,  // Param 2 (echo) = off (TAD echoes)
                    0x03, 0x7E,  // Param 3 (forwarding) = all chars
                    0x04, 0x01,  // Param 4 (idle timer) = 50ms
                    0x0E, 0x00   // Param 14 (editing) = off (TAD edits)
                });

                // Send welcome message immediately
                SendWelcomeMessage(session);

                _logger.LogInformation("LocalTAD: Connection established on LCN {LCN}", lcn);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalTAD: Error handling outgoing call on LCN {LCN}", lcn);
                return false;
            }
        }

        /// <summary>
        /// Handle incoming data from PAD client
        /// </summary>
        public void HandleIncomingData(ushort lcn, byte[] data)
        {
            LocalTadSession? session = null;
            
            lock (_sessionsLock)
            {
                if (!_sessions.TryGetValue(lcn, out session))
                {
                    _logger.LogWarning("LocalTAD: No session found for LCN {LCN}", lcn);
                    return;
                }
            }

            try
            {
                // Acknowledge received data by incrementing P(R)
                session.RecvSequence = (byte)((session.RecvSequence + 1) & 0x07);

                _logger.LogDebug("LocalTAD: Received {Length} bytes on LCN {LCN}", data.Length, lcn);

                // Process each byte: echo back to terminal and buffer for line editing
                var echoBytes = new List<byte>();

                for (int i = 0; i < data.Length; i++)
                {
                    byte b = data[i];
                    if (b == 0x0D || b == 0x0A) // CR or LF
                    {
                        // Skip LF immediately following CR
                        if (b == 0x0D && i + 1 < data.Length && data[i + 1] == 0x0A)
                            i++;

                        // Echo CR+LF
                        echoBytes.Add(0x0D);
                        echoBytes.Add(0x0A);

                        // Flush echo before processing command
                        if (echoBytes.Count > 0)
                        {
                            SendRawBytes(session, echoBytes.ToArray());
                            echoBytes.Clear();
                        }

                        // Process the buffered line
                        var line = session.LineBuffer.ToString().Trim();
                        session.LineBuffer.Clear();

                        if (line.Length == 0)
                        {
                            SendResponse(session, "TAD> ");
                            continue;
                        }

                        _logger.LogInformation("LocalTAD: *** LINE *** LCN {LCN}: '{Line}'", lcn, line);
                        ProcessCommand(session, line);
                    }
                    else if (b == 0x08 || b == 0x7F) // Backspace or DEL
                    {
                        if (session.LineBuffer.Length > 0)
                        {
                            session.LineBuffer.Length--;
                            // Echo BS + SPACE + BS to visually erase character
                            echoBytes.Add(0x08);
                            echoBytes.Add(0x20);
                            echoBytes.Add(0x08);
                        }
                    }
                    else if (b >= 0x20 && b < 0x7F) // Printable ASCII
                    {
                        if (session.LineBuffer.Length >= 100)
                        {
                            _logger.LogWarning("LocalTAD: Line buffer overflow on LCN {LCN}, discarding input", lcn);
                            session.LineBuffer.Clear();
                            SendRawBytes(session, echoBytes.ToArray());
                            echoBytes.Clear();
                            SendResponse(session, "\r\nERROR: Input too long (max 100 chars)\r\nTAD> ");
                            break;
                        }
                        session.LineBuffer.Append((char)b);
                        // Echo the character back
                        echoBytes.Add(b);
                    }
                    // Ignore other control characters
                }

                // Send any remaining echo bytes
                if (echoBytes.Count > 0)
                    SendRawBytes(session, echoBytes.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalTAD: Error processing data on LCN {LCN}", lcn);
            }
        }

        /// <summary>
        /// Process a complete line of input as a command or echo
        /// </summary>
        private void ProcessCommand(LocalTadSession session, string text)
        {
            switch (text.ToUpper())
            {
                case "HELP":
                    SendHelpMessage(session);
                    break;

                case "STATUS":
                    SendStatusMessage(session);
                    break;

                case "QUIT":
                case "EXIT":
                case "BYE":
                    SendGoodbyeMessage(session);
                    SendClearRequest(session);
                    break;

                case "X29":
                    SendX29Information(session);
                    break;

                case "PARAMS":
                    SendParameterInformation(session);
                    break;

                case "LOGIN":
                    HandleLoginCommand(session);
                    break;

                default:
                    SendEchoResponse(session, text);
                    break;
            }
        }

        /// <summary>
        /// Handle call clear request
        /// </summary>
        public void HandleCallClear(ushort lcn)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogInformation("LocalTAD: Call cleared on LCN {LCN}", lcn);
                    _sessions.Remove(lcn);
                    session.Dispose();
                }
            }
        }

        /// <summary>
        /// Send Call Accepted response using proper X25 enums and packet builder
        /// </summary>
        private void SendCallAccepted(LocalTadSession session)
        {
            _logger.LogInformation("LocalTAD: Creating Call Accepted packet for LCN {LCN}", session.LCN);
            
            // Use proper X25PacketBuilder for accurate packet construction
            var packetData = X25PacketBuilder.CreateCallAccepted(
                lcn: session.LCN,
                facilities: null
            );

            var callAcceptedPacket = new X25Packet(packetData);
            callAcceptedPacket.Direction = PacketDirection.Receive;
            callAcceptedPacket.Timestamp = DateTime.UtcNow;

            var eventArgs = new X25PacketEventArgs
            {
                Packet = callAcceptedPacket,
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            };

            _logger.LogInformation("LocalTAD: Notifying PAD client with {PacketType} (PTI=0x{PTI:X2}) on LCN {LCN}", 
                                 X25PacketTypeIdentifier.CallAccepted.GetDescription(),
                                 (byte)X25PacketTypeIdentifier.CallAccepted, 
                                 session.LCN);
            RaisePacketGenerated(eventArgs);
            _logger.LogDebug("LocalTAD: Sent Call Accepted on LCN {LCN}", session.LCN);
        }

        /// <summary>
        /// Send welcome message
        /// </summary>
        private void SendWelcomeMessage(LocalTadSession session)
        {
            var welcomeText = "*** X.25 TAD Loopback Echo Service ***\r\n" +
                             $"Connected to address {_localMachineId}\r\n" +
                             "Type 'HELP' for commands, 'QUIT' to exit\r\n" +
                             "All text will be echoed back to you\r\n" +
                             "TAD> ";
            
            SendResponse(session, welcomeText);
        }

        /// <summary>
        /// Send text response to PAD client using proper X25 enums and packet builder
        /// </summary>
        /// <summary>
        /// Send raw bytes to the PAD (for character echo). No text encoding.
        /// </summary>
        private void SendRawBytes(LocalTadSession session, byte[] rawData)
        {
            if (rawData == null || rawData.Length == 0) return;

            byte ps = session.SendSequence;
            byte pr = session.RecvSequence;
            session.SendSequence = (byte)((ps + 1) & 0x07);

            var packetData = X25PacketBuilder.CreateDataPacket(
                lcn: session.LCN,
                ps: ps,
                pr: pr,
                userData: rawData
            );

            var dataPacket = new X25Packet(packetData);
            dataPacket.Direction = PacketDirection.Receive;
            dataPacket.Timestamp = DateTime.UtcNow;

            RaisePacketGenerated(new X25PacketEventArgs
            {
                Packet = dataPacket,
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Maximum user data per X.25 data packet (128 bytes default, matching C code's pkt_size)
        /// </summary>
        private const int MaxPacketUserData = 128;

        private void SendResponse(LocalTadSession session, string text)
        {
            var allData = Encoding.ASCII.GetBytes(text);

            // Fragment into MaxPacketUserData-sized chunks with M-bit for continuation
            int offset = 0;
            while (offset < allData.Length)
            {
                int remaining = allData.Length - offset;
                int chunkSize = remaining > MaxPacketUserData ? MaxPacketUserData : remaining;
                bool moreData = (offset + chunkSize) < allData.Length;

                var chunk = new byte[chunkSize];
                Array.Copy(allData, offset, chunk, 0, chunkSize);

                byte ps = session.SendSequence;
                byte pr = session.RecvSequence;
                session.SendSequence = (byte)((ps + 1) & 0x07);

                var packetData = X25PacketBuilder.CreateDataPacket(
                    lcn: session.LCN,
                    ps: ps,
                    pr: pr,
                    userData: chunk,
                    mBit: moreData
                );

                var dataPacket = new X25Packet(packetData);
                dataPacket.Direction = PacketDirection.Receive;
                dataPacket.Timestamp = DateTime.UtcNow;

                var eventArgs = new X25PacketEventArgs
                {
                    Packet = dataPacket,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow
                };

                RaisePacketGenerated(eventArgs);
                offset += chunkSize;
            }

            _logger.LogDebug("LocalTAD: Sent response on LCN {LCN} ({Chunks} chunks, {Total} bytes)",
                session.LCN, (allData.Length + MaxPacketUserData - 1) / MaxPacketUserData, allData.Length);
        }

        /// <summary>
        /// Send clear request to terminate connection using proper X25 enums and packet builder
        /// </summary>
        private void SendClearRequest(LocalTadSession session)
        {
            // Use proper X25PacketBuilder for accurate clear request construction
            var packetData = X25PacketBuilder.CreateClearRequest(
                lcn: session.LCN,
                clearingCause: 0x00, // Normal clearing
                diagnosticCode: 0x00
            );

            var clearPacket = new X25Packet(packetData);
            clearPacket.Direction = PacketDirection.Receive;
            clearPacket.Timestamp = DateTime.UtcNow;

            var eventArgs = new X25PacketEventArgs
            {
                Packet = clearPacket,
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow
            };

            RaisePacketGenerated(eventArgs);
            _logger.LogDebug("LocalTAD: Sent {PacketType} on LCN {LCN}", 
                           X25PacketTypeIdentifier.ClearRequest.GetDescription(), session.LCN);
            
            // Remove session
            lock (_sessionsLock)
            {
                _sessions.Remove(session.LCN);
            }
            session.Dispose();
        }

        /// <summary>
        /// Raise PacketGenerated event to notify service layer
        /// </summary>
        private void RaisePacketGenerated(X25PacketEventArgs eventArgs)
        {
            PacketGenerated?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Send enhanced help message with X.25 protocol information
        /// </summary>
        private void SendHelpMessage(LocalTadSession session)
        {
            var helpText = "*** X.25 TAD Server Help ***\r\n" +
                          "Available commands:\r\n" +
                          "HELP - Show this help\r\n" +
                          "STATUS - Show connection status\r\n" +
                          "QUIT - Disconnect\r\n" +
                          "\r\n" +
                          "Echo Service:\r\n" +
                          "  Any other text will be echoed back with analysis\r\n" +
                          "TAD> ";
            
            SendResponse(session, helpText);
        }

        /// <summary>
        /// Send enhanced status message with session details
        /// </summary>
        private void SendStatusMessage(LocalTadSession session)
        {
            var uptime = DateTime.UtcNow - session.StartTime;
            var statusText = $"Connection Status:\r\n" +
                           $"LCN: {session.LCN}\r\n" +
                           $"Connected: {uptime.TotalMinutes:F1} minutes\r\n" +
                           $"Echo service active\r\n" +
                           "TAD> ";
            
            SendResponse(session, statusText);
        }

        /// <summary>
        /// Send X.29 protocol information using enum descriptions
        /// </summary>
        private void SendX29Information(LocalTadSession session)
        {
            var x29Text = "*** X.29 PAD Protocol Information ***\r\n" +
                         "X.29 Message Types:\r\n" +
                         $"  {X29MessageType.ParameterIndication.GetDescription()}\r\n" +
                         $"  {X29MessageType.InvitationToClear.GetDescription()}\r\n" +
                         $"  {X29MessageType.SetParameters.GetDescription()}\r\n" +
                         $"  {X29MessageType.ReadParameters.GetDescription()}\r\n" +
                         $"  {X29MessageType.Error.GetDescription()}\r\n" +
                         "\r\n" +
                         "This TAD server implements X.25 packet switching\r\n" +
                         "with LAPB link layer and HDLC framing.\r\n" +
                         "TAD> ";
            
            SendResponse(session, x29Text);
        }

        /// <summary>
        /// Send session parameter information
        /// </summary>
        private void SendParameterInformation(LocalTadSession session)
        {
            var paramText = "*** Session Parameters ***\r\n" +
                          $"  Protocol Layer: X.25 Packet Layer\r\n" +
                          $"  Link Layer: LAPB (Link Access Procedure Balanced)\r\n" +
                          $"  Physical Layer: HDLC (High-level Data Link Control)\r\n" +
                          $"  Character Encoding: ASCII\r\n" +
                          $"  Flow Control: X.25 Supervisory packets\r\n" +
                          $"  Error Recovery: LAPB retransmission\r\n" +
                          $"  Maximum Frame Size: ITU-T X.25 standard\r\n" +
                          "TAD> ";
            
            SendResponse(session, paramText);
        }

        /// <summary>
        /// Send goodbye message before disconnection
        /// </summary>
        private void SendGoodbyeMessage(LocalTadSession session)
        {
            var goodbyeText = "Goodbye!\r\n" +
                            "Connection will be cleared...\r\n";
            
            SendResponse(session, goodbyeText);
        }

        /// <summary>
        /// Send enhanced echo response with message analysis
        /// </summary>
        private void SendEchoResponse(LocalTadSession session, string originalText)
        {
            var analysis = AnalyzeMessage(originalText);
            var echoText = $"Echo: {originalText}\r\n" +
                         $"Analysis: {analysis}\r\n" +
                         "TAD> ";
            
            SendResponse(session, echoText);
        }

        /// <summary>
        /// Handle LOGIN command to demonstrate password mode functionality
        /// </summary>
        private void HandleLoginCommand(LocalTadSession session)
        {
            var sessionId = $"LCN-{session.LCN}";
            
            var loginText = "*** Login Demonstration ***\r\n" +
                          "This demonstrates X.3 parameter switching for password mode\r\n" +
                          "\r\n" +
                          "Username: ";
            
            SendResponse(session, loginText);
            
            // For demonstration purposes, we could implement a simple state machine
            // In a real implementation, you would track login state per session
            // and handle the password input after receiving username
            
            // Example of entering password mode (commented for demo):
            // EnterPasswordMode(sessionId);
            // After receiving password input, call ExitPasswordMode(sessionId);
            
            var demoText = "\r\n" +
                          "Password mode functionality available:\r\n" +
                          $"- EnterPasswordMode(\"{sessionId}\") - Disables echo, enables char forwarding\r\n" +
                          $"- ExitPasswordMode(\"{sessionId}\") - Restores original parameters\r\n" +
                          $"- IsInPasswordMode(\"{sessionId}\") - Check current mode\r\n" +
                          "TAD> ";
            
            SendResponse(session, demoText);
        }

        /// <summary>
        /// Analyze incoming message for educational purposes
        /// </summary>
        private string AnalyzeMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "Empty message";

            var length = message.Length;

            // Check character types without LINQ
            bool hasNumbers = false;
            bool hasLetters = false;
            bool hasSpecial = false;
            for (int i = 0; i < message.Length; i++)
            {
                char c = message[i];
                if (char.IsDigit(c)) hasNumbers = true;
                else if (char.IsLetter(c)) hasLetters = true;
                else if (!char.IsWhiteSpace(c)) hasSpecial = true;

                // Early exit if all found
                if (hasNumbers && hasLetters && hasSpecial) break;
            }

            var analysis = $"{length} chars";
            if (hasNumbers) analysis += ", numeric";
            if (hasLetters) analysis += ", alphabetic";
            if (hasSpecial) analysis += ", special chars";

            return analysis;
        }

        /// <summary>
        /// Get active sessions count
        /// </summary>
        public int GetActiveSessionsCount()
        {
            lock (_sessionsLock)
            {
                return _sessions.Count;
            }
        }

        /// <summary>
        /// Get detailed information about active sessions
        /// </summary>
        public LocalTadSessionInfo[] GetActiveSessions()
        {
            lock (_sessionsLock)
            {
                var sessions = new LocalTadSessionInfo[_sessions.Count];
                int index = 0;
                foreach (var session in _sessions.Values)
                {
                    sessions[index++] = new LocalTadSessionInfo
                    {
                        LCN = session.LCN,
                        CalledAddress = session.CalledAddress,
                        CallingAddress = session.CallingAddress,
                        StartTime = session.StartTime,
                        Duration = DateTime.UtcNow - session.StartTime
                    };
                }
                return sessions;
            }
        }

        /// <summary>
        /// Disconnect a specific session
        /// </summary>
        public bool DisconnectSession(ushort lcn)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogInformation("LocalTAD: Manually disconnecting session on LCN {LCN}", lcn);
                    SendClearRequest(session);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Get X.3 parameters for a specific session by session ID
        /// </summary>
        public Dictionary<byte, byte>? GetSessionX3Parameters(string sessionId)
        {
            lock (_sessionsLock)
            {
                var session = FindSessionById(sessionId);
                return session?.X3Parameters.GetAllParameters();
            }
        }

        /// <summary>
        /// Get X.3 parameters for a specific session by LCN
        /// </summary>
        public Dictionary<byte, byte>? GetSessionX3ParametersByLCN(ushort lcn)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    return session.X3Parameters.GetAllParameters();
                }
                return null;
            }
        }

        /// <summary>
        /// Set X.3 parameter for a specific session and send X.29 Parameter Indication
        /// </summary>
        public bool SetSessionX3Parameter(string sessionId, byte parameterNumber, byte value)
        {
            lock (_sessionsLock)
            {
                var session = FindSessionById(sessionId);
                if (session == null) return false;

                // Update the parameter
                if (!session.X3Parameters.SetParameter(parameterNumber, value))
                    return false;

                // Send X.29 Parameter Indication to PAD client
                var parameters = new Dictionary<byte, byte> { { parameterNumber, value } };
                var indication = X29Messages.CreateParameterIndication(parameters);
                SendX29Message(session, indication);

                _logger.LogInformation("LocalTAD: Updated X.3 parameter {ParamNum} to {Value} for session {SessionId}", 
                    parameterNumber, value, sessionId);
                return true;
            }
        }

        /// <summary>
        /// Send X.29 control message to PAD client
        /// </summary>
        private void SendX29Message(LocalTadSession session, byte[] message)
        {
            try
            {
                // Create X.25 data packet with Q-bit set for X.29 control message
                byte ps = session.SendSequence;
                byte pr = session.RecvSequence;
                session.SendSequence = (byte)((ps + 1) & 0x07);

                var packetData = X25PacketBuilder.CreateDataPacket(
                    lcn: session.LCN,
                    ps: ps,
                    pr: pr,
                    userData: message,
                    mBit: false,
                    qBit: true, // Q-bit set for X.29 control message
                    dBit: false
                );

                var packet = new X25Packet(packetData);
                packet.Direction = PacketDirection.Transmit;
                packet.Timestamp = DateTime.UtcNow;

                var eventArgs = new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow
                };

                RaisePacketGenerated(eventArgs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalTAD: Error sending X.29 message to session LCN {LCN}", session.LCN);
            }
        }

        /// <summary>
        /// Enter password mode for a session (disables echo, enables character forwarding, disables editing)
        /// This is commonly used when TAD applications need secure password input
        /// </summary>
        public bool EnterPasswordMode(string sessionId)
        {
            lock (_sessionsLock)
            {
                var session = FindSessionById(sessionId);
                if (session == null) return false;

                // Store original parameters for restoration
                session.OriginalEcho = session.X3Parameters.GetParameter(2);
                session.OriginalForwarding = session.X3Parameters.GetParameter(3);
                session.OriginalEditing = session.X3Parameters.GetParameter(15);

                // Apply password mode parameters
                bool success = true;
                success &= SetSessionX3Parameter(sessionId, 2, 0);  // Disable echo
                success &= SetSessionX3Parameter(sessionId, 3, 1);  // Character-by-character forwarding
                success &= SetSessionX3Parameter(sessionId, 15, 0); // Disable editing

                if (success)
                {
                    _logger.LogInformation("LocalTAD: Entered password mode for session {SessionId}", sessionId);
                }
                else
                {
                    _logger.LogWarning("LocalTAD: Failed to fully enter password mode for session {SessionId}", sessionId);
                }

                return success;
            }
        }

        /// <summary>
        /// Exit password mode for a session (restores original echo, forwarding, and editing parameters)
        /// </summary>
        public bool ExitPasswordMode(string sessionId)
        {
            lock (_sessionsLock)
            {
                var session = FindSessionById(sessionId);
                if (session == null) return false;

                // Restore original parameters
                bool success = true;
                success &= SetSessionX3Parameter(sessionId, 2, session.OriginalEcho);
                success &= SetSessionX3Parameter(sessionId, 3, session.OriginalForwarding);
                success &= SetSessionX3Parameter(sessionId, 15, session.OriginalEditing);

                if (success)
                {
                    _logger.LogInformation("LocalTAD: Exited password mode for session {SessionId}", sessionId);
                }
                else
                {
                    _logger.LogWarning("LocalTAD: Failed to fully exit password mode for session {SessionId}", sessionId);
                }

                return success;
            }
        }

        /// <summary>
        /// Check if a session is currently in password mode
        /// </summary>
        public bool IsInPasswordMode(string sessionId)
        {
            var parameters = GetSessionX3Parameters(sessionId);
            if (parameters == null) return false;

            // Password mode: echo disabled, character forwarding enabled, editing disabled
            return parameters[2] == 0 && parameters[3] == 1 && parameters[15] == 0;
        }

        /// <summary>
        /// Enter password mode for a session by LCN
        /// </summary>
        public bool EnterPasswordModeByLCN(ushort lcn)
        {
            return EnterPasswordMode($"LCN-{lcn}");
        }

        /// <summary>
        /// Exit password mode for a session by LCN
        /// </summary>
        public bool ExitPasswordModeByLCN(ushort lcn)
        {
            return ExitPasswordMode($"LCN-{lcn}");
        }

        /// <summary>
        /// Check if a session is in password mode by LCN
        /// </summary>
        public bool IsInPasswordModeByLCN(ushort lcn)
        {
            return IsInPasswordMode($"LCN-{lcn}");
        }

        /// <summary>
        /// Find session by session ID
        /// </summary>
        private LocalTadSession? FindSessionById(string sessionId)
        {
            if (sessionId.StartsWith("LCN-") && ushort.TryParse(sessionId.Substring(4), out var lcn))
            {
                _sessions.TryGetValue(lcn, out var session);
                return session;
            }
            return null;
        }
    }

    /// <summary>
    /// Local TAD session information (public for GUI display)
    /// </summary>
    public class LocalTadSessionInfo
    {
        public ushort LCN { get; set; }
        public string CalledAddress { get; set; } = "";
        public string CallingAddress { get; set; } = "";
        public DateTime StartTime { get; set; }
        public TimeSpan Duration { get; set; }
        public string SessionId => $"LCN-{LCN}"; // Unique session identifier
    }

    /// <summary>
    /// Local TAD session information (internal)
    /// </summary>
    internal class LocalTadSession : IDisposable
    {
        public ushort LCN { get; set; }
        public string CalledAddress { get; set; } = "";
        public string CallingAddress { get; set; } = "";
        public DateTime StartTime { get; set; }
        public X3Parameters X3Parameters { get; set; } = new X3Parameters(); // X.3 parameters for this session

        // X.25 modulo 8 sequence numbers for proper flow control
        public byte SendSequence { get; set; } = 0;    // P(S) - next sequence number to send
        public byte RecvSequence { get; set; } = 0;     // P(R) - next expected sequence from peer

        // Line buffer for accumulating characters until CR/LF
        public StringBuilder LineBuffer { get; } = new StringBuilder();

        // Original parameter values for password mode restoration
        public byte OriginalEcho { get; set; } = 1;      // Default echo enabled
        public byte OriginalForwarding { get; set; } = 13; // Default CR forwarding
        public byte OriginalEditing { get; set; } = 1;   // Default editing enabled

        public void Dispose()
        {
            // No timers to dispose of anymore
        }
    }
}