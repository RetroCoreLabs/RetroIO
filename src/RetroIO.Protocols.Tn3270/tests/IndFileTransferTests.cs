//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;

using Xunit;

namespace RetroIO.Protocols.Tn3270.Tests
{
    // The byte sequences are the ones in RetroTerm docs/spec/IBM3270/IND-FILE-TRANSFER.md section
    // 3, from IBM SS-HCS12-1372-00. They are not a capture of a real host.
    public class IndFileTransferTests
    {
        private static byte[] OpenRequest(bool upload, string name)
        {
            // Short form, 35 bytes (section 3.3).
            var f = new List<byte> { 0x00, 0x23, 0xD0, 0x00, 0x12, 0x01, 0x06, 0x01, 0x01, 0x04, 0x03 };
            f.AddRange(upload
                ? new byte[] { 0x0A, 0x0A, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 }
                : new byte[] { 0x0A, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x11, 0x01, 0x01, 0x00 });
            f.AddRange(new byte[] { 0x50, 0x05, 0x52, 0x03, 0xF0, 0x03, 0x09 });
            for (int i = 0; i < 7; i++)
            {
                f.Add((byte)name[i]);
            }

            return f.ToArray();
        }

        private static byte[] Insert(byte[] data)
        {
            // Insert request, then Data to Insert (section 3.5).
            var f = new List<byte> { 0x00, 0x0A, 0xD0, 0x47, 0x11, 0x01, 0x05, 0x00, 0x80, 0x00 };
            int length = 10 + data.Length;
            f.AddRange(new byte[] { (byte)(length >> 8), (byte)length, 0xD0, 0x47, 0x04, 0xC0, 0x80, 0x61, (byte)((data.Length + 5) >> 8), (byte)(data.Length + 5) });
            f.AddRange(data);
            return f.ToArray();
        }

        private static readonly byte[] SetCursorAndGet =
        {
            0x00, 0x0F, 0xD0, 0x45, 0x11, 0x01, 0x05, 0x00, 0x06, 0x00, 0x09, 0x05, 0x01, 0x03, 0x00,
            0x00, 0x09, 0xD0, 0x46, 0x11, 0x01, 0x04, 0x00, 0x80,
        };

        private static readonly byte[] CloseRequest = { 0x00, 0x05, 0xD0, 0x41, 0x12 };

        private static void Wsf(Display3270 d, byte[] fields)
        {
            byte[] record = new byte[fields.Length + 1];
            record[0] = Command3270.WriteStructuredField;
            fields.CopyTo(record, 1);
            d.ProcessOutbound(record);
        }

        private static (Display3270 Display, IndFileTransfer Transfer, List<byte[]> Sent, List<string> Errors) Create(int limit = 2048)
        {
            var d = new Display3270();
            d.FileTransferLimit = limit;
            var sent = new List<byte[]>();
            var errors = new List<string>();
            d.InboundRecord += record => sent.Add(record.ToArray());
            d.ProtocolError += errors.Add;
            return (d, new IndFileTransfer(d), sent, errors);
        }

        // The host's closing message as IBM draws it and TK5 sends it: Open for messages, one Data
        // to Insert, and nothing after the PC's Data ack (IND p.24-26 Figures 27-30; measured on
        // TK5 23 November 2026). No Close of the message file.
        private static void HostMessage(Display3270 d, string text)
        {
            Wsf(d, OpenRequest(upload: false, "FT:MSG "));
            Wsf(d, Insert(System.Text.Encoding.ASCII.GetBytes(text)));
        }

        // A whole upload as TK5 sent it, 23 November 2026 (RetroTerm guide 08 test, user HERC03,
        // "IND$FILE PUT RTTFT.TEXT ASCII CRLF"): each record is a WSF with an Outbound 3270DS
        // write (WCC C2) before the D0 field. The message's data is 85 bytes: the text, '$', and
        // blanks.
        private static readonly byte[][] Tk5Upload =
        {
            Hex("F3 00 06 40 00 F1 C2 00 29 D0 00 12 01 06 01 01 04 03 0A 0A 00 01 00 00 00 00 01 00 50 05 52 03 F0 08 06 27 04 3F EF 03 09 46 54 3A 44 41 54 41"),
            Hex("F3 00 06 40 00 F1 C2 00 0F D0 45 11 01 05 00 06 00 09 05 01 03 00 00 09 D0 46 11 01 04 00 80"),
            Hex("F3 00 06 40 00 F1 C2 00 0F D0 45 11 01 05 00 06 00 09 05 01 03 00 00 09 D0 46 11 01 04 00 80"),
            Hex("F3 00 06 40 00 F1 C2 00 05 D0 41 12"),
            Hex("F3 00 06 40 00 F1 C2 00 23 D0 00 12 01 06 01 01 04 03 0A 0A 00 00 00 00 11 01 01 00 50 05 52 03 F0 03 09 46 54 3A 4D 53 47 20"),
            Tk5Message(),
        };

        private static byte[] Hex(string text)
        {
            string[] parts = text.Split(' ');
            byte[] bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                bytes[i] = Convert.ToByte(parts[i], 16);
            }

            return bytes;
        }

        private static byte[] Tk5Message()
        {
            byte[] head = Hex("F3 00 06 40 00 F1 C2 00 0A D0 47 11 01 05 00 80 00 00 5F D0 47 04 C0 80 61 00 5A");
            byte[] record = new byte[head.Length + 85];
            head.CopyTo(record, 0);
            byte[] text = System.Text.Encoding.ASCII.GetBytes("TRANS03   File transfer complete$");
            text.CopyTo(record, head.Length);
            for (int i = head.Length + text.Length; i < record.Length; i++)
            {
                record[i] = 0x20;
            }

            return record;
        }

        [Fact]
        public void TheRecordedTk5UploadEndsWithItsMessage()
        {
            var (d, t, sent, errors) = Create(16384);
            bool? ok = null;
            string message = string.Empty;
            t.Finished += (success, text) =>
            {
                ok = success;
                message = text;
            };

            t.ExpectUpload(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("HELLO\r\n")));
            for (int i = 0; i < Tk5Upload.Length; i++)
            {
                d.ProcessOutbound(Tk5Upload[i]);
            }

            Assert.Empty(errors);
            Assert.True(ok);
            Assert.Equal("TRANS03   File transfer complete", message);
            Assert.Equal(FileTransferState.Idle, t.State);

            // What we sent back, as TK5 accepted it: open ack, the record, end of file, close
            // ack, message open ack, message Data ack (record number 1).
            Assert.Equal(new byte[] { 0x88, 0x00, 0x05, 0xD0, 0x00, 0x09 }, sent[0]);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x09, 0xD0, 0x46, 0x08, 0x69, 0x04, 0x22, 0x00 }, sent[2]);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x05, 0xD0, 0x41, 0x09 }, sent[3]);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x0B, 0xD0, 0x47, 0x05, 0x63, 0x06, 0x00, 0x00, 0x00, 0x01 }, sent[5]);
            Assert.Equal(6, sent.Count);
        }

        [Fact]
        public void TheTransferEndsOnTheMessagesDataAckWithNoClose()
        {
            var (d, t, sent, errors) = Create();
            bool? ok = null;
            t.Finished += (success, _) => ok = success;
            t.ExpectUpload(new MemoryStream(new byte[] { 0x41 }));
            Wsf(d, OpenRequest(upload: true, "FT:DATA"));
            Wsf(d, CloseRequest);
            int before = sent.Count;

            HostMessage(d, "TRANS03 File transfer complete");

            // Open ack and Data ack, and the transfer is over: TK5 sends READY next, never a Close.
            Assert.Empty(errors);
            Assert.Equal(before + 2, sent.Count);
            Assert.True(ok);
            Assert.Equal(FileTransferState.Idle, t.State);

            // A Close that some host might still send is only acknowledged.
            Wsf(d, CloseRequest);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x05, 0xD0, 0x41, 0x09 }, sent[sent.Count - 1]);
            Assert.Equal(FileTransferState.Idle, t.State);
        }

        [Fact]
        public void DownloadWritesTheDataAndAcknowledgesEveryRecord()
        {
            var (d, t, sent, errors) = Create();
            var file = new MemoryStream();
            bool? ok = null;
            string message = string.Empty;
            t.Finished += (success, text) =>
            {
                ok = success;
                message = text;
            };

            t.ExpectDownload(file);
            Wsf(d, OpenRequest(upload: false, "FT:DATA"));
            Wsf(d, Insert(new byte[] { 0x41, 0x42, 0x0D, 0x0A }));
            Wsf(d, Insert(new byte[] { 0x43 }));
            Wsf(d, CloseRequest);
            Assert.Equal(FileTransferState.WaitingForMessage, t.State);
            HostMessage(d, "TRANS03 File transfer complete");

            Assert.Empty(errors);
            Assert.Equal(new byte[] { 0x41, 0x42, 0x0D, 0x0A, 0x43 }, file.ToArray());
            Assert.Equal(new byte[] { 0x88, 0x00, 0x05, 0xD0, 0x00, 0x09 }, sent[0]);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x0B, 0xD0, 0x47, 0x05, 0x63, 0x06, 0x00, 0x00, 0x00, 0x01 }, sent[1]);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x0B, 0xD0, 0x47, 0x05, 0x63, 0x06, 0x00, 0x00, 0x00, 0x02 }, sent[2]);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x05, 0xD0, 0x41, 0x09 }, sent[3]);
            Assert.True(ok);
            Assert.Equal("TRANS03 File transfer complete", message);
            Assert.Equal(FileTransferState.Idle, t.State);
            Assert.Equal(5, t.BytesTransferred);
        }

        [Fact]
        public void UploadSendsTheFileInPiecesThenGetPastEndOfFile()
        {
            // Limit 40 (the 35-byte Open must fit too): 40 - 1 (AID) - 16 (header) leaves 23 data bytes
            // per record.
            var (d, t, sent, errors) = Create(limit: 40);
            byte[] content = new byte[30];
            for (int i = 0; i < content.Length; i++)
            {
                content[i] = (byte)('a' + i);
            }

            t.ExpectUpload(new MemoryStream(content));
            Wsf(d, OpenRequest(upload: true, "FT:DATA"));
            Wsf(d, SetCursorAndGet);
            Wsf(d, SetCursorAndGet);
            Wsf(d, SetCursorAndGet);

            Assert.Empty(errors);
            byte[] first = sent[1];
            Assert.Equal(40, first.Length);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x27, 0xD0, 0x46, 0x05, 0x63, 0x06, 0x00, 0x00, 0x00, 0x01, 0xC0, 0x80, 0x61, 0x00, 0x1C }, first.AsSpan(0, 17).ToArray());
            Assert.Equal(content.AsSpan(0, 23).ToArray(), first.AsSpan(17).ToArray());

            byte[] second = sent[2];
            Assert.Equal(0x02, second[11]);
            Assert.Equal(content.AsSpan(23).ToArray(), second.AsSpan(17).ToArray());

            Assert.Equal(new byte[] { 0x88, 0x00, 0x09, 0xD0, 0x46, 0x08, 0x69, 0x04, 0x22, 0x00 }, sent[3]);
            Assert.Equal(30, t.BytesTransferred);
        }

        // TK5 answers each full upload block with a Read Modified command (X'F6') and then wants
        // the same block again before its next Get; answering with an ordinary Read Modified lost
        // the block without any error (measured 24 November 2026, Display3270.SendStructuredFieldReply).
        [Fact]
        public void AReadModifiedAfterUploadDataSendsTheSameBlockAgain()
        {
            var (d, t, sent, errors) = Create(limit: 40);
            byte[] content = new byte[30];
            for (int i = 0; i < content.Length; i++)
            {
                content[i] = (byte)('a' + i);
            }

            t.ExpectUpload(new MemoryStream(content));
            Wsf(d, OpenRequest(upload: true, "FT:DATA"));
            Wsf(d, SetCursorAndGet);
            d.ProcessOutbound(new byte[] { Command3270.ReadModified });
            d.ProcessOutbound(new byte[] { Command3270.ReadModified });
            Wsf(d, SetCursorAndGet);
            Wsf(d, SetCursorAndGet);

            Assert.Empty(errors);
            Assert.Equal(6, sent.Count);
            Assert.Equal(sent[1], sent[2]);
            Assert.Equal(sent[1], sent[3]);
            Assert.Equal(0x02, sent[4][11]);
            Assert.Equal(content.AsSpan(23).ToArray(), sent[4].AsSpan(17).ToArray());
            Assert.Equal(new byte[] { 0x88, 0x00, 0x09, 0xD0, 0x46, 0x08, 0x69, 0x04, 0x22, 0x00 }, sent[5]);
            Assert.Equal(30, t.BytesTransferred);
        }

        // The host's next transmission that is not a read command acknowledges the block (p. 3-23):
        // a Read Modified after it is an ordinary one, and so is one after an Open ack, which is
        // not kept for a retry.
        [Fact]
        public void AReadModifiedAfterTheAcknowledgmentIsAnOrdinaryOne()
        {
            var (d, t, sent, errors) = Create(limit: 40);
            t.ExpectUpload(new MemoryStream(new byte[] { 0x41 }));
            Wsf(d, OpenRequest(upload: true, "FT:DATA"));
            d.ProcessOutbound(new byte[] { Command3270.ReadModified });
            Wsf(d, SetCursorAndGet);
            d.ProcessOutbound(new byte[] { Command3270.Write, 0x00 });
            d.ProcessOutbound(new byte[] { Command3270.ReadModified });

            Assert.Empty(errors);
            Assert.Equal(4, sent.Count);
            Assert.Equal(new byte[] { 0x88, 0x00, 0x05, 0xD0, 0x00, 0x09 }, sent[0]);
            Assert.NotEqual(sent[0], sent[1]);
            Assert.Equal(0x46, sent[2][4]);
            Assert.NotEqual(sent[2], sent[3]);
        }

        [Fact]
        public void AnOpenNobodyAskedForIsRefused()
        {
            var (d, t, sent, _) = Create();

            Wsf(d, OpenRequest(upload: false, "FT:DATA"));

            Assert.Equal(new byte[] { 0x88, 0x00, 0x09, 0xD0, 0x00, 0x08, 0x69, 0x04, 0x01, 0x00 }, sent[0]);
            Assert.Equal(FileTransferState.Idle, t.State);
        }

        [Fact]
        public void AnOpenInTheWrongDirectionIsRefused()
        {
            var (d, t, sent, _) = Create();
            t.ExpectDownload(new MemoryStream());

            Wsf(d, OpenRequest(upload: true, "FT:DATA"));

            Assert.Equal(new byte[] { 0x88, 0x00, 0x09, 0xD0, 0x00, 0x08, 0x69, 0x04, 0x01, 0x00 }, sent[0]);
            Assert.Equal(FileTransferState.Armed, t.State);
        }

        [Fact]
        public void AHostErrorMessageEndsTheTransferAsAFailure()
        {
            var (d, t, _, _) = Create();
            bool? ok = null;
            string message = string.Empty;
            t.Finished += (success, text) =>
            {
                ok = success;
                message = text;
            };

            t.ExpectDownload(new MemoryStream());
            Wsf(d, OpenRequest(upload: false, "FT:DATA"));

            // The host gives up without closing the data file (section 3.13).
            HostMessage(d, "TRANS17 Missing or incorrect TSO data set name: file transfer canceled");

            Assert.False(ok);
            Assert.StartsWith("TRANS17", message);
            Assert.Equal(FileTransferState.Idle, t.State);
        }

        [Fact]
        public void TruncatedRecordsStillCountAsSuccess()
        {
            var (d, t, _, _) = Create();
            bool? ok = null;
            t.Finished += (success, _) => ok = success;
            t.ExpectUpload(new MemoryStream(new byte[] { 1 }));
            Wsf(d, OpenRequest(upload: true, "FT:DATA"));
            Wsf(d, CloseRequest);

            HostMessage(d, "TRANS04 File transfer complete with records segmented");

            Assert.True(ok);
        }

        [Fact]
        public void DataBeforeAnyOpenIsAnArrivalSequenceError()
        {
            var (d, _, sent, _) = Create();

            Wsf(d, Insert(new byte[] { 1, 2 }));

            // The Insert request and the data are both answered with error X'0200'.
            Assert.Equal(new byte[] { 0x88, 0x00, 0x09, 0xD0, 0x47, 0x08, 0x69, 0x04, 0x02, 0x00 }, sent[0]);
            Assert.Equal(sent[0], sent[1]);
        }

        [Fact]
        public void ArmingTwiceIsRefused()
        {
            var (_, t, _, _) = Create();
            t.ExpectDownload(new MemoryStream());

            Assert.Throws<InvalidOperationException>(() => t.ExpectUpload(new MemoryStream()));
            t.Disarm();
            t.ExpectUpload(new MemoryStream());
            Assert.Equal(FileTransferState.Armed, t.State);
        }
    }
}
