using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// X.25 protocol implementation
    /// </summary>
    public class X25
    {
        // X.25 packet types
        public enum PacketType : byte
        {
            // Basic packet types
            DATA = 0x00,  // With Q-bit = 0
            RR = 0x01,  // Flow control
            RNR = 0x05,  // Flow control
            REJ = 0x09,  // Flow control
            CALL_REQUEST = 0x0B,
            CALL_ACCEPTED = 0x0F,
            CLEAR_REQUEST = 0x13,
            CLEAR_CONFIRMATION = 0x17,
            INTERRUPT = 0x23,
            INTERRUPT_CONF = 0x27,
            RESET_REQUEST = 0x1B,
            RESET_CONFIRMATION = 0x1F,
            RESTART_REQUEST = 0xFB,
            RESTART_CONFIRMATION = 0xFF
        }

        // Channel states
        public enum ChannelState
        {
            READY,              // Ready to accept connections
            CALL_REQUEST_SENT,
            INCOMING_CALL,
            DATA_TRANSFER,
            CLEAR_REQUEST_SENT,
            CLEAR_INDICATION
        }

        // Channel information
        public class Channel
        {
            public ushort LCN;                 // Logical channel number
            public ChannelState State;         // Channel state
            public byte PR;                    // Packet receive sequence
            public byte PS;                    // Packet send sequence
            public byte WindowSize;            // Window size (W)
            public bool LocalBusy;             // Local flow control state
            public bool RemoteBusy;            // Remote flow control state
        }

        // Logger for X.25 operations
        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        // Channel list
        private List<Channel> _channels = new List<Channel>();

        // Events for callbacks
        public event Action<ushort, byte[], ushort>? IncomingCall;
        public event Action<ushort, byte[], ushort>? DataReceived;
        public event Action<ushort, byte, byte>? CallCleared;
        public event Action<ushort, byte, byte>? CallReset;
        public event Action<byte, byte>? RestartRequested;

        // Event for sending data to LAPB
        public event Action<byte[], ushort>? PacketToSend;

        /// <summary>
        /// Constructor with logger injection
        /// </summary>
        public X25(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Initialize X.25 context
        /// </summary>
        public void Initialize()
        {
            _channels.Clear();
        }

        /// <summary>
        /// Find a channel by LCN
        /// </summary>
        private Channel? FindChannel(ushort lcn)
        {
            return _channels.Find(c => c.LCN == lcn);
        }

        /// <summary>
        /// Create a new channel
        /// </summary>
        private Channel CreateChannel(ushort lcn)
        {
            Channel channel = new Channel
            {
                LCN = lcn,
                State = ChannelState.READY,
                PR = 0,
                PS = 0,
                WindowSize = 2,  // Default window size
                LocalBusy = false,
                RemoteBusy = false
            };

            _channels.Add(channel);
            return channel;
        }

        /// <summary>
        /// Send an X.25 packet
        /// </summary>
        public void SendPacket(ushort lcn, PacketType type, byte[]? data, ushort length)
        {
            byte[] packet = new byte[length + 3];  // GFI+LCN (2 bytes) + Type + Data
            ushort packetLen = 0;

            // GFI (General Format Identifier) + LCN (Logical Channel Number)
            packet[0] = (byte)((lcn >> 8) & 0x0F);  // High 4 bits of LCN, GFI = 0
            packet[1] = (byte)(lcn & 0xFF);         // Low 8 bits of LCN

            // Packet Type Identifier
            packet[2] = (byte)type;
            packetLen = 3;

            // For data packets, add P(R) and P(S)
            if (((byte)type & 0xF0) == 0x00)  // Data or flow control
            {
                Channel? channel = FindChannel(lcn);
                if (channel != null)
                {
                    // Add sequence numbers
                    if (type == PacketType.DATA)
                    {
                        packet[2] |= (byte)(channel.PR << 4);  // P(R) in high 4 bits
                        packet[2] |= (byte)(channel.PS << 1);  // P(S) in middle 4 bits (shifted left by 1)
                        channel.PS = (byte)((channel.PS + 1) & 0x07);  // Increment P(S)
                    }
                    else  // Flow control packets (RR, RNR, REJ)
                    {
                        packet[2] |= (byte)(channel.PR << 4);  // P(R) in high 4 bits
                    }
                }
            }

            // Add data if any
            if (data != null && length > 0)
            {
                Array.Copy(data, 0, packet, packetLen, length);
                packetLen += length;
            }

            // Send via LAPB
            PacketToSend?.Invoke(packet, packetLen);
        }

        /// <summary>
        /// Process an X.25 packet received from LAPB
        /// </summary>
        public void ProcessPacket(byte[] packet, ushort length)
        {
            // Validate X.25 packet structure first
            if (!ValidatePacketStructure(packet, length))
            {
                _logger.LogWarning("Packet structure validation failed, discarding packet");
                return;
            }

            // Extract LCN
            ushort lcn = (ushort)(((packet[0] & 0x0F) << 8) | packet[1]);

            // Extract packet type
            byte type = (byte)(packet[2] & 0x1F);  // Remove P(R) and P(S)

            // Find or create channel
            Channel? channel = FindChannel(lcn);

            // Process based on type
            switch ((PacketType)type)
            {
                case PacketType.CALL_REQUEST:
                    _logger.LogInformation($"Processing CALL_REQUEST for LCN {lcn}");
                    if (channel == null) channel = CreateChannel(lcn);
                    if (channel != null)
                    {
                        channel.State = ChannelState.INCOMING_CALL;
                        // Notify higher layer
                        byte[] callData = new byte[length - 3];
                        Array.Copy(packet, 3, callData, 0, length - 3);

                        IncomingCall?.Invoke(lcn, callData, (ushort)(length - 3));
                    }
                    break;

                case PacketType.CALL_ACCEPTED:
                    _logger.LogInformation($"Processing CALL_ACCEPTED for LCN {lcn}");
                    if (channel != null)
                    {
                        channel.State = ChannelState.DATA_TRANSFER;
                    }
                    break;

                case PacketType.CLEAR_REQUEST:
                    _logger.LogInformation($"Processing CLEAR_REQUEST for LCN {lcn}");
                    if (channel != null)
                    {
                        channel.State = ChannelState.CLEAR_INDICATION;
                        // Send clear confirmation
                        SendPacket(lcn, PacketType.CLEAR_CONFIRMATION, Array.Empty<byte>(), 0);

                        // Notify higher layer
                        byte cause = (length > 3) ? packet[3] : (byte)0;
                        byte diag = (length > 4) ? packet[4] : (byte)0;
                        CallCleared?.Invoke(lcn, cause, diag);

                        // Remove channel
                        _channels.Remove(channel);
                    }
                    break;

                case PacketType.CLEAR_CONFIRMATION:
                    _logger.LogInformation($"Processing CLEAR_CONFIRMATION for LCN {lcn}");
                    if (channel != null)
                    {
                        channel.State = ChannelState.READY;
                    }
                    break;

                case PacketType.DATA:
                    _logger.LogInformation($"Processing DATA for LCN {lcn}");
                    
                    // Auto-create channel if it doesn't exist (for PVC or test scenarios)
                    if (channel == null)
                    {
                        _logger.LogDebug($"Auto-creating channel for DATA packet on LCN {lcn}");
                        channel = CreateChannel(lcn);
                        if (channel != null)
                        {
                            channel.State = ChannelState.DATA_TRANSFER;
                            // Initialize sequence numbers to match incoming packet
                            byte ps = (byte)((packet[2] >> 1) & 0x07);
                            channel.PR = ps; // Set expected receive sequence to match incoming packet
                            channel.PS = 0; // Reset send sequence
                        }
                    }
                    
                    if (channel != null && channel.State == ChannelState.DATA_TRANSFER)
                    {
                        // Extract sequence numbers
                        byte pr = (byte)((packet[2] >> 4) & 0x07);
                        byte ps = (byte)((packet[2] >> 1) & 0x07);

                        // Check sequence
                        if (ps == channel.PR)
                        {
                            channel.PR = (byte)((channel.PR + 1) & 0x07);

                            // Extract data
                            if (length > 3)
                            {
                                byte[] data = new byte[length - 3];
                                Array.Copy(packet, 3, data, 0, length - 3);

                                // Notify higher layer
                                DataReceived?.Invoke(lcn, data, (ushort)(length - 3));
                            }

                            // Send acknowledgment
                            SendPacket(lcn, PacketType.RR, Array.Empty<byte>(), 0);
                        }
                        else
                        {
                            // Out of sequence - send REJ
                            SendPacket(lcn, PacketType.REJ, Array.Empty<byte>(), 0);
                        }
                    }
                    break;

                case PacketType.RR:
                case PacketType.RNR:
                case PacketType.REJ:
                    _logger.LogInformation($"Processing flow control packet for LCN {lcn}");
                    if (channel != null)
                    {
                        byte pr = (byte)((packet[2] >> 4) & 0x07);

                        switch (type)
                        {
                            case (byte)PacketType.RR:
                                channel.RemoteBusy = false;
                                break;
                            case (byte)PacketType.RNR:
                                channel.RemoteBusy = true;
                                break;
                            case (byte)PacketType.REJ:
                                // Reset send sequence
                                channel.PS = pr;
                                channel.RemoteBusy = false;
                                break;
                        }
                    }
                    break;

                case PacketType.RESET_REQUEST:
                    _logger.LogInformation($"Processing RESET_REQUEST for LCN {lcn}");
                    if (channel != null)
                    {
                        // Reset sequence numbers
                        channel.PR = 0;
                        channel.PS = 0;
                        channel.LocalBusy = false;
                        channel.RemoteBusy = false;

                        // Send reset confirmation
                        SendPacket(lcn, PacketType.RESET_CONFIRMATION, Array.Empty<byte>(), 0);

                        // Notify higher layer
                        byte cause = (length > 3) ? packet[3] : (byte)0;
                        byte diag = (length > 4) ? packet[4] : (byte)0;
                        CallReset?.Invoke(lcn, cause, diag);
                    }
                    break;

                case PacketType.RESTART_REQUEST:
                    _logger.LogInformation("Processing RESTART_REQUEST");
                    // Reset all channels
                    foreach (var ch in _channels)
                    {
                        ch.State = ChannelState.READY;
                        ch.PR = 0;
                        ch.PS = 0;
                        ch.LocalBusy = false;
                        ch.RemoteBusy = false;
                    }

                    // Send restart confirmation
                    SendPacket(0, PacketType.RESTART_CONFIRMATION, Array.Empty<byte>(), 0);

                    // Notify higher layer
                    byte restartCause = (length > 3) ? packet[3] : (byte)0;
                    byte restartDiag = (length > 4) ? packet[4] : (byte)0;
                    RestartRequested?.Invoke(restartCause, restartDiag);
                    break;
            }
        }

        /// <summary>
        /// Accept an incoming call
        /// </summary>
        public void AcceptCall(ushort lcn)
        {
            Channel? channel = FindChannel(lcn);
            if (channel != null && channel.State == ChannelState.INCOMING_CALL)
            {
                channel.State = ChannelState.DATA_TRANSFER;
                SendPacket(lcn, PacketType.CALL_ACCEPTED, Array.Empty<byte>(), 0);
                _logger.LogInformation($"Call accepted on LCN {lcn}");
            }
        }

        /// <summary>
        /// Send data on a channel
        /// </summary>
        public void SendData(ushort lcn, byte[] data, ushort length)
        {
            Channel? channel = FindChannel(lcn);
            if (channel != null && channel.State == ChannelState.DATA_TRANSFER && !channel.RemoteBusy)
            {
                SendPacket(lcn, PacketType.DATA, data, length);
            }
        }

        /// <summary>
        /// Clear a call
        /// </summary>
        public void ClearCall(ushort lcn)
        {
            Channel? channel = FindChannel(lcn);
            if (channel != null)
            {
                channel.State = ChannelState.CLEAR_REQUEST_SENT;
                SendPacket(lcn, PacketType.CLEAR_REQUEST, Array.Empty<byte>(), 0);
            }
        }

        /// <summary>
        /// Get all channels
        /// </summary>
        public List<Channel> GetChannels()
        {
            return new List<Channel>(_channels);
        }

        /// <summary>
        /// Get channel count
        /// </summary>
        public int GetChannelCount()
        {
            return _channels.Count;
        }

        /// <summary>
        /// Process any pending operations
        /// </summary>
        public void Process()
        {
            // Currently no background processing needed for X.25
            // This method is called from the main loop for future extensibility
        }



        /// <summary>
        /// Validates X.25 packet structure
        /// </summary>
        /// <param name="packetData">The X.25 packet data (starting from GFI+LCN)</param>
        /// <param name="length">Length of the packet data</param>
        /// <returns>True if X.25 structure is valid, false otherwise</returns>
        public bool ValidatePacketStructure(byte[] packetData, ushort length)
        {
            // Minimum X.25 packet: GFI+LCN(2) + Type(1) + Data(0)
            if (length < 3) return false;

            // Extract LCN from GFI+LCN field
            ushort lcn = (ushort)(((packetData[0] & 0x0F) << 8) | packetData[1]);

            // Validate LCN range (typically 1-4095)
            if (lcn == 0 || lcn > 4095)
            {
                _logger.LogWarning($"Invalid LCN: {lcn}");
                return false;
            }

            // Extract packet type
            byte packetType = (byte)(packetData[2] & 0x1F);

            // Validate packet type
            bool isValidType = packetType switch
            {
                0x00 => true, // DATA
                0x01 => true, // RR
                0x05 => true, // RNR
                0x09 => true, // REJ
                0x0B => true, // CALL_REQUEST
                0x0F => true, // CALL_ACCEPTED
                0x13 => true, // CLEAR_REQUEST
                0x17 => true, // CLEAR_CONFIRMATION
                0x1B => true, // RESET_REQUEST
                0x1F => true, // RESET_CONFIRMATION
                0x23 => true, // INTERRUPT
                0x27 => true, // INTERRUPT_CONF
                0xFB => true, // RESTART_REQUEST
                0xFF => true, // RESTART_CONFIRMATION
                _ => false
            };

            if (!isValidType)
            {
                _logger.LogWarning($"Invalid packet type: 0x{packetType:X2}");
                return false;
            }

            return true;
        }
    }
}
