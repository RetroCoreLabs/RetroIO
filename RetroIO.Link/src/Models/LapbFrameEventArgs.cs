using System;
using System.Text;

namespace RetroIO.Link.Models
{
    /// <summary>
    /// Event arguments for LAPB frame events
    /// </summary>
    public class LapbFrameEventArgs : EventArgs
    {
        /// <summary>
        /// LAPB frame data
        /// </summary>
        public byte[] FrameData { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Frame length
        /// </summary>
        public int Length { get; set; }

        /// <summary>
        /// Frame direction (TX/RX)
        /// </summary>
        public FrameDirection Direction { get; set; }

        /// <summary>
        /// Frame timestamp
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Frame type (I/S/U)
        /// </summary>
        public LapbFrameType FrameType { get; set; }

        /// <summary>
        /// Address field
        /// </summary>
        public byte Address { get; set; }

        /// <summary>
        /// Control field
        /// </summary>
        public byte Control { get; set; }

        /// <summary>
        /// Information field (for I-frames)
        /// </summary>
        public byte[] Information { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// N(S) - Send sequence number (for I-frames)
        /// </summary>
        public byte? NS { get; set; }

        /// <summary>
        /// N(R) - Receive sequence number (for I/S-frames)
        /// </summary>
        public byte? NR { get; set; }

        /// <summary>
        /// P/F bit
        /// </summary>
        public bool PF { get; set; }

        /// <summary>
        /// Frame ID (shared counter)
        /// </summary>
        public long FrameId { get; set; }

        /// <summary>
        /// Create LAPB frame event arguments
        /// </summary>
        public LapbFrameEventArgs()
        {
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Get frame as hex string
        /// </summary>
        public string GetFrameHex()
        {
            return BitConverter.ToString(FrameData, 0, Math.Min(Length, FrameData.Length)).Replace("-", " ");
        }

        /// <summary>
        /// Get frame summary with descriptive information
        /// </summary>
        public string GetFrameSummary()
        {
            // Address field description
            var addressDesc = GetAddressFieldDescription(Address);
            
            // Control field description using enums
            var controlDesc = GetControlFieldDescription(Control);
            
            string summary = $"LAPB[{FrameType} ({controlDesc}) - 0x{Control:X2}][{Direction}] Addr={addressDesc}";
            
            if (NS.HasValue)
                summary += $" N(S)={NS.Value}";
            
            if (NR.HasValue)
                summary += $" N(R)={NR.Value}";
            
            if (PF)
                summary += " P/F";
            
            summary += $" Len={Length}";
            
            // Show information field for I-frames
            if (FrameType == LapbFrameType.Information && Information.Length > 0)
            {
                if (Information.Length <= 16)
                {
                    summary += $" Info=[{string.Join(" ", Information.Select(b => $"{b:X2}"))}]";
                }
                else
                {
                    summary += $" Info=[{string.Join(" ", Information.Take(8).Select(b => $"{b:X2}"))}...{Information.Length} bytes]";
                }
            }
            
            return summary;
        }

        /// <summary>
        /// Get detailed frame information for export
        /// </summary>
        public string GetDetailedFrameInfo()
        {
            var details = new StringBuilder();
            
            details.AppendLine($"Frame Type: {FrameType} - {GetFrameTypeDescription()}");
            details.AppendLine($"Direction: {Direction} (Transmitted/Received)");
            details.AppendLine();
            
            details.AppendLine("Layer 2 (LAPB) Details:");
            details.AppendLine($"  Address: 0x{Address:X2} - {GetAddressFieldDescription(Address)}");
            details.AppendLine($"  Control: 0x{Control:X2} - {GetControlFieldDescription(Control)}");
            
            if (NS.HasValue)
                details.AppendLine($"  N(S): {NS} (Send sequence number)");
            if (NR.HasValue)
                details.AppendLine($"  N(R): {NR} (Receive sequence number)");
            if (PF)
                details.AppendLine($"  P/F: {PF} (Poll/Final bit set)");
            
            // Information field details
            if (Information.Length > 0)
            {
                details.AppendLine($"  Information Field: {Information.Length} bytes");
                if (Information.Length <= 32)
                {
                    details.AppendLine($"    Hex: {string.Join(" ", Information.Select(b => $"{b:X2}"))}");
                    if (IsTextData(Information))
                    {
                        var text = System.Text.Encoding.ASCII.GetString(Information);
                        details.AppendLine($"    ASCII: {text}");
                    }
                }
            }
            
            return details.ToString();
        }

        /// <summary>
        /// Get frame type description
        /// </summary>
        private string GetFrameTypeDescription()
        {
            // Use enum description if available
            if (Enum.IsDefined(typeof(LapbControlField), Control))
            {
                var controlField = (LapbControlField)Control;
                return controlField.GetDescription();
            }
            
            // Fallback descriptions
            return FrameType switch
            {
                LapbFrameType.Information => "Information Frame - Contains user data with sequence numbers",
                LapbFrameType.Supervisory => GetSupervisoryDescription(),
                LapbFrameType.Unnumbered => GetUnnumberedDescription(),
                _ => FrameType.ToString()
            };
        }


        /// <summary>
        /// Check if data appears to be text
        /// </summary>
        private static bool IsTextData(byte[] data)
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
        /// Get descriptive name for LAPB control field
        /// </summary>
        private string GetControlFieldDescription(byte controlByte)
        {
            if (Enum.IsDefined(typeof(LapbControlField), controlByte))
            {
                var controlField = (LapbControlField)controlByte;
                return controlField.GetDescription();
            }
            
            // For frames not in enum (variable sequence numbers), analyze the pattern
            if ((controlByte & 0x01) == 0)
            {
                return "Information frame";
            }
            else if ((controlByte & 0x03) == 0x01)
            {
                var sType = (controlByte >> 2) & 0x03;
                return sType switch
                {
                    0 => "Receive Ready (RR)",
                    1 => "Reject (REJ)",
                    2 => "Receive Not Ready (RNR)", 
                    3 => "Selective Reject (SREJ)",
                    _ => "Unknown supervisory"
                };
            }
            else
            {
                return $"Unknown unnumbered (0x{controlByte:X2})";
            }
        }

        /// <summary>
        /// Get descriptive name for HDLC address field
        /// </summary>
        private string GetAddressFieldDescription(byte address)
        {
            if (Enum.IsDefined(typeof(HdlcAddress), address))
            {
                var hdlcAddr = (HdlcAddress)address;
                return $"0x{address:X2} ({hdlcAddr.GetDescription()})";
            }
            return $"0x{address:X2} (Unknown address)";
        }

        /// <summary>
        /// Get control field description
        /// </summary>
        public string GetControlDescription()
        {
            return FrameType switch
            {
                LapbFrameType.Information => $"I-frame N(S)={NS} N(R)={NR} {(PF ? "P" : "")}",
                LapbFrameType.Supervisory => GetSupervisoryDescription(),
                LapbFrameType.Unnumbered => GetUnnumberedDescription(),
                _ => $"Unknown frame type 0x{Control:X2}"
            };
        }

        /// <summary>
        /// Get supervisory frame description
        /// </summary>
        private string GetSupervisoryDescription()
        {
            byte sType = (byte)((Control >> 2) & 0x03);
            string sTypeName = sType switch
            {
                0 => "RR",
                1 => "REJ", 
                2 => "RNR",
                3 => "SREJ",
                _ => "Unknown"
            };
            
            return $"{sTypeName} N(R)={NR} {(PF ? "P/F" : "")}";
        }

        /// <summary>
        /// Get unnumbered frame description
        /// </summary>
        private string GetUnnumberedDescription()
        {
            return Control switch
            {
                0x2F => "SABM",
                0x6F => "SABME", 
                0x43 => "DISC",
                0x63 => "UA",
                0x0F => "DM",
                0x87 => "FRMR",
                0x03 => "UI",
                _ => $"Unknown U-frame 0x{Control:X2}"
            } + (PF ? " P/F" : "");
        }
    }

}
