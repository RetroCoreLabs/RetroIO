//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging;

namespace RetroIO.Link
{
    /// <summary>
    /// Detects protocol type from LAPB I-frame payloads (X.25 or SINTRAN).
    /// SINTRAN frames are identified by the 0x21 0x13 marker sequence at the start.
    /// Note: This class detects/discriminates protocols, it does not route them.
    /// </summary>
    public class ProtocolDetector
    {
        private readonly ILogger _logger;

        // SINTRAN routing protocol markers
        private const byte SINTRAN_MARKER_1 = 0x21;
        private const byte SINTRAN_MARKER_2 = 0x13;

        // SINTRAN protocol IDs (from SintranBinaryProtocol)
        private const byte SINTRAN_TAD = 0xDD;
        private const byte SINTRAN_PAD = 0xDA;
        private const byte SINTRAN_ROUTING = 0xDE;

        /// <summary>
        /// Create a new protocol detector
        /// </summary>
        public ProtocolDetector(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Detect the protocol of a LAPB I-frame payload
        /// </summary>
        /// <param name="payload">LAPB I-frame information field</param>
        /// <param name="length">Payload length</param>
        /// <param name="mode">Protocol detection mode for this link</param>
        /// <returns>Detected protocol type</returns>
        public DetectedProtocol DetectProtocol(byte[] payload, int length, ProtocolDetectionMode mode)
        {
            if (payload == null || length == 0)
            {
                return DetectedProtocol.X25;
            }

            switch (mode)
            {
                case ProtocolDetectionMode.X25Only:
                    return DetectedProtocol.X25;

                case ProtocolDetectionMode.SintranOnly:
                    return DetectedProtocol.Sintran;

                case ProtocolDetectionMode.AutoDetect:
                    return DetectByMarkers(payload, length);

                default:
                    return DetectedProtocol.X25;
            }
        }

        /// <summary>
        /// Detect protocol by checking for SINTRAN markers
        /// </summary>
        private DetectedProtocol DetectByMarkers(byte[] payload, int length)
        {
            // SINTRAN routing frames start with 0x21 0x13
            if (length >= 2 && payload[0] == SINTRAN_MARKER_1 && payload[1] == SINTRAN_MARKER_2)
            {
                _logger.LogDebug("ProtocolDetector: Detected SINTRAN frame (markers 0x21 0x13)");
                return DetectedProtocol.Sintran;
            }

            // Everything else is X.25
            return DetectedProtocol.X25;
        }

        /// <summary>
        /// Extract SINTRAN protocol ID from a SINTRAN frame
        /// </summary>
        /// <param name="payload">SINTRAN frame payload (with markers)</param>
        /// <param name="length">Payload length</param>
        /// <returns>Protocol ID (0xDD=TAD, 0xDA=PAD, 0xDE=Routing) or 0 if invalid</returns>
        public byte ExtractSintranProtocolId(byte[] payload, int length)
        {
            // SINTRAN frame format:
            // [0] = 0x21 (marker 1)
            // [1] = 0x13 (marker 2)
            // [2] = Protocol ID (0xDD=TAD, 0xDA=PAD, 0xDE=Routing)
            // [3...] = Protocol-specific data

            if (length < 3)
            {
                return 0;
            }

            if (payload[0] != SINTRAN_MARKER_1 || payload[1] != SINTRAN_MARKER_2)
            {
                return 0;
            }

            return payload[2];
        }

        /// <summary>
        /// Get human-readable name for SINTRAN protocol ID
        /// </summary>
        public string GetSintranProtocolName(byte protocolId)
        {
            return protocolId switch
            {
                SINTRAN_TAD => "TAD",
                SINTRAN_PAD => "PAD",
                SINTRAN_ROUTING => "Routing",
                _ => $"Unknown (0x{protocolId:X2})"
            };
        }

        /// <summary>
        /// Check if payload looks like a valid X.25 packet
        /// </summary>
        /// <param name="payload">Payload to check</param>
        /// <param name="length">Payload length</param>
        /// <returns>True if appears to be valid X.25</returns>
        public bool IsValidX25Packet(byte[] payload, int length)
        {
            // Minimum X.25 packet: GFI (1) + LCN (1) + PTI (1) = 3 bytes
            if (length < 3)
            {
                return false;
            }

            // Check GFI (General Format Identifier)
            byte gfi = (byte)(payload[0] >> 4);

            // Valid GFI values for modulo 8 and modulo 128
            // 0001 = modulo 8, no D-bit
            // 0010 = modulo 8, D-bit
            // 0011 = modulo 128, no D-bit
            // 0100 = modulo 128, D-bit
            if (gfi == 0x01 || gfi == 0x02 || gfi == 0x03 || gfi == 0x04)
            {
                return true;
            }

            // Also accept 0x00 for some implementations
            if (gfi == 0x00)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Create SINTRAN frame with proper markers
        /// </summary>
        /// <param name="protocolId">SINTRAN protocol ID</param>
        /// <param name="data">Protocol-specific data</param>
        /// <param name="dataLength">Data length</param>
        /// <returns>Complete SINTRAN frame with markers</returns>
        public byte[] CreateSintranFrame(byte protocolId, byte[] data, int dataLength)
        {
            var frame = new byte[3 + dataLength];
            frame[0] = SINTRAN_MARKER_1;
            frame[1] = SINTRAN_MARKER_2;
            frame[2] = protocolId;

            if (dataLength > 0 && data != null)
            {
                Array.Copy(data, 0, frame, 3, dataLength);
            }

            return frame;
        }
    }
}
