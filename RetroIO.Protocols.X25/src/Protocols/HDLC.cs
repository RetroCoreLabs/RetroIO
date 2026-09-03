using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// HDLC frame processor that handles byte stuffing, frame validation, and frame extraction
    /// </summary>
    public class HDLC
    {
        // HDLC constants
        private const byte HDLC_FLAG = 0x7E;
        private const byte HDLC_ESCAPE = 0x7D;
        private const byte HDLC_FLAG_ESC = 0x5E;
        private const byte HDLC_ESCAPE_ESC = 0x5D;
        private const byte HDLC_ESCAPE_MASK = 0x20;

        // Logger for HDLC operations
        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        // FCS (Frame Check Sequence) table for CCITT CRC-16
        private static readonly ushort[] hdlc_fcstab = {
            0x0000, 0x1189, 0x2312, 0x329b, 0x4624, 0x57ad, 0x6536, 0x74bf,
            0x8c48, 0x9dc1, 0xaf5a, 0xbed3, 0xca6c, 0xdbe5, 0xe97e, 0xf8f7,
            0x1081, 0x0108, 0x3393, 0x221a, 0x56a5, 0x472c, 0x75b7, 0x643e,
            0x9cc9, 0x8d40, 0xbfdb, 0xae52, 0xdaed, 0xcb64, 0xf9ff, 0xe876,
            0x2102, 0x308b, 0x0210, 0x1399, 0x6726, 0x76af, 0x4434, 0x55bd,
            0xad4a, 0xbcc3, 0x8e58, 0x9fd1, 0xeb6e, 0xfae7, 0xc87c, 0xd9f5,
            0x3183, 0x200a, 0x1291, 0x0318, 0x77a7, 0x662e, 0x54b5, 0x453c,
            0xbdcb, 0xac42, 0x9ed9, 0x8f50, 0xfbef, 0xea66, 0xd8fd, 0xc974,
            0x4204, 0x538d, 0x6116, 0x709f, 0x0420, 0x15a9, 0x2732, 0x36bb,
            0xce4c, 0xdfc5, 0xed5e, 0xfcd7, 0x8868, 0x99e1, 0xab7a, 0xbaf3,
            0x5285, 0x430c, 0x7197, 0x601e, 0x14a1, 0x0528, 0x37b3, 0x263a,
            0xdecd, 0xcf44, 0xfddf, 0xec56, 0x98e9, 0x8960, 0xbbfb, 0xaa72,
            0x6306, 0x728f, 0x4014, 0x519d, 0x2522, 0x34ab, 0x0630, 0x17b9,
            0xef4e, 0xfec7, 0xcc5c, 0xddd5, 0xa96a, 0xb8e3, 0x8a78, 0x9bf1,
            0x7387, 0x620e, 0x5095, 0x411c, 0x35a3, 0x242a, 0x16b1, 0x0738,
            0xffcf, 0xee46, 0xdcdd, 0xcd54, 0xb9eb, 0xa862, 0x9af9, 0x8b70,
            0x8408, 0x9581, 0xa71a, 0xb693, 0xc22c, 0xd3a5, 0xe13e, 0xf0b7,
            0x0840, 0x19c9, 0x2b52, 0x3adb, 0x4e64, 0x5fed, 0x6d76, 0x7cff,
            0x9489, 0x8500, 0xb79b, 0xa612, 0xd2ad, 0xc324, 0xf1bf, 0xe036,
            0x18c1, 0x0948, 0x3bd3, 0x2a5a, 0x5ee5, 0x4f6c, 0x7df7, 0x6c7e,
            0xa50a, 0xb483, 0x8618, 0x9791, 0xe32e, 0xf2a7, 0xc03c, 0xd1b5,
            0x2942, 0x38cb, 0x0a50, 0x1bd9, 0x6f66, 0x7eef, 0x4c74, 0x5dfd,
            0xb58b, 0xa402, 0x9699, 0x8710, 0xf3af, 0xe226, 0xd0bd, 0xc134,
            0x39c3, 0x284a, 0x1ad1, 0x0b58, 0x7fe7, 0x6e6e, 0x5cf5, 0x4d7c,
            0xc60c, 0xd785, 0xe51e, 0xf497, 0x8028, 0x91a1, 0xa33a, 0xb2b3,
            0x4a44, 0x5bcd, 0x6956, 0x78df, 0x0c60, 0x1de9, 0x2f72, 0x3efb,
            0xd68d, 0xc704, 0xf59f, 0xe416, 0x90a9, 0x8120, 0xb3bb, 0xa232,
            0x5ac5, 0x4b4c, 0x79d7, 0x685e, 0x1ce1, 0x0d68, 0x3ff3, 0x2e7a,
            0xe70e, 0xf687, 0xc41c, 0xd595, 0xa12a, 0xb0a3, 0x8238, 0x93b1,
            0x6b46, 0x7acf, 0x4854, 0x59dd, 0x2d62, 0x3ceb, 0x0e70, 0x1ff9,
            0xf78f, 0xe606, 0xd49d, 0xc514, 0xb1ab, 0xa022, 0x92b9, 0x8330,
            0x7bc7, 0x6a4e, 0x58d5, 0x495c, 0x3de3, 0x2c6a, 0x1ef1, 0x0f78
        };

        // Frame processing state
        private byte[] _receiveBuffer = new byte[2048];
        private int _receiveIndex = 0;
        private bool _inFrame = false;
        private bool _inEscape = false;

        // Events for frame processing
        public event Action<byte[], ushort>? FrameReceived;  // Complete frame with FCS

        /// <summary>
        /// Initialize HDLC processor with optional logger
        /// </summary>
        /// <param name="logger">Logger instance (optional)</param>
        public HDLC(Microsoft.Extensions.Logging.ILogger logger )
        {
            _logger = logger;
        }

        /// <summary>
        /// Calculate CRC-16 iteration for FCS
        /// </summary>
        /// <param name="crc">Current CRC value</param>
        /// <param name="b">Byte to process</param>
        /// <returns>Updated CRC value</returns>
        private ushort CrcIteration(ushort crc, byte b)
        {
            return (ushort)((crc >> 8) ^ hdlc_fcstab[(crc ^ b) & 0xff]);
        }

        /// <summary>
        /// Calculate FCS (Frame Check Sequence) for HDLC frame
        /// </summary>
        /// <param name="data">Frame data (without flags)</param>
        /// <param name="length">Length of frame data</param>
        /// <returns>16-bit FCS value</returns>
        public ushort CalculateFCS(byte[] data, int length)
        {
            ushort crc16 = 0xFFFF; // Initialize CRC
            
            for (int i = 0; i < length; i++)
            {
                crc16 = CrcIteration(crc16, data[i]);
            }
            
            return (ushort)(~crc16); // Final XOR
        }

        /// <summary>
        /// Validate FCS of received frame
        /// </summary>
        /// <param name="data">Frame data (without flags)</param>
        /// <param name="length">Length of frame data</param>
        /// <returns>True if FCS is valid</returns>
        public bool ValidateFCS(byte[] data, int length)
        {
            if (length < 2) return false; // Need at least 2 bytes for FCS
            
            // Calculate FCS for frame data (excluding the FCS bytes)
            ushort calculatedFCS = CalculateFCS(data, length - 2);
            
            // Extract FCS from frame (last 2 bytes, little-endian)
            ushort receivedFCS = (ushort)(data[length - 2] | (data[length - 1] << 8));
            
            return calculatedFCS == receivedFCS;
        }

        /// <summary>
        /// Process a received byte for HDLC frame extraction
        /// </summary>
        /// <param name="b">The received byte</param>
        public void ProcessReceivedByte(byte b)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

#if DEBUG_BYTES_RECEIVED
            // Convert byte to printable character (substitute unprintable chars with .)
            char displayChar = (b >= 32 && b <= 126) ? (char)b : '.';
            _logger.LogDebug($"BYTE: 0x{b:X2} ({displayChar}) - Frame: {(_inFrame ? "IN" : "OUT")}, Escape: {(_inEscape ? "YES" : "NO")}, Index: {_receiveIndex}");
#endif

            // HDLC flag byte
            if (b == HDLC_FLAG)
            {
                if (_inFrame && _receiveIndex > 0)
                {
                    // End of frame - process complete frame
#if DEBUG_HDLC_RECEIVED
                    _logger.LogDebug($"End of frame detected, processing {_receiveIndex} bytes");
#endif

                    byte[] frameData = new byte[_receiveIndex];
                    Array.Copy(_receiveBuffer, frameData, _receiveIndex);

                    // Log the complete frame before processing
                    string hexData = BitConverter.ToString(frameData, 0, _receiveIndex);
                    string asciiData = "";
                    for (int i = 0; i < _receiveIndex; i++)
                    {
                        asciiData += (frameData[i] >= 32 && frameData[i] <= 126) ? (char)frameData[i] : '.';
                    }

#if DEBUG_HDLC_RECEIVED
                    _logger.LogDebug($"Complete frame received: {hexData}");
                    _logger.LogDebug($"ASCII: {asciiData}");
#endif

                    // Validate and process the frame
                    if (ValidateAndProcessFrame(frameData, (ushort)_receiveIndex))
                    {
#if DEBUG_HDLC_RECEIVED
                        _logger.LogDebug("Frame processed successfully");
#endif
                        
                        // Notify subscribers of the complete frame (with FCS)
                        FrameReceived?.Invoke(frameData, (ushort)_receiveIndex);
                    }
                    else
                    {
#if DEBUG_HDLC_RECEIVED
                        _logger.LogDebug("Frame validation failed, discarded");
#endif
                    }
                }
                else if (_inFrame)
                {
#if DEBUG_HDLC_RECEIVED
                    _logger.LogDebug("End flag received but no data in frame");
#endif
                }

                // Reset frame state and prepare for next frame
                _inFrame = false;
                _receiveIndex = 0;
                _inEscape = false;

                // Start new frame (this flag becomes the start of the next frame)
                _inFrame = true;
                // Only log start if we're not at the very end of processing
#if DEBUG_HDLC_RECEIVED
                _logger.LogDebug("Frame boundary - end of previous, start of next");
#endif
                return;
            }

            if (!_inFrame)
            {
                // Log unexpected bytes outside of frames
#if DEBUG_HDLC_RECEIVED
                _logger.LogDebug($"Unexpected byte 0x{b:X2} outside frame, ignoring");
#endif
                return;
            }

            // Handle byte stuffing according to RFC 1662
            if (_inEscape)
            {
                // We're in an escape sequence, process the stuffed byte
                // RFC 1662: "Upon receipt of the two-character escape sequence, 
                // the original data is recovered by XORing the second character with 0x20"
                byte originalByte = (byte)(b ^ HDLC_ESCAPE_MASK);
                _receiveBuffer[_receiveIndex++] = originalByte;

                // Log the unstuffing operation
                if (originalByte == HDLC_FLAG)
                {
#if DEBUG_HDLC_RECEIVED
                    _logger.LogDebug($"Byte unstuffing: {HDLC_ESCAPE:X2} {b:X2} -> {HDLC_FLAG:X2} (position {_receiveIndex-1})");
#endif
                }
                else if (originalByte == HDLC_ESCAPE)
                {
#if DEBUG_HDLC_RECEIVED
                    _logger.LogDebug($"Byte unstuffing: {HDLC_ESCAPE:X2} {b:X2} -> {HDLC_ESCAPE:X2} (position {_receiveIndex-1})");
#endif
                }
                else if (originalByte < 0x20)
                {
#if DEBUG_HDLC_RECEIVED
                    _logger.LogDebug($"Control char unstuffing: {HDLC_ESCAPE:X2} {b:X2} -> 0x{originalByte:X2} (position {_receiveIndex-1})");
#endif
                }
                else
                {
#if DEBUG_HDLC_RECEIVED
                    _logger.LogDebug($"General unstuffing: {HDLC_ESCAPE:X2} {b:X2} -> 0x{originalByte:X2} (position {_receiveIndex-1})");
#endif
                }

                _inEscape = false;
            }
            else if (b == HDLC_ESCAPE)
            {
                // Start of escape sequence
                _inEscape = true;
#if DEBUG_HDLC_RECEIVED
                _logger.LogDebug($"Escape character {HDLC_ESCAPE:X2} detected, waiting for stuffed byte");
#endif
            }
            else
            {
                // Regular byte
                _receiveBuffer[_receiveIndex++] = b;
#if DEBUG_HDLC_RECEIVED
                _logger.LogDebug($"Regular byte 0x{b:X2} added to frame (position {_receiveIndex-1})");
#endif
            }

            // Prevent buffer overflow
            if (_receiveIndex >= _receiveBuffer.Length)
            {
#if DEBUG_HDLC_RECEIVED
                _logger.LogDebug("Buffer overflow detected, discarding frame");
#endif
                _inFrame = false;
                _receiveIndex = 0;
                _inEscape = false;
            }
        }

        /// <summary>
        /// Send a frame with HDLC byte stuffing according to RFC 1662
        /// </summary>
        /// <param name="data">The frame data to send</param>
        /// <param name="length">Length of the frame data</param>
        /// <returns>The byte-stuffed frame ready for transmission</returns>
        public byte[] CreateStuffedFrame(byte[] data, ushort length)
        {
            List<byte> stuffedData = new List<byte>();
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

            // Calculate FCS for the frame data
            ushort fcs = CalculateFCS(data, length);
            byte fcsLow = (byte)(fcs & 0xFF);
            byte fcsHigh = (byte)((fcs >> 8) & 0xFF);

#if DEBUG_HDLC_SENDING
            _logger.LogDebug($"Calculated FCS: 0x{fcs:X4} (0x{fcsLow:X2} 0x{fcsHigh:X2})");
#endif

            // Start flag
            stuffedData.Add(HDLC_FLAG);
#if DEBUG_HDLC_SENDING
            _logger.LogDebug($"Adding start flag {HDLC_FLAG:X2}");
#endif

            // Stuff data bytes according to RFC 1662
            for (int i = 0; i < length; i++)
            {
                byte b = data[i];
                if (b == HDLC_FLAG)  // Flag sequence
                {
                    // Stuff HDLC_FLAG as HDLC_ESCAPE, HDLC_FLAG_ESC
                    stuffedData.Add(HDLC_ESCAPE);
                    stuffedData.Add(HDLC_FLAG_ESC);
#if DEBUG_HDLC_SENDING
                    _logger.LogDebug($"Byte stuffing: {HDLC_FLAG:X2} -> {HDLC_ESCAPE:X2} {HDLC_FLAG_ESC:X2}");
#endif
                }
                else if (b == HDLC_ESCAPE)  // Escape sequence
                {
                    // Stuff HDLC_ESCAPE as HDLC_ESCAPE, HDLC_ESCAPE_ESC
                    stuffedData.Add(HDLC_ESCAPE);
                    stuffedData.Add(HDLC_ESCAPE_ESC);
#if DEBUG_HDLC_SENDING
                    _logger.LogDebug($"Byte stuffing: {HDLC_ESCAPE:X2} -> {HDLC_ESCAPE:X2} {HDLC_ESCAPE_ESC:X2}");
#endif
                }
                else if (b < 0x20)  // Control characters (optional in RFC 1662)
                {
                    // Stuff control character as HDLC_ESCAPE, (data ^ HDLC_ESCAPE_MASK)
                    stuffedData.Add(HDLC_ESCAPE);
                    stuffedData.Add((byte)(b ^ HDLC_ESCAPE_MASK));
#if DEBUG_HDLC_SENDING
                    _logger.LogDebug($"Control char stuffing: 0x{b:X2} -> {HDLC_ESCAPE:X2} {(byte)(b ^ HDLC_ESCAPE_MASK):X2}");
#endif
                }
                else
                {
                    // Send as-is
                    stuffedData.Add(b);
#if DEBUG_HDLC_SENDING
                    _logger.LogDebug($"Regular byte 0x{b:X2} added to stuffed data");
#endif
                }
            }

            // Add FCS bytes (little-endian)
            stuffedData.Add(fcsLow);
            stuffedData.Add(fcsHigh);
#if DEBUG_HDLC_SENDING
            _logger.LogDebug($"Added FCS bytes: 0x{fcsLow:X2} 0x{fcsHigh:X2}");
#endif

            // End flag
            stuffedData.Add(HDLC_FLAG);
#if DEBUG_HDLC_SENDING
            _logger.LogDebug($"Adding end flag {HDLC_FLAG:X2}");
            _logger.LogDebug($"Complete stuffed frame: {BitConverter.ToString(stuffedData.ToArray())}");
#endif

            return stuffedData.ToArray();
        }

        /// <summary>
        /// Validates and processes a complete HDLC frame
        /// </summary>
        /// <param name="frameData">The complete frame data (without flags)</param>
        /// <param name="length">Length of the frame data</param>
        /// <returns>True if frame was valid and processed, false otherwise</returns>
        private bool ValidateAndProcessFrame(byte[] frameData, ushort length)
        {
            // Basic frame validation
            if (length < 4) // Minimum: Address + Control + FCS (2 bytes)
            {
                _logger.LogWarning($"Frame too short ({length} bytes), minimum 4 bytes required (Address + Control + FCS)");
                return false;
            }

            // Validate FCS first
            if (!ValidateFCS(frameData, length))
            {
                _logger.LogWarning("Frame FCS validation failed");
                return false;
            }

            // Remove FCS from frame for further processing
            ushort dataLength = (ushort)(length - 2);
            byte[] frameWithoutFCS = new byte[dataLength];
            Array.Copy(frameData, frameWithoutFCS, dataLength);

            // Validate address field (should be 0x01 for DCE->DTE or 0x03 for DTE->DCE)
            byte address = frameWithoutFCS[0];
            if (address != 0x01 && address != 0x03)
            {
                _logger.LogWarning($"Invalid address field 0x{address:X2}, expected 0x01 or 0x03");
                return false;
            }

            // Validate control field
            byte control = frameWithoutFCS[1];
            bool isValidControl = false;

            // Check if it's a valid LAPB control field
            if ((control & 0x01) == 0x01) // Information frame
            {
                isValidControl = true;
            }
            else if ((control & 0x03) == 0x01) // Supervisory frame
            {
                byte sType = (byte)(control & 0x0F);
                if (sType == 0x01 || sType == 0x05 || sType == 0x09) // RR, RNR, REJ
                {
                    isValidControl = true;
                }
            }
            else // Unnumbered frame
            {
                byte uType = (byte)(control & 0xEF); // Remove P/F bit
                if (uType == 0x2F || uType == 0x43 || uType == 0x63 || uType == 0x0F || uType == 0x87) // SABM, DISC, UA, DM, FRMR
                {
                    isValidControl = true;
                }
            }

            if (!isValidControl)
            {
                _logger.LogWarning($"Invalid control field 0x{control:X2}");
                return false;
            }

            // Frame passed validation
            _logger.LogInformation("Frame validation passed, processing with LAPB");
            return true;
        }


    }
}
