//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;

using RetroIO.Protocols.X25.Protocols;
namespace RetroIO.Protocols.X25.Sintran
{
    /// <summary>
    /// SINTRAN TAD Protocol Message
    /// Handles parsing and building of TAD messages
    /// Based on real SINTRAN NPL source code (RP-P2-TAD.NPL:162-178)
    /// </summary>
    public class SintranTadMessage
    {
        /// <summary>
        /// Message type (7DUMM, 7BDAT, 7RFI, etc.)
        /// </summary>
        public byte MessageType { get; set; }

        /// <summary>
        /// Message data payload (0-255 bytes)
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Message priority (High or Normal)
        /// </summary>
        public SintranTadMessageTypes.TadMessagePriority Priority { get; set; }

        /// <summary>
        /// True if this message had a padding byte
        /// Padding occurs when message starts at odd byte position
        /// </summary>
        public bool HasPadding { get; set; }

        /// <summary>
        /// Get human-readable message type name
        /// </summary>
        public string MessageTypeName => SintranTadMessageTypes.GetMessageTypeName(MessageType);

        /// <summary>
        /// Check if this is a data message (7BDAT)
        /// </summary>
        public bool IsDataMessage => SintranTadMessageTypes.IsDataMessage(MessageType);

        /// <summary>
        /// Check if this is a control message
        /// </summary>
        public bool IsControlMessage => SintranTadMessageTypes.IsControlMessage(MessageType);

        /// <summary>
        /// Check if this is a configuration message
        /// </summary>
        public bool IsConfigurationMessage => SintranTadMessageTypes.IsConfigurationMessage(MessageType);

        /// <summary>
        /// Parse TAD message from byte array
        /// Message format (from RP-P2-TAD.NPL:162-178):
        /// [Optional Pad 0x00] [Message Type] [Byte Count] [Data...]
        /// </summary>
        /// <param name="data">
        /// Message bytes
        /// </param>
        /// <param name="offset">
        /// Starting offset in data array
        /// </param>
        /// <param name="length">
        /// Number of bytes to parse
        /// </param>
        /// <returns>
        /// Parsed TAD message
        /// </returns>
        public static SintranTadMessage Parse(byte[] data, int offset = 0, int length = -1)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (length < 0)
                length = data.Length - offset;

            if (offset < 0 || offset >= data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            if (length < SintranTadMessageTypes.MIN_MESSAGE_SIZE)
                throw new ArgumentException($"TAD message too short: {length} bytes (minimum {SintranTadMessageTypes.MIN_MESSAGE_SIZE})");

            var message = new SintranTadMessage();
            int position = offset;

            // Check for padding byte (0x00 at start indicates padding)
            // From WRMHEAD: "IF TDBTPT BIT 0 THEN" - if odd position, pad byte exists
            if (data[position] == 0x00 && length > 2)
            {
                // Might be padding, check if next byte is a valid message type
                byte potentialType = data[position + 1];
                if (IsValidMessageType(potentialType))
                {
                    message.HasPadding = true;
                    position++; // Skip padding byte
                }
            }

            // Parse message type
            message.MessageType = data[position++];

            // Parse byte count
            byte byteCount = data[position++];

            // Validate byte count
            int remainingBytes = length - (position - offset);
            if (byteCount > remainingBytes)
            {
                throw new ArgumentException($"TAD message byte count ({byteCount}) exceeds available data ({remainingBytes})");
            }

            // Parse data field
            if (byteCount > 0)
            {
                message.Data = new byte[byteCount];
                Array.Copy(data, position, message.Data, 0, byteCount);
            }

            // Determine priority based on message type
            message.Priority = SintranTadMessageTypes.RequiresHighPriority(message.MessageType)
                ? SintranTadMessageTypes.TadMessagePriority.High
                : SintranTadMessageTypes.TadMessagePriority.Normal;

            return message;
        }

        /// <summary>
        /// Build TAD message to byte array
        /// Message format: [Optional Pad] [Type] [Count] [Data]
        /// </summary>
        /// <param name="includePadding">
        /// True to include padding byte at start
        /// </param>
        /// <returns>
        /// Message bytes
        /// </returns>
        public byte[] Build(bool includePadding = false)
        {
            if (Data.Length > SintranTadMessageTypes.MAX_MESSAGE_DATA_SIZE)
            {
                throw new InvalidOperationException($"TAD message data too large: {Data.Length} bytes (maximum {SintranTadMessageTypes.MAX_MESSAGE_DATA_SIZE})");
            }

            int messageSize = 2 + Data.Length; // Type + Count + Data
            if (includePadding)
                messageSize++; // Add padding byte

            byte[] result = new byte[messageSize];
            int position = 0;

            // Optional padding byte
            if (includePadding)
            {
                result[position++] = 0x00;
            }

            // Message type
            result[position++] = MessageType;

            // Byte count
            result[position++] = (byte)Data.Length;

            // Data
            if (Data.Length > 0)
            {
                Array.Copy(Data, 0, result, position, Data.Length);
            }

            return result;
        }

        /// <summary>
        /// Create empty message (7DUMM)
        /// </summary>
        public static SintranTadMessage CreateEmpty()
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.DUMM,
                Data = Array.Empty<byte>(),
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Create data message (7BDAT)
        /// </summary>
        public static SintranTadMessage CreateData(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length > SintranTadMessageTypes.MAX_MESSAGE_DATA_SIZE)
                throw new ArgumentException($"Data too large: {data.Length} bytes (maximum {SintranTadMessageTypes.MAX_MESSAGE_DATA_SIZE})");

            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.BDAT,
                Data = data,
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Create data message from string (7BDAT)
        /// </summary>
        public static SintranTadMessage CreateData(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));

            byte[] data = Encoding.ASCII.GetBytes(text);
            return CreateData(data);
        }

        /// <summary>
        /// Create Ready For Input message (7RFI)
        /// </summary>
        public static SintranTadMessage CreateReadyForInput()
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.RFI,
                Data = Array.Empty<byte>(),
                Priority = SintranTadMessageTypes.TadMessagePriority.High
            };
        }

        /// <summary>
        /// Create Terminal Mode message (7TMOD)
        /// </summary>
        public static SintranTadMessage CreateTerminalMode(byte mode)
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.TMOD,
                Data = new byte[] { mode },
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Create Terminal Type message (7TTYP)
        /// </summary>
        public static SintranTadMessage CreateTerminalType(ushort terminalType)
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.TTYP,
                Data = new byte[] { (byte)(terminalType >> 8), (byte)(terminalType & 0xFF) },
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Create Define Escape message (7DESC)
        /// </summary>
        public static SintranTadMessage CreateDefineEscape(byte escapeChar)
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.DESC,
                Data = new byte[] { escapeChar },
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Create Escape message (7ESCA)
        /// </summary>
        public static SintranTadMessage CreateEscape()
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.ESCA,
                Data = Array.Empty<byte>(),
                Priority = SintranTadMessageTypes.TadMessagePriority.High
            };
        }

        /// <summary>
        /// Create OPSYS Version message (7OPSV)
        /// </summary>
        public static SintranTadMessage CreateOpsysVersion(byte osVersion, byte tadProtocolVersion)
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.OPSV,
                Data = new byte[] { osVersion, tadProtocolVersion },
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Create Disconnect message (7DCON)
        /// </summary>
        public static SintranTadMessage CreateDisconnect()
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.DCON,
                Data = Array.Empty<byte>(),
                Priority = SintranTadMessageTypes.TadMessagePriority.High
            };
        }

        /// <summary>
        /// Create Break Mode Matrix message (7BMMX)
        /// </summary>
        public static SintranTadMessage CreateBreakModeMatrix(byte[] matrix)
        {
            if (matrix == null)
                throw new ArgumentNullException(nameof(matrix));

            if (matrix.Length != SintranTadMessageTypes.STRATEGY_TABLE_SIZE)
                throw new ArgumentException($"Break mode matrix must be {SintranTadMessageTypes.STRATEGY_TABLE_SIZE} bytes");

            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.BMMX,
                Data = matrix,
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Create Echo Mode Matrix message (7ECKM)
        /// </summary>
        public static SintranTadMessage CreateEchoModeMatrix(byte[] matrix)
        {
            if (matrix == null)
                throw new ArgumentNullException(nameof(matrix));

            if (matrix.Length != SintranTadMessageTypes.STRATEGY_TABLE_SIZE)
                throw new ArgumentException($"Echo mode matrix must be {SintranTadMessageTypes.STRATEGY_TABLE_SIZE} bytes");

            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.ECKM,
                Data = matrix,
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Check if a byte value is a valid TAD message type
        /// </summary>
        private static bool IsValidMessageType(byte messageType)
        {
            // Valid message types are 0x00-0x13 based on known types
            return messageType <= 0x13;
        }

        /// <summary>
        /// Get string representation of message
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"TAD {MessageTypeName}");

            if (Priority == SintranTadMessageTypes.TadMessagePriority.High)
                sb.Append(" [HIGH]");

            if (HasPadding)
                sb.Append(" [PAD]");

            sb.Append($" ({Data.Length} bytes)");

            // Show data preview for small messages
            if (Data.Length > 0 && Data.Length <= 16)
            {
                sb.Append($": {BitConverter.ToString(Data)}");
            }
            else if (Data.Length > 0)
            {
                sb.Append($": {BitConverter.ToString(Data, 0, Math.Min(16, Data.Length))}...");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Get data as ASCII string (for 7BDAT messages)
        /// </summary>
        public string GetDataAsString()
        {
            if (Data.Length == 0)
                return string.Empty;

            return Encoding.ASCII.GetString(Data);
        }

        /// <summary>
        /// Alias for Build() - used by handler
        /// </summary>
        public byte[] ToBytes()
        {
            return Build(HasPadding);
        }

        /// <summary>
        /// Alias for CreateData() - used by handler
        /// </summary>
        public static SintranTadMessage CreateBinaryData(byte[] data)
        {
            return CreateData(data);
        }

        /// <summary>
        /// Alias for CreateEmpty() - used by handler
        /// </summary>
        public static SintranTadMessage CreateDummy()
        {
            return CreateEmpty();
        }

        /// <summary>
        /// Create Ready For Input message with byte count (7RFI)
        /// </summary>
        public static SintranTadMessage CreateReadyForInput(int byteCount)
        {
            byte[] data = new byte[2];
            data[0] = (byte)(byteCount >> 8);
            data[1] = (byte)(byteCount & 0xFF);

            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.RFI,
                Data = data,
                Priority = SintranTadMessageTypes.TadMessagePriority.High
            };
        }

        /// <summary>
        /// Alias for CreateBreakModeMatrix() - used by handler
        /// </summary>
        public static SintranTadMessage CreateBreakMatrix(byte[] matrix)
        {
            return CreateBreakModeMatrix(matrix);
        }

        /// <summary>
        /// Alias for CreateEchoModeMatrix() - used by handler
        /// </summary>
        public static SintranTadMessage CreateEchoMatrix(byte[] matrix)
        {
            return CreateEchoModeMatrix(matrix);
        }

        /// <summary>
        /// Create Terminal Type message with byte parameter (7TTYP)
        /// </summary>
        public static SintranTadMessage CreateTerminalType(byte terminalType)
        {
            return CreateTerminalType((ushort)terminalType);
        }

        /// <summary>
        /// Create Terminal Mode message with flags (7TMOD)
        /// </summary>
        public static SintranTadMessage CreateTerminalMode(bool echoPlex, bool localEdit, bool pageMode, bool formFeed)
        {
            byte mode = 0;
            if (echoPlex) mode |= 0x01;
            if (localEdit) mode |= 0x02;
            if (pageMode) mode |= 0x04;
            if (formFeed) mode |= 0x08;

            return CreateTerminalMode(mode);
        }

        /// <summary>
        /// Create Descriptor message (7DESC)
        /// </summary>
        public static SintranTadMessage CreateDescriptor(int lineWidth, int pageLength, int tabWidth)
        {
            byte[] data = new byte[6];
            data[0] = (byte)(lineWidth >> 8);
            data[1] = (byte)(lineWidth & 0xFF);
            data[2] = (byte)(pageLength >> 8);
            data[3] = (byte)(pageLength & 0xFF);
            data[4] = (byte)(tabWidth >> 8);
            data[5] = (byte)(tabWidth & 0xFF);

            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.DESC,
                Data = data,
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Create Option Save message (7OPSV)
        /// </summary>
        public static SintranTadMessage CreateOptionSave(bool save)
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.OPSV,
                Data = new byte[] { (byte)(save ? 0x01 : 0x00) },
                Priority = SintranTadMessageTypes.TadMessagePriority.Normal
            };
        }

        /// <summary>
        /// Create Escape Action message (7ESCA)
        /// </summary>
        public static SintranTadMessage CreateEscapeAction()
        {
            return CreateEscape();
        }

        /// <summary>
        /// Create Relocate message (7RLOC)
        /// </summary>
        public static SintranTadMessage CreateRelocate()
        {
            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.RLOC,
                Data = Array.Empty<byte>(),
                Priority = SintranTadMessageTypes.TadMessagePriority.High
            };
        }

        /// <summary>
        /// Create Error message (7ERRS)
        /// </summary>
        public static SintranTadMessage CreateError(string errorMessage)
        {
            byte[] data = Encoding.ASCII.GetBytes(errorMessage);
            if (data.Length > SintranTadMessageTypes.MAX_MESSAGE_DATA_SIZE)
            {
                Array.Resize(ref data, SintranTadMessageTypes.MAX_MESSAGE_DATA_SIZE);
            }

            return new SintranTadMessage
            {
                MessageType = SintranTadMessageTypes.ERRS,
                Data = data,
                Priority = SintranTadMessageTypes.TadMessagePriority.High
            };
        }

        /// <summary>
        /// Try to parse TAD message from byte array
        /// </summary>
        public static bool TryParse(byte[] data, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SintranTadMessage? message, out string error)
        {
            message = null;
            error = string.Empty;

            try
            {
                message = Parse(data);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
