using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace RetroIO.Link
{
    /// <summary>
    /// Event args for frame events
    /// </summary>
    public class FrameEventArgs : EventArgs
    {
        /// <summary>Frame data (unstuffed, FCS stripped)</summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
        /// <summary>Frame length</summary>
        public int Length { get; set; }
        /// <summary>Whether FCS was valid</summary>
        public bool FcsValid { get; set; }
        /// <summary>Timestamp</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Handles HDLC byte-stuffing, frame detection, and FCS calculation/validation.
    /// Used by SerialLink and TcpLapbLink for physical layer framing.
    /// </summary>
    public class HdlcFramer
    {
        private readonly ILogger _logger;
        private readonly string _linkName;

        // HDLC constants
        private const byte FLAG_BYTE = 0x7E;
        private const byte ESCAPE_BYTE = 0x7D;
        private const byte ESCAPE_MASK = 0x20;

        // CRC-16-CCITT constants
        private const ushort CRC_INIT = 0xFFFF;
        private const ushort CRC_POLY = 0x8408; // Reversed polynomial

        // Frame receive state
        private readonly List<byte> _frameBuffer;
        private bool _inFrame = false;
        private bool _escapeNext = false;

        /// <summary>
        /// Create a new HDLC framer
        /// </summary>
        public HdlcFramer(ILogger logger, string linkName)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _linkName = linkName ?? throw new ArgumentNullException(nameof(linkName));
            _frameBuffer = new List<byte>(256);
        }

        /// <summary>
        /// Raised when a complete frame is received (FCS stripped, unstuffed)
        /// </summary>
        public event EventHandler<FrameEventArgs>? FrameReceived;

        /// <summary>
        /// Raised when stuffed bytes should be sent to physical layer
        /// </summary>
        public event EventHandler<byte[]>? BytesToSend;

        /// <summary>
        /// Process received raw bytes (from serial port or TCP)
        /// </summary>
        /// <param name="buffer">Received bytes</param>
        /// <param name="length">Number of bytes</param>
        public void ProcessBytes(byte[] buffer, int length)
        {
            if (buffer == null || length <= 0) return;

            for (int i = 0; i < length; i++)
            {
                ProcessByte(buffer[i]);
            }
        }

        /// <summary>
        /// Process a single received byte
        /// </summary>
        private void ProcessByte(byte b)
        {
            if (_escapeNext)
            {
                // Unescape the byte
                byte unescaped = (byte)(b ^ ESCAPE_MASK);
                _frameBuffer.Add(unescaped);
                _escapeNext = false;
            }
            else if (b == ESCAPE_BYTE)
            {
                // Next byte is escaped
                _escapeNext = true;
            }
            else if (b == FLAG_BYTE)
            {
                if (_inFrame)
                {
                    // End of frame
                    if (_frameBuffer.Count >= 4) // Minimum: Address + Control + 2 FCS
                    {
                        ProcessCompleteFrame();
                    }
                    else if (_frameBuffer.Count > 0)
                    {
                        _logger.LogWarning("HDLC[{Link}]: Short frame discarded ({Count} bytes)",
                            _linkName, _frameBuffer.Count);
                    }

                    _frameBuffer.Clear();
                    _inFrame = false;
                }
                else
                {
                    // Start of frame
                    _inFrame = true;
                    _frameBuffer.Clear();
                    _escapeNext = false;
                }
            }
            else if (_inFrame)
            {
                // Add byte to frame
                _frameBuffer.Add(b);
            }
            // Ignore bytes outside frames
        }

        /// <summary>
        /// Process a complete unstuffed frame (with FCS)
        /// </summary>
        private void ProcessCompleteFrame()
        {
            var frameWithFcs = _frameBuffer.ToArray();
            int frameLength = frameWithFcs.Length;

            // Validate FCS
            bool fcsValid = ValidateFcs(frameWithFcs, frameLength);

            if (fcsValid)
            {
                // Strip FCS (last 2 bytes)
                int dataLength = frameLength - 2;
                var frameData = new byte[dataLength];
                Array.Copy(frameWithFcs, 0, frameData, 0, dataLength);

                _logger.LogDebug("HDLC[{Link}]: Frame received ({Length} bytes, FCS valid)",
                    _linkName, dataLength);

                FrameReceived?.Invoke(this, new FrameEventArgs
                {
                    Data = frameData,
                    Length = dataLength,
                    FcsValid = true,
                    Timestamp = DateTime.UtcNow
                });
            }
            else
            {
                _logger.LogWarning("HDLC[{Link}]: Frame FCS error ({Length} bytes)",
                    _linkName, frameLength);

                // Still report the frame (FCS invalid)
                int dataLength = Math.Max(0, frameLength - 2);
                var frameData = new byte[dataLength];
                if (dataLength > 0)
                {
                    Array.Copy(frameWithFcs, 0, frameData, 0, dataLength);
                }

                FrameReceived?.Invoke(this, new FrameEventArgs
                {
                    Data = frameData,
                    Length = dataLength,
                    FcsValid = false,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        /// <summary>
        /// Send a frame with byte-stuffing and FCS
        /// </summary>
        /// <param name="frameData">Frame data (Address + Control + Information)</param>
        /// <param name="length">Frame length</param>
        public void SendFrame(byte[] frameData, int length)
        {
            if (frameData == null || length <= 0)
            {
                _logger.LogWarning("HDLC[{Link}]: Invalid frame data for transmission", _linkName);
                return;
            }

            // Calculate FCS
            ushort fcs = CalculateFcs(frameData, length);

            // Build stuffed frame
            var stuffedData = new List<byte>(length * 2 + 6); // Worst case: all bytes escaped + flags + FCS

            // Start flag
            stuffedData.Add(FLAG_BYTE);

            // Stuff frame data
            for (int i = 0; i < length; i++)
            {
                StuffByte(stuffedData, frameData[i]);
            }

            // Stuff FCS (LSB first)
            StuffByte(stuffedData, (byte)(fcs & 0xFF));
            StuffByte(stuffedData, (byte)((fcs >> 8) & 0xFF));

            // End flag
            stuffedData.Add(FLAG_BYTE);

            var finalData = stuffedData.ToArray();

            _logger.LogDebug("HDLC[{Link}]: Sending frame ({Length} bytes, stuffed to {StuffedLength})",
                _linkName, length, finalData.Length);

            BytesToSend?.Invoke(this, finalData);
        }

        /// <summary>
        /// Add a byte to the output buffer with escaping if needed
        /// </summary>
        private void StuffByte(List<byte> buffer, byte b)
        {
            if (b == FLAG_BYTE || b == ESCAPE_BYTE || b < 0x20)
            {
                buffer.Add(ESCAPE_BYTE);
                buffer.Add((byte)(b ^ ESCAPE_MASK));
            }
            else
            {
                buffer.Add(b);
            }
        }

        /// <summary>
        /// Calculate CRC-16-CCITT FCS
        /// </summary>
        private ushort CalculateFcs(byte[] data, int length)
        {
            ushort crc = CRC_INIT;

            for (int i = 0; i < length; i++)
            {
                crc = UpdateCrc(crc, data[i]);
            }

            // One's complement
            return (ushort)(crc ^ 0xFFFF);
        }

        /// <summary>
        /// Validate FCS of received frame
        /// </summary>
        private bool ValidateFcs(byte[] frameWithFcs, int length)
        {
            if (length < 4) return false; // Minimum: Address + Control + 2 FCS

            ushort crc = CRC_INIT;

            // Include all bytes including FCS
            for (int i = 0; i < length; i++)
            {
                crc = UpdateCrc(crc, frameWithFcs[i]);
            }

            // Good FCS results in magic value
            return crc == 0xF0B8;
        }

        /// <summary>
        /// Update CRC with one byte (bit-by-bit method)
        /// </summary>
        private ushort UpdateCrc(ushort crc, byte b)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x0001) != 0)
                {
                    crc = (ushort)((crc >> 1) ^ CRC_POLY);
                }
                else
                {
                    crc = (ushort)(crc >> 1);
                }
            }
            return crc;
        }

        /// <summary>
        /// Reset the framer state
        /// </summary>
        public void Reset()
        {
            _frameBuffer.Clear();
            _inFrame = false;
            _escapeNext = false;
        }
    }
}
