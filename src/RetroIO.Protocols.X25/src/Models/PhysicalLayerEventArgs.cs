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
    /// Event arguments for physical layer data events
    /// </summary>
    public class PhysicalLayerDataEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the address the data came from.
        /// </summary>
        public string SourceAddress { get; set; } = "";
        /// <summary>
        /// Gets or sets the kind of physical transport that delivered the data.
        /// </summary>
        public PhysicalTransportType TransportType { get; set; }
        /// <summary>
        /// Gets or sets the received bytes.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
        /// <summary>
        /// Gets or sets the time the event was raised.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Get data summary with transport and byte-stuffing analysis
        /// </summary>
        public string GetDataSummary()
        {
            var summary = $"PHYS[{TransportType}] From={SourceAddress} Len={Data.Length}";
            
            if (Data.Length > 0)
            {
                // Analyze for HDLC flags and escapes
                int flagCount = 0;
                int escapeCount = 0;
                
                for (int i = 0; i < Data.Length; i++)
                {
                    if (Data[i] == (byte)HdlcSpecialByte.Flag)
                        flagCount++;
                    else if (Data[i] == (byte)HdlcSpecialByte.Escape)
                        escapeCount++;
                }
                
                summary += $" Flags={flagCount} Escaped={escapeCount}";
                
                // Show data sample (first 8 bytes)
                if (Data.Length <= 8)
                {
                    summary += $" Data=[{string.Join(" ", Data.Select(b => $"{b:X2}"))}]";
                }
                else
                {
                    summary += $" Data=[{string.Join(" ", Data.Take(8).Select(b => $"{b:X2}"))}...]";
                }
            }
            
            return summary;
        }
    }
    
    /// <summary>
    /// Event arguments for physical layer call events
    /// </summary>
    public class PhysicalLayerCallEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the address that was called.
        /// </summary>
        public string CalledAddress { get; set; } = "";
        /// <summary>
        /// Gets or sets the address of the caller.
        /// </summary>
        public string CallingAddress { get; set; } = "";
        /// <summary>
        /// Gets or sets the kind of physical transport that carried the call.
        /// </summary>
        public PhysicalTransportType TransportType { get; set; }
        /// <summary>
        /// Gets or sets the call user data bytes.
        /// </summary>
        public byte[] CallData { get; set; } = Array.Empty<byte>();
        /// <summary>
        /// Gets or sets the time the event was raised.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Get call summary with address and transport information
        /// </summary>
        public string GetCallSummary()
        {
            var summary = $"PHYS[{TransportType}] CALL From={CallingAddress} To={CalledAddress}";
            
            if (CallData.Length > 0)
            {
                // The octets only. What the Call User Data MEANS is not X.25's
                // business - X.25 5.2.2 gives the field a length and leaves its
                // contents to the two DTEs - and decoding XMSG service ports here
                // put a SINTRAN protocol inside the X.25 layer for a debug line.
                summary += $" CUD({CallData.Length})=[{string.Join(" ", CallData.Select(b => $"{b:X2}"))}]";
            }
            
            return summary;
        }
    }
    
    /// <summary>
    /// Event arguments for physical layer status events  
    /// </summary>
    public class PhysicalLayerStatusEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the name of the transport the status belongs to.
        /// </summary>
        public string TransportType { get; set; } = "";
        /// <summary>
        /// Gets or sets the status text.
        /// </summary>
        public string Status { get; set; } = "";
        /// <summary>
        /// Gets or sets the exception behind the status, or null when there is none.
        /// </summary>
        public Exception? Error { get; set; }
        /// <summary>
        /// Gets or sets the time the event was raised.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Get status summary with transport and error information
        /// </summary>
        public string GetStatusSummary()
        {
            var summary = $"PHYS[{TransportType}] STATUS: {Status}";
            
            if (Error != null)
            {
                summary += $" ERROR: {Error.Message}";
            }
            
            return summary;
        }
    }
    
    /// <summary>
    /// Physical transport types
    /// </summary>
    public enum PhysicalTransportType
    {
        /// <summary>
        /// Transport over a serial line.
        /// </summary>
        Serial,
        /// <summary>
        /// XOT transport where this side connects out to a remote XOT server.
        /// </summary>
        XOT_Client,
        /// <summary>
        /// XOT transport where this side accepts connections from remote XOT clients.
        /// </summary>
        XOT_Server,
        /// <summary>
        /// Transport to a local TAD service inside the same process.
        /// </summary>
        LocalTAD
    }
    
    /// <summary>
    /// Legacy event arguments for backward compatibility
    /// </summary>
    public class PhysicalLayerEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the received bytes.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
        /// <summary>
        /// Gets or sets the number of valid bytes.
        /// </summary>
        public int Length { get; set; }
        /// <summary>
        /// Gets or sets the time the event was raised.
        /// </summary>
        public DateTime Timestamp { get; set; }
        /// <summary>
        /// Gets or sets the error text, or null when there is no error.
        /// </summary>
        public string? Error { get; set; }
    }
}
