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
    /// SINTRAN TAD Protocol Message Types and Constants
    /// Based on real SINTRAN NPL source code analysis
    /// Source: MP-P2-TAD.NPL and RP-P2-TAD.NPL
    /// </summary>
    public static class SintranTadMessageTypes
    {
        // ============================================
        // Message Type Constants (from NPL source)
        // ============================================

        /// <summary>
        /// 7DUMM - Empty buffer / Dummy message (0 bytes data)
        /// Source: MP-P2-TAD.NPL:328
        /// </summary>
        public const byte DUMM = 0x00;

        /// <summary>
        /// 7BDAT - Data message (variable length data)
        /// Source: RP-P2-TAD.NPL:508
        /// </summary>
        public const byte BDAT = 0x01;

        /// <summary>
        /// 7RFI - Ready For Input (0 bytes data)
        /// Source: RP-P2-TAD.NPL:1115
        /// </summary>
        public const byte RFI = 0x02;

        /// <summary>
        /// 7TMOD - Terminal Mode configuration
        /// Source: RP-P2-TAD.NPL:509
        /// </summary>
        public const byte TMOD = 0x03;

        /// <summary>
        /// 7TTYP - Terminal Type configuration
        /// Source: RP-P2-TAD.NPL:512
        /// </summary>
        public const byte TTYP = 0x04;

        /// <summary>
        /// 78MOD - 8-bit Mode configuration
        /// Source: RP-P2-TAD.NPL:515
        /// </summary>
        public const byte MOD8 = 0x05;

        /// <summary>
        /// 7DESC - Define Escape character
        /// Source: RP-P2-TAD.NPL:518
        /// </summary>
        public const byte DESC = 0x06;

        /// <summary>
        /// 7ESCA - Escape message (escape character received)
        /// Source: MP-P2-TAD.NPL:329
        /// </summary>
        public const byte ESCA = 0x07;

        /// <summary>
        /// 7OPSV - OPSYS Version information
        /// Source: RP-P2-TAD.NPL:524
        /// </summary>
        public const byte OPSV = 0x08;

        /// <summary>
        /// 7REJE - Reject message
        /// Source: RP-P2-TAD.NPL:527
        /// </summary>
        public const byte REJE = 0x09;

        /// <summary>
        /// 7RLOC - Remote Local (rubout/delete) - NORD-NET specific
        /// Source: MP-P2-TAD.NPL:330
        /// </summary>
        public const byte RLOC = 0x0A;

        /// <summary>
        /// 7DCON - Disconnect message
        /// Source: MP-P2-TAD.NPL:331
        /// </summary>
        public const byte DCON = 0x0B;

        /// <summary>
        /// 7CERS - CESC Response message
        /// Source: MP-P2-TAD.NPL:332
        /// </summary>
        public const byte CERS = 0x0C;

        /// <summary>
        /// 7RECO - Reset Confirmation message
        /// Source: MP-P2-TAD.NPL:333
        /// </summary>
        public const byte RECO = 0x0D;

        /// <summary>
        /// 7NWRE - NOWAIT Restart message
        /// Source: MP-P2-TAD.NPL:334
        /// </summary>
        public const byte NWRE = 0x0E;

        /// <summary>
        /// 7ISRS - ISIZE Response message (2 bytes data)
        /// Source: MP-P2-TAD.NPL:335
        /// </summary>
        public const byte ISRS = 0x0F;

        /// <summary>
        /// 7ERRS - Error Response message (2 bytes data)
        /// Source: MP-P2-TAD.NPL:336
        /// </summary>
        public const byte ERRS = 0x10;

        /// <summary>
        /// 7TREP - TREPP Status message (2 bytes data)
        /// Source: MP-P2-TAD.NPL:337
        /// </summary>
        public const byte TREP = 0x11;

        // Additional message types found in analysis
        /// <summary>
        /// Message type 0x12: Break Mode Matrix (20 bytes of data, per the comment next to it).
        /// </summary>
        public const byte BMMX = 0x12; // Break Mode Matrix (20 bytes)
        /// <summary>
        /// Message type 0x13: Echo Mode Matrix (20 bytes of data, per the comment next to it).
        /// </summary>
        public const byte ECKM = 0x13; // Echo Mode Matrix (20 bytes)

        // ============================================
        // Message Priority
        // ============================================

        /// <summary>
        /// TAD message priority levels
        /// High priority messages are sent immediately
        /// Normal priority messages are queued
        /// </summary>
        public enum TadMessagePriority : byte
        {
            /// <summary>
            /// Normal priority - queued for transmission
            /// </summary>
            Normal = 0,

            /// <summary>
            /// High priority - immediate transmission
            /// </summary>
            High = 1
        }

        // ============================================
        // Message Format Constants
        // ============================================

        /// <summary>
        /// Maximum TAD message data size (bytes)
        /// Based on SINTRAN buffer size limitations
        /// </summary>
        public const int MAX_MESSAGE_DATA_SIZE = 255;

        /// <summary>
        /// Minimum TAD message size (header only)
        /// Format: [optional pad] [type] [count]
        /// </summary>
        public const int MIN_MESSAGE_SIZE = 2;

        /// <summary>
        /// Break/Echo strategy table size (bytes)
        /// 20 bytes = 160 bits for character mapping
        /// </summary>
        public const int STRATEGY_TABLE_SIZE = 20;

        // ============================================
        // Message Categories
        // ============================================

        /// <summary>
        /// Check if message type is a configuration message
        /// Configuration messages: TMOD, TTYP, 78MOD, DESC, OPSV
        /// </summary>
        public static bool IsConfigurationMessage(byte messageType)
        {
            return messageType == TMOD ||
                   messageType == TTYP ||
                   messageType == MOD8 ||
                   messageType == DESC ||
                   messageType == OPSV;
        }

        /// <summary>
        /// Check if message type is a control message
        /// Control messages: RFI, ESCA, DCON, CERS, RECO, NWRE, RLOC
        /// </summary>
        public static bool IsControlMessage(byte messageType)
        {
            return messageType == RFI ||
                   messageType == ESCA ||
                   messageType == DCON ||
                   messageType == CERS ||
                   messageType == RECO ||
                   messageType == NWRE ||
                   messageType == RLOC;
        }

        /// <summary>
        /// Check if message type carries user data
        /// Data messages: BDAT
        /// </summary>
        public static bool IsDataMessage(byte messageType)
        {
            return messageType == BDAT;
        }

        /// <summary>
        /// Check if message type is a response message
        /// Response messages: ISRS, ERRS, TREP, CERS, RECO
        /// </summary>
        public static bool IsResponseMessage(byte messageType)
        {
            return messageType == ISRS ||
                   messageType == ERRS ||
                   messageType == TREP ||
                   messageType == CERS ||
                   messageType == RECO;
        }

        /// <summary>
        /// Check if message type requires high priority transmission
        /// High priority: Control messages and responses
        /// </summary>
        public static bool RequiresHighPriority(byte messageType)
        {
            return IsControlMessage(messageType) || IsResponseMessage(messageType);
        }

        /// <summary>
        /// Get human-readable message type name
        /// </summary>
        public static string GetMessageTypeName(byte messageType)
        {
            return messageType switch
            {
                DUMM => "7DUMM (Empty)",
                BDAT => "7BDAT (Data)",
                RFI => "7RFI (Ready For Input)",
                TMOD => "7TMOD (Terminal Mode)",
                TTYP => "7TTYP (Terminal Type)",
                MOD8 => "78MOD (8-bit Mode)",
                DESC => "7DESC (Define Escape)",
                ESCA => "7ESCA (Escape)",
                OPSV => "7OPSV (OPSYS Version)",
                REJE => "7REJE (Reject)",
                RLOC => "7RLOC (Remote Local)",
                DCON => "7DCON (Disconnect)",
                CERS => "7CERS (CESC Response)",
                RECO => "7RECO (Reset Confirm)",
                NWRE => "7NWRE (NOWAIT Restart)",
                ISRS => "7ISRS (ISIZE Response)",
                ERRS => "7ERRS (Error Response)",
                TREP => "7TREP (TREPP Status)",
                BMMX => "7BMMX (Break Matrix)",
                ECKM => "7ECKM (Echo Matrix)",
                _ => $"UNKNOWN(0x{messageType:X2})"
            };
        }

        /// <summary>
        /// Get expected data size for message type (0 = variable)
        /// </summary>
        public static int GetExpectedDataSize(byte messageType)
        {
            return messageType switch
            {
                DUMM => 0,      // Empty message
                BDAT => 0,      // Variable length data
                RFI => 0,       // No data
                TMOD => 1,      // 1 byte mode
                TTYP => 2,      // 2 bytes terminal type
                MOD8 => 1,      // 1 byte 8-bit mode flag
                DESC => 1,      // 1 byte escape character
                ESCA => 0,      // No data
                OPSV => 2,      // 2 bytes version info
                REJE => 1,      // 1 byte reject reason
                RLOC => 0,      // No data
                DCON => 0,      // No data
                CERS => 0,      // No data
                RECO => 0,      // No data
                NWRE => 0,      // No data
                ISRS => 2,      // 2 bytes size response
                ERRS => 2,      // 2 bytes error code
                TREP => 2,      // 2 bytes status
                BMMX => STRATEGY_TABLE_SIZE,  // 20 bytes break matrix
                ECKM => STRATEGY_TABLE_SIZE,  // 20 bytes echo matrix
                _ => 0          // Unknown = variable
            };
        }
    }
}
