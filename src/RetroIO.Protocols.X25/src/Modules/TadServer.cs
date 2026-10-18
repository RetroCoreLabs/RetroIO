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
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Modules
{
    /// <summary>
    /// TAD (Terminal Access Daemon) Server according to ITU-T X.3, X.28, X.29
    /// Provides terminal services for incoming PAD connections
    /// Includes built-in loopback echo server on address 200 for testing
    /// </summary>
    public class TadServer : IProtocolModule, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IProtocolRuntime _runtime;
        private readonly Dictionary<ushort, TadSession> _sessions = new Dictionary<ushort, TadSession>();
        private readonly object _sessionsLock = new object();

        // Configuration
        /// <summary>
        /// Gets or sets the address this server answers to; a call to it gets the loopback echo service.
        /// </summary>
        public string LocalAddress { get; set; } = "200"; // Default loopback test address
        /// <summary>
        /// Gets or sets whether text received through the legacy data callback is echoed back.
        /// </summary>
        public bool EnableEchoService { get; set; } = true;
        /// <summary>
        /// Gets or sets the loopback test flag; nothing in this class reads it.
        /// </summary>
        public bool EnableLoopbackTest { get; set; } = true;
        /// <summary>
        /// Gets or sets the text encoding used to turn text into bytes and bytes into text.
        /// </summary>
        public Encoding TextEncoding { get; set; } = Encoding.ASCII;
        
        // IProtocolModule Properties
        /// <summary>
        /// Gets or sets the module name; <c>TAD Server</c> unless changed.
        /// </summary>
        public string Name { get; set; } = "TAD Server";
        /// <summary>
        /// Gets the module description.
        /// </summary>
        public string Description { get; } = "X.3/X.28/X.29 TAD Server providing terminal services for PAD connections";
        /// <summary>
        /// Gets the module version, <c>1.0.0</c>.
        /// </summary>
        public string Version { get; } = "1.0.0";

        // Events
        /// <summary>
        /// Raised when a session starts.
        /// </summary>
        public event EventHandler<TadSessionEventArgs>? SessionStarted;
        /// <summary>
        /// Raised when a session ends.
        /// </summary>
        public event EventHandler<TadSessionEventArgs>? SessionEnded;
        /// <summary>
        /// Raised when text is received on a session.
        /// </summary>
        public event EventHandler<TadDataEventArgs>? DataReceived;
        /// <summary>
        /// Raised when text is sent on a session.
        /// </summary>
        public event EventHandler<TadDataEventArgs>? DataSent;

        /// <summary>
        /// Creates the TAD server.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <param name="runtime">
        /// The protocol runtime that carries the calls.
        /// </param>
        public TadServer(ILogger logger, IProtocolRuntime runtime)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            
            _logger.LogInformation("TAD Server initialized - Local address: {LocalAddress}", LocalAddress);
        }

        #region IProtocolModule Implementation

        /// <summary>
        /// Called when the module is bound to a logical channel. The server only logs it; sessions
        /// are created when a call arrives.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        public void OnBound(ushort lcn)
        {
            _logger.LogDebug("TAD Server bound to LCN {LCN}", lcn);
        }

        /// <summary>
        /// Called when the module is unbound from a logical channel. A session on that channel is
        /// disposed, removed and reported through <see cref="SessionEnded"/>; a channel without a
        /// session is ignored.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number being unbound.
        /// </param>
        public void OnUnbound(ushort lcn)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogInformation("TAD session {SessionId} unbound from LCN {LCN}", session.SessionId, lcn);
                    session.Dispose();
                    _sessions.Remove(lcn);
                    
                    OnSessionEnded(session);
                }
            }
        }

        /// <summary>
        /// Called when an X.25 packet arrives for the module.
        /// </summary>
        /// <param name="args">
        /// The packet event.
        /// </param>
        /// <remarks>
        /// Call Request, Data, Clear Request and Interrupt packets are each passed to their own
        /// handler; other packet types are ignored. An exception thrown while handling a packet is
        /// logged and not passed on.
        /// </remarks>
        public void OnX25Event(X25PacketEventArgs args)
        {
            try
            {
                switch (args.Packet.PacketType)
                {
                    case X25PacketType.CallRequest:
                        HandleIncomingCall(args);
                        break;
                    case X25PacketType.Data:
                        HandleIncomingData(args);
                        break;
                    case X25PacketType.ClearRequest:
                        HandleClearRequest(args);
                        break;
                    case X25PacketType.Interrupt:
                        HandleInterrupt(args);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling X.25 event in TAD Server");
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Get active session count
        /// </summary>
        public int ActiveSessionCount
        {
            get
            {
                lock (_sessionsLock)
                {
                    return _sessions.Count;
                }
            }
        }

        /// <summary>
        /// Get session information
        /// </summary>
        public TadSessionInfo[] GetActiveSessions()
        {
            lock (_sessionsLock)
            {
                var sessions = new TadSessionInfo[_sessions.Count];
                int index = 0;
                foreach (var session in _sessions.Values)
                {
                    sessions[index++] = new TadSessionInfo
                    {
                        SessionId = session.SessionId,
                        LCN = session.LCN,
                        CallingAddress = session.CallingAddress,
                        StartTime = session.StartTime,
                        BytesReceived = session.BytesReceived,
                        BytesSent = session.BytesSent,
                        ServiceType = session.ServiceType
                    };
                }
                return sessions;
            }
        }

        /// <summary>
        /// Terminate session
        /// </summary>
        public bool TerminateSession(string sessionId)
        {
            lock (_sessionsLock)
            {
                var session = FindSessionById(sessionId);
                if (session == null)
                    return false;

                _logger.LogInformation("Terminating TAD session {SessionId}", sessionId);
                
                // Send clear request
                // TODO: Implement via ProtocolRuntime when available
                
                session.Dispose();
                _sessions.Remove(session.LCN);
                OnSessionEnded(session);
                
                return true;
            }
        }

        /// <summary>
        /// Get X.3 parameters for a specific session
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
        /// Get X.3 parameters for a specific LCN
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
        /// Set X.3 parameter for a specific session (simulates host-initiated parameter change)
        /// </summary>
        public bool SetSessionX3Parameter(string sessionId, byte parameterNumber, byte value)
        {
            lock (_sessionsLock)
            {
                var session = FindSessionById(sessionId);
                if (session == null)
                    return false;

                // Update parameter
                session.X3Parameters.SetParameter(parameterNumber, value);
                
                // Send X.29 Parameter Indication to PAD client
                var parameters = new Dictionary<byte, byte> { [parameterNumber] = value };
                var indication = X29Messages.CreateParameterIndication(parameters);
                SendX29Message(session, indication);
                
                _logger.LogDebug("TAD set X.3 parameter {ParamNum}={Value} for session {SessionId}", 
                               parameterNumber, value, sessionId);
                
                return true;
            }
        }

        #endregion

        #region Private Methods - Incoming Call Handling

        /// <summary>
        /// Handle incoming call request
        /// </summary>
        private void HandleIncomingCall(X25PacketEventArgs args)
        {
            var lcn = args.Packet.LCN;
            _logger.LogInformation("TAD Server received incoming call on LCN {LCN}", lcn);

            // For simplicity, accept all calls to our local address
            var session = new TadSession
            {
                SessionId = Guid.NewGuid().ToString("N")[..8],
                LCN = lcn,
                CallingAddress = "unknown", // TODO: Parse from packet data
                CalledAddress = LocalAddress,
                StartTime = DateTime.UtcNow,
                ServiceType = DetermineServiceType(LocalAddress),
                X3Parameters = new X3Parameters()
            };

            // Process initial X.29 parameters if present
            if (args.Packet.Data != null && args.Packet.Data.Length > 0)
            {
                ProcessInitialX29Parameters(session, args.Packet.Data);
            }

            // Store session
            lock (_sessionsLock)
            {
                _sessions[lcn] = session;
            }

            // Accept the call
            SendCallAccepted(lcn, session);
            
            _logger.LogInformation("TAD session {SessionId} started for {CallingAddress} - Service: {ServiceType}",
                                 session.SessionId, session.CallingAddress, session.ServiceType);
            
            OnSessionStarted(session);

            // Send welcome message for loopback test
            if (session.ServiceType == TadServiceType.LoopbackEcho)
            {
                SendWelcomeMessage(session);
            }
        }

        /// <summary>
        /// Check if we should accept the incoming call
        /// </summary>
        private bool ShouldAcceptCall(string calledAddress, string callingAddress)
        {
            // Accept calls to our local address
            if (calledAddress == LocalAddress)
                return true;

            // Add other service addresses as needed
            // For now, only accept loopback test calls
            return false;
        }

        /// <summary>
        /// Determine service type based on called address
        /// </summary>
        private TadServiceType DetermineServiceType(string calledAddress)
        {
            return calledAddress switch
            {
                _ when calledAddress == LocalAddress => TadServiceType.LoopbackEcho,
                _ => TadServiceType.GeneralTerminal
            };
        }

        #endregion

        #region Private Methods - Data Handling

        /// <summary>
        /// Handle incoming data
        /// </summary>
        private void HandleIncomingData(X25PacketEventArgs args)
        {
            lock (_sessionsLock)
            {
                var lcn = args.Packet.LCN;
                if (!_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogWarning("Received data for unknown session LCN {LCN}", lcn);
                    return;
                }

                if (args.Packet.Data == null || args.Packet.Data.Length == 0)
                    return;

                session.BytesReceived += args.Packet.Data.Length;
                session.LastActivity = DateTime.UtcNow;

                if (args.Packet.QBit)
                {
                    // X.29 control message
                    HandleX29ControlMessage(session, args.Packet.Data);
                }
                else
                {
                    // Regular text data
                    var text = TextEncoding.GetString(args.Packet.Data);
                    HandleTextData(session, text);
                }
            }
        }

        /// <summary>
        /// Handle X.29 control messages
        /// </summary>
        private void HandleX29ControlMessage(TadSession session, byte[] data)
        {
            try
            {
                var message = X29Messages.ParseMessage(data);
                if (!message.IsValid)
                {
                    _logger.LogWarning("Invalid X.29 message from session {SessionId}", session.SessionId);
                    return;
                }

                _logger.LogDebug("TAD received X.29 message from {SessionId}: {MessageType}",
                               session.SessionId, message.ToString());

                switch (message.Type)
                {
                    case X29Messages.SET_PARAMETERS:
                        // Update session parameters
                        foreach (var param in message.Parameters)
                        {
                            session.X3Parameters.SetParameter(param.Key, param.Value);
                        }
                        
                        // Send acknowledgment
                        var ack = X29Messages.CreateParameterIndication(message.Parameters);
                        SendX29Message(session, ack);
                        break;

                    case X29Messages.READ_PARAMETERS:
                        // Send current parameter values
                        var responseParams = new Dictionary<byte, byte>();
                        foreach (var paramNum in message.ParameterNumbers)
                        {
                            responseParams[paramNum] = session.X3Parameters.GetParameter(paramNum);
                        }
                        var response = X29Messages.CreateParameterIndication(responseParams);
                        SendX29Message(session, response);
                        break;

                    case X29Messages.SET_AND_READ_PARAMETERS:
                        // Set parameters and return values
                        foreach (var param in message.Parameters)
                        {
                            session.X3Parameters.SetParameter(param.Key, param.Value);
                        }
                        var setAndReadResponse = X29Messages.CreateParameterIndication(message.Parameters);
                        SendX29Message(session, setAndReadResponse);
                        break;

                    case X29Messages.INVITATION_TO_CLEAR:
                        // Remote PAD wants to disconnect
                        _logger.LogInformation("Session {SessionId} received invitation to clear", session.SessionId);
                        TerminateSession(session);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing X.29 message from session {SessionId}", session.SessionId);
            }
        }

        /// <summary>
        /// Handle incoming text data
        /// </summary>
        private void HandleTextData(TadSession session, string text)
        {
            _logger.LogDebug("TAD session {SessionId} received text: {Length} chars", session.SessionId, text.Length);
            
            OnDataReceived(session, text);

            // Process based on service type
            switch (session.ServiceType)
            {
                case TadServiceType.LoopbackEcho:
                    HandleLoopbackEcho(session, text);
                    break;
                    
                case TadServiceType.GeneralTerminal:
                    HandleGeneralTerminalData(session, text);
                    break;
            }
        }

        /// <summary>
        /// Handle loopback echo service
        /// </summary>
        private void HandleLoopbackEcho(TadSession session, string text)
        {
            // Echo back the received text
            var echoText = ProcessTextForEcho(session, text);
            
            if (!string.IsNullOrEmpty(echoText))
            {
                SendTextToSession(session, echoText);
            }

            // Check for special commands
            var trimmedText = text.Trim().ToUpperInvariant();
            switch (trimmedText)
            {
                case "QUIT":
                case "EXIT":
                case "BYE":
                    SendTextToSession(session, "\r\nGoodbye!\r\n");
                TerminateSession(session);
                    break;
                    
                case "HELP":
                case "?":
                    SendHelpMessage(session);
                    break;
                    
                case "STATUS":
                case "STAT":
                    SendStatusMessage(session);
                    break;
            }
        }

        /// <summary>
        /// Handle general terminal data (placeholder for future services)
        /// </summary>
        private void HandleGeneralTerminalData(TadSession session, string text)
        {
            // Placeholder for other terminal services
            _logger.LogDebug("General terminal data from session {SessionId}: {Text}", session.SessionId, text);
        }

        /// <summary>
        /// Process text for echo according to X.3 parameters
        /// </summary>
        private string ProcessTextForEcho(TadSession session, string text)
        {
            if (session.X3Parameters.GetParameter(X3Parameters.ParameterNumber.ECHO) == 0)
                return ""; // Echo disabled

            var result = new StringBuilder();
            
            foreach (char c in text)
            {
                // Apply X.3 parameter processing
                if (c == '\r')
                {
                    result.Append(c);
                    
                    // Check for line feed insertion
                    if (session.X3Parameters.GetParameter(X3Parameters.ParameterNumber.LINE_FEED_INSERTION) > 0)
                    {
                        result.Append('\n');
                    }
                    
                    // Add padding after CR
                    var padding = session.X3Parameters.GetParameter(X3Parameters.ParameterNumber.PADDING_AFTER_CR);
                    if (padding > 0)
                    {
                        result.Append(new string(' ', padding));
                    }
                }
                else if (c == '\n')
                {
                    result.Append(c);
                    
                    // Add padding after LF
                    var padding = session.X3Parameters.GetParameter(X3Parameters.ParameterNumber.PADDING_AFTER_LINE_FEED);
                    if (padding > 0)
                    {
                        result.Append(new string(' ', padding));
                    }
                }
                else
                {
                    // Check echo mask for this character
                    if (ShouldEchoCharacter(c, session.X3Parameters.GetParameter(X3Parameters.ParameterNumber.ECHO_MASK)))
                    {
                        result.Append(c);
                    }
                }
            }

            return result.ToString();
        }

        #endregion

        #region Private Methods - Message Sending

        /// <summary>
        /// Send welcome message for loopback test
        /// </summary>
        private void SendWelcomeMessage(TadSession session)
        {
            
            var welcome = new StringBuilder();
            welcome.AppendLine("\r\n*** X.25 TAD Loopback Echo Service ***");
            welcome.AppendLine("Connected to address 200");
            welcome.AppendLine("Type 'HELP' for commands, 'QUIT' to exit");
            welcome.AppendLine("All text will be echoed back to you");
            welcome.Append("TAD> ");

            SendTextToSession(session, welcome.ToString());
        }

        /// <summary>
        /// Send help message
        /// </summary>
        private void SendHelpMessage(TadSession session)
        {
            var help = new StringBuilder();
            help.AppendLine("\r\nAvailable commands:");
            help.AppendLine("  HELP or ?    - Show this help");
            help.AppendLine("  STATUS       - Show connection status");
            help.AppendLine("  QUIT or EXIT - Disconnect");
            help.AppendLine("  BYE          - Disconnect");
            help.AppendLine("Any other text will be echoed back");
            help.Append("TAD> ");

            SendTextToSession(session, help.ToString());
        }

        /// <summary>
        /// Send status message
        /// </summary>
        private void SendStatusMessage(TadSession session)
        {
            var uptime = DateTime.UtcNow - session.StartTime;
            var status = new StringBuilder();
            status.AppendLine($"\r\nConnection Status:");
            status.AppendLine($"  Session ID:    {session.SessionId}");
            status.AppendLine($"  LCN:           {session.LCN}");
            status.AppendLine($"  Calling Addr:  {session.CallingAddress}");
            status.AppendLine($"  Connected:     {uptime:hh\\:mm\\:ss}");
            status.AppendLine($"  Bytes RX:      {session.BytesReceived:N0}");
            status.AppendLine($"  Bytes TX:      {session.BytesSent:N0}");
            status.AppendLine($"  Service:       {session.ServiceType}");
            status.Append("TAD> ");

            SendTextToSession(session, status.ToString());
        }

        /// <summary>
        /// Send text data to session
        /// </summary>
        private bool SendTextToSession(TadSession session, string text)
        {
            try
            {
                var data = TextEncoding.GetBytes(text);
                session.BytesSent += data.Length;

                // Create X.25 data packet
                var packet = X25PacketBuilder.CreateDataPacket(
                    session.LCN,
                    0, // PS - should be managed by protocol stack
                    0, // PR - should be managed by protocol stack
                    data,
                    false, // M-bit
                    false, // Q-bit (regular data)
                    false  // D-bit
                );

                // TODO: Send via ProtocolRuntime when available
                _logger.LogDebug("TAD sending {Length} bytes to session {SessionId}", data.Length, session.SessionId);
                
                OnDataSent(session, text);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending text to session {SessionId}", session.SessionId);
                return false;
            }
        }

        /// <summary>
        /// Send X.29 control message to session
        /// </summary>
        private bool SendX29Message(TadSession session, byte[] messageData)
        {
            try
            {
                // Create X.25 data packet with Q-bit set
                var packet = X25PacketBuilder.CreateDataPacket(
                    session.LCN,
                    0, // PS
                    0, // PR
                    messageData,
                    false, // M-bit
                    true,  // Q-bit (qualified/control data)
                    false  // D-bit
                );

                // TODO: Send via ProtocolRuntime
                _logger.LogDebug("TAD sending X.29 message to session {SessionId}: {Length} bytes",
                               session.SessionId, messageData.Length);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending X.29 message to session {SessionId}", session.SessionId);
                return false;
            }
        }

        /// <summary>
        /// Send call accepted
        /// </summary>
        private void SendCallAccepted(ushort lcn, TadSession session)
        {
            // Send initial X.29 parameters as call user data
            var initialParams = new Dictionary<byte, byte>
            {
                [X3Parameters.ParameterNumber.ECHO] = session.X3Parameters.GetParameter(X3Parameters.ParameterNumber.ECHO),
                [X3Parameters.ParameterNumber.DATA_FORWARDING] = session.X3Parameters.GetParameter(X3Parameters.ParameterNumber.DATA_FORWARDING)
            };
            
            var callUserData = X29Messages.CreateParameterIndication(initialParams);
            
            var packet = X25PacketBuilder.CreateCallAccepted(lcn, null, callUserData);
            
            // TODO: Send via ProtocolRuntime
            _logger.LogDebug("TAD sending Call Accepted for LCN {LCN}", lcn);
        }

        /// <summary>
        /// Send clear request
        /// </summary>
        private void SendClearRequest(ushort lcn, byte clearingCause)
        {
            var packet = X25PacketBuilder.CreateClearRequest(lcn, clearingCause);
            
            // TODO: Send via ProtocolRuntime
            _logger.LogDebug("TAD sending Clear Request for LCN {LCN} with cause {Cause}", lcn, clearingCause);
        }

        #endregion

        #region Private Methods - Session Management

        /// <summary>
        /// Process initial X.29 parameters from call user data
        /// </summary>
        private void ProcessInitialX29Parameters(TadSession session, byte[] callUserData)
        {
            try
            {
                var message = X29Messages.ParseMessage(callUserData);
                if (message.IsValid && message.Type == X29Messages.SET_PARAMETERS)
                {
                    foreach (var param in message.Parameters)
                    {
                        session.X3Parameters.SetParameter(param.Key, param.Value);
                    }
                    
                    _logger.LogDebug("Processed initial X.29 parameters for session {SessionId}", session.SessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing initial X.29 parameters");
            }
        }

        /// <summary>
        /// Handle clear request
        /// </summary>
        private void HandleClearRequest(X25PacketEventArgs args)
        {
            lock (_sessionsLock)
            {
                var lcn = args.Packet.LCN;
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogInformation("TAD session {SessionId} cleared by remote on LCN {LCN}",
                                         session.SessionId, lcn);
                    
                    session.Dispose();
                    _sessions.Remove(lcn);
                    OnSessionEnded(session);
                }
            }
        }

        /// <summary>
        /// Handle interrupt
        /// </summary>
        private void HandleInterrupt(X25PacketEventArgs args)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(args.Packet.LCN, out var session))
                {
                    _logger.LogDebug("TAD session {SessionId} received interrupt", session.SessionId);
                    
                    // Could process break signal or other urgent notifications
                    // For now, just log it
                }
            }
        }

        /// <summary>
        /// Terminate session
        /// </summary>
        private void TerminateSession(TadSession session)
        {
            SendClearRequest(session.LCN, 0x00); // Normal clear
            
            lock (_sessionsLock)
            {
                _sessions.Remove(session.LCN);
            }
            
            session.Dispose();
            OnSessionEnded(session);
        }

        /// <summary>
        /// Find session by ID
        /// </summary>
        private TadSession? FindSessionById(string sessionId)
        {
            foreach (var session in _sessions.Values)
            {
                if (session.SessionId == sessionId)
                    return session;
            }
            return null;
        }

        /// <summary>
        /// Check if character should be echoed based on echo mask
        /// </summary>
        private bool ShouldEchoCharacter(char c, byte echoMask)
        {
            // Simplified implementation - full X.3 echo mask is more complex
            if (echoMask == 0) return true; // Echo all characters
            
            // For now, echo printable characters
            return c >= 32 && c <= 126;
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Fire session started event
        /// </summary>
        private void OnSessionStarted(TadSession session)
        {
            SessionStarted?.Invoke(this, new TadSessionEventArgs(session.SessionId, TadSessionEvent.Started));
        }

        /// <summary>
        /// Fire session ended event
        /// </summary>
        private void OnSessionEnded(TadSession session)
        {
            SessionEnded?.Invoke(this, new TadSessionEventArgs(session.SessionId, TadSessionEvent.Ended));
        }

        /// <summary>
        /// Fire data received event
        /// </summary>
        private void OnDataReceived(TadSession session, string data)
        {
            DataReceived?.Invoke(this, new TadDataEventArgs(session.SessionId, data, TadDataDirection.Received));
        }

        /// <summary>
        /// Fire data sent event
        /// </summary>
        private void OnDataSent(TadSession session, string data)
        {
            DataSent?.Invoke(this, new TadDataEventArgs(session.SessionId, data, TadDataDirection.Sent));
        }

        #endregion

        #region IProtocolModule Additional Methods

        /// <summary>
        /// Called when X.25 data packet is received
        /// </summary>
        public void OnX25Data(ushort lcn, byte[] data)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    try
                    {
                        var text = TextEncoding.GetString(data);
                        session.BytesReceived += (uint)data.Length;
                        session.LastActivity = DateTime.UtcNow;
                        
                        OnDataReceived(session, text);
                        
                        // Handle loopback echo service
                        if (EnableEchoService)
                        {
                            HandleLoopbackEcho(session, text);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing X.25 data for LCN {LCN}", lcn);
                    }
                }
            }
        }

        /// <summary>
        /// Called when X.25 reset is requested
        /// </summary>
        public void OnX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogInformation("TAD Server received X.25 reset for LCN {LCN}: cause={Cause}, diagnostic={Diagnostic}", lcn, cause, diagnostic);
                    // Reset can be acknowledged or session can be cleared
                    EndSession(session, $"Reset: {cause}/{diagnostic}");
                }
            }
        }

        /// <summary>
        /// Called when X.25 clear is requested
        /// </summary>
        public void OnX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogInformation("TAD Server received X.25 clear for LCN {LCN}: cause={Cause}, diagnostic={Diagnostic}", lcn, cause, diagnostic);
                    EndSession(session, $"Cleared: {cause}/{diagnostic}");
                }
            }
        }

        /// <summary>
        /// Called when X.25 interrupt is received
        /// </summary>
        public void OnX25Interrupt(ushort lcn, byte[] data)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogDebug("TAD Server received X.25 interrupt for LCN {LCN} with {Length} bytes", lcn, data.Length);
                    
                    // Handle X.29 control messages
                    if (data.Length > 0 && data[0] <= 6)
                    {
                        var x29Message = X29Messages.ParseMessage(data);
                        if (x29Message.IsValid)
                        {
                            HandleX29Message(session, x29Message);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Send X.25 data packet
        /// </summary>
        public void SendX25Data(ushort lcn, byte[] data)
        {
            if (_runtime != null)
            {
                _ = _runtime.SendX25Data(lcn, data);
            }
        }

        /// <summary>
        /// Send X.25 reset request
        /// </summary>
        public void SendX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            // TODO: Implement when ProtocolRuntime supports reset sending
            _logger.LogWarning("TAD Server SendX25Reset not yet implemented");
        }

        /// <summary>
        /// Send X.25 clear request
        /// </summary>
        public void SendX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            // TODO: Implement when ProtocolRuntime supports clear sending
            _logger.LogWarning("TAD Server SendX25Clear not yet implemented");
        }

        /// <summary>
        /// Send X.25 interrupt
        /// </summary>
        public void SendX25Interrupt(ushort lcn, byte[] data)
        {
            // TODO: Implement when ProtocolRuntime supports interrupt sending
            _logger.LogWarning("TAD Server SendX25Interrupt not yet implemented");
        }

        /// <summary>
        /// Get module status
        /// </summary>
        public ModuleStatus GetStatus()
        {
            lock (_sessionsLock)
            {
                long totalReceived = 0;
                long totalSent = 0;
                DateTime lastActivity = DateTime.MinValue;
                
                foreach (var session in _sessions.Values)
                {
                    totalReceived += session.BytesReceived;
                    totalSent += session.BytesSent;
                    if (session.LastActivity > lastActivity)
                        lastActivity = session.LastActivity;
                }
                
                return new ModuleStatus
                {
                    IsActive = _sessions.Count > 0,
                    ActiveConnections = _sessions.Count,
                    TotalDataReceived = totalReceived,
                    TotalDataTransmitted = totalSent,
                    LastActivity = lastActivity,
                    ErrorCount = 0, // TODO: Track errors
                    StatusMessage = $"Active sessions: {_sessions.Count}, Local address: {LocalAddress}"
                };
            }
        }

        /// <summary>
        /// Initialize module
        /// </summary>
        public void Initialize()
        {
            _logger.LogInformation("TAD Server module initialized - Local address: {LocalAddress}", LocalAddress);
        }

        /// <summary>
        /// Shutdown module
        /// </summary>
        public void Shutdown()
        {
            lock (_sessionsLock)
            {
                foreach (var session in _sessions.Values.ToArray())
                {
                    EndSession(session, "Server shutdown");
                }
            }
            _logger.LogInformation("TAD Server module shutdown");
        }

        /// <summary>
        /// End a TAD session
        /// </summary>
        private void EndSession(TadSession session, string reason)
        {
            lock (_sessionsLock)
            {
                if (_sessions.ContainsKey(session.LCN))
                {
                    _sessions.Remove(session.LCN);
                    _logger.LogInformation("TAD session {SessionId} ended: {Reason}", session.SessionId, reason);
                    OnSessionEnded(session);
                    session.Dispose();
                }
            }
        }

        /// <summary>
        /// Handle X.29 control message
        /// </summary>
        private void HandleX29Message(TadSession session, X29Message message)
        {
            switch (message.Type)
            {
                case X29Messages.SET_PARAMETERS:
                    foreach (var param in message.Parameters)
                    {
                        session.X3Parameters.SetParameter(param.Key, param.Value);
                    }
                    _logger.LogDebug("TAD session {SessionId} set X.3 parameters", session.SessionId);
                    break;

                case X29Messages.READ_PARAMETERS:
                    var response = new Dictionary<byte, byte>();
                    foreach (var paramNum in message.ParameterNumbers)
                    {
                        response[paramNum] = session.X3Parameters.GetParameter(paramNum);
                    }
                    var responseData = X29Messages.CreateParameterIndication(response);
                    SendX25Interrupt(session.LCN, responseData);
                    break;

                case X29Messages.INVITATION_TO_CLEAR:
                    _logger.LogInformation("TAD session {SessionId} received invitation to clear", session.SessionId);
                    EndSession(session, "Invitation to clear");
                    break;

                default:
                    _logger.LogDebug("TAD session {SessionId} received X.29 message type {Type}", session.SessionId, message.Type);
                    break;
            }
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Disposes all sessions and clears the session table.
        /// </summary>
        public void Dispose()
        {
            lock (_sessionsLock)
            {
                foreach (var session in _sessions.Values)
                {
                    session.Dispose();
                }
                _sessions.Clear();
            }
        }

        #endregion
    }

    #region Supporting Classes and Enums

    /// <summary>
    /// TAD session information
    /// </summary>
    internal class TadSession : IDisposable
    {
        public string SessionId { get; set; } = "";
        public ushort LCN { get; set; }
        public string CallingAddress { get; set; } = "";
        public string CalledAddress { get; set; } = "";
        public DateTime StartTime { get; set; }
        public TadServiceType ServiceType { get; set; }
        public X3Parameters X3Parameters { get; set; } = new X3Parameters();
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;

        public void Dispose()
        {
            // Cleanup any session resources
        }
    }

    /// <summary>
    /// TAD service types
    /// </summary>
    public enum TadServiceType
    {
        /// <summary>
        /// Loopback echo service.
        /// </summary>
        LoopbackEcho,
        /// <summary>
        /// General terminal service.
        /// </summary>
        GeneralTerminal,
        /// <summary>
        /// File transfer service.
        /// </summary>
        FileTransfer,
        /// <summary>
        /// Remote login service.
        /// </summary>
        RemoteLogin
    }

    /// <summary>
    /// TAD session information (public)
    /// </summary>
    public class TadSessionInfo
    {
        /// <summary>
        /// Gets or sets the session identifier.
        /// </summary>
        public string SessionId { get; set; } = "";
        /// <summary>
        /// Gets or sets the logical channel number of the session.
        /// </summary>
        public ushort LCN { get; set; }
        /// <summary>
        /// Gets or sets the address of the caller.
        /// </summary>
        public string CallingAddress { get; set; } = "";
        /// <summary>
        /// Gets or sets the time the session started.
        /// </summary>
        public DateTime StartTime { get; set; }
        /// <summary>
        /// Gets or sets the number of bytes received on the session.
        /// </summary>
        public long BytesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of bytes sent on the session.
        /// </summary>
        public long BytesSent { get; set; }
        /// <summary>
        /// Gets or sets the service type of the session.
        /// </summary>
        public TadServiceType ServiceType { get; set; }
    }

    /// <summary>
    /// TAD session events
    /// </summary>
    public enum TadSessionEvent
    {
        /// <summary>
        /// The session started.
        /// </summary>
        Started,
        /// <summary>
        /// The session ended.
        /// </summary>
        Ended
    }

    /// <summary>
    /// TAD data direction
    /// </summary>
    public enum TadDataDirection
    {
        /// <summary>
        /// Text sent by the server.
        /// </summary>
        Sent,
        /// <summary>
        /// Text received by the server.
        /// </summary>
        Received
    }

    /// <summary>
    /// TAD session event arguments
    /// </summary>
    public class TadSessionEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the session identifier.
        /// </summary>
        public string SessionId { get; }
        /// <summary>
        /// Gets the session event.
        /// </summary>
        public TadSessionEvent Event { get; }

        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="sessionId">
        /// The session identifier.
        /// </param>
        /// <param name="sessionEvent">
        /// The session event.
        /// </param>
        public TadSessionEventArgs(string sessionId, TadSessionEvent sessionEvent)
        {
            SessionId = sessionId;
            Event = sessionEvent;
        }
    }

    /// <summary>
    /// TAD data event arguments
    /// </summary>
    public class TadDataEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the session identifier.
        /// </summary>
        public string SessionId { get; }
        /// <summary>
        /// Gets the text that was sent or received.
        /// </summary>
        public string Data { get; }
        /// <summary>
        /// Gets whether the text was sent or received.
        /// </summary>
        public TadDataDirection Direction { get; }

        /// <summary>
        /// Creates the event arguments.
        /// </summary>
        /// <param name="sessionId">
        /// The session identifier.
        /// </param>
        /// <param name="data">
        /// The text that was sent or received.
        /// </param>
        /// <param name="direction">
        /// Whether the text was sent or received.
        /// </param>
        public TadDataEventArgs(string sessionId, string data, TadDataDirection direction)
        {
            SessionId = sessionId;
            Data = data;
            Direction = direction;
        }
    }

    #endregion
}