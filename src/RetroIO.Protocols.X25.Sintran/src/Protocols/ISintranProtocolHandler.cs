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
        public ushort LCN { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string Protocol { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Statistics for SINTRAN protocol handlers
    /// </summary>
    public class SintranHandlerStats
    {
        public string Protocol { get; set; } = "";
        public ushort LCN { get; set; }
        public int MessagesReceived { get; set; }
        public int MessagesSent { get; set; }
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public DateTime LastActivity { get; set; }
        public bool IsActive { get; set; }
        public string Status { get; set; } = "Ready";
    }
}