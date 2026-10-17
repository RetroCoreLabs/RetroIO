//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

using RetroIO.Protocols.X25.Protocols;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// Interface for SINTRAN-specific protocol handlers over PVC connections
    /// Handles different SINTRAN protocols like routing info, XMSG, and file transfer
    /// </summary>
    public interface ISintranProtocolHandler
    {
        /// <summary>
        /// Protocol name (e.g., "ROUTING", "XMSG", "FILE")
        /// </summary>
        string Protocol { get; }

        /// <summary>
        /// Protocol description for monitoring/debugging
        /// </summary>
        string Description { get; }

        /// <summary>
        /// LCN this handler is bound to
        /// </summary>
        ushort LCN { get; }

        /// <summary>
        /// Called when PVC channel is established
        /// </summary>
        void OnChannelEstablished(ushort lcn);

        /// <summary>
        /// Called when PVC channel is closed
        /// </summary>
        void OnChannelClosed();

        /// <summary>
        /// Handle incoming data from SINTRAN system
        /// </summary>
        void HandleData(byte[] data);

        /// <summary>
        /// Event raised when handler needs to send response data
        /// </summary>
        event EventHandler<SintranResponseEventArgs> ResponseReady;

        /// <summary>
        /// Get handler statistics
        /// </summary>
        SintranHandlerStats GetStats();

        /// <summary>
        /// Initialize handler
        /// </summary>
        void Initialize();

        /// <summary>
        /// Cleanup handler resources
        /// </summary>
        void Dispose();
    }

    /// <summary>
    /// Event args for SINTRAN protocol handler responses
    /// </summary>
    public class SintranResponseEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the logical channel number the response is for.
        /// </summary>
        public ushort LCN { get; set; }
        /// <summary>
        /// Gets or sets the response bytes.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
        /// <summary>
        /// Gets or sets the name of the protocol that produced the response.
        /// </summary>
        public string Protocol { get; set; } = "";
        /// <summary>
        /// Gets or sets the creation time of the event object (UTC by default).
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Statistics for SINTRAN protocol handlers
    /// </summary>
    public class SintranHandlerStats
    {
        /// <summary>
        /// Gets or sets the protocol name.
        /// </summary>
        public string Protocol { get; set; } = "";
        /// <summary>
        /// Gets or sets the logical channel number.
        /// </summary>
        public ushort LCN { get; set; }
        /// <summary>
        /// Gets or sets the number of messages received.
        /// </summary>
        public int MessagesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of messages sent.
        /// </summary>
        public int MessagesSent { get; set; }
        /// <summary>
        /// Gets or sets the number of bytes received.
        /// </summary>
        public long BytesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of bytes sent.
        /// </summary>
        public long BytesSent { get; set; }
        /// <summary>
        /// Gets or sets the time of the last activity.
        /// </summary>
        public DateTime LastActivity { get; set; }
        /// <summary>
        /// Gets or sets whether the handler is active.
        /// </summary>
        public bool IsActive { get; set; }
        /// <summary>
        /// Gets or sets the status text. Defaults to Ready.
        /// </summary>
        public string Status { get; set; } = "Ready";
    }
}