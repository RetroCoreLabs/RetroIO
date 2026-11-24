//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// The DATA-TYPE byte of a TN3270E header (RFC 2355 section 8.1.1).
    /// </summary>
    public static class Tn3270EDataType
    {
        /// <summary>
        /// X'00', the data is a 3270 data stream.
        /// </summary>
        public const byte Data3270 = 0x00;

        /// <summary>
        /// X'01', the data is SNA Character Stream (printer sessions).
        /// </summary>
        public const byte ScsData = 0x01;

        /// <summary>
        /// X'02', the data is one status byte answering an earlier message; see
        /// <see cref="Tn3270EResponseFlag"/> and <see cref="Tn3270EResponseStatus"/>.
        /// </summary>
        public const byte Response = 0x02;

        /// <summary>
        /// X'03', the data is the SNA BIND image of the host session.
        /// </summary>
        public const byte BindImage = 0x03;

        /// <summary>
        /// X'04', the data is an UNBIND reason code.
        /// </summary>
        public const byte Unbind = 0x04;

        /// <summary>
        /// X'05', the data is NVT (plain line mode) data.
        /// </summary>
        public const byte NvtData = 0x05;

        /// <summary>
        /// X'06', no data; only the REQUEST-FLAG means anything.
        /// </summary>
        public const byte Request = 0x06;

        /// <summary>
        /// X'07', the data comes from the SSCP-LU session.
        /// </summary>
        public const byte SscpLuData = 0x07;

        /// <summary>
        /// X'08', no data, end of a print job; sent only by the server on a printer session.
        /// </summary>
        public const byte PrintEndOfJob = 0x08;
    }

    /// <summary>
    /// The RESPONSE-FLAG byte of a TN3270E header (RFC 2355 section 8.1.3). Its meaning depends on
    /// the DATA-TYPE.
    /// </summary>
    public static class Tn3270EResponseFlag
    {
        /// <summary>
        /// X'00' on 3270-DATA or SCS-DATA: the sender wants no response.
        /// </summary>
        public const byte NoResponse = 0x00;

        /// <summary>
        /// X'01' on 3270-DATA or SCS-DATA: answer only if something went wrong.
        /// </summary>
        public const byte ErrorResponse = 0x01;

        /// <summary>
        /// X'02' on 3270-DATA or SCS-DATA: always answer, positively or negatively.
        /// </summary>
        public const byte AlwaysResponse = 0x02;

        /// <summary>
        /// X'00' on RESPONSE: the earlier message was done without errors.
        /// </summary>
        public const byte Positive = 0x00;

        /// <summary>
        /// X'01' on RESPONSE: the earlier message had an error.
        /// </summary>
        public const byte Negative = 0x01;
    }

    /// <summary>
    /// The REQUEST-FLAG byte of a TN3270E header (RFC 2355 section 8.1.2); it means something only
    /// when the DATA-TYPE is <see cref="Tn3270EDataType.Request"/>.
    /// </summary>
    public static class Tn3270ERequestFlag
    {
        /// <summary>
        /// X'00', ERR-COND-CLEARED: "The client sends this to the server when some previously
        /// encountered printer error condition has been cleared" (section 8.1.2), after it has
        /// answered with Intervention Required or Component Disconnected (section 10.4.1).
        /// </summary>
        public const byte ErrorConditionCleared = 0x00;
    }

    /// <summary>
    /// The one data byte of a RESPONSE message (RFC 2355 section 10.4.1).
    /// </summary>
    public static class Tn3270EResponseStatus
    {
        /// <summary>
        /// Positive: done ("Device End").
        /// </summary>
        public const byte Successful = 0x00;

        /// <summary>
        /// Negative: an invalid 3270 command ("Command Reject", SNA sense X'10030000').
        /// </summary>
        public const byte CommandReject = 0x00;

        /// <summary>
        /// Negative: the printer is not ready ("Intervention Required", SNA sense X'08020000').
        /// </summary>
        public const byte InterventionRequired = 0x01;

        /// <summary>
        /// Negative: a bad buffer address or order sequence ("Operation Check", SNA sense
        /// X'10050000').
        /// </summary>
        public const byte OperationCheck = 0x02;

        /// <summary>
        /// Negative: the printer is off or not connected ("Component Disconnected", SNA sense
        /// X'08310000').
        /// </summary>
        public const byte ComponentDisconnected = 0x03;
    }

    /// <summary>
    /// The five-byte header in front of every TN3270E data message (RFC 2355 section 8.1).
    /// </summary>
    /// <remarks>
    /// The header is read and written after Telnet has removed or added its IAC doubling: a X'FF' in
    /// the sequence number travels as X'FFFF' on the wire (section 8.1.4), which is the Telnet
    /// layer's job like every other X'FF' in the record.
    /// </remarks>
    public readonly struct Tn3270EHeader
    {
        /// <summary>
        /// The header length in bytes.
        /// </summary>
        public const int Length = 5;

        /// <summary>
        /// Creates a header.
        /// </summary>
        /// <param name="dataType">
        /// The DATA-TYPE, from <see cref="Tn3270EDataType"/>.
        /// </param>
        /// <param name="requestFlag">
        /// The REQUEST-FLAG; only meaningful for <see cref="Tn3270EDataType.Request"/>, X'00'
        /// otherwise.
        /// </param>
        /// <param name="responseFlag">
        /// The RESPONSE-FLAG, from <see cref="Tn3270EResponseFlag"/>.
        /// </param>
        /// <param name="sequenceNumber">
        /// The SEQ-NUMBER; 0 unless the RESPONSES function was agreed.
        /// </param>
        public Tn3270EHeader(byte dataType, byte requestFlag, byte responseFlag, ushort sequenceNumber)
        {
            DataType = dataType;
            RequestFlag = requestFlag;
            ResponseFlag = responseFlag;
            SequenceNumber = sequenceNumber;
        }

        /// <summary>
        /// Gets the DATA-TYPE byte.
        /// </summary>
        public byte DataType { get; }

        /// <summary>
        /// Gets the REQUEST-FLAG byte.
        /// </summary>
        public byte RequestFlag { get; }

        /// <summary>
        /// Gets the RESPONSE-FLAG byte.
        /// </summary>
        public byte ResponseFlag { get; }

        /// <summary>
        /// Gets the SEQ-NUMBER, big-endian on the wire.
        /// </summary>
        public ushort SequenceNumber { get; }

        /// <summary>
        /// Reads the header at the start of a TN3270E record.
        /// </summary>
        /// <param name="record">
        /// The record, header first, Telnet escaping already removed.
        /// </param>
        /// <param name="header">
        /// The header, when the record is long enough.
        /// </param>
        /// <returns>
        /// False when the record is shorter than 5 bytes.
        /// </returns>
        public static bool TryRead(ReadOnlySpan<byte> record, out Tn3270EHeader header)
        {
            if (record.Length < Length)
            {
                header = default;
                return false;
            }

            header = new Tn3270EHeader(record[0], record[1], record[2], (ushort)((record[3] << 8) | record[4]));
            return true;
        }

        /// <summary>
        /// Builds the RESPONSE message that answers a 3270-DATA or SCS-DATA message (RFC 2355
        /// section 10.4.1): header, then one status byte.
        /// </summary>
        /// <param name="sequenceNumber">
        /// The SEQ-NUMBER of the message being answered.
        /// </param>
        /// <param name="positive">
        /// True for a positive response.
        /// </param>
        /// <param name="status">
        /// The status byte, from <see cref="Tn3270EResponseStatus"/>.
        /// </param>
        /// <param name="destination">
        /// At least 6 bytes.
        /// </param>
        /// <returns>
        /// The number of bytes written, 6.
        /// </returns>
        public static int WriteResponse(ushort sequenceNumber, bool positive, byte status, Span<byte> destination)
        {
            new Tn3270EHeader(Tn3270EDataType.Response, 0x00, positive ? Tn3270EResponseFlag.Positive : Tn3270EResponseFlag.Negative, sequenceNumber).Write(destination);
            destination[Length] = status;
            return Length + 1;
        }

        /// <summary>
        /// Builds the REQUEST message that says a printer error condition has been cleared (RFC
        /// 2355 section 10.4.1): DATA-TYPE REQUEST, REQUEST-FLAG ERR-COND-CLEARED, and "Note that
        /// this message has no data portion."
        /// </summary>
        /// <remarks>
        /// The RESPONSE-FLAG is X'00' because section 8.1.3 gives it a meaning only on 3270-DATA,
        /// SCS-DATA and RESPONSE, and section 8.1.2 says a flag without meaning "should be set to
        /// 0x00 by the sender". The SEQ-NUMBER is X'0000' because section 10.4 counts only
        /// 3270-DATA and SCS-DATA messages; the RFC does not say what a REQUEST carries there.
        /// </remarks>
        /// <param name="destination">
        /// At least 5 bytes.
        /// </param>
        /// <returns>
        /// The number of bytes written, 5.
        /// </returns>
        public static int WriteErrorConditionCleared(Span<byte> destination)
        {
            new Tn3270EHeader(Tn3270EDataType.Request, Tn3270ERequestFlag.ErrorConditionCleared, 0x00, 0).Write(destination);
            return Length;
        }

        /// <summary>
        /// Writes the header.
        /// </summary>
        /// <param name="destination">
        /// At least 5 bytes.
        /// </param>
        public void Write(Span<byte> destination)
        {
            if (destination.Length < Length)
            {
                throw new ArgumentException("The header needs 5 bytes.", nameof(destination));
            }

            destination[0] = DataType;
            destination[1] = RequestFlag;
            destination[2] = ResponseFlag;
            destination[3] = (byte)(SequenceNumber >> 8);
            destination[4] = (byte)SequenceNumber;
        }
    }
}
