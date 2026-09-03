using System;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// X.25-specific history item
    /// </summary>
    public class X25HistoryItem : HistoryItem
    {
        /// <summary>
        /// X.25 packet
        /// </summary>
        public X25Packet Packet { get; set; }

        /// <summary>
        /// Packet ID (for tracking)
        /// </summary>
        public long PacketId { get; set; }

        /// <summary>
        /// Create X.25 history item
        /// </summary>
        public X25HistoryItem(Direction direction, X25Packet packet, long packetId, string? description = null)
            : base(LayerType.X25, direction, packet?.FullPacket ?? Array.Empty<byte>(), 
                  description ?? $"X.25 {packet?.PacketType} Packet")
        {
            Packet = packet ?? throw new ArgumentNullException(nameof(packet));
            PacketId = packetId;
        }

        /// <summary>
        /// Get summary string with detailed X.25 header information
        /// </summary>
        public override string GetSummary()
        {
            if (Packet == null)
                return $"X25[{Direction}] PacketId={PacketId} Len={Length}";

            // Extract detailed header information according to X.25 spec
            string packetTypeDetail = GetPacketTypeDetail();
            string headerDetail = GetHeaderDetail();
            
            return $"X25[{packetTypeDetail}][{Direction}] {headerDetail} PacketId={PacketId} Len={Length}";
        }
        
        /// <summary>
        /// Get detailed packet type with hex PTI
        /// </summary>
        private string GetPacketTypeDetail()
        {
            return $"{Packet.PacketType} (0x{Packet.PTI:X2})";
        }
        
        /// <summary>
        /// Get detailed header information
        /// </summary>
        private string GetHeaderDetail()
        {
            var details = $"LCI={Packet.LCI} (LCG={Packet.LCG}, LCN={Packet.LCN & 0xFF})";
            details += $" Q={Packet.QBit} D={Packet.DBit} Mod={GetModulo()}";
            
            return details;
        }
        
        /// <summary>
        /// Get modulo value from GFI
        /// </summary>
        private string GetModulo()
        {
            byte modulo = Packet.Modulo;
            return modulo switch
            {
                0x01 => "8",
                0x02 => "128", 
                _ => $"Reserved({modulo})"
            };
        }

        /// <summary>
        /// Get packet data as hex string
        /// </summary>
        public string GetPacketDataHex()
        {
            if (Packet?.Data == null || Packet.Data.Length == 0)
                return "";
                
            return BitConverter.ToString(Packet.Data).Replace("-", " ");
        }

        /// <summary>
        /// Get packet data as string (if printable)
        /// </summary>
        public string GetPacketDataString()
        {
            if (Packet?.Data == null || Packet.Data.Length == 0)
                return "";
                
            try
            {
                return System.Text.Encoding.ASCII.GetString(Packet.Data);
            }
            catch
            {
                return GetPacketDataHex();
            }
        }
        
        /// <summary>
        /// Get detailed packet-specific information
        /// </summary>
        public string GetPacketSpecificDetails()
        {
            if (Packet?.FullPacket == null || Packet.FullPacket.Length < 3)
                return "Invalid packet";
                
            try
            {
                var info = X25PacketParser.ParsePacket(Packet.FullPacket);
                return GetDetailedPacketInfo(info);
            }
            catch (Exception ex)
            {
                return $"Parse error: {ex.Message}";
            }
        }
        
        /// <summary>
        /// Get detailed packet information based on packet type
        /// </summary>
        private string GetDetailedPacketInfo(X25PacketInfo info)
        {
            var details = new System.Text.StringBuilder();
            
            switch (info.PacketType)
            {
                case X25PacketType.CallRequest:
                    details.AppendLine($"Call Request Packet:");
                    details.AppendLine($"  Called Address: {info.CalledAddress}");
                    details.AppendLine($"  Calling Address: {info.CallingAddress}");
                    details.AppendLine($"  Facilities: {info.Facilities.Length} bytes");
                    if (info.Facilities.Length > 0)
                    {
                        details.AppendLine($"    Facilities Hex: {BitConverter.ToString(info.Facilities)}");
                    }
                    details.AppendLine($"  Call User Data (CUD): {info.CallUserData.Length} bytes");
                    if (info.CallUserData.Length > 0)
                    {
                        details.AppendLine($"    CUD Hex: {BitConverter.ToString(info.CallUserData).Replace("-", " ")}");
                        var protocol = IdentifyProtocolFromCUD(info.CallUserData);
                        details.AppendLine($"    Protocol: {protocol}");
                    }
                    break;
                    
                case X25PacketType.CallAccepted:
                    details.AppendLine($"Call Accepted Packet:");
                    details.AppendLine($"  Facilities: {info.Facilities.Length} bytes");
                    details.AppendLine($"  Response User Data: {info.CallUserData.Length} bytes");
                    break;
                    
                case X25PacketType.Data:
                    details.AppendLine($"Data Packet:");
                    details.AppendLine($"  P(S): {info.PS}, P(R): {info.PR}");
                    details.AppendLine($"  M-bit: {info.MBit} (More data: {(info.MBit ? "Yes" : "No")})");
                    details.AppendLine($"  User Data: {info.UserData.Length} bytes");
                    if (info.UserData.Length > 0 && info.UserData.Length <= 32)
                    {
                        details.AppendLine($"    Data Hex: {BitConverter.ToString(info.UserData).Replace("-", " ")}");
                        if (IsTextData(info.UserData))
                        {
                            details.AppendLine($"    Data Text: {System.Text.Encoding.ASCII.GetString(info.UserData)}");
                        }
                    }
                    break;
                    
                case X25PacketType.RR:
                    details.AppendLine($"RR (Receive Ready) Packet:");
                    details.AppendLine($"  P(R): {info.PR} (Next expected sequence)");
                    break;
                    
                case X25PacketType.RNR:
                    details.AppendLine($"RNR (Receive Not Ready) Packet:");
                    details.AppendLine($"  P(R): {info.PR} (Next expected sequence)");
                    break;
                    
                case X25PacketType.ClearRequest:
                    details.AppendLine($"Clear Request Packet:");
                    details.AppendLine($"  Clearing Cause: 0x{info.ClearingCause:X2} ({GetClearCauseDescription(info.ClearingCause)})");
                    details.AppendLine($"  Diagnostic Code: 0x{info.DiagnosticCode:X2}");
                    if (info.ClearUserData.Length > 0)
                    {
                        details.AppendLine($"  Clear User Data: {BitConverter.ToString(info.ClearUserData).Replace("-", " ")}");
                    }
                    break;
                    
                case X25PacketType.ResetRequest:
                    details.AppendLine($"Reset Request Packet:");
                    details.AppendLine($"  Reset Cause: 0x{info.ResetCause:X2}");
                    details.AppendLine($"  Diagnostic Code: 0x{info.DiagnosticCode:X2}");
                    break;
                    
                case X25PacketType.Interrupt:
                    details.AppendLine($"Interrupt Packet:");
                    details.AppendLine($"  Interrupt Data: {info.InterruptData.Length} bytes");
                    if (info.InterruptData.Length > 0)
                    {
                        details.AppendLine($"    Data Hex: {BitConverter.ToString(info.InterruptData).Replace("-", " ")}");
                    }
                    break;
                    
                default:
                    details.AppendLine($"{info.GetPacketTypeName()} Packet");
                    break;
            }
            
            return details.ToString();
        }
        
        /// <summary>
        /// Identify protocol from Call User Data
        /// </summary>
        private string IdentifyProtocolFromCUD(byte[] cud)
        {
            if (cud == null || cud.Length == 0)
                return "None";
                
            // PAD Protocol: 0x01 0x00 0x00 0x00
            if (cud.Length >= 4 && cud[0] == 0x01 && cud[1] == 0x00 && cud[2] == 0x00 && cud[3] == 0x00)
                return "X.29 PAD";
                
            // XMSG Protocol: Check for version 0x4C03 and port in range 0x1000-0x1FFF
            if (cud.Length >= 6 && cud[0] == 0x4C && cud[1] == 0x03)
            {
                if (cud.Length >= 6)
                {
                    ushort port = (ushort)((cud[4] << 8) | cud[5]);
                    if (port >= 0x1000 && port <= 0x1FFF)
                        return $"XMSG (Port {port:X4})";
                }
                return "XMSG";
            }
                
            // IP Protocol
            if (cud.Length >= 1 && cud[0] == 0xCC)
                return "IP";
                
            // CLNP Protocol  
            if (cud.Length >= 1 && cud[0] == 0x81)
                return "CLNP";
                
            // TAD or other
            return "Unknown/TAD";
        }
        
        /// <summary>
        /// Check if data appears to be text
        /// </summary>
        private bool IsTextData(byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            
            foreach (byte b in data)
            {
                if (b < 32 && b != 10 && b != 13 && b != 9) // Allow CR, LF, TAB
                    return false;
                if (b > 126)
                    return false;
            }
            return true;
        }
        
        /// <summary>
        /// Get description for clear cause codes
        /// </summary>
        private string GetClearCauseDescription(byte cause)
        {
            return cause switch
            {
                0x00 => "DTE originated",
                0x01 => "Number busy", 
                0x02 => "Out of order",
                0x03 => "Remote procedure error",
                0x04 => "Reverse charging acceptance not subscribed",
                0x05 => "Incompatible destination",
                0x06 => "Fast select acceptance not subscribed",
                0x09 => "Invalid facility request",
                0x0B => "Access barred",
                0x0D => "Local procedure error",
                0x11 => "Network congestion",
                0x13 => "Not obtainable",
                0x15 => "RPOA out of order",
                _ => $"Reserved/Unknown"
            };
        }
    }
}
