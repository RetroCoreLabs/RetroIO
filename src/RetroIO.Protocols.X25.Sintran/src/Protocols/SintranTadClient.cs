//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Sintran;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// SINTRAN TAD Client
    /// Connects to remote SINTRAN TAD servers using routing protocol and X.25
    /// </summary>
    public class SintranTadClient : IProtocolModule, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IProtocolRuntime _runtime;
        private SintranTadSession _session;
        private bool _disposed = false;

        // Connection state
        private ushort _currentLCN = 0;
        private string _remoteAddress = "";

        /// <summary>
        /// Raised when the connection state changes.
        /// </summary>
        public event EventHandler<SintranTadClientEventArgs>? ConnectionStateChanged;
        /// <summary>
        /// Raised when data is received.
        /// </summary>
        public event EventHandler<SintranTadClientDataEventArgs>? DataReceived;
        /// <summary>
        /// Raised when an error occurs.
        /// </summary>
        public event EventHandler<SintranTadClientEventArgs>? ErrorOccurred;

        /// <summary>
        /// Gets the ID of the client.
        /// </summary>
        public string ClientId { get; }
        /// <summary>
        /// Gets whether the session state is Active.
        /// </summary>
        public bool IsConnected => _session.State == TadSessionState.Active;
        /// <summary>
        /// Gets the state of the session.
        /// </summary>
        public TadSessionState ConnectionState => _session.State;

        // IProtocolModule Properties
        /// <summary>
        /// Gets or sets the module name.
        /// </summary>
        /// <value>
        /// Set to the client ID by the constructor; it can be changed later.
        /// </value>
        public string Name { get; set; }
        /// <summary>
        /// Gets the module description.
        /// </summary>
        /// <value>
        /// Always <c>"SINTRAN TAD Client for connecting to remote TAD servers"</c>.
        /// </value>
        public string Description { get; } = "SINTRAN TAD Client for connecting to remote TAD servers";
        /// <summary>
        /// Gets the module version.
        /// </summary>
        /// <value>
        /// Always <c>"1.0.0"</c>.
        /// </value>
        public string Version { get; } = "1.0.0";

        /// <summary>
        /// Creates the client. The name is set to the client ID.
        /// </summary>
        /// <param name="logger">
        /// The logger to write to.
        /// </param>
        /// <param name="runtime">
        /// The protocol runtime.
        /// </param>
        /// <param name="clientId">
        /// The ID of the client.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="logger"/>, <paramref name="runtime"/> or <paramref name="clientId"/> is null.
        /// </exception>
        public SintranTadClient(ILogger logger, IProtocolRuntime runtime, string clientId)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            ClientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
            Name = clientId;

            _session = new SintranTadSession
            {
                State = TadSessionState.Created,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            _logger.LogInformation("SintranTadClient: Created client {ClientId}", ClientId);
        }

        #region IProtocolModule Implementation

        /// <summary>
        /// Called when the module is bound to a logical channel; records the channel as the current one.
        /// </summary>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        public void OnBound(ushort lcn)
        {
            _currentLCN = lcn;
            _logger.LogDebug("SintranTadClient: Bound to LCN {LCN}", lcn);
        }

        /// <summary>
        /// Called when the module is unbound from a logical channel.
        /// </summary>
        /// <remarks>
        /// When <paramref name="lcn"/> is the current channel, the current channel is cleared and the
        /// session is marked closed. Any other channel number is ignored.
        /// </remarks>
        /// <param name="lcn">
        /// The logical channel number.
        /// </param>
        public void OnUnbound(ushort lcn)
        {
            if (lcn == _currentLCN)
            {
                _logger.LogInformation("SintranTadClient: Unbound from LCN {LCN}", lcn);
                _currentLCN = 0;
                HandleDisconnection();
            }
        }

        /// <summary>
        /// Called when an X.25 event occurs; dispatches the packet by its type.
        /// </summary>
        /// <remarks>
        /// Packets for any channel other than the current one are ignored. Call Accepted, Data and
        /// Clear Request packets are handled; other packet types are logged and ignored. An exception
        /// while handling is logged, not thrown.
        /// </remarks>
        /// <param name="args">
        /// The event data, holding the received packet.
        /// </param>
        public void OnX25Event(X25PacketEventArgs args)
        {
            _logger.LogDebug("SintranTadClient: Received X25 event: PacketType={PacketType}, LCN={LCN}",
                           args.Packet.PacketType, args.Packet.LCN);

            if (args.Packet.LCN != _currentLCN)
            {
                _logger.LogDebug("SintranTadClient: Ignoring event - LCN mismatch");
                return;
            }

            try
            {
                switch (args.Packet.PacketType)
                {
                    case X25PacketType.CallAccepted:
                        HandleCallAccepted(args);
                        break;
                    case X25PacketType.Data:
                        HandleDataReceived(args);
                        break;
                    case X25PacketType.ClearRequest:
                        HandleClearRequest(args);
                        break;
                    default:
                        _logger.LogDebug("SintranTadClient: Unhandled packet type: {PacketType}", args.Packet.PacketType);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling X.25 event in SINTRAN TAD Client");
            }
        }

        /// <summary>
        /// Initializes the module. This implementation only writes a log line.
        /// </summary>
        public void Initialize()
        {
            _logger.LogInformation("SintranTadClient: Module initialized");
        }

        /// <summary>
        /// Shuts down the module by calling <see cref="Disconnect"/>.
        /// </summary>
        public void Shutdown()
        {
            Disconnect();
            _logger.LogInformation("SintranTadClient: Module shutdown");
        }

        /// <summary>
        /// Gets the module status.
        /// </summary>
        /// <returns>
        /// A new status object. The module counts as active when the session is neither Created nor
        /// Closed, it reports one connection while a channel is bound, and the error count is always 0.
        /// </returns>
        public ModuleStatus GetStatus()
        {
            return new ModuleStatus
            {
                IsActive = _session.State != TadSessionState.Closed && _session.State != TadSessionState.Created,
                ActiveConnections = _currentLCN > 0 ? 1 : 0,
                TotalDataReceived = _session.BytesReceived,
                TotalDataTransmitted = _session.BytesSent,
                LastActivity = _session.LastActivity,
                ErrorCount = 0,
                StatusMessage = $"State: {_session.State}, LCN: {_currentLCN}, Remote: {_remoteAddress}"
            };
        }

        #endregion

        /// <summary>
        /// Connect to remote TAD server
        /// </summary>
        public bool Connect(string remoteAddress, string callingAddress = "")
        {
            var traceId = Guid.NewGuid().ToString("N").Substring(0, 8);

            _logger.LogInformation("🔵 [TRACE:{TraceId}] SintranTadClient.Connect() ENTER: RemoteAddress={RemoteAddress}, CallingAddress={CallingAddress}, ClientId={ClientId}",
                traceId, remoteAddress, callingAddress, ClientId);

            if (_session.State != TadSessionState.Created && _session.State != TadSessionState.Closed)
            {
                _logger.LogWarning("🔵 [TRACE:{TraceId}] SintranTadClient.Connect() EXIT: Already connected/connecting - State={State}",
                    traceId, _session.State);
                return false;
            }

            _remoteAddress = remoteAddress;
            _session.State = TadSessionState.WaitingForInibdr;
            _session.LastActivity = DateTime.UtcNow;

            // Notify state change
            OnConnectionStateChanged(TadSessionState.WaitingForInibdr, "Connecting to TAD server");

            _logger.LogInformation("🔵 [TRACE:{TraceId}] SintranTadClient: Placing X.25 call to {RemoteAddress}", traceId, remoteAddress);

            // Create call user data (empty for TAD)
            byte[]? callUserData = null;

            // Place X.25 call with this module bound
            _logger.LogInformation("🔵 [TRACE:{TraceId}] SintranTadClient: Calling _runtime.PlaceX25CallWithModule()", traceId);
            var success = _runtime.PlaceX25CallWithModule(
                remoteAddress,
                callingAddress,
                callUserData,
                this
            );
            _logger.LogInformation("🔵 [TRACE:{TraceId}] SintranTadClient: PlaceX25CallWithModule() returned {Success}", traceId, success);

            if (!success)
            {
                _session.State = TadSessionState.Closed;
                _logger.LogError("🔵 [TRACE:{TraceId}] Failed to initiate X.25 call to {RemoteAddress}", traceId, remoteAddress);
                OnConnectionStateChanged(TadSessionState.Closed, "Connection failed");
            }

            _logger.LogInformation("🔵 [TRACE:{TraceId}] SintranTadClient.Connect() EXIT: Success={Success}", traceId, success);
            return success;
        }

        /// <summary>
        /// Handle call accepted response
        /// </summary>
        private void HandleCallAccepted(X25PacketEventArgs args)
        {
            _currentLCN = args.Packet.LCN;

            _logger.LogInformation("SintranTadClient: Call accepted on LCN {LCN}, starting TAD handshake", _currentLCN);

            _session.State = TadSessionState.ConfiguringConnection;
            OnConnectionStateChanged(TadSessionState.ConfiguringConnection, "Sending configuration");

            // Start TAD handshake - send initial dummy message
            var dummyMsg = SintranTadMessage.CreateDummy();
            SendMessage(dummyMsg);

            // Send client configuration
            SendClientConfiguration();
        }

        /// <summary>
        /// Handle incoming data from X.25
        /// </summary>
        private void HandleDataReceived(X25PacketEventArgs args)
        {
            if (args.Packet.Data == null || args.Packet.Data.Length == 0)
                return;

            _session.LastActivity = DateTime.UtcNow;

            // Check if this is a routing protocol frame (0x21 0x13 0xDD)
            if (args.Packet.Data.Length >= 3 &&
                args.Packet.Data[0] == 0x21 &&
                args.Packet.Data[1] == 0x13 &&
                args.Packet.Data[2] == 0xDD)
            {
                // Extract TAD payload (skip 0x21 0x13 0xDD header)
                byte[] tadPayload = new byte[args.Packet.Data.Length - 3];
                Array.Copy(args.Packet.Data, 3, tadPayload, 0, tadPayload.Length);

                _logger.LogDebug("SintranTadClient: Received TAD message ({Length} bytes)", tadPayload.Length);

                // Process TAD message
                HandleIncomingMessage(tadPayload);
            }
            else
            {
                _logger.LogWarning("SintranTadClient: Received non-routing protocol data");
            }
        }

        /// <summary>
        /// Handle clear request from remote
        /// </summary>
        private void HandleClearRequest(X25PacketEventArgs args)
        {
            _logger.LogInformation("SintranTadClient: Connection cleared by remote on LCN {LCN}", args.Packet.LCN);
            HandleDisconnection();
            OnConnectionStateChanged(TadSessionState.Closed, "Remote disconnect");
        }

        /// <summary>
        /// Handle disconnection cleanup
        /// </summary>
        private void HandleDisconnection()
        {
            _session.State = TadSessionState.Closed;
            _currentLCN = 0;
        }

        /// <summary>
        /// Disconnect from TAD server
        /// </summary>
        public void Disconnect()
        {
            if (_session.State == TadSessionState.Closed || _session.State == TadSessionState.Created)
            {
                return;
            }

            if (_currentLCN == 0)
            {
                return;
            }

            _logger.LogInformation("SintranTadClient: Disconnecting from TAD server");

            _session.State = TadSessionState.Closing;
            OnConnectionStateChanged(TadSessionState.Closing, "Disconnecting");

            // Send disconnect message
            var disconnectMsg = SintranTadMessage.CreateDisconnect();
            SendMessage(disconnectMsg);

            // Send X.25 Clear Request
            _logger.LogDebug("SintranTadClient: Sending X.25 Clear Request on LCN {LCN}", _currentLCN);
            _runtime.ClearCallByLocalLcn(_currentLCN, 0, 0);

            _session.State = TadSessionState.Closed;
            _currentLCN = 0;
            OnConnectionStateChanged(TadSessionState.Closed, "Disconnected");
        }

        /// <summary>
        /// Send text data to TAD server
        /// </summary>
        public void SendData(string text)
        {
            if (!IsConnected)
            {
                _logger.LogWarning("SintranTadClient: Cannot send data - not connected");
                return;
            }

            byte[] data = System.Text.Encoding.ASCII.GetBytes(text);
            var dataMsg = SintranTadMessage.CreateBinaryData(data);
            SendMessage(dataMsg);

            _session.MessagesSent++;
            _session.BytesSent += data.Length;
        }

        /// <summary>
        /// Send raw bytes to TAD server
        /// </summary>
        public void SendData(byte[] data)
        {
            if (!IsConnected)
            {
                _logger.LogWarning("SintranTadClient: Cannot send data - not connected");
                return;
            }

            var dataMsg = SintranTadMessage.CreateBinaryData(data);
            SendMessage(dataMsg);

            _session.MessagesSent++;
            _session.BytesSent += data.Length;
        }

        /// <summary>
        /// Handle incoming TAD message from server
        /// This is called by the routing handler when TAD protocol data arrives
        /// </summary>
        public void HandleIncomingMessage(byte[] data)
        {
            _session.LastActivity = DateTime.UtcNow;
            _session.MessagesReceived++;
            _session.BytesReceived += data.Length;

            try
            {
                // Parse TAD message
                if (!SintranTadMessage.TryParse(data, out var message, out var error))
                {
                    _logger.LogError("SintranTadClient: Failed to parse TAD message: {Error}", error);
                    OnError($"Message parse error: {error}");
                    return;
                }

                _logger.LogDebug("SintranTadClient: Received TAD message: {Message}", message);

                // Process message based on type
                ProcessIncomingMessage(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SintranTadClient: Error processing incoming message");
                OnError($"Processing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Process incoming TAD message
        /// </summary>
        private void ProcessIncomingMessage(SintranTadMessage message)
        {
            switch (message.MessageType)
            {
                case SintranTadMessageTypes.DUMM:
                    // Dummy message - no action needed
                    break;

                case SintranTadMessageTypes.BDAT:
                    // Binary data from server
                    HandleBinaryData(message);
                    break;

                case SintranTadMessageTypes.RFI:
                    // Ready for input
                    HandleReadyForInput(message);
                    break;

                case SintranTadMessageTypes.TMOD:
                    // Terminal mode configuration from server
                    HandleTerminalMode(message);
                    break;

                case SintranTadMessageTypes.TTYP:
                    // Terminal type from server
                    HandleTerminalType(message);
                    break;

                case SintranTadMessageTypes.DESC:
                    // Descriptor from server
                    HandleDescriptor(message);
                    break;

                case SintranTadMessageTypes.OPSV:
                    // Option save from server
                    HandleOptionSave(message);
                    break;

                case SintranTadMessageTypes.BMMX:
                    // Break matrix from server
                    HandleBreakMatrix(message);
                    break;

                case SintranTadMessageTypes.ECKM:
                    // Echo matrix from server
                    HandleEchoMatrix(message);
                    break;

                case SintranTadMessageTypes.DCON:
                    // Disconnect from server
                    HandleDisconnect(message);
                    break;

                case SintranTadMessageTypes.ERRS:
                    // Error message from server
                    HandleError(message);
                    break;

                default:
                    _logger.LogWarning("SintranTadClient: Unhandled message type: {MessageType}",
                        SintranTadMessageTypes.GetMessageTypeName(message.MessageType));
                    break;
            }
        }

        /// <summary>
        /// Send client configuration to server
        /// </summary>
        private void SendClientConfiguration()
        {
            _logger.LogInformation("SintranTadClient: Sending client configuration");

            // Send terminal type
            var ttypMsg = SintranTadMessage.CreateTerminalType(0x01); // ASCII terminal
            SendMessage(ttypMsg);

            // Send terminal mode
            var tmodMsg = SintranTadMessage.CreateTerminalMode(
                echoPlex: true,
                localEdit: true,
                pageMode: false,
                formFeed: false);
            SendMessage(tmodMsg);

            // Send descriptor
            var descMsg = SintranTadMessage.CreateDescriptor(
                lineWidth: 80,
                pageLength: 24,
                tabWidth: 8);
            SendMessage(descMsg);

            // Send option save
            var opsvMsg = SintranTadMessage.CreateOptionSave(true);
            SendMessage(opsvMsg);

            _logger.LogInformation("SintranTadClient: Client configuration sent");
        }

        /// <summary>
        /// Handle binary data from server
        /// </summary>
        private void HandleBinaryData(SintranTadMessage message)
        {
            string text = message.GetDataAsString();
            _logger.LogDebug("SintranTadClient: Received data: {Data}", text);

            // Notify data received
            OnDataReceived(text);

            // If we're still configuring, transition to active state
            if (_session.State == TadSessionState.ConfiguringConnection)
            {
                _session.State = TadSessionState.Active;
                OnConnectionStateChanged(TadSessionState.Active, "Connected");
            }
        }

        /// <summary>
        /// Handle ready for input from server
        /// </summary>
        private void HandleReadyForInput(SintranTadMessage message)
        {
            _logger.LogDebug("SintranTadClient: Server ready for input");
            _session.ReadyForInput = true;

            // If we're still configuring, transition to active state
            if (_session.State == TadSessionState.ConfiguringConnection)
            {
                _session.State = TadSessionState.Active;
                OnConnectionStateChanged(TadSessionState.Active, "Connected");
            }
        }

        /// <summary>
        /// Handle terminal mode from server
        /// </summary>
        private void HandleTerminalMode(SintranTadMessage message)
        {
            if (message.Data.Length >= 1)
            {
                _session.EchoPlex = (message.Data[0] & 0x01) != 0;
                _session.LocalEdit = (message.Data[0] & 0x02) != 0;
                _session.PageMode = (message.Data[0] & 0x04) != 0;
                _session.FormFeed = (message.Data[0] & 0x08) != 0;

                _logger.LogDebug("SintranTadClient: Terminal mode updated");
            }
        }

        /// <summary>
        /// Handle terminal type from server
        /// </summary>
        private void HandleTerminalType(SintranTadMessage message)
        {
            if (message.Data.Length >= 2)
            {
                _session.TerminalType = (ushort)((message.Data[0] << 8) | message.Data[1]);
                _logger.LogDebug("SintranTadClient: Terminal type: 0x{Type:X4}", _session.TerminalType);
            }
        }

        /// <summary>
        /// Handle descriptor from server
        /// </summary>
        private void HandleDescriptor(SintranTadMessage message)
        {
            if (message.Data.Length >= 6)
            {
                _session.LineWidth = (message.Data[0] << 8) | message.Data[1];
                _session.PageLength = (message.Data[2] << 8) | message.Data[3];
                _session.TabWidth = (message.Data[4] << 8) | message.Data[5];

                _logger.LogDebug("SintranTadClient: Descriptor: {Width}x{Height}, Tab={Tab}",
                    _session.LineWidth, _session.PageLength, _session.TabWidth);
            }
        }

        /// <summary>
        /// Handle option save from server
        /// </summary>
        private void HandleOptionSave(SintranTadMessage message)
        {
            if (message.Data.Length >= 1)
            {
                _session.OptionSave = message.Data[0] != 0;
                _logger.LogDebug("SintranTadClient: Option save: {OptionSave}", _session.OptionSave);
            }
        }

        /// <summary>
        /// Handle break matrix from server
        /// </summary>
        private void HandleBreakMatrix(SintranTadMessage message)
        {
            if (message.Data.Length == 20)
            {
                Array.Copy(message.Data, _session.BreakModeMatrix, 20);
                _logger.LogDebug("SintranTadClient: Break matrix updated");
            }
        }

        /// <summary>
        /// Handle echo matrix from server
        /// </summary>
        private void HandleEchoMatrix(SintranTadMessage message)
        {
            if (message.Data.Length == 20)
            {
                Array.Copy(message.Data, _session.EchoModeMatrix, 20);
                _logger.LogDebug("SintranTadClient: Echo matrix updated");
            }
        }

        /// <summary>
        /// Handle disconnect from server
        /// </summary>
        private void HandleDisconnect(SintranTadMessage message)
        {
            _logger.LogInformation("SintranTadClient: Server disconnected");

            _session.State = TadSessionState.Closed;
            OnConnectionStateChanged(TadSessionState.Closed, "Server disconnected");
        }

        /// <summary>
        /// Handle error from server
        /// </summary>
        private void HandleError(SintranTadMessage message)
        {
            string errorMsg = message.GetDataAsString();
            _logger.LogError("SintranTadClient: Server error: {Error}", errorMsg);
            OnError($"Server error: {errorMsg}");
        }

        /// <summary>
        /// Send TAD message to server (wrapped in routing protocol)
        /// </summary>
        private void SendMessage(SintranTadMessage message)
        {
            if (_currentLCN == 0)
            {
                _logger.LogWarning("SintranTadClient: Cannot send message - not connected (LCN=0)");
                return;
            }

            byte[] tadBytes = message.Build();

            _logger.LogDebug("SintranTadClient: Sending TAD message: {Message}", message);

            // Wrap in routing protocol frame (0x21 0x13 0xDD)
            byte[] routingFrame = new byte[3 + tadBytes.Length];
            routingFrame[0] = 0x21; // SINTRAN marker
            routingFrame[1] = 0x13; // Routing protocol marker
            routingFrame[2] = 0xDD; // TAD protocol ID
            Array.Copy(tadBytes, 0, routingFrame, 3, tadBytes.Length);

            // Send through X.25 layer
            var success = _runtime.SendX25Data(
                _currentLCN,
                routingFrame,
                false, // M-bit
                false, // Q-bit
                false  // D-bit
            );

            if (!success)
            {
                _logger.LogError("SintranTadClient: Failed to send TAD message");
            }

            _session.LastActivity = DateTime.UtcNow;
        }

        /// <summary>
        /// Raise connection state changed event
        /// </summary>
        private void OnConnectionStateChanged(TadSessionState state, string message)
        {
            ConnectionStateChanged?.Invoke(this, new SintranTadClientEventArgs
            {
                ClientId = ClientId,
                State = state,
                Message = message
            });
        }

        /// <summary>
        /// Raise data received event
        /// </summary>
        private void OnDataReceived(string data)
        {
            DataReceived?.Invoke(this, new SintranTadClientDataEventArgs
            {
                ClientId = ClientId,
                Data = data
            });
        }

        /// <summary>
        /// Raise error occurred event
        /// </summary>
        private void OnError(string errorMessage)
        {
            ErrorOccurred?.Invoke(this, new SintranTadClientEventArgs
            {
                ClientId = ClientId,
                State = _session.State,
                Message = errorMessage
            });
        }

        #region IProtocolModule Legacy Methods

        /// <summary>
        /// Called when X.25 data packet is received (legacy interface - now unused)
        /// </summary>
        public void OnX25Data(ushort lcn, byte[] data)
        {
            // Legacy interface - no longer used, data processing now handled via OnX25Event
        }

        /// <summary>
        /// Called when X.25 reset is requested
        /// </summary>
        public void OnX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            if (_currentLCN == lcn)
            {
                _logger.LogInformation("SintranTadClient: Received X.25 reset: cause={Cause}, diagnostic={Diagnostic}", cause, diagnostic);
                HandleDisconnection();
                OnConnectionStateChanged(TadSessionState.Closed, $"Reset: {cause}/{diagnostic}");
            }
        }

        /// <summary>
        /// Called when X.25 clear is requested
        /// </summary>
        public void OnX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            if (_currentLCN == lcn)
            {
                _logger.LogInformation("SintranTadClient: Received X.25 clear: cause={Cause}, diagnostic={Diagnostic}", cause, diagnostic);
                HandleDisconnection();
                OnConnectionStateChanged(TadSessionState.Closed, $"Cleared: {cause}/{diagnostic}");
            }
        }

        /// <summary>
        /// Called when X.25 interrupt is received
        /// </summary>
        public void OnX25Interrupt(ushort lcn, byte[] data)
        {
            if (_currentLCN == lcn)
            {
                _logger.LogDebug("SintranTadClient: Received X.25 interrupt with {Length} bytes", data.Length);
                // TAD protocol doesn't use interrupts
            }
        }

        /// <summary>
        /// Send X.25 data packet (legacy interface)
        /// </summary>
        public void SendX25Data(ushort lcn, byte[] data)
        {
            if (_currentLCN == lcn && _runtime != null)
            {
                _runtime.SendX25Data(lcn, data);
            }
        }

        /// <summary>
        /// Send X.25 reset request
        /// </summary>
        public void SendX25Reset(ushort lcn, byte cause, byte diagnostic)
        {
            _logger.LogWarning("SintranTadClient: SendX25Reset not implemented");
        }

        /// <summary>
        /// Send X.25 clear request
        /// </summary>
        public void SendX25Clear(ushort lcn, byte cause, byte diagnostic)
        {
            _logger.LogWarning("SintranTadClient: SendX25Clear not implemented");
        }

        /// <summary>
        /// Send X.25 interrupt
        /// </summary>
        public void SendX25Interrupt(ushort lcn, byte[] data)
        {
            _logger.LogWarning("SintranTadClient: SendX25Interrupt not implemented");
        }

        #endregion

        /// <summary>
        /// Disposes the client, calling <see cref="Disconnect"/> first when it is connected.
        /// </summary>
        /// <remarks>
        /// A second call does nothing.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;

            _logger.LogDebug("SintranTadClient: Disposing client {ClientId}", ClientId);

            if (IsConnected)
            {
                Disconnect();
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Event args for SINTRAN TAD client events
    /// </summary>
    public class SintranTadClientEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the ID of the client.
        /// </summary>
        public string ClientId { get; set; } = "";
        /// <summary>
        /// Gets or sets the session state.
        /// </summary>
        public TadSessionState State { get; set; }
        /// <summary>
        /// Gets or sets the message text.
        /// </summary>
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Event args for SINTRAN TAD client data events
    /// </summary>
    public class SintranTadClientDataEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the ID of the client.
        /// </summary>
        public string ClientId { get; set; } = "";
        /// <summary>
        /// Gets or sets the received data as text.
        /// </summary>
        public string Data { get; set; } = "";
    }
}
