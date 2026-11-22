//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// What an <see cref="IndFileTransfer"/> is doing.
    /// </summary>
    public enum FileTransferState
    {
        /// <summary>
        /// Nothing armed and nothing open.
        /// </summary>
        Idle,

        /// <summary>
        /// A file is ready for the host to fetch or fill; the operator still has to type the
        /// IND$FILE command.
        /// </summary>
        Armed,

        /// <summary>
        /// The host opened the data file and sends records to the PC.
        /// </summary>
        Downloading,

        /// <summary>
        /// The host opened the data file and fetches records from the PC.
        /// </summary>
        Uploading,

        /// <summary>
        /// The host closed the data file; the closing message has not come yet.
        /// </summary>
        WaitingForMessage,

        /// <summary>
        /// The host opened the message file and sends its TRANSxx message.
        /// </summary>
        ReceivingMessage,
    }

    /// <summary>
    /// The PC side of an IND$FILE transfer in DFT mode: the X'D0' structured fields a host sends
    /// inside Write Structured Field, and the replies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The formats are from IBM SS-HCS12-1372-00, "IND$FILE Datastream Description" (1995),
    /// figures 27 to 43, as written down in RetroTerm docs/spec/IBM3270/IND-FILE-TRANSFER.md
    /// section 3. IBM's own cover says that paper is not a specification. Every reply is AID X'88'
    /// and one structured field.
    /// </para>
    /// <para>
    /// How to use: set <see cref="Display3270.FileTransferLimit"/> above 0 so the host is told
    /// the terminal can do DFT, make this object on the display, call
    /// <see cref="ExpectDownload"/> or <see cref="ExpectUpload"/>, then type the IND$FILE command
    /// on the host screen. <see cref="Finished"/> says how it ended.
    /// </para>
    /// <para>
    /// Bytes pass unchanged both ways. In ASCII mode IND$FILE translates on the host and adds CR
    /// LF after each downloaded record, which is a Windows text file as it stands; any other line
    /// end handling is the caller's.
    /// </para>
    /// <para>
    /// NOT VERIFIED against a trace of a real host, all marked where they are used:
    ///  - the first record number (1 here);
    ///  - the character code of the file name in the Open and of the message text;
    ///  - which error code a host expects when the PC cannot read the file it uploads.
    /// </para>
    /// </remarks>
    public sealed class IndFileTransfer
    {
        // Request types (byte 3) and kinds (byte 4) of a D0 field (IND-FILE-TRANSFER.md 3.2).
        private const byte TypeOpen = 0x00;
        private const byte TypeClose = 0x41;
        private const byte TypeSetCursor = 0x45;
        private const byte TypeGet = 0x46;
        private const byte TypeInsert = 0x47;
        private const byte KindRequest = 0x12;
        private const byte KindAck = 0x09;
        private const byte KindCommand = 0x11;
        private const byte KindData = 0x04;
        private const byte KindDataReply = 0x05;
        private const byte KindError = 0x08;

        // Error codes (IND-FILE-TRANSFER.md 3.11).
        private const ushort OpenFailed = 0x0100;
        private const ushort ArrivalSequenceNotAllowed = 0x0200;
        private const ushort GetPastEndOfFile = 0x2200;
        private const ushort OperationNotAuthorized = 0x3E00;
        private const ushort CommandSyntaxError = 0x6000;

        // The upload data header: length, D0 46 05, 63 06 and the record number, C0 80, 61, length.
        private const int UploadHeader = 16;

        private readonly Display3270 _display;
        private readonly StringBuilder _message = new StringBuilder();

        private Stream? _stream;
        private bool _upload;
        private uint _record;
        private byte[] _scratch = Array.Empty<byte>();

        /// <summary>
        /// Creates the file transfer side of a display and starts listening to its X'D0' fields.
        /// </summary>
        /// <param name="display">
        /// The display whose <see cref="Display3270.FileTransferField"/> event feeds this object
        /// and through which the replies go.
        /// </param>
        public IndFileTransfer(Display3270 display)
        {
            ArgumentNullException.ThrowIfNull(display);
            _display = display;
            _display.FileTransferField += OnField;
        }

        /// <summary>
        /// The transfer ended: the host sent its closing message, or the host or the PC gave up.
        /// </summary>
        public event FileTransferFinishedHandler? Finished;

        /// <summary>
        /// A data record went one way or the other.
        /// </summary>
        public event FileTransferProgressHandler? Progress;

        /// <summary>
        /// Gets what the transfer is doing now.
        /// </summary>
        public FileTransferState State { get; private set; }

        /// <summary>
        /// Gets the number of file bytes moved since the transfer was armed.
        /// </summary>
        public long BytesTransferred { get; private set; }

        /// <summary>
        /// Gets ready to receive a file the host will send (IND$FILE GET on TSO, RECEIVE on the PC).
        /// </summary>
        /// <param name="destination">
        /// Where the file's bytes go. This object does not close it.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a transfer is already armed or running.
        /// </exception>
        public void ExpectDownload(Stream destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            Arm(destination, upload: false);
        }

        /// <summary>
        /// Gets ready to send a file the host will fetch (IND$FILE PUT on TSO, SEND on the PC).
        /// </summary>
        /// <param name="source">
        /// Where the file's bytes come from. This object does not close it.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a transfer is already armed or running.
        /// </exception>
        public void ExpectUpload(Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            Arm(source, upload: true);
        }

        /// <summary>
        /// Forgets an armed transfer that the host has not opened yet.
        /// </summary>
        /// <remarks>
        /// A running transfer cannot be stopped from here without the host: it ends with the
        /// host's message.
        /// </remarks>
        public void Disarm()
        {
            if (State == FileTransferState.Armed)
            {
                _stream = null;
                State = FileTransferState.Idle;
            }
        }

        private void Arm(Stream stream, bool upload)
        {
            if (State != FileTransferState.Idle)
            {
                throw new InvalidOperationException("A file transfer is already " + State + ".");
            }

            _stream = stream;
            _upload = upload;
            BytesTransferred = 0;
            State = FileTransferState.Armed;
        }

        private void OnField(ReadOnlySpan<byte> field)
        {
            // L L D0 type kind ... (IND-FILE-TRANSFER.md 3.2).
            if (field.Length < 5)
            {
                Reply(TypeOpen, CommandSyntaxError);
                return;
            }

            byte type = field[3];
            byte kind = field[4];
            if (type == TypeOpen && kind == KindRequest)
            {
                Open(field);
            }
            else if (type == TypeInsert && kind == KindCommand)
            {
                // The Insert request carries nothing; the data field follows it (3.5).
                if (State != FileTransferState.Downloading && State != FileTransferState.ReceivingMessage)
                {
                    Reply(TypeInsert, ArrivalSequenceNotAllowed);
                }
            }
            else if (type == TypeInsert && kind == KindData)
            {
                Insert(field);
            }
            else if (type == TypeSetCursor && kind == KindCommand)
            {
                // Set Cursor "next" carries nothing a sequential reader needs (3.7).
                if (State != FileTransferState.Uploading)
                {
                    Reply(TypeSetCursor, ArrivalSequenceNotAllowed);
                }
            }
            else if (type == TypeGet && kind == KindCommand)
            {
                Get();
            }
            else if (type == TypeClose && kind == KindRequest)
            {
                Close();
            }
            else
            {
                Reply(type, CommandSyntaxError);
            }
        }

        private void Open(ReadOnlySpan<byte> field)
        {
            // Short form 0x23 bytes, long form with record size 0x29; the name is the last 7 bytes
            // and offset 14 is 01 for upload, 00 for download or message (3.3).
            if (field.Length != 0x23 && field.Length != 0x29)
            {
                Reply(TypeOpen, CommandSyntaxError);
                return;
            }

            bool upload = field[14] == 0x01;
            if (!IsDataFileName(field.Slice(field.Length - 7)))
            {
                // Anything that is not FT:DATA is the message file (3.3). The host may open it
                // without closing the data file first (3.13), so this is accepted in any state.
                _message.Clear();
                _record = 1;
                State = FileTransferState.ReceivingMessage;
                Acknowledge(TypeOpen);
                return;
            }

            if (State != FileTransferState.Armed || upload != _upload)
            {
                // No transfer of this direction was asked for on this PC.
                Reply(TypeOpen, OpenFailed);
                return;
            }

            // NOT VERIFIED: the first record number. IBM does not say; 1 is what the IBM figures'
            // sequences need if they count from the first record.
            _record = 1;
            State = upload ? FileTransferState.Uploading : FileTransferState.Downloading;
            Acknowledge(TypeOpen);
        }

        private static bool IsDataFileName(ReadOnlySpan<byte> name)
        {
            // NOT VERIFIED which character code the host uses for the name, so both are accepted:
            // ASCII "FT:DATA", and the same in EBCDIC code page 037.
            ReadOnlySpan<byte> ascii = "FT:DATA"u8;
            ReadOnlySpan<byte> ebcdic = stackalloc byte[] { 0xC6, 0xE3, 0x7A, 0xC4, 0xC1, 0xE3, 0xC1 };
            return name.SequenceEqual(ascii) || name.SequenceEqual(ebcdic);
        }

        private void Insert(ReadOnlySpan<byte> field)
        {
            // L L D0 47 04 C0 80 61 dd dd data, where dd dd is the data length plus 5 (3.5).
            if (field.Length < 10 || field[5] != 0xC0 || field[6] != 0x80 || field[7] != 0x61)
            {
                Reply(TypeInsert, CommandSyntaxError);
                return;
            }

            int length = ((field[8] << 8) | field[9]) - 5;
            if (length < 0 || 10 + length > field.Length)
            {
                Reply(TypeInsert, CommandSyntaxError);
                return;
            }

            ReadOnlySpan<byte> data = field.Slice(10, length);
            if (State == FileTransferState.ReceivingMessage)
            {
                // NOT VERIFIED: the message's character code. Read as ISO 8859-1 bytes, as both
                // oracle programs do.
                for (int i = 0; i < data.Length; i++)
                {
                    _message.Append((char)data[i]);
                }
            }
            else if (State == FileTransferState.Downloading)
            {
                try
                {
                    _stream!.Write(data);
                }
                catch (IOException)
                {
                    // The PC could not write: tell the host, which ends with its message (3.13).
                    Reply(TypeInsert, OperationNotAuthorized);
                    return;
                }

                BytesTransferred += data.Length;
                Progress?.Invoke(BytesTransferred);
            }
            else
            {
                Reply(TypeInsert, ArrivalSequenceNotAllowed);
                return;
            }

            // Data acknowledgement: L L D0 47 05 63 06 and the record number (3.6).
            Span<byte> ack = stackalloc byte[11];
            ack[0] = 0x00;
            ack[1] = 0x0B;
            ack[2] = StructuredFieldId.DataTransfer;
            ack[3] = TypeInsert;
            ack[4] = KindDataReply;
            ack[5] = 0x63;
            ack[6] = 0x06;
            WriteRecordNumber(ack.Slice(7));
            _display.SendStructuredFieldReply(ack);
        }

        private void Get()
        {
            if (State != FileTransferState.Uploading)
            {
                Reply(TypeGet, ArrivalSequenceNotAllowed);
                return;
            }

            // The whole inbound message, AID X'88' included, must stay within LIMIN (DS p. 6-37).
            int limit = _display.FileTransferLimit;
            int room = limit - 1 - UploadHeader;
            if (room < 1)
            {
                Reply(TypeGet, CommandSyntaxError);
                return;
            }

            if (_scratch.Length < UploadHeader + room)
            {
                _scratch = new byte[UploadHeader + room];
            }

            int read;
            try
            {
                read = FillFrom(_stream!, _scratch.AsSpan(UploadHeader, room));
            }
            catch (IOException)
            {
                // NOT VERIFIED which code a host wants when the PC cannot read its own file; 6000 is
                // the one IBM lists for Get that is not about the order of requests.
                Reply(TypeGet, CommandSyntaxError);
                return;
            }

            if (read == 0)
            {
                // No more data: "Get past end of file", then the host closes (3.9).
                Reply(TypeGet, GetPastEndOfFile);
                return;
            }

            // L L D0 46 05 63 06 record C0 80 61 dd dd data (3.8).
            Span<byte> s = _scratch.AsSpan(0, UploadHeader + read);
            s[0] = (byte)(s.Length >> 8);
            s[1] = (byte)s.Length;
            s[2] = StructuredFieldId.DataTransfer;
            s[3] = TypeGet;
            s[4] = KindDataReply;
            s[5] = 0x63;
            s[6] = 0x06;
            WriteRecordNumber(s.Slice(7));
            s[11] = 0xC0;
            s[12] = 0x80;
            s[13] = 0x61;
            s[14] = (byte)((read + 5) >> 8);
            s[15] = (byte)(read + 5);
            BytesTransferred += read;
            _display.SendStructuredFieldReply(s);
            Progress?.Invoke(BytesTransferred);
        }

        private static int FillFrom(Stream stream, Span<byte> buffer)
        {
            // Read returns what it has; a record is filled unless the file ends.
            int total = 0;
            while (total < buffer.Length)
            {
                int n = stream.Read(buffer.Slice(total));
                if (n == 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }

        private void Close()
        {
            // Close acknowledgement: L L D0 41 09 (3.10).
            Acknowledge(TypeClose);
            if (State == FileTransferState.ReceivingMessage)
            {
                // The message ends the transfer; TRANS03 and TRANS04 are success (3.13).
                string text = _message.ToString().TrimEnd('\0', ' ', '\r', '\n');
                bool success = text.StartsWith("TRANS03", StringComparison.Ordinal) || text.StartsWith("TRANS04", StringComparison.Ordinal);
                _stream = null;
                State = FileTransferState.Idle;
                Finished?.Invoke(success, text);
                return;
            }

            if (State == FileTransferState.Downloading || State == FileTransferState.Uploading)
            {
                State = FileTransferState.WaitingForMessage;
            }
        }

        private void Acknowledge(byte type)
        {
            Span<byte> s = stackalloc byte[] { 0x00, 0x05, StructuredFieldId.DataTransfer, type, KindAck };
            _display.SendStructuredFieldReply(s);
        }

        private void Reply(byte type, ushort error)
        {
            // Error reply: L L D0 tt 08 69 04 and the code (3.11).
            Span<byte> s = stackalloc byte[] { 0x00, 0x09, StructuredFieldId.DataTransfer, type, KindError, 0x69, 0x04, (byte)(error >> 8), (byte)error };
            _display.SendStructuredFieldReply(s);
        }

        private void WriteRecordNumber(Span<byte> target)
        {
            // Big-endian; the current number is sent, then counted up (3.6).
            target[0] = (byte)(_record >> 24);
            target[1] = (byte)(_record >> 16);
            target[2] = (byte)(_record >> 8);
            target[3] = (byte)_record;
            _record++;
        }
    }

    /// <summary>
    /// Receives the end of a file transfer.
    /// </summary>
    /// <param name="success">
    /// True when the host's message starts with TRANS03 (complete) or TRANS04 (complete, records
    /// cut to the host's length).
    /// </param>
    /// <param name="message">
    /// The host's message, for example "TRANS03 File transfer complete".
    /// </param>
    public delegate void FileTransferFinishedHandler(bool success, string message);

    /// <summary>
    /// Receives the number of file bytes moved so far.
    /// </summary>
    /// <param name="bytes">
    /// The bytes moved since the transfer was armed.
    /// </param>
    public delegate void FileTransferProgressHandler(long bytes);
}
