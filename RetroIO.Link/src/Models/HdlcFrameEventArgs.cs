using System;
using System.Text;

namespace RetroIO.Link.Models
{
    /// <summary>
    /// Event arguments for HDLC frame events
    /// </summary>
    public class HdlcFrameEventArgs : EventArgs
    {
        /// <summary>
        /// HDLC frame data
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
        /// FCS validation result
        /// </summary>
        public bool FcsValid { get; set; }

        /// <summary>
        /// Frame ID (shared counter)
        /// </summary>
        public long FrameId { get; set; }

        /// <summary>
        /// Create HDLC frame event arguments
        /// </summary>
        public HdlcFrameEventArgs()
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
        /// Get frame summary with detailed HDLC information
        /// </summary>
        public string GetFrameSummary()
        {
            var summary = $"HDLC[{Direction}] Len={Length} FCS={FcsValid}";
            
            // Analyze frame structure if we have enough data
            if (FrameData.Length >= 4)
            {
                // Check for HDLC flag (0x7E) at start and end
                bool hasStartFlag = FrameData[0] == (byte)HdlcSpecialByte.Flag;
                bool hasEndFlag = FrameData[^1] == (byte)HdlcSpecialByte.Flag;
                
                summary += $" Flags={hasStartFlag}/{hasEndFlag}";
                
                // If proper HDLC frame with flags, analyze address/control
                if (hasStartFlag && hasEndFlag && FrameData.Length >= 4)
                {
                    byte address = FrameData[1];
                    byte control = FrameData[2];
                    
                    var addressDesc = GetAddressDescription(address);
                    var controlDesc = GetControlDescription(control);
                    
                    summary += $" Addr={addressDesc} Ctrl={controlDesc}";
                }
            }
            
            // Show escaped bytes if any
            int escapedCount = CountEscapedBytes();
            if (escapedCount > 0)
            {
                summary += $" Escaped={escapedCount}";
            }
            
            return summary;
        }

        /// <summary>
        /// Get address field description
        /// </summary>
        private string GetAddressDescription(byte address)
        {
            if (Enum.IsDefined(typeof(HdlcAddress), address))
            {
                var hdlcAddr = (HdlcAddress)address;
                return $"0x{address:X2} ({hdlcAddr.GetDescription()})";
            }
            return $"0x{address:X2}";
        }

        /// <summary>
        /// Get control field description
        /// </summary>
        private string GetControlDescription(byte control)
        {
            if (Enum.IsDefined(typeof(LapbControlField), control))
            {
                var controlField = (LapbControlField)control;
                return $"0x{control:X2} ({controlField.GetDescription()})";
            }
            
            // Analyze frame type pattern
            if ((control & 0x01) == 0)
            {
                return $"0x{control:X2} (I-frame)";
            }
            else if ((control & 0x03) == 0x01)
            {
                var sType = (control >> 2) & 0x03;
                var sName = sType switch
                {
                    0 => "RR",
                    1 => "REJ", 
                    2 => "RNR",
                    3 => "SREJ",
                    _ => "S-frame"
                };
                return $"0x{control:X2} ({sName})";
            }
            else
            {
                return $"0x{control:X2} (U-frame)";
            }
        }

        /// <summary>
        /// Count escaped bytes in frame
        /// </summary>
        private int CountEscapedBytes()
        {
            int count = 0;
            for (int i = 0; i < FrameData.Length; i++)
            {
                if (FrameData[i] == (byte)HdlcSpecialByte.Escape)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Get detailed frame information for export
        /// </summary>
        public string GetDetailedFrameInfo()
        {
            var details = new StringBuilder();
            
            details.AppendLine($"Frame Type: HDLC - High-Level Data Link Control frame");
            details.AppendLine($"Direction: {Direction} (Transmitted/Received)");
            details.AppendLine();
            
            details.AppendLine("HDLC Frame Details:");
            details.AppendLine($"  Frame Length: {Length} bytes");
            details.AppendLine($"  FCS Valid: {FcsValid}");
            
            // Analyze frame structure
            if (FrameData.Length >= 4)
            {
                bool hasStartFlag = FrameData[0] == (byte)HdlcSpecialByte.Flag;
                bool hasEndFlag = FrameData[^1] == (byte)HdlcSpecialByte.Flag;
                
                details.AppendLine($"  Start Flag: {(hasStartFlag ? "Present (0x7E)" : "Missing")}");
                details.AppendLine($"  End Flag: {(hasEndFlag ? "Present (0x7E)" : "Missing")}");
                
                if (hasStartFlag && hasEndFlag && FrameData.Length >= 4)
                {
                    byte address = FrameData[1];
                    byte control = FrameData[2];
                    
                    details.AppendLine($"  Address Field: {GetAddressDescription(address)}");
                    details.AppendLine($"  Control Field: {GetControlDescription(control)}");
                }
            }
            
            // Byte stuffing analysis
            int escapedCount = CountEscapedBytes();
            if (escapedCount > 0)
            {
                details.AppendLine($"  Byte Stuffing: {escapedCount} escaped bytes (transparency mechanism)");
            }
            
            // Frame integrity
            details.AppendLine($"  Frame Check Sequence: {(FcsValid ? "Valid" : "Invalid/Missing")}");
            
            return details.ToString();
        }
    }

}
