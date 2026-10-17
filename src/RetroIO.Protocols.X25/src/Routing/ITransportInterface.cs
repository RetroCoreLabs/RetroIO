//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

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
        /// <param name="callRequest">
        /// X.25 call request to place
        /// </param>
        /// <returns>
        /// True if call was successfully initiated, false otherwise
        /// </returns>
        bool PlaceCall(X25CallRequest callRequest);

        /// <summary>
        /// Accept an inbound X.25 call request
        /// </summary>
        /// <param name="callRequest">
        /// X.25 call request to accept
        /// </param>
        /// <returns>
        /// True if call was successfully accepted, false otherwise
        /// </returns>
        bool AcceptCall(X25CallRequest callRequest);

        /// <summary>
        /// Reject an inbound X.25 call request with specified cause
        /// </summary>
        /// <param name="callRequest">
        /// X.25 call request to reject
        /// </param>
        /// <param name="cause">
        /// X.25 clear cause code
        /// </param>
        /// <returns>
        /// True if rejection was sent, false otherwise
        /// </returns>
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
        /// <summary>
        /// Gets or sets the status before the change.
        /// </summary>
        public TransportStatus OldStatus { get; set; }
        /// <summary>
        /// Gets or sets the status after the change.
        /// </summary>
        public TransportStatus NewStatus { get; set; }
        /// <summary>
        /// Gets or sets the reason text for the change, or null.
        /// </summary>
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

        /// <summary>
        /// The connection the packet arrived on, when the transport has more than one (an XOT
        /// server's <c>XotServerConnection</c>). The runtime keeps one X.25 engine per connection
        /// and needs this to find it.
        /// </summary>
        public object? Connection { get; set; }
    }

    /// <summary>
    /// Transport-specific statistics
    /// </summary>
    public class TransportStatistics
    {
        /// <summary>
        /// Gets or sets the transport name.
        /// </summary>
        public string TransportName { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the transport type.
        /// </summary>
        public TransportType TransportType { get; set; }
        /// <summary>
        /// Gets or sets the status of the transport when the statistics were read.
        /// </summary>
        public TransportStatus CurrentStatus { get; set; }
        /// <summary>
        /// Gets or sets the time the transport started.
        /// </summary>
        public DateTime StartTime { get; set; }
        /// <summary>
        /// Gets the time since the transport started.
        /// </summary>
        public TimeSpan Uptime => DateTime.UtcNow - StartTime;
        
        // Call statistics
        /// <summary>
        /// Gets or sets the number of outbound calls attempted.
        /// </summary>
        public int OutboundCallsAttempted { get; set; }
        /// <summary>
        /// Gets or sets the number of outbound calls that succeeded.
        /// </summary>
        public int OutboundCallsSucceeded { get; set; }
        /// <summary>
        /// Gets or sets the number of inbound calls received.
        /// </summary>
        public int InboundCallsReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of inbound calls accepted.
        /// </summary>
        public int InboundCallsAccepted { get; set; }
        /// <summary>
        /// Gets or sets the number of inbound calls rejected.
        /// </summary>
        public int InboundCallsRejected { get; set; }
        
        // Data statistics
        /// <summary>
        /// Gets or sets the number of bytes transmitted.
        /// </summary>
        public long BytesTransmitted { get; set; }
        /// <summary>
        /// Gets or sets the number of bytes received.
        /// </summary>
        public long BytesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of active connections.
        /// </summary>
        public int ActiveConnections { get; set; }
        
        // Error statistics
        /// <summary>
        /// Gets or sets the number of connection errors.
        /// </summary>
        public int ConnectionErrors { get; set; }
        /// <summary>
        /// Gets or sets the number of timeout errors.
        /// </summary>
        public int TimeoutErrors { get; set; }
        /// <summary>
        /// Gets or sets the number of protocol errors.
        /// </summary>
        public int ProtocolErrors { get; set; }
    }
}