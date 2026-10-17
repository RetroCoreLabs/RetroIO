//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Modules
{
    /// <summary>
    /// PAD (Packet Assembler/Disassembler) Client according to ITU-T X.3, X.28, X.29
    /// Provides telnet-style terminal access over X.25 networks
    /// Connects to remote TAD servers for interactive sessions
    /// </summary>
    public class PadClient : IProtocolModule, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IPadTransport _transport;
        private readonly X3 _x3Protocol;
        private readonly X28 _x28Protocol;
        private readonly X29 _x29Protocol;
        private readonly X121 _x121Protocol;
        
        // Connection state
        private ushort _currentLCN = 0;
        private PadState _state = PadState.Disconnected;
        private string _remoteAddress = "";
        private readonly StringBuilder _inputBuffer = new StringBuilder();
        private readonly StringBuilder _outputBuffer = new StringBuilder();
        private System.Threading.Timer? _idleTimer;
        
        // Configuration
        public string LocalAddress { get; set; } = "";
        public int IdleTimeoutMs { get; set; } = 30000;
        public bool AutoConnect { get; set; } = true;
        public Encoding TextEncoding { get; set; } = Encoding.ASCII;
        
        // IProtocolModule Properties
        public string Name { get; set; } = "PAD Client";
        public string Description { get; } = "X.3/X.28/X.29 PAD Client for terminal access over X.25";
        public string Version { get; } = "1.0.0";

        // Protocol Access Properties (for GUI display)
        public X28 X28Protocol => _x28Protocol;
        public X3 X3Protocol => _x3Protocol;

        // Events
        public event EventHandler<PadConnectionEventArgs>? ConnectionStateChanged;
        public event EventHandler<PadDataEventArgs>? DataReceived;
        public event EventHandler<PadDataEventArgs>? DataSent;
        public event EventHandler<X29MessageEventArgs>? X29MessageReceived;

        /// <summary>
        /// Creates a PAD client over the concrete protocol runtime (used by the GUI and the runtime's
        /// managed-PAD factory). The runtime binds this instance as an <see cref="IProtocolModule"/> and
        /// delivers received packets through <see cref="OnX25Event"/>.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <param name="runtime">
        /// The protocol runtime that carries the calls.
        /// </param>
        public PadClient(ILogger logger, IProtocolRuntime runtime)
            : this(logger, new RuntimePadTransport(runtime))
        {
            // The runtime adapter needs a back-reference to this module so outgoing calls bind to it.
            ((RuntimePadTransport)_transport).Module = this;
        }

        /// <summary>
        /// Creates a PAD client over an arbitrary <see cref="IPadTransport"/> (used by the console PAD on
        /// the clean X.25 layer). Received events are delivered through the public delivery methods rather
        /// than through <see cref="IProtocolModule"/>.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <param name="transport">
        /// The transport that carries the calls.
        /// </param>
        public PadClient(ILogger logger, IPadTransport transport)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));

            _x3Protocol = new X3(_logger);
            _x28Protocol = new X28(_x3Protocol, _logger);
            _x29Protocol = new X29(_logger);
            _x121Protocol = new X121(_logger);
            
            // Subscribe to X.28 events for proper delegation
            _x28Protocol.DataForwarded += OnX28DataForwarded;
            _x28Protocol.CommandReceived += OnX28CommandReceived;
            _x28Protocol.ModeChanged += OnX28ModeChanged;
            
            // Subscribe to NEW event-based architecture events
            _x28Protocol.CharacterEcho += OnX28CharacterEcho;
            _x28Protocol.CommandResponse += OnX28CommandResponse;
            _x28Protocol.ErrorMessage += OnX28ErrorMessage;
            
            // Subscribe to X.3 events for parameter updates
            _x3Protocol.ParametersChanged += OnX3ParametersChanged;
            
            _logger.LogInformation("PAD Client initialized with protocol handlers");
        }

        #region IProtocolModule Implementation

        public void OnBound(ushort lcn)
        {
            _currentLCN = lcn;
            _logger.LogDebug("PAD Client bound to LCN {LCN}", lcn);
        }

        public void OnUnbound(ushort lcn)
        {
            if (lcn == _currentLCN)
            {
                _logger.LogInformation("PAD Client unbound from LCN {LCN}", lcn);
                _currentLCN = 0; // Reset current LCN
                HandleDisconnection();
            }
        }

        public void OnX25Event(X25PacketEventArgs args)
        {
            _logger.LogDebug("PAD Client received X25 event: PacketType={PacketType}, LCN={LCN}, Current LCN={CurrentLCN}", 
                           args.Packet.PacketType, args.Packet.LCN, _currentLCN);
            
            if (args.Packet.LCN != _currentLCN) 
            {
                _logger.LogDebug("PAD Client ignoring event - LCN mismatch");
                return;
            }

            try
            {
                switch (args.Packet.PacketType)
                {
                    case X25PacketType.CallAccepted:
                        _logger.LogDebug("PAD Client handling Call Accepted");
                        NotifyCallAccepted(args.Packet.LCN);
                        break;
                    case X25PacketType.Data:
                        DeliverData(args.Packet.LCN, args.Packet.Data, args.Packet.QBit);
                        break;
                    case X25PacketType.ClearRequest:
                        DeliverClear(args.Packet.LCN);
                        break;
                    case X25PacketType.Interrupt:
                        DeliverInterrupt(args.Packet.LCN);
                        break;
                    default:
                        _logger.LogDebug("PAD Client received unhandled packet type: {PacketType}", args.Packet.PacketType);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling X.25 event in PAD Client");
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Connect to remote TAD server
        /// </summary>
        public bool Connect(string remoteAddress, string callingAddress = "")
        {
            if (_state != PadState.Disconnected)
            {
                _logger.LogWarning("PAD Client already connected or connecting");
                return false;
            }

            _remoteAddress = remoteAddress;
            _state = PadState.Connecting;
            
            var callUserData = CreateInitialX29Message();
            
            _logger.LogInformation("PAD Client connecting to {RemoteAddress}", remoteAddress);
            
            var success = _transport.PlaceCall(
                remoteAddress,
                callingAddress.Length > 0 ? callingAddress : LocalAddress,
                callUserData
            );

            if (!success)
            {
                _state = PadState.Disconnected;
                _logger.LogError("Failed to initiate X.25 call to {RemoteAddress}", remoteAddress);
            }

            return success;
        }

        /// <summary>
        /// Disconnect from remote server
        /// </summary>
        public void Disconnect()
        {
            if (_currentLCN == 0 || _state == PadState.Disconnected)
                return;

            _logger.LogInformation("PAD Client disconnecting from {RemoteAddress}", _remoteAddress);
            
            // Send invitation to clear (X.29 protocol)
            var inviteMessage = _x29Protocol.CreateInvitationToClear();
            SendX29Message(inviteMessage);
            
            // Also send proper X.25 Clear Request to properly clean up the call
            _logger.LogDebug("PAD sending X.25 Clear Request on LCN {LCN}", _currentLCN);
            _transport.ClearCall(_currentLCN, 0, 0);
            
            _state = PadState.Disconnected;
            var clearedLCN = _currentLCN;
            _currentLCN = 0;
            
            // Switch X.28 protocol back to command mode
            _x28Protocol.SwitchToCommandMode();
            
            OnConnectionStateChanged(PadConnectionState.Disconnected, "User initiated");
        }

        /// <summary>
        /// Send text data to remote terminal
        /// </summary>
        public bool SendText(string text)
        {
            if (_state != PadState.DataTransfer)
            {
                _logger.LogWarning("Cannot send text: PAD not in data transfer state");
                return false;
            }

            try
            {
                var processedText = ProcessOutgoingText(text);
                var data = TextEncoding.GetBytes(processedText);
                
                // Send as X.25 data packet through the transport
                var success = _transport.SendData(
                    _currentLCN,
                    data,
                    false, // M-bit
                    false, // Q-bit (regular data)
                    false  // D-bit
                );

                if (!success)
                {
                    _logger.LogError("Failed to send X.25 data packet");
                    return false;
                }

                _logger.LogDebug("PAD Client sent {Length} bytes of text data", data.Length);
                
                OnDataSent(text);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending text data");
                return false;
            }
        }

        /// <summary>
        /// Send command to remote PAD/TAD
        /// </summary>
        public bool SendCommand(string command)
        {
            if (_state != PadState.DataTransfer)
                return false;

            // Process X.28 commands (PAD user commands)
            var result = ProcessPadCommand(command);
            // Command processing and responses now handled via X28 events
            return result == CommandResult.Success;
        }

        /// <summary>
        /// Process single character input by delegating to X.28 terminal interface
        /// X.28 handles both command mode (when disconnected) and data transfer mode (when connected)
        /// </summary>
        public bool ProcessCharacter(byte character)
        {
            try
            {
                char ch = (char)character;
                
                // Always delegate character processing to X.28 protocol
                // X.28 will handle command mode when disconnected and data transfer mode when connected
                bool processed = _x28Protocol.ProcessCharacter(ch);
                
                // Character processing now handled via events from X.28
                if (processed)
                {
                    // All character echoing and responses now handled via X28 events
                    // No need to manually handle echo here anymore

                    if (ch == '\n')
                    {
                        if ((_x28Protocol.CurrentMode == X28.PadMode.Command) || (_x28Protocol.CurrentMode == X28.PadMode.Escape))
                        {
                            SendPrompt();
                        }
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing character {Character}", character);
                return false;
            }
        }

        /// <summary>
        /// Set X.3 parameter
        /// </summary>
        public bool SetParameter(byte parameterNumber, byte value)
        {
            if (!_x3Protocol.SetParameter(parameterNumber, value))
                return false;

            if (_state == PadState.DataTransfer)
            {
                // Send X.29 SET PARAMETERS message
                var parameters = new Dictionary<byte, byte> { [parameterNumber] = value };
                var message = _x29Protocol.CreateSetParameters(parameters);
                SendX29Message(message);
            }

            _logger.LogDebug("PAD parameter {ParamNum} set to {Value}", parameterNumber, value);
            
            // Notify that parameters have changed
            OnParametersChanged();
            
            return true;
        }

        /// <summary>
        /// Get X.3 parameter value
        /// </summary>
        public byte GetParameter(byte parameterNumber)
        {
            return _x3Protocol.GetParameter(parameterNumber);
        }

        /// <summary>
        /// Get current connection state
        /// </summary>
        public PadConnectionState ConnectionState => MapPadState(_state);

        /// <summary>
        /// Get current X.3 parameters
        /// </summary>
        public Dictionary<byte, byte> Parameters => _x3Protocol.GetAllParameters();

        #endregion

        #region Private Methods

        /// <summary>
        /// Notifies the PAD that its outgoing call was accepted, moving the session to data transfer. This
        /// is the single connected handler for both the runtime path (via <see cref="OnX25Event"/>) and a
        /// transport driving the clean X.25 layer.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel the call was accepted on.
        /// </param>
        public void NotifyCallAccepted(ushort lcn)
        {
            _currentLCN = lcn;
            _state = PadState.DataTransfer;

            // Switch X.28 protocol to data transfer mode.
            _x28Protocol.SwitchToDataTransferMode();

            _logger.LogInformation("PAD connection established to {RemoteAddress} on LCN {LCN}",
                                 _remoteAddress, _currentLCN);

            StartIdleTimer();
            OnConnectionStateChanged(PadConnectionState.Connected, "Call accepted");
        }

        /// <summary>
        /// Delivers received data to the PAD (called from the runtime's <see cref="OnX25Event"/> or from a
        /// transport driving the clean X.25 layer). Qualified data is treated as an X.29 control message.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel the data arrived on.
        /// </param>
        /// <param name="data">
        /// Received payload bytes.
        /// </param>
        /// <param name="qBit">
        /// Qualifier bit: true for an X.29 control message, false for terminal data.
        /// </param>
        public void DeliverData(ushort lcn, byte[] data, bool qBit)
        {
            if (data == null || data.Length == 0)
                return;

            RestartIdleTimer();

            if (qBit)
            {
                // X.29 control message
                HandleX29Message(data);
            }
            else
            {
                // Regular text data
                var text = TextEncoding.GetString(data);
                var processedText = ProcessIncomingText(text);

                _logger.LogDebug("PAD received {Length} bytes of text data", data.Length);
                OnDataReceived(processedText);
            }
        }

        /// <summary>
        /// Delivers a remote clear to the PAD, disconnecting the session.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel that was cleared.
        /// </param>
        public void DeliverClear(ushort lcn)
        {
            _logger.LogInformation("PAD connection cleared by remote on LCN {LCN}", lcn);
            HandleDisconnection();

            var reason = "Remote disconnect";
            OnConnectionStateChanged(PadConnectionState.Disconnected, reason);
        }

        /// <summary>
        /// Delivers an interrupt (break) indication to the PAD.
        /// </summary>
        /// <param name="lcn">
        /// Logical channel the interrupt arrived on.
        /// </param>
        public void DeliverInterrupt(ushort lcn)
        {
            _logger.LogDebug("PAD received interrupt signal");
            // Could be break signal or other urgent notification
            // TODO: Process based on X.3 parameter 7 (break handling)
        }

        /// <summary>
        /// Handle X.29 control messages
        /// </summary>
        private void HandleX29Message(byte[] data)
        {
            try
            {
                var message = _x29Protocol.ParseMessage(data);
                if (message == null)
                {
                    _logger.LogWarning("Invalid X.29 message received");
                    return;
                }

                _logger.LogDebug("PAD received X.29 message: {MessageType}", message.ToString());

                switch (message.Type)
                {
                    case (byte)X29MessageType.ParameterIndication:
                        // Remote PAD is sending parameter values
                        foreach (var param in message.Parameters)
                        {
                            _x3Protocol.SetParameter(param.Key, param.Value);
                        }
                        break;

                    case (byte)X29MessageType.InvitationToClear:
                        // Remote wants to disconnect
                        _logger.LogInformation("Received invitation to clear from remote PAD");
                        System.Threading.ThreadPool.QueueUserWorkItem((_) => Disconnect());
                        break;

                    case (byte)X29MessageType.IndicationOfBreak:
                        // Break signal received
                        _logger.LogDebug("Break indication received from remote PAD");
                        break;

                    case (byte)X29MessageType.Error:
                        _logger.LogWarning("X.29 error received");
                        break;
                }

                OnX29MessageReceived(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing X.29 message");
            }
        }

        /// <summary>
        /// Process outgoing text according to X.3 parameters
        /// </summary>
        private string ProcessOutgoingText(string text)
        {
            return _x3Protocol.ProcessOutgoingText(text);
        }

        /// <summary>
        /// Process incoming text according to X.3 parameters
        /// </summary>
        private string ProcessIncomingText(string text)
        {
            return _x3Protocol.ProcessIncomingText(text);
        }

        /// <summary>
        /// Process X.28 PAD commands
        /// </summary>
        private CommandResult ProcessPadCommand(string command)
        {
            return _x28Protocol.ProcessCommand(command);
        }

        /// <summary>
        /// Create PAD Call User Data according to X.25 specification
        /// PAD protocol identification: CUD = 0x01000000 (4 bytes)
        /// </summary>
        private byte[] CreateInitialX29Message()
        {
            return _x29Protocol.CreatePadCallUserData();
        }

        /// <summary>
        /// Send X.29 control message
        /// </summary>
        private void SendX29Message(byte[] messageData)
        {
            if (_currentLCN == 0 || _state != PadState.DataTransfer)
                return;

            // Send as X.25 data packet with Q-bit set
            var packet = X25PacketBuilder.CreateDataPacket(
                _currentLCN,
                0, // PS
                0, // PR  
                messageData,
                false, // M-bit
                true,  // Q-bit (qualified/control data)
                false  // D-bit
            );

            // TODO: Send via ProtocolRuntime
            _logger.LogDebug("PAD sending X.29 message: {Length} bytes", messageData.Length);
        }

        /// <summary>
        /// Handle disconnection cleanup
        /// </summary>
        private void HandleDisconnection()
        {
            StopIdleTimer();
            _state = PadState.Disconnected;
            _currentLCN = 0;
            _inputBuffer.Clear();
            _outputBuffer.Clear();
            
            // Switch X.28 protocol back to command mode
            _x28Protocol.SwitchToCommandMode();
        }

        /// <summary>
        /// Start idle timer
        /// </summary>
        private void StartIdleTimer()
        {
            if (_x3Protocol.GetParameter(X3.ParameterNumber.IDLE_TIMER) == 0)
                return;

            var timeoutMs = _x3Protocol.GetParameter(X3.ParameterNumber.IDLE_TIMER) * 20; // 20ms units
            _idleTimer = new System.Threading.Timer(OnIdleTimeout, null, timeoutMs, Timeout.Infinite);
        }

        /// <summary>
        /// Restart idle timer
        /// </summary>
        private void RestartIdleTimer()
        {
            if (_idleTimer == null) return;
            
            var timeoutMs = _x3Protocol.GetParameter(X3.ParameterNumber.IDLE_TIMER) * 20;
            _idleTimer.Change(timeoutMs, Timeout.Infinite);
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
        /// Handle idle timeout
        /// </summary>
        private void OnIdleTimeout(object? state)
        {
            _logger.LogInformation("PAD idle timeout - disconnecting");
            System.Threading.ThreadPool.QueueUserWorkItem((_) => Disconnect());
        }


        /// <summary>
        /// Map internal state to public connection state
        /// </summary>
        private PadConnectionState MapPadState(PadState state)
        {
            return state switch
            {
                PadState.Disconnected => PadConnectionState.Disconnected,
                PadState.Connecting => PadConnectionState.Connecting,
                PadState.DataTransfer => PadConnectionState.Connected,
                _ => PadConnectionState.Disconnected
            };
        }

        /// <summary>
        /// Get description for clearing cause
        /// </summary>
        private string GetClearingCauseDescription(byte cause)
        {
            return cause switch
            {
                X25ClearingCause.DTE_ORIGINATED => "Remote disconnect",
                X25ClearingCause.NUMBER_BUSY => "Number busy",
                X25ClearingCause.NETWORK_CONGESTION => "Network congestion", 
                X25ClearingCause.ACCESS_BARRED => "Access barred",
                X25ClearingCause.LOCAL_PROCEDURE_ERROR => "Protocol error",
                _ => $"Unknown cause ({cause})"
            };
        }

        /// <summary>
        /// Fire connection state changed event
        /// </summary>
        private void OnConnectionStateChanged(PadConnectionState state, string reason)
        {
            ConnectionStateChanged?.Invoke(this, new PadConnectionEventArgs(state, reason));
        }

        /// <summary>
        /// Fire data received event
        /// </summary>
        private void OnDataReceived(string data)
        {
            DataReceived?.Invoke(this, new PadDataEventArgs(data, PadDataDirection.Received));
        }

        /// <summary>
        /// Fire data sent event
        /// </summary>
        private void OnDataSent(string data)
        {
            DataSent?.Invoke(this, new PadDataEventArgs(data, PadDataDirection.Sent));
        }

        /// <summary>
        /// Fire X.29 message received event
        /// </summary>
        private void OnX29MessageReceived(RetroIO.Protocols.X25.Protocols.X29Message message)
        {
            X29MessageReceived?.Invoke(this, new X29MessageEventArgs(message));
        }

        #endregion

        #region X.28 Event Handlers

        /// <summary>
        /// Event for character echo (from X.28 terminal interface)  
        /// </summary>
        public event EventHandler<PadCharacterEventArgs>? CharacterEcho;

        /// <summary>
        /// Event for X.3 parameter changes
        /// </summary>
        public event EventHandler? ParametersChanged;

        /// <summary>
        /// Fire character echo event
        /// </summary>
        private void OnCharacterEcho(byte character)
        {
            CharacterEcho?.Invoke(this, new PadCharacterEventArgs(character));
        }

        /// <summary>
        /// Fire parameters changed event
        /// </summary>
        private void OnParametersChanged()
        {
            ParametersChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Handle data forwarded from X.28 terminal interface
        /// </summary>
        private void OnX28DataForwarded(object? sender, X28DataEventArgs e)
        {
            try
            {
                _logger.LogInformation("PAD: *** DATA FORWARDED *** State: {State}, LCN: {LCN}, Data: '{Data}', Condition: {Condition}",
                    _state, _currentLCN, e.Data, e.Condition);
                    
                if (_state == PadState.DataTransfer && _currentLCN > 0)
                {
                    // Convert forwarded data to bytes and send via the transport.
                    byte[] data = TextEncoding.GetBytes(e.Data);

                    _logger.LogInformation("PAD: Forwarding {Length} bytes to LCN {LCN}: '{Data}'",
                        data.Length, _currentLCN, e.Data);

                    // The transport routes XOT-mapped channels through XOT and others through the packet layer.
                    _transport.ForwardData(_currentLCN, data, data.Length);

                    _logger.LogDebug("PAD forwarded {Length} bytes from X.28: '{Data}' (Condition: {Condition})",
                                   data.Length, e.Data, e.Condition);
                    OnDataSent(e.Data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling X.28 data forwarded event");
            }
        }

        /// <summary>
        /// Handle commands from X.28 terminal interface
        /// </summary>
        private void OnX28CommandReceived(object? sender, X28CommandEventArgs e)
        {
            try
            {
                _logger.LogDebug("PAD received X.28 command: {Command}", e.Command.Verb);
                
                // Process PAD commands (CALL, CLR, SET, PAR, etc.)
                ProcessX28Command(e.Command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling X.28 command event");
            }
        }

        /// <summary>
        /// Handle mode changes from X.28 terminal interface
        /// </summary>
        private void OnX28ModeChanged(object? sender, X28ModeChangedEventArgs e)
        {
            _logger.LogDebug("PAD X.28 mode changed: {OldMode} → {NewMode}", e.OldMode, e.NewMode);
            
            // Update PAD state based on X.28 mode
            if (e.NewMode == X28.PadMode.DataTransfer)
            {
                // Ensure we're in data transfer state for X.25 communication
                if (_state != PadState.DataTransfer && _currentLCN > 0)
                {
                    _state = PadState.DataTransfer;
                }
            }
        }

        /// <summary>
        /// Handle X.3 parameters changed event
        /// </summary>
        private void OnX3ParametersChanged(object? sender, EventArgs e)
        {
            _logger.LogDebug("PAD X.3 parameters changed, notifying listeners");
            OnParametersChanged();
        }

        /// <summary>
        /// Handle character echo from X.28 protocol - forwards to UI for display
        /// </summary>
        private void OnX28CharacterEcho(object? sender, CharacterEchoEventArgs e)
        {
            try
            {
                _logger.LogDebug("PAD received X.28 character echo: '{Character}' type: {Type}", e.Character, e.Type);
                
                // Forward character echo to UI
                byte characterByte = (byte)e.Character;
                OnCharacterEcho(characterByte);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling X.28 character echo event");
            }
        }

        /// <summary>
        /// Handle command response from X.28 protocol - forwards to UI as data
        /// </summary>
        private void OnX28CommandResponse(object? sender, CommandResponseEventArgs e)
        {
            try
            {
                _logger.LogDebug("PAD received X.28 command response: '{Response}' type: {Type} immediate: {IsImmediate}", 
                    e.Response, e.Type, e.IsImmediate);
                
                // Forward command response to UI as received data
                OnDataReceived(e.Response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling X.28 command response event");
            }
        }

        /// <summary>
        /// Handle error messages from X.28 protocol - forwards to UI as data
        /// </summary>
        private void OnX28ErrorMessage(object? sender, ErrorMessageEventArgs e)
        {
            try
            {
                _logger.LogError("PAD received X.28 error: {ErrorMessage}", e.ErrorMessage);
                
                // Forward error message to UI as received data
                OnDataReceived($"ERROR: {e.ErrorMessage}\r\n");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling X.28 error message event");
            }
        }

        /// <summary>
        /// Process X.28 PAD commands (CALL, CLR, SET, PAR, etc.)
        /// </summary>
        private void ProcessX28Command(X28Command command)
        {
            try
            {
                switch (command.Verb.ToUpper())
                {
                    case "C":
                    case "CALL":
                        if (command.Arguments.Count > 0)
                        {
                            string address = command.Arguments[0];
                            Connect(address, LocalAddress);
                        }
                        break;
                        
                    case "CLR":
                    case "CLEAR":
                        Disconnect();
                        break;
                        
                    case "SET":
                        ProcessSetParameterCommand(command);
                        break;
                        
                    case "PAR":
                        ProcessParameterCommand(command);
                        break;
                        
                    default:
                        _logger.LogWarning("Unknown X.28 command: {Command}", command.Verb);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing X.28 command {Command}", command.Verb);
            }
        }

        /// <summary>
        /// Process SET parameter command (e.g., "SET 2:0,12:5")
        /// </summary>
        private void ProcessSetParameterCommand(X28Command command)
        {
            if (command.Arguments.Count == 0)
                return;
                
            foreach (var param in command.Arguments)
            {
                var parts = param.Split(':');
                if (parts.Length == 2 && 
                    byte.TryParse(parts[0], out byte paramNum) && 
                    byte.TryParse(parts[1], out byte value))
                {
                    SetParameter(paramNum, value);
                }
            }
        }

        /// <summary>
        /// Process PAR command to display current parameters
        /// </summary>
        private void ProcessParameterCommand(X28Command command)
        {
            // This would typically display parameters to terminal
            // For now, just log the current parameters
            var parameters = _x3Protocol.GetAllParameters();
            foreach (var kvp in parameters)
            {
                _logger.LogInformation("Parameter {Number}: {Value}", kvp.Key, kvp.Value);
            }
        }

        #endregion

        #region IProtocolModule Implementation

        /// <summary>
        /// Called when X.25 data packet is received (legacy interface - now unused)
        /// </summary>
        public void OnX25Data(ushort lcn, byte[] data)
        {
            // Legacy interface - no longer used, data processing now handled via OnX25Event
            // This method is kept only for interface compatibility
        }

        /// <summary>
        /// Called when X.25 reset is requested
        /// </summary>
        public void OnX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            if (_currentLCN == lcn)
            {
                _logger.LogInformation("PAD Client received X.25 reset: cause={Cause}, diagnostic={Diagnostic}", cause, diagnostic);
                OnConnectionStateChanged(PadConnectionState.Disconnected, $"Reset: {cause}/{diagnostic}");
                _state = PadState.Disconnected;
            }
        }

        /// <summary>
        /// Called when X.25 clear is requested
        /// </summary>
        public void OnX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            if (_currentLCN == lcn)
            {
                _logger.LogInformation("PAD Client received X.25 clear: cause={Cause}, diagnostic={Diagnostic}", cause, diagnostic);
                OnConnectionStateChanged(PadConnectionState.Disconnected, $"Cleared: {cause}/{diagnostic}");
                _state = PadState.Disconnected;
                _currentLCN = 0;
            }
        }

        /// <summary>
        /// Called when X.25 interrupt is received
        /// </summary>
        public void OnX25Interrupt(ushort lcn, byte[] data)
        {
            if (_currentLCN == lcn)
            {
                _logger.LogDebug("PAD Client received X.25 interrupt with {Length} bytes", data.Length);
                // Handle interrupt data - could be X.29 control messages
                if (data.Length > 0 && data[0] <= 6)
                {
                    var x29Message = _x29Protocol.ParseMessage(data);
                    if (x29Message != null)
                    {
                        OnX29MessageReceived(x29Message);
                    }
                }
            }
        }

        /// <summary>
        /// Send X.25 data packet
        /// </summary>
        public void SendX25Data(ushort lcn, byte[] data)
        {
            if (_currentLCN == lcn)
            {
                _ = _transport.SendData(lcn, data, false, false, false);
            }
        }

        /// <summary>
        /// Send X.25 reset request
        /// </summary>
        public void SendX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            // TODO: Implement when ProtocolRuntime supports reset sending
            _logger.LogWarning("PAD Client SendX25Reset not yet implemented");
        }

        /// <summary>
        /// Send X.25 clear request
        /// </summary>
        public void SendX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            // TODO: Implement when ProtocolRuntime supports clear sending
            _logger.LogWarning("PAD Client SendX25Clear not yet implemented");
        }

        /// <summary>
        /// Send X.25 interrupt
        /// </summary>
        public void SendX25Interrupt(ushort lcn, byte[] data)
        {
            // TODO: Implement when ProtocolRuntime supports interrupt sending
            _logger.LogWarning("PAD Client SendX25Interrupt not yet implemented");
        }

        /// <summary>
        /// Get module status
        /// </summary>
        public ModuleStatus GetStatus()
        {
            return new ModuleStatus
            {
                IsActive = _state != PadState.Disconnected,
                ActiveConnections = _currentLCN > 0 ? 1 : 0,
                TotalDataReceived = 0, // TODO: Track statistics
                TotalDataTransmitted = 0, // TODO: Track statistics
                LastActivity = DateTime.UtcNow,
                ErrorCount = 0, // TODO: Track errors
                StatusMessage = $"State: {_state}, LCN: {_currentLCN}, Remote: {_remoteAddress}"
            };
        }

        /// <summary>
        /// Initialize module
        /// </summary>
        public void Initialize()
        {
            _logger.LogInformation("PAD Client module initialized");
        }

        /// <summary>
        /// Shutdown module
        /// </summary>
        public void Shutdown()
        {
            Disconnect();
            _logger.LogInformation("PAD Client module shutdown");
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            StopIdleTimer();
            
            // Unsubscribe from X.28 events
            if (_x28Protocol != null)
            {
                _x28Protocol.DataForwarded -= OnX28DataForwarded;
                _x28Protocol.CommandReceived -= OnX28CommandReceived;
                _x28Protocol.ModeChanged -= OnX28ModeChanged;
                
                // Unsubscribe from NEW event-based architecture events
                _x28Protocol.CharacterEcho -= OnX28CharacterEcho;
                _x28Protocol.CommandResponse -= OnX28CommandResponse;
                _x28Protocol.ErrorMessage -= OnX28ErrorMessage;
            }
            
            // Unsubscribe from X.3 events
            if (_x3Protocol != null)
            {
                _x3Protocol.ParametersChanged -= OnX3ParametersChanged;
            }
            
            _inputBuffer.Clear();
            _outputBuffer.Clear();
        }

        public void OnKey(char keyChar)
        {
            // Delegate to the existing ProcessCharacter method with direct char conversion
            ProcessCharacter((byte)keyChar);
        }

        public void Start()
        {
            SendPrompt();
            
        }

        void SendPrompt()
        {
            OnDataReceived("PAD> ");
        }



        #endregion
    }

    #region Enums and Event Args

    /// <summary>
    /// PAD internal state
    /// </summary>
    internal enum PadState
    {
        Disconnected,
        Connecting,
        DataTransfer
    }

    /// <summary>
    /// PAD connection state
    /// </summary>
    public enum PadConnectionState
    {
        Disconnected,
        Connecting,
        Connected
    }

    /// <summary>
    /// PAD data direction
    /// </summary>
    public enum PadDataDirection
    {
        Sent,
        Received
    }

    /// <summary>
    /// PAD connection state change event arguments
    /// </summary>
    public class PadConnectionEventArgs : EventArgs
    {
        public PadConnectionState State { get; }
        public string Reason { get; }

        public PadConnectionEventArgs(PadConnectionState state, string reason)
        {
            State = state;
            Reason = reason;
        }
    }

    /// <summary>
    /// PAD data event arguments
    /// </summary>
    public class PadDataEventArgs : EventArgs
    {
        public string Data { get; }
        public PadDataDirection Direction { get; }

        public PadDataEventArgs(string data, PadDataDirection direction)
        {
            Data = data;
            Direction = direction;
        }
    }

    /// <summary>
    /// PAD character event arguments for echo and character-level processing
    /// </summary>
    public class PadCharacterEventArgs : EventArgs
    {
        public byte Character { get; }

        public PadCharacterEventArgs(byte character)
        {
            Character = character;
        }
    }

    /// <summary>
    /// X.29 message event arguments
    /// </summary>
    public class X29MessageEventArgs : EventArgs
    {
        public RetroIO.Protocols.X25.Protocols.X29Message Message { get; }

        public X29MessageEventArgs(RetroIO.Protocols.X25.Protocols.X29Message message)
        {
            Message = message;
        }
    }

    #endregion
}