//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// LAPB-specific history item
    /// </summary>
    public class LapbHistoryItem : HistoryItem
    {
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
        /// Create LAPB history item
        /// </summary>
        public LapbHistoryItem(Direction direction, byte[] payload, LapbFrameType frameType, 
            byte address, byte control, byte? ns, byte? nr, bool pf, long frameId, string? description = null)
            : base(LayerType.LAPB, direction, payload, description ?? $"LAPB {frameType} Frame")
        {
            FrameType = frameType;
            Address = address;
            Control = control;
            NS = ns;
            NR = nr;
            PF = pf;
            FrameId = frameId;
        }

        /// <summary>
        /// Get summary string
        /// </summary>
        public override string GetSummary()
        {
            string summary = $"LAPB[{FrameType}][{Direction}] FrameId={FrameId} Addr=0x{Address:X2} Ctrl=0x{Control:X2}";
            
            if (NS.HasValue)
                summary += $" N(S)={NS.Value}";
            
            if (NR.HasValue)
                summary += $" N(R)={NR.Value}";
            
            if (PF)
                summary += " P/F";
            
            summary += $" Len={Length}";
            
            return summary;
        }
    }
}
