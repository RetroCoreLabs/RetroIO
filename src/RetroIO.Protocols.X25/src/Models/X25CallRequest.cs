//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// Represents an X.25 CALL REQUEST packet with parsed fields
    /// </summary>
    public class X25CallRequest
    {
        /// <summary>
        /// Called DTE address
        /// </summary>
        public string CalledAddress { get; set; } = "";

        /// <summary>
        /// Calling DTE address
        /// </summary>
        public string CallingAddress { get; set; } = "";

        /// <summary>
        /// Facilities data
        /// </summary>
        public byte[] Facilities { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Call User Data (CUD)
        /// </summary>
        public byte[] CallUserData { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Called address length
        /// </summary>
        public byte CalledAddressLength { get; set; }

        /// <summary>
        /// Calling address length
        /// </summary>
        public byte CallingAddressLength { get; set; }

        /// <summary>
        /// Facilities length
        /// </summary>
        public byte FacilitiesLength { get; set; }

        /// <summary>
        /// Call User Data length
        /// </summary>
        public byte CallUserDataLength { get; set; }

        /// <summary>
        /// Source LCN from the incoming CALL REQUEST packet (for response routing)
        /// </summary>
        public ushort SourceLcn { get; set; }

        /// <summary>
        /// Source connection object for sending responses back (set by transport)
        /// </summary>
        public object? SourceConnection { get; set; }

        /// <summary>
        /// The transport interface used for this call (set by router for outbound calls)
        /// </summary>
        public object? UsedTransport { get; set; }

        /// <summary>
        /// Create call request from components
        /// </summary>
        public X25CallRequest(string calledAddress, string callingAddress, byte[]? facilities = null, byte[]? callUserData = null)
        {
            CalledAddress = calledAddress ?? "";
            CallingAddress = callingAddress ?? "";
            Facilities = facilities ?? Array.Empty<byte>();
            CallUserData = callUserData ?? Array.Empty<byte>();

            CalledAddressLength = (byte)CalledAddress.Length;
            CallingAddressLength = (byte)CallingAddress.Length;
            FacilitiesLength = (byte)Facilities.Length;
            CallUserDataLength = (byte)CallUserData.Length;
        }

        /// <summary>
        /// Create call request from CALL REQUEST packet data
        /// </summary>
        public X25CallRequest(byte[] packetData)
        {
            if (packetData == null || packetData.Length < 4)
                throw new ArgumentException("Call request packet data must be at least 4 bytes", nameof(packetData));

            if (packetData[2] != 0x0B) // CALL REQUEST PTI
                throw new ArgumentException("Packet is not a CALL REQUEST", nameof(packetData));

            ParseCallRequest(packetData);
        }

        /// <summary>
        /// Parse CALL REQUEST packet data.
        /// X.25 address block format (ITU-T X.25 section 5.2.1):
        ///   Byte 0: (calling_length &lt;&lt; 4) | called_length
        ///   Then called + calling digits packed as one contiguous BCD stream
        ///   (2 digits per byte, high nibble first, zero-padded if odd total)
        /// </summary>
        private void ParseCallRequest(byte[] packetData)
        {
            int offset = 3; // Skip GFI, LCN, PTI

            if (offset >= packetData.Length) return;

            // Address length byte: high nibble = calling length, low nibble = called length
            byte addressLengths = packetData[offset++];
            CallingAddressLength = (byte)((addressLengths >> 4) & 0x0F);
            CalledAddressLength = (byte)(addressLengths & 0x0F);

            int totalDigits = CalledAddressLength + CallingAddressLength;
            int totalBytes = (totalDigits + 1) / 2;

            if (offset + totalBytes > packetData.Length) return;

            // Unpack all BCD nibbles from the contiguous stream
            var nibbles = new byte[totalDigits + 1]; // +1 for potential padding nibble
            int nibbleIdx = 0;
            for (int i = 0; i < totalBytes; i++)
            {
                nibbles[nibbleIdx++] = (byte)((packetData[offset + i] >> 4) & 0x0F);
                nibbles[nibbleIdx++] = (byte)(packetData[offset + i] & 0x0F);
            }

            // Called address = first CalledAddressLength nibbles
            var calledSb = new StringBuilder(CalledAddressLength);
            for (int i = 0; i < CalledAddressLength; i++)
                calledSb.Append((char)('0' + nibbles[i]));
            CalledAddress = calledSb.ToString();

            // Calling address = next CallingAddressLength nibbles
            var callingSb = new StringBuilder(CallingAddressLength);
            for (int i = 0; i < CallingAddressLength; i++)
                callingSb.Append((char)('0' + nibbles[CalledAddressLength + i]));
            CallingAddress = callingSb.ToString();

            offset += totalBytes;

            // Parse facilities
            if (offset < packetData.Length)
            {
                FacilitiesLength = packetData[offset++];
                if (FacilitiesLength > 0 && offset + FacilitiesLength <= packetData.Length)
                {
                    Facilities = new byte[FacilitiesLength];
                    Array.Copy(packetData, offset, Facilities, 0, FacilitiesLength);
                    offset += FacilitiesLength;
                }
            }

            // Remaining bytes are Call User Data (no length prefix per X.25 spec)
            if (offset < packetData.Length)
            {
                int cudLen = packetData.Length - offset;
                CallUserData = new byte[cudLen];
                Array.Copy(packetData, offset, CallUserData, 0, cudLen);
                CallUserDataLength = (byte)cudLen;
            }
        }

        /// <summary>
        /// Build CALL REQUEST packet body (after GFI/LCN/PTI header).
        /// Encodes addresses as one contiguous BCD stream per X.25 spec.
        /// </summary>
        public byte[] BuildPacketData()
        {
            int cldLen = CalledAddress.Length;
            int clgLen = CallingAddress.Length;
            int totalDigits = cldLen + clgLen;
            int addrBytes = (totalDigits + 1) / 2;

            // 1 (addr lengths) + addrBytes + 1 (fac length) + fac + CUD
            int totalLength = 1 + addrBytes + 1 + FacilitiesLength + CallUserDataLength;
            var packetData = new byte[totalLength];
            int offset = 0;

            // Address length byte: calling in high nibble, called in low nibble
            packetData[offset++] = (byte)((clgLen << 4) | cldLen);

            // Pack called + calling digits into contiguous BCD nibble stream
            var nibbles = new byte[totalDigits];
            int idx = 0;
            for (int i = 0; i < cldLen; i++)
                nibbles[idx++] = (byte)(CalledAddress[i] - '0');
            for (int i = 0; i < clgLen; i++)
                nibbles[idx++] = (byte)(CallingAddress[i] - '0');

            for (int i = 0; i < addrBytes; i++)
            {
                byte hi = nibbles[i * 2];
                byte lo = (i * 2 + 1 < totalDigits) ? nibbles[i * 2 + 1] : (byte)0;
                packetData[offset++] = (byte)((hi << 4) | lo);
            }

            // Facilities length + data
            packetData[offset++] = FacilitiesLength;
            if (FacilitiesLength > 0)
            {
                Array.Copy(Facilities, 0, packetData, offset, FacilitiesLength);
                offset += FacilitiesLength;
            }

            // Call User Data (no length prefix)
            if (CallUserDataLength > 0)
            {
                Array.Copy(CallUserData, 0, packetData, offset, CallUserDataLength);
            }

            return packetData;
        }

        /// <summary>
        /// Get string representation
        /// </summary>
        public override string ToString()
        {
            return $"CALL REQUEST: Called={CalledAddress} Calling={CallingAddress} Facilities={Facilities.Length} CUD={CallUserData.Length}";
        }
    }
}
