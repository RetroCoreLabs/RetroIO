using System;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Event arguments for physical layer data events
    /// </summary>
    public class PhysicalLayerDataEventArgs : EventArgs
    {
        public string SourceAddress { get; set; } = "";
        public PhysicalTransportType TransportType { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
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
        public string CalledAddress { get; set; } = "";
        public string CallingAddress { get; set; } = "";
        public PhysicalTransportType TransportType { get; set; }
        public byte[] CallData { get; set; } = Array.Empty<byte>();
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
        public string TransportType { get; set; } = "";
        public string Status { get; set; } = "";
        public Exception? Error { get; set; }
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
        Serial,
        XOT_Client,
        XOT_Server,
        LocalTAD
    }
    
    /// <summary>
    /// Legacy event arguments for backward compatibility
    /// </summary>
    public class PhysicalLayerEventArgs : EventArgs
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int Length { get; set; }
        public DateTime Timestamp { get; set; }
        public string? Error { get; set; }
    }
}
