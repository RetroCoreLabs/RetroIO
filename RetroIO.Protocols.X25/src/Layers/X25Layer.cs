using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Protocols;

using RetroIO.Link.Lapb;
namespace RetroIO.Protocols.X25.Layers
{
using X25 = RetroIO.Protocols.X25.Protocols.X25;


    public class X25Layer
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly X25 _x25;

        public X25Layer(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // Use Microsoft.Extensions.Logging.ILogger directly
            _x25 = new X25(_logger);
            
            // Wire up the existing X25 events to our new event system
            _x25.IncomingCall += OnIncomingCall;
            _x25.DataReceived += OnDataReceived;
            _x25.CallCleared += OnCallCleared;
            _x25.CallReset += OnCallReset;
            _x25.RestartRequested += OnRestartRequested;
            _x25.PacketToSend += OnPacketToSend;
            
            // Initialize immediately
            _x25.Initialize();
            
            _logger.LogInformation("X.25 layer initialized and ready");
        }

        public void SendPacket(byte[] packetData, int length)
        {
            if (packetData == null || length <= 0)
            {
                _logger.LogWarning("Invalid packet data for transmission");
                return;
            }

            try
            {
                _logger.LogDebug("X.25: Sending packet of {Length} bytes", length);
                
                // Parse the X.25 packet header to extract LCN and packet type
                if (length < 3)
                {
                    _logger.LogError("X.25 packet too short: {Length} bytes", length);
                    return;
                }

                // Extract GFI, LCN, and packet type from X.25 header
                byte gfi = (byte)(packetData[0] & 0xF0);
                ushort lcn = (ushort)(((packetData[0] & 0x0F) << 8) | packetData[1]);
                byte typeField = packetData[2];
                
                // Determine packet type and extract data
                byte[] data = Array.Empty<byte>();
                if (length > 3)
                {
                    data = new byte[length - 3];
                    Array.Copy(packetData, 3, data, 0, length - 3);
                }

                // Convert type field to PacketType enum (simplified)
                var packetType = (X25.PacketType)(typeField & 0xFF);
                
                // Send through the old X25 implementation using proper method
                _x25.SendPacket(lcn, packetType, data, (ushort)(length - 3));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending X.25 packet");
                ErrorOccurred?.Invoke(this, new X25PacketEventArgs
                {
                    Packet = new X25Packet
                    {
                        FullPacket = packetData,
                        Length = (ushort)length,
                        Direction = PacketDirection.Transmit,
                        Timestamp = DateTime.UtcNow
                    },
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow,
                    PacketId = 0
                });
            }
        }

        public void ReceiveX25Packet(byte[] packetData, int length)
        {
            if (packetData == null || length <= 0)
            {
                _logger.LogWarning("Invalid X.25 packet data");
                return;
            }

            try
            {
                _logger.LogDebug("X.25: Processing X.25 packet of {Length} bytes", length);

                // Fire PacketReceived event BEFORE processing so ProtocolRuntime can link with LAPB
                // This enables RelatedItem population for history items
                // Use constructor that parses the packet (populates Data, LCN, PTI, etc.)
                var packet = new X25Packet(packetData);
                packet.Direction = PacketDirection.Receive;
                packet.Timestamp = DateTime.UtcNow;

                PacketReceived?.Invoke(this, new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow,
                    PacketId = 0 // Will be set by ProtocolRuntime
                });

                // Process the X.25 packet directly through the existing X25 implementation
                _x25.ProcessPacket(packetData, (ushort)length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing X.25 packet");
                ErrorOccurred?.Invoke(this, new X25PacketEventArgs
                {
                    Packet = new X25Packet
                    {
                        FullPacket = packetData,
                        Length = (ushort)length,
                        Direction = PacketDirection.Receive,
                        Timestamp = DateTime.UtcNow
                    },
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow,
                    PacketId = 0
                });
            }
        }

        public void MakeCall(ushort lcn, string calledAddress, string callingAddress, byte[]? userData = null)
        {
            try
            {
                _logger.LogInformation("X.25: Making call on LCN {LCN} to {CalledAddress}", lcn, calledAddress);
                
                // Create CALL REQUEST packet
                byte[] callData = CreateCallRequestData(calledAddress, callingAddress, userData);
                _x25.SendPacket(lcn, X25.PacketType.CALL_REQUEST, callData, (ushort)callData.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error making X.25 call");
                ErrorOccurred?.Invoke(this, new X25PacketEventArgs
                {
                    Packet = new X25Packet
                    {
                        Direction = PacketDirection.Transmit,
                        Timestamp = DateTime.UtcNow
                    },
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow,
                    PacketId = 0
                });
            }
        }

        public void AcceptCall(ushort lcn)
        {
            try
            {
                _logger.LogInformation("X.25: Accepting call on LCN {LCN}", lcn);
                
                // Send CALL ACCEPTED packet
                _x25.SendPacket(lcn, X25.PacketType.CALL_ACCEPTED, null, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting X.25 call");
                ErrorOccurred?.Invoke(this, new X25PacketEventArgs
                {
                    Packet = new X25Packet
                    {
                        Direction = PacketDirection.Transmit,
                        Timestamp = DateTime.UtcNow
                    },
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow,
                    PacketId = 0
                });
            }
        }

        public void ClearCall(ushort lcn, byte cause = 0, byte diagnostic = 0)
        {
            try
            {
                _logger.LogInformation("X.25: Clearing call on LCN {LCN}", lcn);
                
                // Create CLEAR REQUEST packet with cause and diagnostic
                byte[] clearData = new byte[2];
                clearData[0] = cause;
                clearData[1] = diagnostic;
                
                _x25.SendPacket(lcn, X25.PacketType.CLEAR_REQUEST, clearData, 2);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing X.25 call");
                ErrorOccurred?.Invoke(this, new X25PacketEventArgs
                {
                    Packet = new X25Packet
                    {
                        Direction = PacketDirection.Transmit,
                        Timestamp = DateTime.UtcNow
                    },
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow,
                    PacketId = 0
                });
            }
        }

        public void SendData(ushort lcn, byte[] data, int length)
        {
            if (data == null || length <= 0)
            {
                _logger.LogWarning("Invalid data for transmission");
                return;
            }

            try
            {
                _logger.LogDebug("X.25: Sending data on LCN {LCN}, length: {Length}", lcn, length);
                
                // Send DATA packet
                _x25.SendPacket(lcn, X25.PacketType.DATA, data, (ushort)length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending X.25 data");
                ErrorOccurred?.Invoke(this, new X25PacketEventArgs
                {
                    Packet = new X25Packet
                    {
                        Direction = PacketDirection.Transmit,
                        Timestamp = DateTime.UtcNow
                    },
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow,
                    PacketId = 0
                });
            }
        }

        private byte[] CreateCallRequestData(string calledAddress, string callingAddress, byte[]? userData)
        {
            // Simple implementation - in a real system, this would be more complex
            // with proper X.25 address encoding and facilities
            List<byte> callData = new List<byte>();
            
            // Add called address length and address
            callData.Add((byte)calledAddress.Length);
            callData.AddRange(System.Text.Encoding.ASCII.GetBytes(calledAddress));
            
            // Add calling address length and address
            callData.Add((byte)callingAddress.Length);
            callData.AddRange(System.Text.Encoding.ASCII.GetBytes(callingAddress));
            
            // Add user data if provided
            if (userData != null && userData.Length > 0)
            {
                callData.AddRange(userData);
            }
            
            return callData.ToArray();
        }

        private void OnIncomingCall(ushort lcn, byte[] callData, ushort length)
        {
            _logger.LogInformation("X.25: Incoming call on LCN {LCN}, data length: {Length}", lcn, length);
            
            // Raise the incoming call event
            IncomingCall?.Invoke(this, new X25PacketEventArgs
            {
                Packet = new X25Packet
                {
                    FullPacket = callData,
                    Length = length,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow,
                    PTI = 0x0B // Call Request
                },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow,
                PacketId = 0
            });
        }

        // Store original packet for relationship linking
        private byte[]? _lastOriginalPacket = null;

        public void SetOriginalPacketForRelationship(byte[] originalPacket)
        {
            _lastOriginalPacket = originalPacket;
        }

        private void OnDataReceived(ushort lcn, byte[] data, ushort length)
        {
            _logger.LogDebug("X.25: Data received on LCN {LCN}, length: {Length}", lcn, length);
            
            // Use original packet if available (for relationship linking), otherwise reconstruct
            byte[] fullPacket;
            if (_lastOriginalPacket != null)
            {
                fullPacket = _lastOriginalPacket;
                _lastOriginalPacket = null; // Clear after use
            }
            else
            {
                // Reconstruct complete X.25 packet for proper history tracking
                fullPacket = new byte[length + 3]; // +3 for X.25 header
                
                // Build X.25 header: GFI + LCN (2 bytes) + PTI (1 byte)
                fullPacket[0] = (byte)(0x10 | ((lcn >> 8) & 0x0F)); // GFI=0x10 + LCN high nibble
                fullPacket[1] = (byte)(lcn & 0xFF); // LCN low byte
                fullPacket[2] = 0x00; // PTI = Data packet
                
                // Copy payload data
                Array.Copy(data, 0, fullPacket, 3, length);
            }
            
            // Raise the data received event using constructor that parses the packet
            var x25Packet = new X25Packet(fullPacket);
            x25Packet.Direction = PacketDirection.Receive;
            x25Packet.Timestamp = DateTime.UtcNow;
            
            _logger.LogDebug("🔥 FIRING DataReceived event for LCN {LCN}, packet length {Length}", 
                x25Packet.LCN, x25Packet.FullPacket.Length);
            
            DataReceived?.Invoke(this, new X25PacketEventArgs
            {
                Packet = x25Packet,
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow,
                PacketId = 0
            });
        }

        private void OnCallCleared(ushort lcn, byte cause, byte diagnostic)
        {
            _logger.LogInformation("X.25: Call cleared on LCN {LCN}, cause: {Cause}, diagnostic: {Diagnostic}", lcn, cause, diagnostic);
            
            // Raise the call cleared event
            CallCleared?.Invoke(this, new X25PacketEventArgs
            {
                Packet = new X25Packet
                {
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow,
                    PTI = 0x13 // Clear Request
                },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow,
                PacketId = 0
            });
        }

        private void OnCallReset(ushort lcn, byte cause, byte diagnostic)
        {
            _logger.LogInformation("X.25: Call reset on LCN {LCN}, cause: {Cause}, diagnostic: {Diagnostic}", lcn, cause, diagnostic);
            
            // Raise the call reset event
            CallReset?.Invoke(this, new X25PacketEventArgs
            {
                Packet = new X25Packet
                {
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow,
                    PTI = 0x1B // Reset Request
                },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow,
                PacketId = 0
            });
        }

        private void OnRestartRequested(byte cause, byte diagnostic)
        {
            _logger.LogInformation("X.25: Restart requested, cause: {Cause}, diagnostic: {Diagnostic}", cause, diagnostic);
            
            // Raise the restart requested event
            RestartRequested?.Invoke(this, new X25PacketEventArgs
            {
                Packet = new X25Packet
                {
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow,
                    PTI = 0xFB // Restart Request
                },
                Direction = PacketDirection.Receive,
                Timestamp = DateTime.UtcNow,
                PacketId = 0
            });
        }

        private void OnPacketToSend(byte[] packetData, ushort length)
        {
            _logger.LogDebug("X.25: Sending packet of {Length} bytes", length);
            
            // Create packet for events
            var packet = new X25Packet(packetData);
            packet.Direction = PacketDirection.Transmit;
            packet.Timestamp = DateTime.UtcNow;
            
            var eventArgs = new X25PacketEventArgs
            {
                Packet = packet,
                Direction = PacketDirection.Transmit,
                Timestamp = DateTime.UtcNow,
                PacketId = 0 // Will be set by ProtocolRuntime
            };
            
            // ONLY fire DataToSend event for actual outgoing packets
            // PacketTransmitted will be handled by ProtocolRuntime after logging
            DataToSend?.Invoke(this, eventArgs);
        }

        // Events
        public event EventHandler<X25PacketEventArgs>? PacketReceived;
        public event EventHandler<X25PacketEventArgs>? PacketTransmitted;
        public event EventHandler<X25PacketEventArgs>? IncomingCall;
        public event EventHandler<X25PacketEventArgs>? CallCleared;
        public event EventHandler<X25PacketEventArgs>? DataReceived;
        public event EventHandler<X25PacketEventArgs>? CallReset;
        public event EventHandler<X25PacketEventArgs>? RestartRequested;
        public event EventHandler<X25PacketEventArgs>? ErrorOccurred;
        public event EventHandler<X25PacketEventArgs>? DataToSend;

        /// <summary>
        /// Get channel count from underlying X25 implementation
        /// </summary>
        public int GetChannelCount()
        {
            return _x25.GetChannelCount();
        }

        /// <summary>
        /// Send packet with specific packet type
        /// </summary>
        public void SendPacket(ushort lcn, X25.PacketType packetType, byte[] data, ushort length)
        {
            _x25.SendPacket(lcn, packetType, data, length);
        }

        /// <summary>
        /// Receive and process an X.25 packet
        /// </summary>
        /// <param name="packet">Packet data</param>
        /// <param name="length">Length of packet</param>
        public void ReceivePacket(byte[] packet, ushort length)
        {
            _x25.ProcessPacket(packet, length);
        }

        /// <summary>
        /// Fire the PacketTransmitted event for external use (e.g., serial routing)
        /// </summary>
        public void FirePacketTransmitted(X25PacketEventArgs eventArgs)
        {
            PacketTransmitted?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// Fire the PacketReceived event for protocol runtime tracking
        /// </summary>
        public void FirePacketReceived(X25PacketEventArgs eventArgs)
        {
            PacketReceived?.Invoke(this, eventArgs);
        }
    }
}
