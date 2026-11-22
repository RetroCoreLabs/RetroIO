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

        private static void HostMessage(Display3270 d, string text)
        {
            Wsf(d, OpenRequest(upload: false, "FT:MSG "));
            Wsf(d, Insert(System.Text.Encoding.ASCII.GetBytes(text)));
            Wsf(d, CloseRequest);
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
