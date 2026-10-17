//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using RetroIO.Link.Lapb;
using RetroIO.Link.Models;

namespace RetroIO.Link
{
    /// <summary>
    /// LAPB statistics for links that implement LAPB
    /// </summary>
    public class LapbStatistics
    {
        public byte VS { get; set; }
        public byte VR { get; set; }
        public byte VA { get; set; }
        public int WindowSize { get; set; }
        public int UnackedFrameCount { get; set; }
        public long IFramesSent { get; set; }
        public long IFramesReceived { get; set; }
        public long SFramesSent { get; set; }
        public long SFramesReceived { get; set; }
        public long UFramesSent { get; set; }
        public long UFramesReceived { get; set; }
        public long Retransmissions { get; set; }
        public long RejectionsSent { get; set; }
        public long RejectionsReceived { get; set; }
        public long T1Timeouts { get; set; }
        public long T2Timeouts { get; set; }
    }

    /// <summary>
    /// Event args for LAPB state changes
    /// </summary>
    public class LapbStateChangedEventArgs : EventArgs
    {
        public string LinkName { get; set; } = string.Empty;
        public LapbState OldState { get; set; }
        public LapbState NewState { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Interface for links that implement LAPB (Layer 2 protocol).
    /// Serial and TCP-LAPB links implement this; XOT does not (per RFC 1613).
    /// </summary>
    public interface ILapbCapable
    {
        /// <summary>
        /// Current LAPB state
        /// </summary>
        LapbState LapbState { get; }

        /// <summary>
        /// Get current sequence numbers (V(S), V(R), V(A))
        /// </summary>
        (byte VS, byte VR, byte VA) GetSequenceNumbers();

        /// <summary>
        /// Initiate LAPB connection (send SABM)
        /// </summary>
        void Connect();

        /// <summary>
        /// Initiate LAPB disconnection (send DISC)
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Get LAPB-specific statistics
        /// </summary>
        LapbStatistics GetLapbStatistics();

        /// <summary>
        /// Raised when LAPB state changes
        /// </summary>
        event EventHandler<LapbStateChangedEventArgs> LapbStateChanged;

        /// <summary>
        /// Inject a raw frame into the LAPB layer (for testing/simulation)
        /// </summary>
        /// <param name="frameData">Frame data (Address + Control + Information)</param>
        /// <param name="length">Frame length</param>
        void InjectFrame(byte[] frameData, int length);

        /// <summary>
        /// Send raw LAPB frame
        /// </summary>
        /// <param name="frame">Frame to send</param>
        /// <param name="length">Frame length</param>
        void SendFrame(byte[] frame, int length);

        /// <summary>
        /// Raised when a frame is received and processed by LAPB
        /// </summary>
        event EventHandler<LapbFrameEventArgs>? FrameReceived;

        /// <summary>
        /// Raised when a frame is transmitted by LAPB
        /// </summary>
        event EventHandler<LapbFrameEventArgs>? FrameTransmitted;
    }
}
