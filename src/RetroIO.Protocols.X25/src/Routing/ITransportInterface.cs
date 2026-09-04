using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// Transport interface abstraction for X.25 routing
    /// Supports Serial, XOT Client, and XOT Server transports
    /// </summary>
    public interface ITransportInterface
    {
        /// <summary>
        /// Unique name for this transport interface
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Type of transport (Serial, XOT_Client, XOT_Server)
        /// </summary>
        TransportType Type { get; }

        /// <summary>
        /// Current connection status
        /// </summary>
        TransportStatus Status { get; }

        /// <summary>
        /// Place an outbound X.25 call request
        /// </summary>
        /// <param name="callRequest">X.25 call request to place</param>
        /// <returns>True if call was successfully initiated, false otherwise</returns>
        bool PlaceCall(X25CallRequest callRequest);

        /// <summary>
        /// Accept an inbound X.25 call request
        /// </summary>
        /// <param name="callRequest">X.25 call request to accept</param>
        /// <returns>True if call was successfully accepted, false otherwise</returns>
        bool AcceptCall(X25CallRequest callRequest);

        /// <summary>
        /// Reject an inbound X.25 call request with specified cause
        /// </summary>
        /// <param name="callRequest">X.25 call request to reject</param>
        /// <param name="cause">X.25 clear cause code</param>
        /// <returns>True if rejection was sent, false otherwise</returns>
        bool RejectCall(X25CallRequest callRequest, byte cause);

        /// <summary>
        /// Start the transport interface
        /// </summary>
        bool Start();

        /// <summary>
        /// Stop the transport interface
        /// </summary>
        void Stop();

        /// <summary>
        /// Get transport-specific statistics
        /// </summary>
        TransportStatistics GetStatistics();

        /// <summary>
        /// Event fired when an incoming call is received
        /// </summary>
        event EventHandler<X25CallRequest>? IncomingCall;

        /// <summary>
        /// Event fired when transport status changes
        /// </summary>
        event EventHandler<TransportStatusEventArgs>? StatusChanged;

        /// <summary>
        /// Event fired when raw data is received from transport
        /// Used for bridge pass-through mode
        /// </summary>
        event EventHandler<TransportDataEventArgs>? DataReceived;
    }

    /// <summary>
    /// Types of transport interfaces
    /// </summary>
    public enum TransportType
    {
        /// <summary>
        /// Serial interface (X.25 over LAPB)
        /// </summary>
        Serial,

        /// <summary>
        /// XOT client (outbound X.25 over TCP)
        /// </summary>
        XOT_Client,

        /// <summary>
        /// XOT server (inbound X.25 over TCP listener)
        /// </summary>
        XOT_Server
    }

    /// <summary>
    /// Transport interface status
    /// </summary>
    public enum TransportStatus
    {
        /// <summary>
        /// Transport is stopped
        /// </summary>
        Stopped,

        /// <summary>
        /// Transport is starting up
        /// </summary>
        Starting,

        /// <summary>
        /// Transport is active and ready
        /// </summary>
        Active,

        /// <summary>
        /// Transport is connected (for client transports)
        /// </summary>
        Connected,

        /// <summary>
        /// Transport has encountered an error
        /// </summary>
        Error,

        /// <summary>
        /// Transport is stopping
        /// </summary>
        Stopping
    }

    /// <summary>
    /// Transport status change event arguments
    /// </summary>
    public class TransportStatusEventArgs : EventArgs
    {
        public TransportStatus OldStatus { get; set; }
        public TransportStatus NewStatus { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Transport data received event arguments
    /// Contains raw data received from transport layer
    /// </summary>
    public class TransportDataEventArgs : EventArgs
    {
        /// <summary>
        /// Raw data buffer
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Length of valid data in buffer
        /// </summary>
        public int Length { get; set; }

        /// <summary>
        /// Transport interface that received the data
        /// </summary>
        public ITransportInterface? Transport { get; set; }
    }

    /// <summary>
    /// Transport-specific statistics
    /// </summary>
    public class TransportStatistics
    {
        public string TransportName { get; set; } = string.Empty;
        public TransportType TransportType { get; set; }
        public TransportStatus CurrentStatus { get; set; }
        public DateTime StartTime { get; set; }
        public TimeSpan Uptime => DateTime.UtcNow - StartTime;
        
        // Call statistics
        public int OutboundCallsAttempted { get; set; }
        public int OutboundCallsSucceeded { get; set; }
        public int InboundCallsReceived { get; set; }
        public int InboundCallsAccepted { get; set; }
        public int InboundCallsRejected { get; set; }
        
        // Data statistics
        public long BytesTransmitted { get; set; }
        public long BytesReceived { get; set; }
        public int ActiveConnections { get; set; }
        
        // Error statistics
        public int ConnectionErrors { get; set; }
        public int TimeoutErrors { get; set; }
        public int ProtocolErrors { get; set; }
    }
}