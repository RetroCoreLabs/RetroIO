//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// X.25 Packet Builder API - Creates all X.25 packet types according to ITU-T X.25 standard
    /// Provides 100% accurate packet construction with proper GFI, LCN, and PTI encoding
    /// </summary>
    public static class X25PacketBuilder
    {
        #region General Format Identifier (GFI) Constants

        /// <summary>
        /// GFI bit masks according to ITU-T X.25
        /// </summary>
        public static class GFI
        {
            /// <summary>
            /// Mask of the qualifier bit in the first octet, bit 7.
            /// </summary>
            public const byte Q_BIT_MASK = 0x80;      // Bit 7: Qualifier bit
            /// <summary>
            /// Mask of the delivery confirmation bit in the first octet, bit 6.
            /// </summary>
            public const byte D_BIT_MASK = 0x40;      // Bit 6: Delivery confirmation bit
            /// <summary>
            /// Mask of the address bit in the first octet, bit 5. It is always 0 for modulo 8.
            /// </summary>
            public const byte A_BIT_MASK = 0x20;      // Bit 5: Address bit (always 0 for modulo 8)
            /// <summary>
            /// Mask of the two sequence numbering bits, bits 5 and 4.
            /// </summary>
            public const byte MODULO_MASK = 0x30;     // Bits 5-4: Modulo/sequence numbering
            // X.25 5.1.2: the GFI is bits 8-5 of octet 1, so the logical channel GROUP is bits 4-1 -
            // FOUR bits, 0..15, and the channel number is twelve bits, 0..4095 (5.1.3). Until
            // 2026-09-06 this was 0x07, which corrupted every channel above 2047 in both directions
            // (pinned by the vector pkt-clear-request-not-obtainable) and, once the engine started
            // choosing channels from the top of the range as a DTE should, broke the first call.
            /// <summary>
            /// Mask of the logical channel group, bits 4 to 1 of the first octet.
            /// </summary>
            public const byte LCG_MASK = 0x0F;        // Bits 4-1: Logical Channel Group

            /// <summary>
            /// Value of the sequence numbering bits for modulo 8, binary 01 in bits 5 and 4.
            /// </summary>
            public const byte MODULO_8 = 0x10;        // 01 in bits 5-4 = Modulo 8 (X.25 standard)
            /// <summary>
            /// Value of the sequence numbering bits for modulo 128, binary 10 in bits 5 and 4.
            /// </summary>
            public const byte MODULO_128 = 0x20;      // 10 in bits 5-4 = Modulo 128

            /// <summary>
            /// Create a GFI byte with the given qualifier/delivery bits, sequence-numbering mode, and
            /// logical channel group. The sequence-numbering (SS) bits are always set — 01 (0x10) for
            /// modulo 8, 10 (0x20) for modulo 128 — as required by ITU-T X.25.
            /// </summary>
            public static byte Create(bool qBit = false, bool dBit = false, bool modulo128 = false, byte lcg = 0)
            {
                byte gfi = 0;
                if (qBit) gfi |= Q_BIT_MASK;
                if (dBit) gfi |= D_BIT_MASK;
                // Sequence-numbering (SS) bits are mandatory: 01 = modulo 8, 10 = modulo 128 (ITU-T X.25).
                // Omitting them (SS=00) is invalid and breaks interop with conforming peers.
                gfi |= modulo128 ? MODULO_128 : MODULO_8;
                gfi |= (byte)(lcg & LCG_MASK);
                return gfi;
            }
        }

        /// <summary>
        /// X.25 Packet Type Identifier constants - actual PTI values from ITU-T X.25
        /// </summary>
        public static class PTI
        {
            // Call establishment/clearing (fixed values)
            /// <summary>
            /// Packet type identifier of a call request. The value is 0x0B.
            /// </summary>
            public const byte CALL_REQUEST = 0x0B;
            /// <summary>
            /// Packet type identifier of a call accepted. The value is 0x0F.
            /// </summary>
            public const byte CALL_ACCEPTED = 0x0F;
            /// <summary>
            /// Packet type identifier of a clear request. The value is 0x13.
            /// </summary>
            public const byte CLEAR_REQUEST = 0x13;
            /// <summary>
            /// Packet type identifier of a clear confirmation. The value is 0x17.
            /// </summary>
            public const byte CLEAR_CONFIRMATION = 0x17;

            // Interrupt packets
            /// <summary>
            /// Packet type identifier of an interrupt. The value is 0x23.
            /// </summary>
            public const byte INTERRUPT = 0x23;
            /// <summary>
            /// Packet type identifier of an interrupt confirmation. The value is 0x27.
            /// </summary>
            public const byte INTERRUPT_CONFIRMATION = 0x27;

            // Reset packets
            /// <summary>
            /// Packet type identifier of a reset request. The value is 0x1B.
            /// </summary>
            public const byte RESET_REQUEST = 0x1B;
            /// <summary>
            /// Packet type identifier of a reset confirmation. The value is 0x1F.
            /// </summary>
            public const byte RESET_CONFIRMATION = 0x1F;

            // Restart packets
            /// <summary>
            /// Packet type identifier of a restart request. The value is 0xFB.
            /// </summary>
            public const byte RESTART_REQUEST = 0xFB;
            /// <summary>
            /// Packet type identifier of a restart confirmation. The value is 0xFF.
            /// </summary>
            public const byte RESTART_CONFIRMATION = 0xFF;

            // Registration packets
            /// <summary>
            /// Packet type identifier of a registration request. The value is 0xF3.
            /// </summary>
            public const byte REGISTRATION_REQUEST = 0xF3;
            /// <summary>
            /// Packet type identifier of a registration confirmation. The value is 0xF7.
            /// </summary>
            public const byte REGISTRATION_CONFIRMATION = 0xF7;

            // Diagnostic
            /// <summary>
            /// Packet type identifier of a diagnostic packet. The value is 0xF1.
            /// </summary>
            public const byte DIAGNOSTIC = 0xF1;

            // Data packets: LSB = 0, bits 1-3 = P(S), bits 5-7 = P(R)
            /// <summary>
            /// Creates the packet type identifier octet of a data packet.
            /// </summary>
            /// <remarks>
            /// Bits 7 to 5 hold the receive sequence number, bits 3 to 1 hold the send sequence number, and bit 4 holds the more data bit. Bit 0 is 0. Each sequence number is cut to its lower three bits.
            /// </remarks>
            /// <param name="ps">
            /// The send sequence number P(S).
            /// </param>
            /// <param name="pr">
            /// The receive sequence number P(R).
            /// </param>
            /// <param name="mBit">
            /// True to set the more data bit.
            /// </param>
            /// <returns>
            /// The packet type identifier octet.
            /// </returns>
            public static byte CreateDataPTI(byte ps, byte pr, bool mBit = false)
            {
                byte pti = (byte)((pr & 0x07) << 5 | (ps & 0x07) << 1);
                if (mBit) pti |= 0x10; // M-bit in bit 4 for modulo 8
                return pti;
            }

            // Flow control packets: Variable based on P(R)
            /// <summary>
            /// Creates the packet type identifier octet of a receive ready (RR) packet.
            /// </summary>
            /// <remarks>
            /// The octet is 0x01 with the receive sequence number in bits 7 to 5, cut to its lower three bits.
            /// </remarks>
            /// <param name="pr">
            /// The receive sequence number P(R).
            /// </param>
            /// <returns>
            /// The packet type identifier octet.
            /// </returns>
            public static byte CreateRR(byte pr) => (byte)(0x01 | ((pr & 0x07) << 5));
            /// <summary>
            /// Creates the packet type identifier octet of a receive not ready (RNR) packet.
            /// </summary>
            /// <remarks>
            /// The octet is 0x05 with the receive sequence number in bits 7 to 5, cut to its lower three bits.
            /// </remarks>
            /// <param name="pr">
            /// The receive sequence number P(R).
            /// </param>
            /// <returns>
            /// The packet type identifier octet.
            /// </returns>
            public static byte CreateRNR(byte pr) => (byte)(0x05 | ((pr & 0x07) << 5));
            /// <summary>
            /// Creates the packet type identifier octet of a reject (REJ) packet.
            /// </summary>
            /// <remarks>
            /// The octet is 0x09 with the receive sequence number in bits 7 to 5, cut to its lower three bits.
            /// </remarks>
            /// <param name="pr">
            /// The receive sequence number P(R).
            /// </param>
            /// <returns>
            /// The packet type identifier octet.
            /// </returns>
            public static byte CreateREJ(byte pr) => (byte)(0x09 | ((pr & 0x07) << 5));
        }

        #endregion

        #region Call Establishment Packets

        /// <summary>
        /// Create CALL REQUEST packet according to ITU-T X.25 format
        /// </summary>
        public static byte[] CreateCallRequest(
            ushort lcn,
            string calledAddress,
            string callingAddress = "",
            byte[]? facilities = null,
            byte[]? callUserData = null,
            bool qBit = false,
            bool dBit = false)
        {
            using var ms = new MemoryStream();

            // GFI + LCG (upper 4 bits of LCN)
            byte gfi = GFI.Create(qBit, dBit, false, (byte)((lcn >> 8) & 0x0F));
            ms.WriteByte(gfi);

            // LCN (lower 8 bits)
            ms.WriteByte((byte)(lcn & 0xFF));

            // PTI
            ms.WriteByte(PTI.CALL_REQUEST);

            // Address length byte: calling in high nibble, called in low nibble (X.25 spec)
            byte addressLengths = (byte)(((callingAddress.Length & 0x0F) << 4) | (calledAddress.Length & 0x0F));
            ms.WriteByte(addressLengths);

            // Called + calling addresses packed as one contiguous BCD stream
            WriteContiguousAddressBCD(ms, calledAddress, callingAddress);

            // Facilities
            facilities = facilities ?? Array.Empty<byte>();
            ms.WriteByte((byte)facilities.Length);
            if (facilities.Length > 0)
            {
                ms.Write(facilities);
            }

            // Call User Data
            callUserData = callUserData ?? Array.Empty<byte>();
            if (callUserData.Length > 0)
            {
                ms.Write(callUserData);
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Create CALL ACCEPTED packet
        /// </summary>
        public static byte[] CreateCallAccepted(
            ushort lcn,
            byte[]? facilities = null,
            byte[]? callUserData = null,
            bool qBit = false,
            bool dBit = false)
        {
            using var ms = new MemoryStream();

            // GFI + LCG
            byte gfi = GFI.Create(qBit, dBit, false, (byte)((lcn >> 8) & 0x0F));
            ms.WriteByte(gfi);

            // LCN
            ms.WriteByte((byte)(lcn & 0xFF));

            // PTI
            ms.WriteByte(PTI.CALL_ACCEPTED);

            facilities = facilities ?? Array.Empty<byte>();
            callUserData = callUserData ?? Array.Empty<byte>();

            // X.25 5.2.3.1: with nothing to say, the basic format is the three header octets alone.
            if (facilities.Length == 0 && callUserData.Length == 0)
            {
                return ms.ToArray();
            }

            // X.25 5.2.3.1.2: the address length octet is mandatory whenever a facility length field
            // is present. No addresses are carried in this stack's Call Accepted, so it is 00. Until
            // 2026-09-06 this octet was missing and the facility length landed where the address
            // lengths belong (pinned by the vector pkt-call-accepted-with-facilities).
            ms.WriteByte(0x00);

            // Facilities
            ms.WriteByte((byte)facilities.Length);
            if (facilities.Length > 0)
            {
                ms.Write(facilities);
            }

            // Called user data (5.2.3.1.5)
            if (callUserData.Length > 0)
            {
                ms.Write(callUserData);
            }

            return ms.ToArray();
        }

        #endregion

        #region Call Clearing Packets

        /// <summary>
        /// Create CLEAR REQUEST packet
        /// </summary>
        public static byte[] CreateClearRequest(
            ushort lcn,
            byte clearingCause,
            byte diagnosticCode = 0x00,
            byte[]? facilities = null,
            byte[]? clearUserData = null)
        {
            using var ms = new MemoryStream();

            // GFI + LCG
            byte gfi = GFI.Create(false, false, false, (byte)((lcn >> 8) & 0x0F));
            ms.WriteByte(gfi);

            // LCN
            ms.WriteByte((byte)(lcn & 0xFF));

            // PTI
            ms.WriteByte(PTI.CLEAR_REQUEST);

            // Clearing cause
            ms.WriteByte(clearingCause);

            // Diagnostic code
            ms.WriteByte(diagnosticCode);

            // Facilities (optional)
            facilities = facilities ?? Array.Empty<byte>();
            if (facilities.Length > 0)
            {
                ms.WriteByte((byte)facilities.Length);
                ms.Write(facilities);
            }

            // Clear user data (optional)
            clearUserData = clearUserData ?? Array.Empty<byte>();
            if (clearUserData.Length > 0)
            {
                ms.Write(clearUserData);
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Create CLEAR CONFIRMATION packet
        /// </summary>
        public static byte[] CreateClearConfirmation(ushort lcn)
        {
            return new byte[]
            {
                GFI.Create(false, false, false, (byte)((lcn >> 8) & 0x0F)),
                (byte)(lcn & 0xFF),
                PTI.CLEAR_CONFIRMATION
            };
        }

        #endregion

        #region Data Transfer Packets

        /// <summary>
        /// Create DATA packet (Modulo 8)
        /// </summary>
        public static byte[] CreateDataPacket(
            ushort lcn,
            byte ps,
            byte pr,
            byte[] userData,
            bool mBit = false,
            bool qBit = false,
            bool dBit = false)
        {
            if (userData == null || userData.Length == 0)
                throw new ArgumentException("User data cannot be null or empty for data packet");

            using var ms = new MemoryStream();

            // GFI + LCG
            byte gfi = GFI.Create(qBit, dBit, false, (byte)((lcn >> 8) & 0x0F));
            ms.WriteByte(gfi);

            // LCN
            ms.WriteByte((byte)(lcn & 0xFF));

            // PTI with sequence numbers
            byte pti = PTI.CreateDataPTI(ps, pr, mBit);
            ms.WriteByte(pti);

            // User data
            ms.Write(userData);

            return ms.ToArray();
        }

        /// <summary>
        /// Create DATA packet (Modulo 128)
        /// </summary>
        public static byte[] CreateDataPacketModulo128(
            ushort lcn,
            byte ps,
            byte pr,
            byte[] userData,
            bool mBit = false,
            bool qBit = false,
            bool dBit = false)
        {
            if (userData == null || userData.Length == 0)
                throw new ArgumentException("User data cannot be null or empty for data packet");

            using var ms = new MemoryStream();

            // GFI + LCG (with modulo 128 flag)
            byte gfi = GFI.Create(qBit, dBit, true, (byte)((lcn >> 8) & 0x0F));
            if (mBit) gfi |= 0x20; // M-bit in GFI for modulo 128
            ms.WriteByte(gfi);

            // LCN
            ms.WriteByte((byte)(lcn & 0xFF));

            // PTI (always 0x00 for modulo 128 data)
            ms.WriteByte(0x00);

            // P(S) - separate byte
            ms.WriteByte(ps);

            // P(R) - separate byte
            ms.WriteByte(pr);

            // User data
            ms.Write(userData);

            return ms.ToArray();
        }

        #endregion

        #region Flow Control Packets

        /// <summary>
        /// Create RR (Receive Ready) packet
        /// </summary>
        public static byte[] CreateRR(ushort lcn, byte pr, bool modulo128 = false)
        {
            if (modulo128)
            {
                return new byte[]
                {
                    GFI.Create(false, false, true, (byte)((lcn >> 8) & 0x0F)),
                    (byte)(lcn & 0xFF),
                    0x01,  // RR PTI for modulo 128
                    pr     // P(R) as separate byte
                };
            }
            else
            {
                return new byte[]
                {
                    GFI.Create(false, false, false, (byte)((lcn >> 8) & 0x0F)),
                    (byte)(lcn & 0xFF),
                    PTI.CreateRR(pr)
                };
            }
        }

        /// <summary>
        /// Create RNR (Receive Not Ready) packet
        /// </summary>
        public static byte[] CreateRNR(ushort lcn, byte pr, bool modulo128 = false)
        {
            if (modulo128)
            {
                return new byte[]
                {
                    GFI.Create(false, false, true, (byte)((lcn >> 8) & 0x0F)),
                    (byte)(lcn & 0xFF),
                    0x05,  // RNR PTI for modulo 128
                    pr     // P(R) as separate byte
                };
            }
            else
            {
                return new byte[]
                {
                    GFI.Create(false, false, false, (byte)((lcn >> 8) & 0x0F)),
                    (byte)(lcn & 0xFF),
                    PTI.CreateRNR(pr)
                };
            }
        }

        /// <summary>
        /// Create REJ (Reject) packet
        /// </summary>
        public static byte[] CreateREJ(ushort lcn, byte pr, bool modulo128 = false)
        {
            if (modulo128)
            {
                return new byte[]
                {
                    GFI.Create(false, false, true, (byte)((lcn >> 8) & 0x0F)),
                    (byte)(lcn & 0xFF),
                    0x09,  // REJ PTI for modulo 128
                    pr     // P(R) as separate byte
                };
            }
            else
            {
                return new byte[]
                {
                    GFI.Create(false, false, false, (byte)((lcn >> 8) & 0x0F)),
                    (byte)(lcn & 0xFF),
                    PTI.CreateREJ(pr)
                };
            }
        }

        #endregion

        #region Interrupt Packets

        /// <summary>
        /// Create INTERRUPT packet
        /// </summary>
        public static byte[] CreateInterrupt(ushort lcn, byte[]? interruptData = null)
        {
            using var ms = new MemoryStream();

            // GFI + LCG
            byte gfi = GFI.Create(false, false, false, (byte)((lcn >> 8) & 0x0F));
            ms.WriteByte(gfi);

            // LCN
            ms.WriteByte((byte)(lcn & 0xFF));

            // PTI
            ms.WriteByte(PTI.INTERRUPT);

            // Interrupt user data (up to 32 bytes)
            interruptData = interruptData ?? Array.Empty<byte>();
            if (interruptData.Length > 32)
                throw new ArgumentException("Interrupt data cannot exceed 32 bytes");

            if (interruptData.Length > 0)
            {
                ms.Write(interruptData);
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Create INTERRUPT CONFIRMATION packet
        /// </summary>
        public static byte[] CreateInterruptConfirmation(ushort lcn)
        {
            return new byte[]
            {
                GFI.Create(false, false, false, (byte)((lcn >> 8) & 0x0F)),
                (byte)(lcn & 0xFF),
                PTI.INTERRUPT_CONFIRMATION
            };
        }

        #endregion

        #region Reset Packets

        /// <summary>
        /// Create RESET REQUEST packet
        /// </summary>
        public static byte[] CreateResetRequest(ushort lcn, byte resetCause, byte diagnosticCode = 0x00)
        {
            return new byte[]
            {
                GFI.Create(false, false, false, (byte)((lcn >> 8) & 0x0F)),
                (byte)(lcn & 0xFF),
                PTI.RESET_REQUEST,
                resetCause,
                diagnosticCode
            };
        }

        /// <summary>
        /// Create RESET CONFIRMATION packet
        /// </summary>
        public static byte[] CreateResetConfirmation(ushort lcn)
        {
            return new byte[]
            {
                GFI.Create(false, false, false, (byte)((lcn >> 8) & 0x0F)),
                (byte)(lcn & 0xFF),
                PTI.RESET_CONFIRMATION
            };
        }

        #endregion

        #region Restart Packets

        /// <summary>
        /// Create RESTART REQUEST packet (LCN = 0)
        /// </summary>
        public static byte[] CreateRestartRequest(byte restartCause, byte diagnosticCode = 0x00)
        {
            return new byte[]
            {
                GFI.Create(false, false, false, 0x00),  // LCN = 0 for restart
                0x00,
                PTI.RESTART_REQUEST,
                restartCause,
                diagnosticCode
            };
        }

        /// <summary>
        /// Create RESTART CONFIRMATION packet (LCN = 0)
        /// </summary>
        public static byte[] CreateRestartConfirmation()
        {
            return new byte[]
            {
                GFI.Create(false, false, false, 0x00),  // LCN = 0 for restart
                0x00,
                PTI.RESTART_CONFIRMATION
            };
        }

        #endregion

        #region Registration Packets

        /// <summary>
        /// Create REGISTRATION REQUEST packet (LCN = 0)
        /// </summary>
        public static byte[] CreateRegistrationRequest(string dteAddress, byte[]? parameters = null)
        {
            using var ms = new MemoryStream();

            // GFI + LCG (LCN = 0 for registration)
            ms.WriteByte(GFI.Create(false, false, false, 0x00));
            ms.WriteByte(0x00);
            ms.WriteByte(PTI.REGISTRATION_REQUEST);

            // DTE address length
            byte addressLength = (byte)dteAddress.Length;
            ms.WriteByte(addressLength);

            // DTE address (BCD encoded)
            WriteAddressBCD(ms, dteAddress);

            // Registration parameters (optional)
            parameters = parameters ?? Array.Empty<byte>();
            if (parameters.Length > 0)
            {
                ms.WriteByte((byte)parameters.Length);
                ms.Write(parameters);
            }
            else
            {
                ms.WriteByte(0x00); // No parameters
            }

            return ms.ToArray();
        }

        /// <summary>
        /// Create REGISTRATION CONFIRMATION packet
        /// </summary>
        public static byte[] CreateRegistrationConfirmation(byte registrationCause, byte diagnosticCode = 0x00)
        {
            return new byte[]
            {
                GFI.Create(false, false, false, 0x00),  // LCN = 0 for registration
                0x00,
                PTI.REGISTRATION_CONFIRMATION,
                registrationCause,
                diagnosticCode
            };
        }

        #endregion

        #region Diagnostic Packet

        /// <summary>
        /// Create DIAGNOSTIC packet
        /// </summary>
        public static byte[] CreateDiagnostic(byte diagnosticCode, byte[]? diagnosticInfo = null)
        {
            using var ms = new MemoryStream();

            // GFI + LCG (LCN = 0 for diagnostic)
            ms.WriteByte(GFI.Create(false, false, false, 0x00));
            ms.WriteByte(0x00);
            ms.WriteByte(PTI.DIAGNOSTIC);
            ms.WriteByte(diagnosticCode);

            // Diagnostic information (optional)
            diagnosticInfo = diagnosticInfo ?? Array.Empty<byte>();
            if (diagnosticInfo.Length > 0)
            {
                ms.Write(diagnosticInfo);
            }

            return ms.ToArray();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Write a single X.121 address in BCD format (for registration packets etc.)
        /// </summary>
        private static void WriteAddressBCD(MemoryStream ms, string address)
        {
            if (string.IsNullOrEmpty(address)) return;

            for (int i = 0; i < address.Length; i += 2)
            {
                byte hi = (byte)(address[i] - '0');
                byte lo = (i + 1 < address.Length) ? (byte)(address[i + 1] - '0') : (byte)0;
                ms.WriteByte((byte)((hi << 4) | lo));
            }
        }

        /// <summary>
        /// Write called + calling addresses as one contiguous BCD stream (X.25 spec).
        /// Called digits first, then calling digits, packed 2 per byte, zero-padded if odd total.
        /// </summary>
        private static void WriteContiguousAddressBCD(MemoryStream ms, string calledAddress, string callingAddress)
        {
            int cldLen = calledAddress?.Length ?? 0;
            int clgLen = callingAddress?.Length ?? 0;
            int totalDigits = cldLen + clgLen;
            int totalBytes = (totalDigits + 1) / 2;

            var nibbles = new byte[totalDigits];
            int idx = 0;
            for (int i = 0; i < cldLen; i++)
                nibbles[idx++] = (byte)(calledAddress![i] - '0');
            for (int i = 0; i < clgLen; i++)
                nibbles[idx++] = (byte)(callingAddress![i] - '0');

            for (int i = 0; i < totalBytes; i++)
            {
                byte hi = nibbles[i * 2];
                byte lo = (i * 2 + 1 < totalDigits) ? nibbles[i * 2 + 1] : (byte)0;
                ms.WriteByte((byte)((hi << 4) | lo));
            }
        }

        /// <summary>
        /// Extract LCN from GFI and second byte
        /// </summary>
        public static ushort ExtractLCN(byte gfi, byte lcnLow)
        {
            byte lcg = (byte)(gfi & GFI.LCG_MASK);
            return (ushort)((lcg << 8) | lcnLow);
        }

        /// <summary>
        /// Extract P(S) from data packet PTI (modulo 8)
        /// </summary>
        public static byte ExtractPS(byte pti)
        {
            return (byte)((pti >> 1) & 0x07);
        }

        /// <summary>
        /// Extract P(R) from data/supervisory packet PTI (modulo 8)
        /// </summary>
        public static byte ExtractPR(byte pti)
        {
            return (byte)((pti >> 5) & 0x07);
        }

        /// <summary>
        /// Check if M-bit is set in data packet
        /// </summary>
        public static bool HasMBit(byte gfiOrPti, bool modulo128)
        {
            if (modulo128)
            {
                return (gfiOrPti & 0x20) != 0; // M-bit in GFI for modulo 128
            }
            else
            {
                return (gfiOrPti & 0x10) != 0; // M-bit in PTI for modulo 8
            }
        }

        /// <summary>
        /// Check if packet is a data packet
        /// </summary>
        public static bool IsDataPacket(byte pti)
        {
            return (pti & 0x01) == 0;
        }

        /// <summary>
        /// Check if packet is a supervisory (flow control) packet
        /// </summary>
        public static bool IsSupervisoryPacket(byte pti)
        {
            byte baseType = (byte)(pti & 0x1F);
            return baseType == 0x01 || baseType == 0x05 || baseType == 0x09; // RR, RNR, REJ
        }

        #endregion
    }

    #region X.25 Standard Constants

    /// <summary>
    /// Standard X.25 clearing causes according to ITU-T X.25
    /// </summary>
    public static class X25ClearingCause
    {
        /// <summary>
        /// Clearing cause DTE originated, the value 0x00.
        /// </summary>
        public const byte DTE_ORIGINATED = 0x00;
        /// <summary>
        /// Clearing cause number busy, the value 0x01.
        /// </summary>
        public const byte NUMBER_BUSY = 0x01;
        /// <summary>
        /// Clearing cause invalid facility request, the value 0x03.
        /// </summary>
        public const byte INVALID_FACILITY_REQUEST = 0x03;
        /// <summary>
        /// Clearing cause network congestion, the value 0x05.
        /// </summary>
        public const byte NETWORK_CONGESTION = 0x05;
        /// <summary>
        /// Clearing cause out of order, the value 0x09.
        /// </summary>
        public const byte OUT_OF_ORDER = 0x09;
        /// <summary>
        /// Clearing cause access barred, the value 0x0B.
        /// </summary>
        public const byte ACCESS_BARRED = 0x0B;
        /// <summary>
        /// Clearing cause not obtainable, the value 0x0D.
        /// </summary>
        public const byte NOT_OBTAINABLE = 0x0D;
        /// <summary>
        /// Clearing cause remote procedure error, the value 0x11.
        /// </summary>
        public const byte REMOTE_PROCEDURE_ERROR = 0x11;
        /// <summary>
        /// Clearing cause local procedure error, the value 0x13.
        /// </summary>
        public const byte LOCAL_PROCEDURE_ERROR = 0x13;
        /// <summary>
        /// Clearing cause RPOA out of order, the value 0x15.
        /// </summary>
        public const byte RPOA_OUT_OF_ORDER = 0x15;
        /// <summary>
        /// Clearing cause reverse charging not subscribed, the value 0x19.
        /// </summary>
        public const byte REVERSE_CHARGING_NOT_SUBSCRIBED = 0x19;
        /// <summary>
        /// Clearing cause incompatible destination, the value 0x21.
        /// </summary>
        public const byte INCOMPATIBLE_DESTINATION = 0x21;
        /// <summary>
        /// Clearing cause fast select not subscribed, the value 0x29.
        /// </summary>
        public const byte FAST_SELECT_NOT_SUBSCRIBED = 0x29;
        /// <summary>
        /// Clearing cause ship absent, the value 0x39.
        /// </summary>
        public const byte SHIP_ABSENT = 0x39;
        /// <summary>
        /// Clearing cause invalid called DTE address, the value 0x3B.
        /// </summary>
        public const byte INVALID_CALLED_DTE_ADDRESS = 0x3B;
        /// <summary>
        /// Clearing cause invalid calling DTE address, the value 0x3D.
        /// </summary>
        public const byte INVALID_CALLING_DTE_ADDRESS = 0x3D;
        /// <summary>
        /// Clearing cause invalid facility length, the value 0x41.
        /// </summary>
        public const byte INVALID_FACILITY_LENGTH = 0x41;
        /// <summary>
        /// Clearing cause illegal facility code, the value 0x43.
        /// </summary>
        public const byte ILLEGAL_FACILITY_CODE = 0x43;
    }

    /// <summary>
    /// Standard X.25 reset causes according to ITU-T X.25
    /// </summary>
    public static class X25ResetCause
    {
        /// <summary>
        /// Reset cause DTE originated, the value 0x00.
        /// </summary>
        public const byte DTE_ORIGINATED = 0x00;
        /// <summary>
        /// Reset cause out of order, the value 0x01.
        /// </summary>
        public const byte OUT_OF_ORDER = 0x01;
        /// <summary>
        /// Reset cause remote procedure error, the value 0x03.
        /// </summary>
        public const byte REMOTE_PROCEDURE_ERROR = 0x03;
        /// <summary>
        /// Reset cause local procedure error, the value 0x05.
        /// </summary>
        public const byte LOCAL_PROCEDURE_ERROR = 0x05;
        /// <summary>
        /// Reset cause network congestion, the value 0x07.
        /// </summary>
        public const byte NETWORK_CONGESTION = 0x07;
        /// <summary>
        /// Reset cause remote DTE operational, the value 0x09.
        /// </summary>
        public const byte REMOTE_DTE_OPERATIONAL = 0x09;
        /// <summary>
        /// Reset cause network operational, the value 0x0F.
        /// </summary>
        public const byte NETWORK_OPERATIONAL = 0x0F;
        /// <summary>
        /// Reset cause incompatible destination, the value 0x11.
        /// </summary>
        public const byte INCOMPATIBLE_DESTINATION = 0x11;
        /// <summary>
        /// Reset cause network out of order, the value 0x1D.
        /// </summary>
        public const byte NETWORK_OUT_OF_ORDER = 0x1D;
    }

    /// <summary>
    /// Standard X.25 restart causes according to ITU-T X.25
    /// </summary>
    public static class X25RestartCause
    {
        /// <summary>
        /// Restart cause DTE originated, the value 0x00.
        /// </summary>
        public const byte DTE_ORIGINATED = 0x00;
        /// <summary>
        /// Restart cause local procedure error, the value 0x01.
        /// </summary>
        public const byte LOCAL_PROCEDURE_ERROR = 0x01;
        /// <summary>
        /// Restart cause network congestion, the value 0x03.
        /// </summary>
        public const byte NETWORK_CONGESTION = 0x03;
        /// <summary>
        /// Restart cause network operational, the value 0x07.
        /// </summary>
        public const byte NETWORK_OPERATIONAL = 0x07;
        /// <summary>
        /// Restart cause registration cancellation confirmed, the value 0x7F.
        /// </summary>
        public const byte REGISTRATION_CANCELLATION_CONFIRMED = 0x7F;
    }

    /// <summary>
    /// Standard X.25 diagnostic codes according to ITU-T X.25
    /// </summary>
    public static class X25DiagnosticCode
    {
        /// <summary>
        /// Diagnostic code no additional info, the value 0x00.
        /// </summary>
        public const byte NO_ADDITIONAL_INFO = 0x00;
        /// <summary>
        /// Diagnostic code invalid P(S), the value 0x01.
        /// </summary>
        public const byte INVALID_PS = 0x01;
        /// <summary>
        /// Diagnostic code invalid P(R), the value 0x02.
        /// </summary>
        public const byte INVALID_PR = 0x02;
        /// <summary>
        /// Diagnostic code packet type invalid, the value 0x10.
        /// </summary>
        public const byte PACKET_TYPE_INVALID = 0x10;
        /// <summary>
        /// Diagnostic code packet type invalid for state, the value 0x11.
        /// </summary>
        public const byte PACKET_TYPE_INVALID_FOR_STATE = 0x11;
        /// <summary>
        /// Diagnostic code packet length invalid, the value 0x12.
        /// </summary>
        public const byte PACKET_LENGTH_INVALID = 0x12;
        /// <summary>
        /// Diagnostic code packet not allowed, the value 0x13.
        /// </summary>
        public const byte PACKET_NOT_ALLOWED = 0x13;
        /// <summary>
        /// Diagnostic code timer expired incoming call, the value 0x20.
        /// </summary>
        public const byte TIMER_EXPIRED_INCOMING_CALL = 0x20;
        /// <summary>
        /// Diagnostic code timer expired clear indication, the value 0x21.
        /// </summary>
        public const byte TIMER_EXPIRED_CLEAR_INDICATION = 0x21;
        /// <summary>
        /// Diagnostic code timer expired reset indication, the value 0x22.
        /// </summary>
        public const byte TIMER_EXPIRED_RESET_INDICATION = 0x22;
        /// <summary>
        /// Diagnostic code timer expired restart indication, the value 0x23.
        /// </summary>
        public const byte TIMER_EXPIRED_RESTART_INDICATION = 0x23;
    }

    #endregion
}