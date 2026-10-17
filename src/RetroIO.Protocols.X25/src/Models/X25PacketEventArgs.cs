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
    /// Event arguments for X.25 packet events
    /// </summary>
    public class X25PacketEventArgs : EventArgs
    {
        /// <summary>
        /// X.25 packet
        /// </summary>
        public X25Packet Packet { get; set; } = new X25Packet();

        /// <summary>
        /// Packet direction (TX/RX)
        /// </summary>
        public PacketDirection Direction { get; set; }

        /// <summary>
        /// Packet timestamp
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Packet ID (for tracking)
        /// </summary>
        public long PacketId { get; set; }

        /// <summary>
        /// Create X.25 packet event arguments
        /// </summary>
        public X25PacketEventArgs()
        {
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Get packet as hex string
        /// </summary>
        public string GetPacketHex()
        {
            return BitConverter.ToString(Packet.FullPacket).Replace("-", " ");
        }

        /// <summary>
        /// Get packet summary
        /// </summary>
        public string GetPacketSummary()
        {
            return $"X25[{Packet.PacketType}][{Direction}] LCN={Packet.LCN} Q={Packet.QBit} D={Packet.DBit} Len={Packet.Length} ID={PacketId}";
        }

        /// <summary>
        /// Get packet data as hex string
        /// </summary>
        public string GetDataHex()
        {
            if (Packet.Data.Length == 0)
                return "";
                
            return BitConverter.ToString(Packet.Data).Replace("-", " ");
        }

        /// <summary>
        /// Get packet data as string (if printable)
        /// </summary>
        public string GetDataString()
        {
            if (Packet.Data.Length == 0)
                return "";
                
            try
            {
                return System.Text.Encoding.ASCII.GetString(Packet.Data);
            }
            catch
            {
                return GetDataHex();
            }
        }
    }
}
