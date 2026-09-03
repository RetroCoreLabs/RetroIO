using System;
using System.Collections.Generic;
using System.Text;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
using X25 = RetroIO.Protocols.X25.Protocols.X25;

    /// <summary>
    /// Unified packet information that accumulates data as packet moves through protocol layers
    /// ONE packet ID follows the packet through its entire journey
    /// </summary>
    public class UnifiedPacketInfo
    {
        /// <summary>
        /// Global unique packet ID - follows packet through all layers
        /// </summary>
        public long GlobalPacketId { get; set; }
        
        /// <summary>
        /// Direction of packet flow
        /// </summary>
        public Direction Direction { get; set; }
        
        /// <summary>
        /// When this packet was first created
        /// </summary>
        public DateTime Created { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Layer where this packet originated (first layer to process it)
        /// </summary>
        public LayerType OriginLayer { get; set; }
        
        /// <summary>
        /// Highest layer this packet has reached in its journey
        /// </summary>
        public LayerType HighestLayer { get; set; }
        
        /// <summary>
        /// All layers this packet has visited in order
        /// </summary>
        public List<LayerType> LayersVisited { get; set; } = new List<LayerType>();
        
        /// <summary>
        /// HDLC layer information (filled when packet reaches HDLC layer)
        /// </summary>
        public HdlcLayerInfo? HDLC { get; set; }
        
        /// <summary>
        /// LAPB layer information (filled when packet reaches LAPB layer)
        /// </summary>
        public LapbLayerInfo? LAPB { get; set; }
        
        /// <summary>
        /// X25 layer information (filled when packet reaches X25 layer)
        /// </summary>
        public X25LayerInfo? X25 { get; set; }
        
        /// <summary>
        /// Whether this packet encountered a processing error
        /// </summary>
        public bool HasProcessingError { get; set; }
        
        /// <summary>
        /// Error reason if HasProcessingError is true
        /// </summary>
        public string? ErrorReason { get; set; }
        
        /// <summary>
        /// Layer where the error occurred
        /// </summary>
        public LayerType? ErrorLayer { get; set; }
        
        /// <summary>
        /// Whether this packet is terminal (completed its journey or failed)
        /// </summary>
        public bool IsTerminal
        {
            get
            {
                // Always terminal if there was an error
                if (HasProcessingError) return true;
                
                // RX packets are terminal when they reach X25 (complete journey)
                // OR when they reach LAPB and contain no X25 data (simulation/control frames)
                if (Direction == Direction.RX)
                {
                    return HighestLayer == LayerType.X25 || 
                           (HighestLayer == LayerType.LAPB && !HasX25Data());
                }
                    
                // TX packets are terminal when they reach HDLC (complete journey)
                if (Direction == Direction.TX)
                    return HighestLayer == LayerType.HDLC;
                    
                return false;
            }
        }
        
        /// <summary>
        /// Check if LAPB frame contains X25 data (I-frames with payload >= 3 bytes)
        /// Used to determine if RX LAPB packets should be considered terminal
        /// </summary>
        private bool HasX25Data()
        {
            // Only check for LAPB layer packets
            if (LAPB == null || HighestLayer != LayerType.LAPB)
                return false;
                
            // I-frames with sufficient payload for X25 header (minimum 3 bytes)
            return LAPB.FrameType == LapbFrameType.Information && 
                   LAPB.Information != null && LAPB.Information.Length >= 3;
        }
        
        /// <summary>
        /// Get a summary string for this packet based on its highest layer
        /// </summary>
        public string GetSummary()
        {
            if (HasProcessingError)
            {
                var errorLayerName = ErrorLayer?.ToString() ?? HighestLayer.ToString();
                return $"{errorLayerName} [ERROR: {ErrorReason}]";
            }
            
            return HighestLayer switch
            {
                LayerType.X25 => GetX25Summary(),
                LayerType.LAPB => GetLapbSummary(),
                LayerType.HDLC => GetHdlcSummary(),
                _ => "Unknown packet"
            };
        }
        
        private string GetX25Summary()
        {
            if (X25 == null) return "X25 (no data)";
            return $"X25[{X25.PacketType}] LCN={X25.LCN}";
        }
        
        private string GetLapbSummary()
        {
            if (LAPB == null) return "LAPB (no data)";
            return $"LAPB[{LAPB.FrameType}] Addr=0x{LAPB.Address:X2} Ctrl=0x{LAPB.Control:X2}";
        }
        
        private string GetHdlcSummary()
        {
            if (HDLC == null) return "HDLC (no data)";
            return $"HDLC FCS={HDLC.FcsValid} Len={HDLC.Length}";
        }
        
        /// <summary>
        /// Get detailed information about all layers for display
        /// </summary>
        public string GetDetailedInfo()
        {
            var details = new StringBuilder();
            
            details.AppendLine($"=== PACKET #{GlobalPacketId} DETAILS ===");
            details.AppendLine($"Direction: {Direction}");
            details.AppendLine($"Created: {Created:yyyy-MM-dd HH:mm:ss.fff}");
            details.AppendLine($"Origin Layer: {OriginLayer}");
            details.AppendLine($"Highest Layer: {HighestLayer}");
            details.AppendLine($"Journey: {string.Join(" → ", LayersVisited)}");
            details.AppendLine($"Terminal: {IsTerminal}");
            
            if (HasProcessingError)
            {
                details.AppendLine($"ERROR: {ErrorReason} (at {ErrorLayer})");
            }
            
            details.AppendLine();
            
            // Show details for each layer visited
            foreach (var layer in LayersVisited)
            {
                switch (layer)
                {
                    case LayerType.HDLC:
                        details.AppendLine(GetHdlcDetails());
                        break;
                    case LayerType.LAPB:
                        details.AppendLine(GetLapbDetails());
                        break;
                    case LayerType.X25:
                        details.AppendLine(GetX25Details());
                        break;
                }
                details.AppendLine();
            }
            
            return details.ToString();
        }
        
        private string GetHdlcDetails()
        {
            if (HDLC == null) return "HDLC Layer: No data";
            
            var details = new StringBuilder();
            details.AppendLine("=== HDLC LAYER ===");
            details.AppendLine($"Frame ID: {HDLC.FrameId}");
            details.AppendLine($"FCS Valid: {HDLC.FcsValid}");
            details.AppendLine($"Frame Length: {HDLC.Length} bytes");
            details.AppendLine($"Frame Data: {Convert.ToHexString(HDLC.FrameData)}");
            
            return details.ToString();
        }
        
        private string GetLapbDetails()
        {
            if (LAPB == null) return "LAPB Layer: No data";
            
            var details = new StringBuilder();
            details.AppendLine("=== LAPB LAYER ===");
            details.AppendLine($"Frame Type: {LAPB.FrameType}");
            details.AppendLine($"Address: 0x{LAPB.Address:X2}");
            details.AppendLine($"Control: 0x{LAPB.Control:X2}");
            
            if (LAPB.NS.HasValue)
                details.AppendLine($"N(S): {LAPB.NS}");
            if (LAPB.NR.HasValue)
                details.AppendLine($"N(R): {LAPB.NR}");
                
            details.AppendLine($"P/F Bit: {LAPB.PollFinal}");
            details.AppendLine($"Information Length: {LAPB.InformationLength} bytes");
            
            if (LAPB.Information.Length > 0)
                details.AppendLine($"Information: {Convert.ToHexString(LAPB.Information)}");
            
            return details.ToString();
        }
        
        private string GetX25Details()
        {
            if (X25 == null) return "X25 Layer: No data";
            
            var details = new StringBuilder();
            details.AppendLine("=== X25 LAYER ===");
            details.AppendLine($"Packet Type: {X25.PacketType}");
            details.AppendLine($"LCN: {X25.LCN}");
            details.AppendLine($"Packet ID: {X25.PacketId}");
            
            if (X25.CalledAddress != null)
                details.AppendLine($"Called Address: {X25.CalledAddress}");
            if (X25.CallingAddress != null)
                details.AppendLine($"Calling Address: {X25.CallingAddress}");
                
            if (X25.Data.Length > 0)
                details.AppendLine($"Data Length: {X25.Data.Length} bytes");
                
            if (X25.Data.Length > 0 && X25.Data.Length <= 100) // Show data if not too long
                details.AppendLine($"Data: {Convert.ToHexString(X25.Data)}");
            
            return details.ToString();
        }
    }
    
    /// <summary>
    /// HDLC layer specific information
    /// </summary>
    public class HdlcLayerInfo
    {
        public long FrameId { get; set; }
        public bool FcsValid { get; set; }
        public byte[] FrameData { get; set; } = Array.Empty<byte>();
        public int Length { get; set; }
    }
    
    /// <summary>
    /// LAPB layer specific information
    /// </summary>
    public class LapbLayerInfo
    {
        public LapbFrameType FrameType { get; set; }
        public byte Address { get; set; }
        public byte Control { get; set; }
        public byte? NS { get; set; }       // Send sequence number (for I-frames)
        public byte? NR { get; set; }       // Receive sequence number
        public bool PollFinal { get; set; }
        public byte[] Information { get; set; } = Array.Empty<byte>();
        public int InformationLength { get; set; }
    }
    
    /// <summary>
    /// X25 layer specific information
    /// </summary>
    public class X25LayerInfo
    {
        public X25PacketType PacketType { get; set; }
        public ushort LCN { get; set; }
        public long PacketId { get; set; }
        public string? CalledAddress { get; set; }
        public string? CallingAddress { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public X25Packet? Packet { get; set; }  // Reference to original packet if needed
    }
}