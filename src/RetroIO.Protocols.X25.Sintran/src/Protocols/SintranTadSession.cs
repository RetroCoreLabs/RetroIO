//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;

using RetroIO.Protocols.X25.Protocols;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// SINTRAN TAD Session State
    /// Tracks the state of a single TAD connection
    /// </summary>
    public class SintranTadSession
    {
        /// <summary>
        /// X.25 Logical Channel Number for this session
        /// </summary>
        public ushort LCN { get; set; }

        /// <summary>
        /// Session creation timestamp
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Last activity timestamp
        /// </summary>
        public DateTime LastActivity { get; set; }

        /// <summary>
        /// Current session state
        /// </summary>
        public TadSessionState State { get; set; }

        /// <summary>
        /// Terminal type (from 7TTYP message)
        /// </summary>
        public ushort TerminalType { get; set; }

        /// <summary>
        /// Terminal mode (from 7TMOD message)
        /// </summary>
        public byte TerminalMode { get; set; }

        /// <summary>
        /// Echo plex mode (from 7TMOD bit 0)
        /// </summary>
        public bool EchoPlex { get; set; }

        /// <summary>
        /// Local edit mode (from 7TMOD bit 1)
        /// </summary>
        public bool LocalEdit { get; set; }

        /// <summary>
        /// Page mode (from 7TMOD bit 2)
        /// </summary>
        public bool PageMode { get; set; }

        /// <summary>
        /// Form feed mode (from 7TMOD bit 3)
        /// </summary>
        public bool FormFeed { get; set; }

        /// <summary>
        /// Line width (from 7DESC message)
        /// </summary>
        public int LineWidth { get; set; } = 80;

        /// <summary>
        /// Page length (from 7DESC message)
        /// </summary>
        public int PageLength { get; set; } = 24;

        /// <summary>
        /// Tab width (from 7DESC message)
        /// </summary>
        public int TabWidth { get; set; } = 8;

        /// <summary>
        /// Option save flag (from 7OPSV message)
        /// </summary>
        public bool OptionSave { get; set; }

        /// <summary>
        /// Escape character (from 7DESC message)
        /// Default: 0x10 (DLE - Data Link Escape)
        /// </summary>
        public byte EscapeCharacter { get; set; } = 0x10;

        /// <summary>
        /// 8-bit mode enabled (from 78MOD message)
        /// </summary>
        public bool EightBitMode { get; set; }

        /// <summary>
        /// OPSYS version received (from 7OPSV message)
        /// </summary>
        public byte OpsysVersion { get; set; }

        /// <summary>
        /// TAD protocol version received (from 7OPSV message)
        /// </summary>
        public byte TadProtocolVersion { get; set; }

        /// <summary>
        /// Break mode matrix (20 bytes = 160 bits)
        /// Each bit indicates if character is a break character
        /// </summary>
        public byte[] BreakModeMatrix { get; set; } = new byte[20];

        /// <summary>
        /// Echo mode matrix (20 bytes = 160 bits)
        /// Each bit indicates if character should be echoed
        /// </summary>
        public byte[] EchoModeMatrix { get; set; } = new byte[20];

        /// <summary>
        /// Ready for input flag
        /// True when 7RFI has been sent and waiting for data
        /// </summary>
        public bool ReadyForInput { get; set; }

        /// <summary>
        /// Number of messages sent
        /// </summary>
        public int MessagesSent { get; set; }

        /// <summary>
        /// Number of messages received
        /// </summary>
        public int MessagesReceived { get; set; }

        /// <summary>
        /// Number of bytes sent
        /// </summary>
        public long BytesSent { get; set; }

        /// <summary>
        /// Number of bytes received
        /// </summary>
        public long BytesReceived { get; set; }

        /// <summary>
        /// Pending output messages (normal priority queue)
        /// </summary>
        public Queue<SintranTadMessage> OutputQueueNormal { get; set; } = new Queue<SintranTadMessage>();

        /// <summary>
        /// Pending output messages (high priority queue)
        /// High priority messages are sent before normal priority
        /// </summary>
        public Queue<SintranTadMessage> OutputQueueHigh { get; set; } = new Queue<SintranTadMessage>();

        /// <summary>
        /// Initialization complete flag
        /// Set to true after INIBDR/INISND handshake and configuration exchange
        /// </summary>
        public bool InitializationComplete { get; set; }

        /// <summary>
        /// Configuration received flags
        /// </summary>
        public bool ReceivedTerminalType { get; set; }
        /// <summary>
        /// Gets or sets whether the terminal mode has been received.
        /// </summary>
        public bool ReceivedTerminalMode { get; set; }
        /// <summary>
        /// Gets or sets whether the operating system version has been received.
        /// </summary>
        public bool ReceivedOpsysVersion { get; set; }

        /// <summary>
        /// Last error message
        /// </summary>
        public string? LastError { get; set; }

        /// <summary>
        /// Check if character is a break character according to break mode matrix
        /// </summary>
        /// <param name="ch">
        /// Character to check
        /// </param>
        /// <returns>
        /// True if character triggers a break
        /// </returns>
        public bool IsBreakCharacter(byte ch)
        {
            if (ch >= 160) return false; // Only 160 bits in matrix

            int byteIndex = ch / 8;
            int bitIndex = ch % 8;

            return (BreakModeMatrix[byteIndex] & (1 << bitIndex)) != 0;
        }

        /// <summary>
        /// Check if character should be echoed according to echo mode matrix
        /// </summary>
        /// <param name="ch">
        /// Character to check
        /// </param>
        /// <returns>
        /// True if character should be echoed
        /// </returns>
        public bool IsEchoCharacter(byte ch)
        {
            if (ch >= 160) return false; // Only 160 bits in matrix

            int byteIndex = ch / 8;
            int bitIndex = ch % 8;

            return (EchoModeMatrix[byteIndex] & (1 << bitIndex)) != 0;
        }

        /// <summary>
        /// Set break character in break mode matrix
        /// </summary>
        public void SetBreakCharacter(byte ch, bool isBreak)
        {
            if (ch >= 160) return;

            int byteIndex = ch / 8;
            int bitIndex = ch % 8;

            if (isBreak)
                BreakModeMatrix[byteIndex] |= (byte)(1 << bitIndex);
            else
                BreakModeMatrix[byteIndex] &= (byte)~(1 << bitIndex);
        }

        /// <summary>
        /// Set echo character in echo mode matrix
        /// </summary>
        public void SetEchoCharacter(byte ch, bool shouldEcho)
        {
            if (ch >= 160) return;

            int byteIndex = ch / 8;
            int bitIndex = ch % 8;

            if (shouldEcho)
                EchoModeMatrix[byteIndex] |= (byte)(1 << bitIndex);
            else
                EchoModeMatrix[byteIndex] &= (byte)~(1 << bitIndex);
        }

        /// <summary>
        /// Enqueue message for sending
        /// High priority messages go to high priority queue
        /// Normal priority messages go to normal priority queue
        /// </summary>
        public void EnqueueMessage(SintranTadMessage message)
        {
            if (message.Priority == SintranTadMessageTypes.TadMessagePriority.High)
            {
                OutputQueueHigh.Enqueue(message);
            }
            else
            {
                OutputQueueNormal.Enqueue(message);
            }
        }

        /// <summary>
        /// Dequeue next message for sending
        /// High priority messages are dequeued first
        /// </summary>
        public SintranTadMessage? DequeueMessage()
        {
            if (OutputQueueHigh.Count > 0)
                return OutputQueueHigh.Dequeue();

            if (OutputQueueNormal.Count > 0)
                return OutputQueueNormal.Dequeue();

            return null;
        }

        /// <summary>
        /// Check if there are pending messages to send
        /// </summary>
        public bool HasPendingMessages => OutputQueueHigh.Count > 0 || OutputQueueNormal.Count > 0;

        /// <summary>
        /// Get total pending message count
        /// </summary>
        public int PendingMessageCount => OutputQueueHigh.Count + OutputQueueNormal.Count;

        /// <summary>
        /// Check if initialization is complete
        /// Configuration messages received and handshake done
        /// </summary>
        public bool IsInitializationComplete()
        {
            return InitializationComplete &&
                   ReceivedTerminalType &&
                   ReceivedTerminalMode &&
                   ReceivedOpsysVersion;
        }

        /// <summary>
        /// Update last activity timestamp
        /// </summary>
        public void Touch()
        {
            LastActivity = DateTime.UtcNow;
        }

        /// <summary>
        /// Get session age (time since creation)
        /// </summary>
        public TimeSpan GetAge()
        {
            return DateTime.UtcNow - CreatedAt;
        }

        /// <summary>
        /// Get idle time (time since last activity)
        /// </summary>
        public TimeSpan GetIdleTime()
        {
            return DateTime.UtcNow - LastActivity;
        }

        /// <summary>
        /// Get string representation of session
        /// </summary>
        public override string ToString()
        {
            return $"TAD Session LCN={LCN} State={State} " +
                   $"In={MessagesReceived} Out={MessagesSent} " +
                   $"Idle={GetIdleTime().TotalSeconds:F1}s";
        }
    }

    /// <summary>
    /// TAD Session State
    /// Tracks the connection lifecycle
    /// </summary>
    public enum TadSessionState
    {
        /// <summary>
        /// Session created but not yet initialized
        /// </summary>
        Created,

        /// <summary>
        /// Waiting for INIBDR (initialization begin data receiver)
        /// </summary>
        WaitingForInibdr,

        /// <summary>
        /// INIBDR received, sent INISND (initialization send)
        /// </summary>
        InitializingSend,

        /// <summary>
        /// Exchanging configuration messages
        /// </summary>
        ConfiguringConnection,

        /// <summary>
        /// Configuration complete, ready for data transfer
        /// </summary>
        Active,

        /// <summary>
        /// Connection is being closed
        /// </summary>
        Closing,

        /// <summary>
        /// Connection closed
        /// </summary>
        Closed,

        /// <summary>
        /// Error state
        /// </summary>
        Error
    }
}
