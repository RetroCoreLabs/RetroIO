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
        /// <summary>
        /// Gets or sets the sequence number of the packet.
        /// </summary>
        public int PacketNumber { get; set; }
        /// <summary>
        /// Gets or sets the time of the packet.
        /// </summary>
        public DateTime Timestamp { get; set; }
        /// <summary>
        /// Gets or sets the packet type text.
        /// </summary>
        public string Type { get; set; } = "";
        /// <summary>
        /// Gets or sets the direction text, RX or TX.
        /// </summary>
        public string Direction { get; set; } = ""; // "RX" or "TX"
        /// <summary>
        /// Gets or sets the length of the packet.
        /// </summary>
        public ushort Length { get; set; }
        /// <summary>
        /// Gets or sets the raw bytes of the packet.
        /// </summary>
        public byte[] Data { get; set; } = new byte[0];
        /// <summary>
        /// Gets or sets a text description of the packet.
        /// </summary>
        public string Description { get; set; } = "";
        
        // Enhanced LAPB details
        /// <summary>
        /// Gets or sets the LAPB frame type text, I, S or U.
        /// </summary>
        public string L2Type { get; set; } = ""; // I, S, U
        /// <summary>
        /// Gets or sets the LAPB address as text, DTE, DCE or hex.
        /// </summary>
        public string L2Address { get; set; } = ""; // DTE, DCE, or hex
        /// <summary>
        /// Gets or sets the LAPB control byte.
        /// </summary>
        public byte L2Control { get; set; }
        /// <summary>
        /// Gets or sets the send sequence number N(S), set for I-frames.
        /// </summary>
        public byte? L2NS { get; set; } // N(S) for I-frames
        /// <summary>
        /// Gets or sets the receive sequence number N(R), set for I-frames and S-frames.
        /// </summary>
        public byte? L2NR { get; set; } // N(R) for I/S-frames
        /// <summary>
        /// Gets or sets the poll/final bit.
        /// </summary>
        public bool L2PF { get; set; } // Poll/Final bit
        /// <summary>
        /// Gets or sets whether the frame check sequence is valid. The default is true.
        /// </summary>
        public bool L2FCSValid { get; set; } = true;
        /// <summary>
        /// Gets or sets the frame check sequence value.
        /// </summary>
        public ushort L2FCS { get; set; }
        
        // Enhanced X.25 details
        /// <summary>
        /// Gets or sets the X.25 packet type text, for example DATA or CALL-REQ.
        /// </summary>
        public string L3Type { get; set; } = ""; // DATA, CALL-REQ, CALL-CONF, etc.
        /// <summary>
        /// Gets or sets the logical channel number.
        /// </summary>
        public int? L3LCN { get; set; } // Logical Channel Number
        /// <summary>
        /// Gets or sets the more data bit.
        /// </summary>
        public bool L3M { get; set; } // More data bit
        /// <summary>
        /// Gets or sets the qualifier bit.
        /// </summary>
        public bool L3Q { get; set; } // Qualifier bit
        /// <summary>
        /// Gets or sets the delivery confirmation bit.
        /// </summary>
        public bool L3D { get; set; } // Delivery confirmation bit
        /// <summary>
        /// Gets or sets the clear or reset cause code.
        /// </summary>
        public byte? L3Cause { get; set; } // Clear/Reset cause
        /// <summary>
        /// Gets or sets the clear or reset diagnostic code.
        /// </summary>
        public byte? L3Diag { get; set; } // Clear/Reset diagnostic
        /// <summary>
        /// Gets or sets the facilities as text.
        /// </summary>
        public string L3Facilities { get; set; } = ""; // Facilities string
        /// <summary>
        /// Gets or sets a preview of the call user data as text.
        /// </summary>
        public string L3CUD { get; set; } = ""; // Call User Data preview
        
        // Computed display fields
        /// <summary>
        /// Gets or sets the text for the display row of the packet.
        /// </summary>
        public string DisplayRow { get; set; } = "";
        /// <summary>
        /// Gets or sets whether the packet is marked as an error.
        /// </summary>
        public bool IsError { get; set; } = false;
        
        // Relationship information for two-line display
        /// <summary>
        /// Gets or sets the packet this packet is related to, for a two-line display.
        /// </summary>
        public PacketInfo? RelatedPacket { get; set; }
        /// <summary>
        /// Gets or sets the kind of relation to the related packet, for example Encapsulates, EncapsulatedBy or None.
        /// </summary>
        public string RelationshipType { get; set; } = ""; // "Encapsulates", "EncapsulatedBy", "None"
    }
}
