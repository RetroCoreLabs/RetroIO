using System;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Represents an X.25 packet with full header and data
    /// </summary>
    public class X25Packet
    {
        /// <summary>
        /// General Format Identifier (GFI)
        /// </summary>
        public byte GFI { get; set; }

        /// <summary>
        /// Logical Channel Number (LCN)
        /// </summary>
        public ushort LCN { get; set; }

        /// <summary>
        /// Packet Type Identifier (PTI) as raw byte value.
        /// For new code, prefer using the PacketTypeId property with descriptive enum values.
        /// </summary>
        public byte PTI { get; set; }

        /// <summary>
        /// Packet Type Identifier with descriptive enum names and documentation.
        /// This is the preferred way to set packet types in new code.
        /// Automatically synchronizes with the PTI byte property.
        /// </summary>
        public X25PacketTypeIdentifier PacketTypeId
        {
            get => (X25PacketTypeIdentifier)PTI;
            set => PTI = (byte)value;
        }

        /// <summary>
        /// Packet data (excluding header)
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Full packet bytes (including header)
        /// </summary>
        public byte[] FullPacket { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Packet length
        /// </summary>
        public int Length { get; set; }

        /// <summary>
        /// Timestamp when packet was created/processed
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Direction (TX/RX)
        /// </summary>
        public PacketDirection Direction { get; set; }

        /// <summary>
        /// Packet type derived from PTI
        /// </summary>
        public X25PacketType PacketType => GetPacketType(PTI);

        /// <summary>
        /// Q-bit from GFI
        /// </summary>
        public bool QBit => (GFI & 0x80) != 0;

        /// <summary>
        /// D-bit from GFI
        /// </summary>
        public bool DBit => (GFI & 0x40) != 0;

        /// <summary>
        /// Modulo from GFI
        /// </summary>
        public byte Modulo => (byte)((GFI >> 4) & 0x03);

        /// <summary>
        /// Logical Channel Group from GFI
        /// </summary>
        public byte LCG => (byte)(GFI & 0x07);

        /// <summary>
        /// Logical Channel Identifier (LCI) combining Logical Channel Group and Number.
        /// This is the 12-bit channel id used for routing (LCI = (LCG << 8) | (LCN & 0xFF)).
        /// Many call this value simply "LCN" in code; this property makes it explicit.
        /// </summary>
        public ushort LCI => (ushort)((LCG << 8) | (LCN & 0xFF));

        /// <summary>
        /// M-bit (More data) indicator for data packets (Modulo 8 encoding).
        /// For non-data packets this value is not meaningful and returns false.
        /// </summary>
        public bool MBit => (PTI & 0x10) != 0 && ((PTI & 0x01) == 0);

        /// <summary>
        /// Default constructor
        /// </summary>
        public X25Packet()
        {
            GFI = 0;
            LCN = 0;
            PTI = 0;
            Data = Array.Empty<byte>();
            FullPacket = Array.Empty<byte>();
            Length = 0;
            Timestamp = DateTime.UtcNow;
            Direction = PacketDirection.Receive;
        }

        /// <summary>
        /// Create X.25 packet from components
        /// </summary>
        public X25Packet(byte gfi, ushort lcn, byte pti, byte[]? data = null)
        {
            GFI = gfi;
            LCN = lcn;
            PTI = pti;
            Data = data ?? Array.Empty<byte>();
            
            // Build full packet
            BuildFullPacket();
        }

        /// <summary>
        /// Create X.25 packet from raw bytes
        /// </summary>
        public X25Packet(byte[] packetData)
        {
            if (packetData == null || packetData.Length < 3)
                throw new ArgumentException("Packet data must be at least 3 bytes", nameof(packetData));

            FullPacket = packetData;
            Length = packetData.Length;
            GFI = packetData[0];
            LCN = ExtractLCN(packetData[0], packetData[1]);
            PTI = packetData[2];
            
            if (packetData.Length > 3)
            {
                Data = new byte[packetData.Length - 3];
                Array.Copy(packetData, 3, Data, 0, Data.Length);
            }
        }

        /// <summary>
        /// Extract LCN from GFI and LCN bytes
        /// </summary>
        private static ushort ExtractLCN(byte gfi, byte lcn)
        {
            byte lcg = (byte)(gfi & 0x0F);   // four bits (X.25 5.1.2); was 0x07 until 2026-09-06
            return (ushort)((lcg << 8) | lcn);
        }

        /// <summary>
        /// Build full packet from components
        /// </summary>
        private void BuildFullPacket()
        {
            int totalLength = 3 + Data.Length;
            FullPacket = new byte[totalLength];
            
            FullPacket[0] = GFI;
            FullPacket[1] = (byte)(LCN & 0xFF);
            FullPacket[2] = PTI;
            
            if (Data.Length > 0)
            {
                Array.Copy(Data, 0, FullPacket, 3, Data.Length);
            }
            
            Length = FullPacket.Length;
        }

        /// <summary>
        /// Get packet type from PTI according to ITU-T X.25 standard
        /// Handles all X.25 packet types including variable PTI values for data/flow control
        /// </summary>
        private static X25PacketType GetPacketType(byte pti)
        {
            // Data packets: LSB = 0 and not supervisory (control) packets
            if ((pti & 0x01) == 0)
            {
                return X25PacketType.Data;
            }

            // Supervisory (Flow Control) packets: Check base type (bits 1-4)
            byte baseType = (byte)(pti & 0x1F);
            switch (baseType)
            {
                case 0x01: return X25PacketType.RR;   // RR with any P(R)
                case 0x05: return X25PacketType.RNR;  // RNR with any P(R)
                case 0x09: return X25PacketType.REJ;  // REJ with any P(R)
            }

            // Fixed PTI values for other packet types
            return pti switch
            {
                0x0B => X25PacketType.CallRequest,
                0x0F => X25PacketType.CallAccepted,
                0x13 => X25PacketType.ClearRequest,
                0x17 => X25PacketType.ClearConfirmation,
                0x1B => X25PacketType.ResetRequest,
                0x1F => X25PacketType.ResetConfirmation,
                0x23 => X25PacketType.Interrupt,
                0x27 => X25PacketType.InterruptConfirmation,
                0xFB => X25PacketType.RestartRequest,
                0xFF => X25PacketType.RestartConfirmation,
                0xF3 => X25PacketType.RegistrationRequest,
                0xF7 => X25PacketType.RegistrationConfirmation,
                0xF1 => X25PacketType.Diagnostic,
                _ => X25PacketType.Unknown
            };
        }

        /// <summary>
        /// Get string representation with detailed packet-specific information
        /// </summary>
        public override string ToString()
        {
            var modulo = Modulo switch { 0x01 => "8", 0x02 => "128", _ => $"Reserved({Modulo})" };
            var lci = (ushort)((LCG << 8) | (LCN & 0xFF));
            
            // Base packet information
            var result = $"X25[{PacketType} ({PacketTypeId.GetDescription()}) - 0x{PTI:X2}] LCI={lci} Q={QBit} D={DBit} Mod={modulo}";
            
            // Add packet-specific details
            switch (PacketType)
            {
                case X25PacketType.Data:
                    var ps = (PTI >> 1) & 0x07;
                    var pr = (PTI >> 5) & 0x07;
                    var mBit = (PTI & 0x10) != 0;
                    result += $" P(S)={ps} P(R)={pr} M={mBit} DataLen={Data.Length}";
                    if (Data.Length > 0 && Data.Length <= 32)
                    {
                        result += $" Data=[{string.Join(" ", Data.Select(b => $"{b:X2}"))}]";
                    }
                    else if (Data.Length > 32)
                    {
                        result += $" Data=[{string.Join(" ", Data.Take(16).Select(b => $"{b:X2}"))}...{string.Join(" ", Data.Skip(Data.Length - 16).Select(b => $"{b:X2}"))}]";
                    }
                    break;

                case X25PacketType.RR:
                case X25PacketType.RNR:
                case X25PacketType.REJ:
                    var flowPR = (PTI >> 5) & 0x07;
                    result += $" P(R)={flowPR}";
                    break;

                case X25PacketType.CallRequest:
                case X25PacketType.CallAccepted:
                    result += AnalyzeCallPacket();
                    break;

                case X25PacketType.ClearRequest:
                    result += AnalyzeClearPacket();
                    break;

                case X25PacketType.ResetRequest:
                    result += AnalyzeResetPacket();
                    break;

                case X25PacketType.Diagnostic:
                    result += AnalyzeDiagnosticPacket();
                    break;

                case X25PacketType.Interrupt:
                    if (Data.Length > 0)
                    {
                        result += $" InterruptData=[{string.Join(" ", Data.Select(b => $"{b:X2}"))}]";
                    }
                    break;
            }
            
            result += $" Len={Length}";
            return result;
        }

        /// <summary>
        /// Analyze Call Request/Accept packet data
        /// </summary>
        private string AnalyzeCallPacket()
        {
            if (Data.Length < 2) return " InvalidCall";
            
            var result = "";
            int offset = 0;
            
            // Address lengths
            byte addressLengths = Data[offset++];
            byte callingAddrLen = (byte)(addressLengths >> 4);
            byte calledAddrLen = (byte)(addressLengths & 0x0F);
            
            result += $" CalledLen={calledAddrLen} CallingLen={callingAddrLen}";
            
            // Extract addresses
            if (offset < Data.Length)
            {
                int totalAddrBytes = (calledAddrLen + callingAddrLen + 1) / 2;
                if (offset + totalAddrBytes <= Data.Length)
                {
                    var addressData = new byte[totalAddrBytes];
                    Array.Copy(Data, offset, addressData, 0, totalAddrBytes);
                    
                    // Extract called address
                    if (calledAddrLen > 0)
                    {
                        var calledAddr = ExtractBCDAddress(addressData, 0, calledAddrLen);
                        result += $" Called={calledAddr}";
                    }
                    
                    // Extract calling address  
                    if (callingAddrLen > 0)
                    {
                        var callingAddr = ExtractBCDAddress(addressData, calledAddrLen, callingAddrLen);
                        result += $" Calling={callingAddr}";
                    }
                    
                    offset += totalAddrBytes;
                }
            }
            
            // Facilities length
            if (offset < Data.Length)
            {
                byte facilitiesLen = Data[offset++];
                result += $" FacLen={facilitiesLen}";
                offset += facilitiesLen;
            }
            
            // Call User Data (CUD)
            if (offset < Data.Length)
            {
                var cudLen = Data.Length - offset;
                var cud = new byte[cudLen];
                Array.Copy(Data, offset, cud, 0, cudLen);
                result += AnalyzeCudData(cud);
            }
            
            return result;
        }

        /// <summary>
        /// Extract BCD-encoded address from byte array
        /// </summary>
        private static string ExtractBCDAddress(byte[] data, int startDigit, int digitCount)
        {
            var address = "";
            for (int i = 0; i < digitCount; i++)
            {
                int byteIndex = (startDigit + i) / 2;
                if (byteIndex >= data.Length) break;
                
                int nibble = ((startDigit + i) % 2 == 0) ? 
                    (data[byteIndex] & 0x0F) : 
                    (data[byteIndex] >> 4);
                    
                address += nibble.ToString();
            }
            return address;
        }

        /// <summary>
        /// Analyze Call User Data for protocol identification
        /// </summary>
        private string AnalyzeCudData(byte[] cud)
        {
            var result = $" CUD({cud.Length})=[{string.Join(" ", cud.Select(b => $"{b:X2}"))}]";
            
            // Check for XMSG protocol
            var version = XmsgProtocolHelper.ExtractVersionFromCud(cud);
            if (version.HasValue)
            {
                result += $" XMSG:{version.Value}";
                var port = XmsgProtocolHelper.ExtractPortFromCud(cud);
                if (port.HasValue)
                {
                    var servicePort = (XmsgServicePort)port.Value;
                    result += $" Port:0x{port:X4}({servicePort.GetServiceDescription()})";
                }
            }
            
            return result;
        }

        /// <summary>
        /// Analyze Clear Request packet
        /// </summary>
        private string AnalyzeClearPacket()
        {
            if (Data.Length >= 2)
            {
                return $" Cause=0x{Data[0]:X2} Diagnostic=0x{Data[1]:X2}";
            }
            return "";
        }

        /// <summary>
        /// Analyze Reset Request packet
        /// </summary>
        private string AnalyzeResetPacket()
        {
            if (Data.Length >= 2)
            {
                return $" Cause=0x{Data[0]:X2} Diagnostic=0x{Data[1]:X2}";
            }
            return "";
        }

        /// <summary>
        /// Analyze Diagnostic packet
        /// </summary>
        private string AnalyzeDiagnosticPacket()
        {
            if (Data.Length >= 1)
            {
                return $" DiagCode=0x{Data[0]:X2}";
            }
            return "";
        }
    }

    /// <summary>
    /// X.25 Packet Type Classification according to ITU-T X.25 standard
    /// NOTE: Many packet types have variable PTI values based on sequence numbers
    /// Use X25PacketTypeHelper for accurate packet type detection
    /// </summary>
    public enum X25PacketType
    {
        /// <summary>
        /// Unknown packet type
        /// </summary>
        Unknown,

        // Data transfer packets

        /// <summary>
        /// Data Packet (Modulo 8: 0x00-0xEF with P(S) in bits 1-3, P(R) in bits 5-7)
        /// (Modulo 128: PTI=0x00, followed by separate P(S) and P(R) bytes)
        /// Alternative names: Data, DTE Data, DCE Data
        /// Carries user data over established virtual circuit
        /// </summary>
        Data,

        // Flow control packets (Variable PTI based on P(R))

        /// <summary>
        /// Receive Ready (Modulo 8: 0x01, 0x21, 0x41, 0x61, 0x81, 0xA1, 0xC1, 0xE1)
        /// (PTI = 0x01 | (P(R) << 5))
        /// Alternative names: RR, DTE RR, DCE RR  
        /// Acknowledges received data packets and indicates readiness for more
        /// </summary>
        RR,

        /// <summary>
        /// Receive Not Ready (Modulo 8: 0x05, 0x25, 0x45, 0x65, 0x85, 0xA5, 0xC5, 0xE5)
        /// (PTI = 0x05 | (P(R) << 5))
        /// Alternative names: RNR, DTE RNR, DCE RNR
        /// Acknowledges received data but indicates temporary inability to receive more
        /// </summary>
        RNR,

        /// <summary>
        /// Reject (Modulo 8: 0x09, 0x29, 0x49, 0x69, 0x89, 0xA9, 0xC9, 0xE9)
        /// (PTI = 0x09 | (P(R) << 5))
        /// Alternative names: REJ, Reject, DTE REJ
        /// Requests retransmission of data packets starting from specified sequence number
        /// </summary>
        REJ,

        // Call establishment and clearing packets (Fixed PTI values)

        /// <summary>
        /// Call Request (PTI = 0x0B)
        /// Alternative names: Call, Call Request, Incoming Call
        /// Used to initiate a virtual circuit
        /// </summary>
        CallRequest,

        /// <summary>
        /// Call Accepted (PTI = 0x0F) 
        /// Alternative names: Call Confirm, Call Accepted, Call Connected
        /// Response to Call Request indicating call acceptance
        /// </summary>
        CallAccepted,

        /// <summary>
        /// Clear Request (PTI = 0x13)
        /// Alternative names: Clear, Clear Request, Clear Indication
        /// Used to clear a virtual circuit
        /// </summary>
        ClearRequest,

        /// <summary>
        /// Clear Confirmation (PTI = 0x17)
        /// Alternative names: Clear Confirm, DTE Clear Confirmation, DCE Clear Confirmation
        /// Response to Clear Request confirming circuit clearing
        /// </summary>
        ClearConfirmation,

        // Interrupt packets

        /// <summary>
        /// Interrupt (PTI = 0x23)
        /// Alternative names: Interrupt, DTE Interrupt, DCE Interrupt
        /// Sends out-of-band interrupt data
        /// </summary>
        Interrupt,

        /// <summary>
        /// Interrupt Confirmation (PTI = 0x27)
        /// Alternative names: Interrupt Confirm, DTE Interrupt Confirmation, DCE Interrupt Confirmation  
        /// Acknowledges receipt of Interrupt packet
        /// </summary>
        InterruptConfirmation,

        // Reset packets

        /// <summary>
        /// Reset Request (PTI = 0x1B)
        /// Alternative names: Reset, Reset Request, Reset Indication
        /// Resets a virtual circuit to initial state
        /// </summary>
        ResetRequest,

        /// <summary>
        /// Reset Confirmation (PTI = 0x1F)
        /// Alternative names: Reset Confirm, DTE Reset Confirmation, DCE Reset Confirmation
        /// Acknowledges Reset Request
        /// </summary>
        ResetConfirmation,

        // Restart packets (affects all virtual circuits)

        /// <summary>
        /// Restart Request (PTI = 0xFB)
        /// Alternative names: Restart, Restart Request, Restart Indication
        /// Restarts all virtual circuits on the interface
        /// </summary>
        RestartRequest,

        /// <summary>
        /// Restart Confirmation (PTI = 0xFF)
        /// Alternative names: Restart Confirm, DTE Restart Confirmation, DCE Restart Confirmation
        /// Acknowledges Restart Request
        /// </summary>
        RestartConfirmation,

        // Registration packets (optional, used for address registration)

        /// <summary>
        /// Registration Request (PTI = 0xF3)
        /// Used in some X.25 implementations for dynamic address registration
        /// </summary>
        RegistrationRequest,

        /// <summary>
        /// Registration Confirmation (PTI = 0xF7)
        /// Response to Registration Request
        /// </summary>
        RegistrationConfirmation,

        // Diagnostic packet

        /// <summary>
        /// Diagnostic (PTI = 0xF1)
        /// Provides diagnostic information about network conditions
        /// </summary>
        Diagnostic
    }

    /// <summary>
    /// Packet direction
    /// </summary>
    public enum PacketDirection
    {
        /// <summary>
        /// Transmitted
        /// </summary>
        Transmit = 0,

        /// <summary>
        /// Received
        /// </summary>
        Receive = 1
    }
}
