//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// X.25 Packet Type Identifier (PTI) values as defined in ITU-T X.25 specification.
    /// The PTI field in the X.25 packet header identifies the specific packet type and
    /// contains control information for data transfer and flow control.
    /// </summary>
    public enum X25PacketTypeIdentifier : byte
    {
        // Data Transfer Packets
        // Note: Data packets have LSB=0, so 0x00, 0x02, 0x04, etc. are all data packets
        // We provide the most common data packet PTI value
        
        /// <summary>
        /// Data packet with P(S)=0, P(R)=0, M-bit=0.
        /// Data packets have even PTI values (LSB=0) and contain user data.
        /// P(S) = Send sequence number (bits 1-3), P(R) = Receive sequence number (bits 5-7).
        /// </summary>
        Data = 0x00,

        // Flow Control (Supervisory) Packets
        // These packets control data flow and acknowledge received packets

        /// <summary>
        /// Receive Ready (RR) packet with P(R)=0.
        /// Acknowledges receipt of I-frames up to N(R)-1 and indicates readiness to receive more.
        /// Base pattern: 0x01, actual values vary based on P(R) field.
        /// </summary>
        ReceiveReady = 0x01,

        /// <summary>
        /// Receive Not Ready (RNR) packet with P(R)=0.
        /// Acknowledges receipt of I-frames up to N(R)-1 but indicates temporary inability to receive more.
        /// Base pattern: 0x05, actual values vary based on P(R) field.
        /// </summary>
        ReceiveNotReady = 0x05,

        /// <summary>
        /// Reject (REJ) packet with P(R)=0.
        /// Requests retransmission of I-frames starting from N(R).
        /// Base pattern: 0x09, actual values vary based on P(R) field.
        /// </summary>
        Reject = 0x09,

        // Call Setup and Clearing Packets

        /// <summary>
        /// Incoming Call packet (Call Request from DTE perspective).
        /// Initiates a virtual circuit connection with calling/called address and facilities.
        /// Contains calling DTE address, called DTE address, and optional facilities.
        /// </summary>
        CallRequest = 0x0B,

        /// <summary>
        /// Call Connected packet (Call Accepted from DTE perspective).
        /// Accepts an incoming call and establishes the virtual circuit.
        /// May contain responding address and accepted facilities.
        /// </summary>
        CallAccepted = 0x0F,

        /// <summary>
        /// Clear Indication packet (Clear Request from DTE perspective).
        /// Initiates clearing of a virtual circuit connection.
        /// Contains cause and diagnostic codes explaining the reason for clearing.
        /// </summary>
        ClearRequest = 0x13,

        /// <summary>
        /// Clear Confirmation packet.
        /// Confirms the clearing of a virtual circuit initiated by Clear Request.
        /// Completes the clearing procedure and deallocates the logical channel.
        /// </summary>
        ClearConfirmation = 0x17,

        // Reset Packets

        /// <summary>
        /// Reset Indication packet (Reset Request from DTE perspective).
        /// Resets a virtual circuit to initial state, clearing all data in transit.
        /// Contains cause and diagnostic codes. Used for error recovery.
        /// </summary>
        ResetRequest = 0x1B,

        /// <summary>
        /// Reset Confirmation packet.
        /// Confirms the reset of a virtual circuit initiated by Reset Request.
        /// Completes the reset procedure and reinitializes sequence numbers.
        /// </summary>
        ResetConfirmation = 0x1F,

        // Interrupt Packets

        /// <summary>
        /// Interrupt packet.
        /// Sends urgent out-of-band data that bypasses normal flow control.
        /// Limited to 32 bytes of user data. Used for urgent signaling.
        /// </summary>
        Interrupt = 0x23,

        /// <summary>
        /// Interrupt Confirmation packet.
        /// Acknowledges receipt of an Interrupt packet.
        /// Must be sent in response to every Interrupt packet received.
        /// </summary>
        InterruptConfirmation = 0x27,

        // Diagnostic and Registration Packets

        /// <summary>
        /// Diagnostic packet.
        /// Contains diagnostic information about network problems or status.
        /// Used for network troubleshooting and error reporting.
        /// </summary>
        Diagnostic = 0xF1,

        /// <summary>
        /// Registration Request packet.
        /// Requests registration of DTE facilities or parameters with the network.
        /// Contains registration parameters and requested facilities.
        /// </summary>
        RegistrationRequest = 0xF3,

        /// <summary>
        /// Registration Confirmation packet.
        /// Confirms or rejects a registration request from the DTE.
        /// Contains cause and diagnostic information if registration fails.
        /// </summary>
        RegistrationConfirmation = 0xF7,

        // Restart Packets (Network Level)

        /// <summary>
        /// Restart Indication packet (Restart Request from DTE perspective).
        /// Restarts all virtual circuits on the interface simultaneously.
        /// Used for major error recovery or network reconfiguration.
        /// </summary>
        RestartRequest = 0xFB,

        /// <summary>
        /// Restart Confirmation packet.
        /// Confirms the restart of all virtual circuits initiated by Restart Request.
        /// Completes the restart procedure for the entire interface.
        /// </summary>
        RestartConfirmation = 0xFF
    }

    /// <summary>
    /// Extension methods for X25PacketTypeIdentifier enum to provide additional functionality.
    /// </summary>
    public static class X25PacketTypeIdentifierExtensions
    {
        /// <summary>
        /// Determines if the PTI represents a data packet.
        /// Data packets have the least significant bit (LSB) set to 0.
        /// </summary>
        /// <param name="pti">
        /// The packet type identifier
        /// </param>
        /// <returns>
        /// True if this is a data packet, false otherwise
        /// </returns>
        public static bool IsDataPacket(this X25PacketTypeIdentifier pti)
        {
            return ((byte)pti & 0x01) == 0;
        }

        /// <summary>
        /// Determines if the PTI represents a supervisory (flow control) packet.
        /// Supervisory packets include RR, RNR, and REJ with variable P(R) values.
        /// </summary>
        /// <param name="pti">
        /// The packet type identifier
        /// </param>
        /// <returns>
        /// True if this is a supervisory packet, false otherwise
        /// </returns>
        public static bool IsSupervisoryPacket(this X25PacketTypeIdentifier pti)
        {
            if (!((byte)pti).IsSupervisoryPacket()) return false;
            
            byte baseType = (byte)((byte)pti & 0x1F);
            return baseType == 0x01 || baseType == 0x05 || baseType == 0x09;
        }

        /// <summary>
        /// Determines if the PTI represents an unnumbered (control) packet.
        /// Unnumbered packets include call setup, clearing, reset, interrupt, and restart packets.
        /// </summary>
        /// <param name="pti">
        /// The packet type identifier
        /// </param>
        /// <returns>
        /// True if this is an unnumbered packet, false otherwise
        /// </returns>
        public static bool IsUnnumberedPacket(this X25PacketTypeIdentifier pti)
        {
            return !IsDataPacket(pti) && !IsSupervisoryPacket(pti);
        }

        /// <summary>
        /// Gets the packet category description for documentation and debugging.
        /// </summary>
        /// <param name="pti">
        /// The packet type identifier
        /// </param>
        /// <returns>
        /// A string describing the packet category
        /// </returns>
        public static string GetCategory(this X25PacketTypeIdentifier pti)
        {
            if (IsDataPacket(pti)) return "Data Transfer";
            if (IsSupervisoryPacket(pti)) return "Flow Control";
            
            return (byte)pti switch
            {
                >= 0x0B and <= 0x17 => "Call Management",
                >= 0x1B and <= 0x1F => "Reset Control", 
                >= 0x23 and <= 0x27 => "Interrupt Control",
                >= 0xF1 and <= 0xF7 => "Registration/Diagnostic",
                >= 0xFB and <= 0xFF => "Restart Control",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Gets a human-readable description of the packet type identifier.
        /// </summary>
        /// <param name="pti">
        /// The packet type identifier
        /// </param>
        /// <returns>
        /// A descriptive string explaining the packet type
        /// </returns>
        public static string GetDescription(this X25PacketTypeIdentifier pti)
        {
            return pti switch
            {
                X25PacketTypeIdentifier.Data => "User data transfer packet",
                X25PacketTypeIdentifier.ReceiveReady => "Flow control - ready to receive more data",
                X25PacketTypeIdentifier.ReceiveNotReady => "Flow control - temporarily unable to receive",
                X25PacketTypeIdentifier.Reject => "Flow control - request retransmission",
                X25PacketTypeIdentifier.CallRequest => "Initiate virtual circuit connection",
                X25PacketTypeIdentifier.CallAccepted => "Accept virtual circuit connection", 
                X25PacketTypeIdentifier.ClearRequest => "Clear virtual circuit connection",
                X25PacketTypeIdentifier.ClearConfirmation => "Confirm virtual circuit clearing",
                X25PacketTypeIdentifier.ResetRequest => "Reset virtual circuit to initial state",
                X25PacketTypeIdentifier.ResetConfirmation => "Confirm virtual circuit reset",
                X25PacketTypeIdentifier.Interrupt => "Out-of-band urgent data",
                X25PacketTypeIdentifier.InterruptConfirmation => "Acknowledge interrupt packet",
                X25PacketTypeIdentifier.Diagnostic => "Network diagnostic information",
                X25PacketTypeIdentifier.RegistrationRequest => "Request DTE parameter registration",
                X25PacketTypeIdentifier.RegistrationConfirmation => "Confirm DTE registration",
                X25PacketTypeIdentifier.RestartRequest => "Restart all virtual circuits",
                X25PacketTypeIdentifier.RestartConfirmation => "Confirm virtual circuits restart",
                _ => $"Unknown packet type 0x{(byte)pti:X2}"
            };
        }
    }

    /// <summary>
    /// Extension methods for byte values to support PTI operations.
    /// </summary>
    public static class BytePTIExtensions
    {
        /// <summary>
        /// Determines if a byte PTI value represents a supervisory packet.
        /// </summary>
        /// <param name="pti">
        /// The PTI byte value
        /// </param>
        /// <returns>
        /// True if this is a supervisory packet PTI, false otherwise
        /// </returns>
        public static bool IsSupervisoryPacket(this byte pti)
        {
            return (pti & 0x01) != 0;
        }
    }
}