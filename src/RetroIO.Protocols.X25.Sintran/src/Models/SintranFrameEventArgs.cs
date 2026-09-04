using System;

using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// Event arguments for SINTRAN frame events
    /// </summary>
    public class SintranFrameEventArgs : EventArgs
    {
        /// <summary>
        /// SINTRAN frame data (payload from LAPB I-frame)
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
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Name of the link this frame was received/sent on
        /// </summary>
        public string LinkName { get; set; } = string.Empty;

        /// <summary>
        /// SINTRAN protocol ID (0xDD=TAD, 0xDA=PAD, 0xDE=Routing)
        /// </summary>
        public byte ProtocolId { get; set; }

        /// <summary>
        /// Get frame as hex string
        /// </summary>
        public string GetFrameHex()
        {
            return BitConverter.ToString(FrameData, 0, Math.Min(Length, FrameData.Length)).Replace("-", " ");
        }

        /// <summary>
        /// Get SINTRAN protocol name
        /// </summary>
        public string GetProtocolName()
        {
            return ProtocolId switch
            {
                0xDD => "TAD",
                0xDA => "PAD",
                0xDE => "Routing",
                _ => $"Unknown (0x{ProtocolId:X2})"
            };
        }

        /// <summary>
        /// Get frame summary with descriptive information
        /// </summary>
        public string GetFrameSummary()
        {
            return $"SINTRAN[{GetProtocolName()}][{Direction}] Link={LinkName} Len={Length}";
        }
    }
}
