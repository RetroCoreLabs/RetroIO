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
        /// <summary>
        /// Gets or sets the send state variable V(S).
        /// </summary>
        public byte VS { get; set; }
        /// <summary>
        /// Gets or sets the receive state variable V(R).
        /// </summary>
        public byte VR { get; set; }
        /// <summary>
        /// Gets or sets the acknowledge state variable V(A).
        /// </summary>
        public byte VA { get; set; }
        /// <summary>
        /// Gets or sets the LAPB window size.
        /// </summary>
        public int WindowSize { get; set; }
        /// <summary>
        /// Gets or sets the number of sent I-frames not yet acknowledged.
        /// </summary>
        public int UnackedFrameCount { get; set; }
        /// <summary>
        /// Gets or sets the number of I-frames sent.
        /// </summary>
        public long IFramesSent { get; set; }
        /// <summary>
        /// Gets or sets the number of I-frames received.
        /// </summary>
        public long IFramesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of S-frames sent.
        /// </summary>
        public long SFramesSent { get; set; }
        /// <summary>
        /// Gets or sets the number of S-frames received.
        /// </summary>
        public long SFramesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of U-frames sent.
        /// </summary>
        public long UFramesSent { get; set; }
        /// <summary>
        /// Gets or sets the number of U-frames received.
        /// </summary>
        public long UFramesReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of retransmissions.
        /// </summary>
        public long Retransmissions { get; set; }
        /// <summary>
        /// Gets or sets the number of rejections sent.
        /// </summary>
        public long RejectionsSent { get; set; }
        /// <summary>
        /// Gets or sets the number of rejections received.
        /// </summary>
        public long RejectionsReceived { get; set; }
        /// <summary>
        /// Gets or sets the number of T1 timer expiries.
        /// </summary>
        public long T1Timeouts { get; set; }
        /// <summary>
        /// Gets or sets the number of T2 timer expiries.
        /// </summary>
        public long T2Timeouts { get; set; }
    }

    /// <summary>
    /// Event args for LAPB state changes
    /// </summary>
    public class LapbStateChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets or sets the name of the link whose state changed.
        /// </summary>
        public string LinkName { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the state before the change.
        /// </summary>
        public LapbState OldState { get; set; }
        /// <summary>
        /// Gets or sets the state after the change.
        /// </summary>
        public LapbState NewState { get; set; }
        /// <summary>
        /// Gets or sets the reason for the change.
        /// </summary>
        public string Reason { get; set; } = string.Empty;
        /// <summary>
        /// Gets or sets the time of the change. Defaults to the time the object was created (UTC).
        /// </summary>
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
        /// <param name="frameData">
        /// Frame data (Address + Control + Information)
        /// </param>
        /// <param name="length">
        /// Frame length
        /// </param>
        void InjectFrame(byte[] frameData, int length);

        /// <summary>
        /// Send raw LAPB frame
        /// </summary>
        /// <param name="frame">
        /// Frame to send
        /// </param>
        /// <param name="length">
        /// Frame length
        /// </param>
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
