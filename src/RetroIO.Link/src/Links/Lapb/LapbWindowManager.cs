using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RetroIO.Link.Lapb
{
    /// <summary>
    /// Represents an unacknowledged I-frame for retransmission
    /// </summary>
    public class UnackedFrame
    {
        /// <summary>Complete LAPB frame data (Address + Control + Information)</summary>
        public byte[] FrameData { get; set; } = Array.Empty<byte>();
        /// <summary>N(S) sequence number of this frame</summary>
        public byte NS { get; set; }
        /// <summary>Original N(R) when transmitted (may be updated on retransmit)</summary>
        public byte NR { get; set; }
        /// <summary>Time of last transmission</summary>
        public DateTime TransmitTime { get; set; }
        /// <summary>Number of times this frame has been transmitted</summary>
        public int TransmitCount { get; set; }
    }

    /// <summary>
    /// Manages LAPB window enforcement, frame tracking, and Go-Back-N retransmission.
    /// Implements ITU-T X.25 LAPB window logic with modulo-8 sequence numbers.
    /// </summary>
    public class LapbWindowManager
    {
        private readonly ILogger _logger;
        private readonly byte _windowSize;
        private readonly byte _modulo;
        private readonly Dictionary<byte, UnackedFrame> _unackedFrames;

        /// <summary>
        /// Create a new LAPB window manager
        /// </summary>
        /// <param name="logger">Logger</param>
        /// <param name="windowSize">Window size K (1-7 for modulo 8)</param>
        /// <param name="modulo">Sequence number modulo (8 or 128)</param>
        public LapbWindowManager(ILogger logger, byte windowSize = 7, byte modulo = 8)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (windowSize < 1 || windowSize > modulo - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(windowSize),
                    $"Window size must be between 1 and {modulo - 1}");
            }

            _windowSize = windowSize;
            _modulo = modulo;
            _unackedFrames = new Dictionary<byte, UnackedFrame>();
        }

        /// <summary>
        /// Current window size K
        /// </summary>
        public byte WindowSize => _windowSize;

        /// <summary>
        /// Sequence number modulo (8 or 128)
        /// </summary>
        public byte Modulo => _modulo;

        /// <summary>
        /// Number of currently unacknowledged frames
        /// </summary>
        public int UnackedFrameCount => _unackedFrames.Count;

        /// <summary>
        /// Check if window allows sending (outstanding frames less than K)
        /// </summary>
        /// <param name="vs">Current V(S) - Send sequence number</param>
        /// <param name="va">Current V(A) - Acknowledge sequence number</param>
        /// <returns>True if window allows sending</returns>
        public bool CanSend(byte vs, byte va)
        {
            int outstanding = (vs - va + _modulo) % _modulo;
            bool canSend = outstanding < _windowSize;

            if (!canSend)
            {
                _logger.LogDebug("LAPB Window: Cannot send - window full. V(S)={VS}, V(A)={VA}, outstanding={Outstanding}, K={K}",
                    vs, va, outstanding, _windowSize);
            }

            return canSend;
        }

        /// <summary>
        /// Track a frame for potential retransmission.
        /// Call this BEFORE incrementing V(S).
        /// </summary>
        /// <param name="ns">N(S) of the frame being sent</param>
        /// <param name="nr">N(R) of the frame being sent</param>
        /// <param name="frameData">Complete frame data</param>
        public void TrackFrame(byte ns, byte nr, byte[] frameData)
        {
            if (frameData == null || frameData.Length == 0)
            {
                _logger.LogWarning("LAPB Window: Cannot track null or empty frame");
                return;
            }

            var frame = new UnackedFrame
            {
                FrameData = new byte[frameData.Length],
                NS = ns,
                NR = nr,
                TransmitTime = DateTime.UtcNow,
                TransmitCount = 1
            };
            Array.Copy(frameData, frame.FrameData, frameData.Length);

            _unackedFrames[ns] = frame;

            _logger.LogDebug("LAPB Window: Tracking frame N(S)={NS}, total unacked={Count}",
                ns, _unackedFrames.Count);
        }

        /// <summary>
        /// Acknowledge frames up to but not including N(R).
        /// Called when receiving valid N(R) in I/S frames.
        /// </summary>
        /// <param name="nr">Received N(R) - acknowledges all frames with N(S) less than N(R)</param>
        /// <param name="va">Current V(A) value</param>
        /// <returns>Number of frames acknowledged</returns>
        public int AcknowledgeFrames(byte nr, byte va)
        {
            int acknowledged = 0;
            var toRemove = new List<byte>();

            // Acknowledge all frames with N(S) in range [V(A), N(R))
            foreach (byte ns in _unackedFrames.Keys)
            {
                bool shouldAck;
                if (va <= nr)
                {
                    // Normal case: V(A) <= N(S) < N(R)
                    shouldAck = ns >= va && ns < nr;
                }
                else
                {
                    // Wrap-around case
                    shouldAck = ns >= va || ns < nr;
                }

                if (shouldAck)
                {
                    toRemove.Add(ns);
                    acknowledged++;
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                _unackedFrames.Remove(toRemove[i]);
            }

            if (acknowledged > 0)
            {
                _logger.LogDebug("LAPB Window: Acknowledged {Count} frames up to N(R)={NR}, remaining unacked={Remaining}",
                    acknowledged, nr, _unackedFrames.Count);
            }

            return acknowledged;
        }

        /// <summary>
        /// Get frames for Go-Back-N retransmission starting from N(R).
        /// Called when REJ received or T1 timeout.
        /// </summary>
        /// <param name="nr">Starting sequence number (received N(R) from REJ)</param>
        /// <param name="currentVs">Current V(S) value</param>
        /// <param name="newNr">New N(R) value to use for retransmitted frames</param>
        /// <returns>List of frames to retransmit with updated control bytes</returns>
        public List<byte[]> GetFramesForRetransmission(byte nr, byte currentVs, byte newNr)
        {
            var frames = new List<byte[]>();

            // Collect frames from nr to currentVs
            byte seq = nr;
            while (seq != currentVs)
            {
                if (_unackedFrames.TryGetValue(seq, out var frame))
                {
                    // Create copy with updated N(R)
                    var retransmitFrame = new byte[frame.FrameData.Length];
                    Array.Copy(frame.FrameData, retransmitFrame, frame.FrameData.Length);

                    // Update control byte with new N(R) (preserve N(S) and P/F)
                    if (retransmitFrame.Length >= 2)
                    {
                        byte control = retransmitFrame[1];
                        // For I-frame: bits 7-5 = N(R), bit 4 = P/F, bits 3-1 = N(S), bit 0 = 0
                        control = (byte)((control & 0x1F) | (newNr << 5));
                        retransmitFrame[1] = control;
                    }

                    frames.Add(retransmitFrame);

                    // Update tracking info
                    frame.TransmitCount++;
                    frame.TransmitTime = DateTime.UtcNow;
                    frame.NR = newNr;
                }

                seq = (byte)((seq + 1) % _modulo);
            }

            if (frames.Count > 0)
            {
                _logger.LogInformation("LAPB Window: Go-Back-N retransmission of {Count} frames from N(R)={NR}",
                    frames.Count, nr);
            }

            return frames;
        }

        /// <summary>
        /// Get the oldest unacknowledged frame for selective retransmission.
        /// Called on T1 timeout.
        /// </summary>
        /// <returns>Oldest unacked frame, or null if none</returns>
        public UnackedFrame? GetOldestUnackedFrame()
        {
            if (_unackedFrames.Count == 0)
                return null;

            UnackedFrame? oldest = null;
            foreach (var frame in _unackedFrames.Values)
            {
                if (oldest == null || frame.TransmitTime < oldest.TransmitTime)
                {
                    oldest = frame;
                }
            }

            return oldest;
        }

        /// <summary>
        /// Check if a specific frame has exceeded retry limit
        /// </summary>
        /// <param name="ns">Sequence number of frame to check</param>
        /// <param name="maxRetries">Maximum allowed retries (N2)</param>
        /// <returns>True if retry limit exceeded</returns>
        public bool IsRetryLimitExceeded(byte ns, int maxRetries)
        {
            if (_unackedFrames.TryGetValue(ns, out var frame))
            {
                return frame.TransmitCount > maxRetries;
            }
            return false;
        }

        /// <summary>
        /// Check if any frame has exceeded retry limit
        /// </summary>
        /// <param name="maxRetries">Maximum allowed retries (N2)</param>
        /// <returns>True if any frame exceeded retry limit</returns>
        public bool AnyRetryLimitExceeded(int maxRetries)
        {
            foreach (var frame in _unackedFrames.Values)
            {
                if (frame.TransmitCount > maxRetries)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Clear all tracked frames (on link reset)
        /// </summary>
        public void Clear()
        {
            int count = _unackedFrames.Count;
            _unackedFrames.Clear();

            if (count > 0)
            {
                _logger.LogDebug("LAPB Window: Cleared {Count} unacked frames", count);
            }
        }

        /// <summary>
        /// Validate that N(R) is in valid range [V(A), V(S)]
        /// </summary>
        /// <param name="nr">Received N(R) to validate</param>
        /// <param name="va">Current V(A)</param>
        /// <param name="vs">Current V(S)</param>
        /// <returns>True if N(R) is valid</returns>
        public bool IsValidNR(byte nr, byte va, byte vs)
        {
            if (va == vs)
            {
                // No outstanding frames - N(R) should equal V(A)
                return nr == va;
            }

            // N(R) should be in range [V(A), V(S)]
            if (va <= vs)
            {
                return nr >= va && nr <= vs;
            }
            else
            {
                // Wrap-around case
                return nr >= va || nr <= vs;
            }
        }
    }
}
