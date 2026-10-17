//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Common
{
    /// <summary>
    /// Packet information structure for data transfer between layers
    /// </summary>
    public class PacketInfo
    {
        public int PacketNumber { get; set; }
        public DateTime Timestamp { get; set; }
        public string Type { get; set; } = "";
        public string Direction { get; set; } = ""; // "RX" or "TX"
        public ushort Length { get; set; }
        public byte[] Data { get; set; } = new byte[0];
        public string Description { get; set; } = "";
        
        // Enhanced LAPB details
        public string L2Type { get; set; } = ""; // I, S, U
        public string L2Address { get; set; } = ""; // DTE, DCE, or hex
        public byte L2Control { get; set; }
        public byte? L2NS { get; set; } // N(S) for I-frames
        public byte? L2NR { get; set; } // N(R) for I/S-frames
        public bool L2PF { get; set; } // Poll/Final bit
        public bool L2FCSValid { get; set; } = true;
        public ushort L2FCS { get; set; }
        
        // Enhanced X.25 details
        public string L3Type { get; set; } = ""; // DATA, CALL-REQ, CALL-CONF, etc.
        public int? L3LCN { get; set; } // Logical Channel Number
        public bool L3M { get; set; } // More data bit
        public bool L3Q { get; set; } // Qualifier bit
        public bool L3D { get; set; } // Delivery confirmation bit
        public byte? L3Cause { get; set; } // Clear/Reset cause
        public byte? L3Diag { get; set; } // Clear/Reset diagnostic
        public string L3Facilities { get; set; } = ""; // Facilities string
        public string L3CUD { get; set; } = ""; // Call User Data preview
        
        // Computed display fields
        public string DisplayRow { get; set; } = "";
        public bool IsError { get; set; } = false;
        
        // Relationship information for two-line display
        public PacketInfo? RelatedPacket { get; set; }
        public string RelationshipType { get; set; } = ""; // "Encapsulates", "EncapsulatedBy", "None"
    }
}
