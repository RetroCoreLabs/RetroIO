//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Tests how the Econet file server uses what a backend says about a file. A backend now answers
/// with a length and times, and a time may be unknown (null). These tests drive the server
/// through a backend whose answer the test chooses, and read the bytes that go on the wire.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class BackendFileInfoConsumerTests
{
    private const string ProgUnix = EconetSampleCatalogFixture.UserDirectory + "/PROG";

    private static readonly DateTime KnownWriteTime = new DateTime(2024, 5, 17, 12, 0, 0, DateTimeKind.Utc);

    private RecordingFileSystemBackend _backend = null!;
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _backend = new RecordingFileSystemBackend();
        _harness = new EconetFsTestHarness(_backend);
        _harness.LoginAsSyst();
    }

    // Makes the backend answer every file question with this length and write time.
    private void BackendSays(long length, DateTime? lastWriteUtc)
    {
        _backend.GetFileInfoHandler = _ => new BackendFileInfo(length, null, lastWriteUtc, null);
    }

    // Sends GetObjectInfo (fsop 0x12) command 5 for PROG. Reply: 2 status bytes, type, load(4), exec(4),
    // length(3), access, day, month and year, owner.
    private byte[] ObjectInfoAll()
    {
        byte[] name = Encoding.ASCII.GetBytes("PROG\r");
        byte[] request = new byte[1 + name.Length];
        request[0] = 5;
        name.CopyTo(request, 1);
        return _harness.Send(FileServerOpcodes.GetObjectInfo, request).Payload;
    }

    private static int LengthOf(byte[] objectInfo) => objectInfo[11] | (objectInfo[12] << 8) | (objectInfo[13] << 16);

    /// <summary>
    /// An empty file has no stored length, so the length and the date both come from the backend answer.
    /// </summary>
    [Test]
    public void ObjectInfo_FileOfLengthZero_TakesTheDateFromTheBackend()
    {
        _harness.FileSystem.AddFile(ProgUnix, Array.Empty<byte>());
        BackendSays(0, KnownWriteTime);

        byte[] info = ObjectInfoAll();

        AcornPath.EncodeFileDate(KnownWriteTime, out byte day, out byte monthYear);
        Assert.That(LengthOf(info), Is.EqualTo(0));
        Assert.That(info[15], Is.EqualTo(day), "day byte");
        Assert.That(info[16], Is.EqualTo(monthYear), "month and year byte");
        Assert.That(day, Is.Not.EqualTo(0), "the chosen date must give a date that is not zero");
    }

    /// <summary>
    /// A file that has a length takes the length and the date from the backend answer too.
    /// </summary>
    [Test]
    public void ObjectInfo_FileWithLength_TakesLengthAndDateFromTheBackend()
    {
        _harness.FileSystem.AddFile(ProgUnix, Encoding.ASCII.GetBytes("abc"));
        BackendSays(300, KnownWriteTime);

        byte[] info = ObjectInfoAll();

        AcornPath.EncodeFileDate(KnownWriteTime, out byte day, out byte monthYear);
        Assert.That(LengthOf(info), Is.EqualTo(300));
        Assert.That(info[15], Is.EqualTo(day), "day byte");
        Assert.That(info[16], Is.EqualTo(monthYear), "month and year byte");
    }

    /// <summary>
    /// A backend that does not know the write time gives a zero date, and the reply still has its length.
    /// The case with length 0 and the case with a length take different code paths.
    /// </summary>
    /// <param name="length">
    /// The length the backend reports.
    /// </param>
    [TestCase(0L)]
    [TestCase(300L)]
    public void ObjectInfo_BackendKnowsNoWriteTime_DateIsZero(long length)
    {
        _harness.FileSystem.AddFile(ProgUnix, Encoding.ASCII.GetBytes("abc"));
        BackendSays(length, null);

        byte[] info = ObjectInfoAll();

        Assert.That(LengthOf(info), Is.EqualTo(length));
        Assert.That(info[15], Is.EqualTo(0), "day byte");
        Assert.That(info[16], Is.EqualTo(0), "month and year byte");
    }

    /// <summary>
    /// LOAD reports the date the backend gives. Reply: 2 status bytes, load(4), exec(4), length(3),
    /// access, day, month and year.
    /// </summary>
    [Test]
    public void Load_TakesTheDateFromTheBackend()
    {
        _harness.FileSystem.AddFile(ProgUnix, Encoding.ASCII.GetBytes("machine code image"));
        BackendSays(18, KnownWriteTime);

        byte[] metadata = LoadRequest("PROG");

        AcornPath.EncodeFileDate(KnownWriteTime, out byte day, out byte monthYear);
        Assert.That(metadata, Has.Length.EqualTo(16));
        Assert.That(metadata[14], Is.EqualTo(day), "day byte");
        Assert.That(metadata[15], Is.EqualTo(monthYear), "month and year byte");
    }

    /// <summary>
    /// LOAD of a file whose write time the backend does not know reports a zero date, and still sends the file.
    /// </summary>
    [Test]
    public void Load_BackendKnowsNoWriteTime_DateIsZero()
    {
        byte[] content = Encoding.ASCII.GetBytes("machine code image");
        _harness.FileSystem.AddFile(ProgUnix, content);
        BackendSays(content.Length, null);

        byte[] metadata = LoadRequest("PROG");

        Assert.That(metadata, Has.Length.EqualTo(16));
        Assert.That(metadata[10] | (metadata[11] << 8), Is.EqualTo(content.Length), "length");
        Assert.That(metadata[14], Is.EqualTo(0), "day byte");
        Assert.That(metadata[15], Is.EqualTo(0), "month and year byte");
    }

    /// <summary>
    /// The star command SAVE with no length given fills the length from the file that already exists.
    /// The length comes from the backend answer.
    /// </summary>
    [Test]
    public void StarSave_NoLengthGiven_UsesTheLengthOfTheExistingFile()
    {
        byte[] content = Encoding.ASCII.GetBytes("machine code image");
        _harness.FileSystem.AddFile(ProgUnix, content);
        AcornAttributeStore.WriteAttributes(_harness.FileSystem, ProgUnix, 0x1900, 0x1900, content.Length, 0x33);
        BackendSays(777, KnownWriteTime);

        byte[] reply = _harness.HandleOsCli("SAVE PROG 1900 1900\r").Payload;

        Assert.That(reply[0], Is.EqualTo(FileServerOpcodes.Save));
        Assert.That(reply[10] | (reply[11] << 8) | (reply[12] << 16), Is.EqualTo(777));
    }

    // Builds a LOAD request (fsop 0x02): [replyPort, opcode, dataPort, relativeTo, 0, name\r].
    private byte[] LoadRequest(string filename)
    {
        byte[] name = Encoding.ASCII.GetBytes(filename + "\r");
        byte[] payload = new byte[FileServerBulkPacketLayout.LoadFilenameOffset + name.Length];
        payload[0] = 0x80;
        payload[1] = FileServerOpcodes.Load;
        payload[2] = 0x40;
        payload[3] = AcornFileHandles.WireCwd;
        payload[4] = 0;
        name.CopyTo(payload, FileServerBulkPacketLayout.LoadFilenameOffset);

        return _harness.Handler.Handle(EconetFsTestHarness.CreateMessage(payload))!.Payload;
    }
}
