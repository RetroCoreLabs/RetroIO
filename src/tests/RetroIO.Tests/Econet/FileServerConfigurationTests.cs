//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// The file server configuration flags and their wire form, checked against the PiFS reference
/// (PiEconetBridge utilities/fs.c: fs_get_parameters line 555, fs_set_parameters line 574, the
/// PiBridge calls 0x11 and 0x12 at lines 4240 to 4300, the new-configuration defaults at lines 3460 to 3467,
/// and include/econet-fs-hpbridge-common.h lines 10 to 17).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class FileServerConfigurationTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EconetFsTestHarness();

    [Test]
    public void DefaultFlags_AreThePiFsNewConfiguration_SjFunctionsAndAcornDirectoryDisplay()
    {
        // fs.c 3460 to 3464: a new configuration sets fs_sjfunc (0x02) and fs_mask_dir_wrr (0x80); the rest are 0.
        FileServerConfiguration configuration = new FileServerConfiguration();

        Assert.That(configuration.ConfigBits, Is.EqualTo(0x82));
        Assert.That(configuration.ManyHandles, Is.False);
    }

    [Test]
    public void DefaultPermissions_ArePiFsDirectoryOwnWriteReadOtherRead_FileOwnWriteRead()
    {
        // fs.c 3466 and 3467: directory = FS_PERM_OWN_W | FS_PERM_OWN_R | FS_PERM_OTH_R (0x02 | 0x01 | 0x10),
        // file = FS_PERM_OWN_W | FS_PERM_OWN_R (0x02 | 0x01).
        FileServerConfiguration configuration = new FileServerConfiguration();

        Assert.That(configuration.DefaultDirPerm, Is.EqualTo(0x13));
        Assert.That(configuration.DefaultFilePerm, Is.EqualTo(0x03));
    }

    [Test]
    public void PiBridgeGetParameters_ReplyIsFourParameterBytesThenTheFilenameLength()
    {
        // fs.c 4254 to 4266: data[0..3] = the 32-bit params value, low byte first, data[4] = filename length.
        // fs_get_parameters (555 to 571) puts the flags in bits 0 to 7, the default file permission in bits 16 to 23
        // and the default directory permission in bits 24 to 31.
        _harness.LoginAsSyst();
        _harness.Configuration.ConfigBits = 0x82;
        _harness.Configuration.DefaultFilePerm = 0x03;
        _harness.Configuration.DefaultDirPerm = 0x13;
        _harness.Configuration.FilenameLength = 10;

        byte[] reply = _harness.Send(FileServerOpcodes.PiBridge, [17]).Payload;

        Assert.That(reply, Is.EqualTo(new byte[] { 0, 0, 0x82, 0x00, 0x03, 0x13, 10 }));
    }

    [Test]
    public void PiBridgeSetParameters_ReadsFourParameterBytesThenTheFilenameLength()
    {
        // fs.c 4272 to 4291: params = data[6] + data[7] << 8 + data[8] << 16 + data[9] << 24, filename length = data[10].
        _harness.LoginAsSyst();

        byte[] reply = _harness.Send(FileServerOpcodes.PiBridge, [18, 0x56, 0x00, 0x23, 0x31, 20]).Payload;

        Assert.That(reply[1], Is.EqualTo(0), "return code OK");
        Assert.That(_harness.Configuration.ConfigBits, Is.EqualTo(0x56));
        Assert.That(_harness.Configuration.DefaultFilePerm, Is.EqualTo(0x23));
        Assert.That(_harness.Configuration.DefaultDirPerm, Is.EqualTo(0x31));
        Assert.That(_harness.Configuration.FilenameLength, Is.EqualTo(20));
    }

    [TestCase((byte)9)]
    [TestCase((byte)80)]
    public void PiBridgeSetParameters_RefusesAFilenameLengthOutsideTenToSeventyNine(byte filenameLength)
    {
        // fs.c 4281 to 4285: fnlength < 10 or > 79 answers error 0xFF "Bad filename length" and changes nothing.
        _harness.LoginAsSyst();

        byte[] reply = _harness.Send(FileServerOpcodes.PiBridge, [18, 0x01, 0x00, 0x23, 0x31, filenameLength]).Payload;

        Assert.That(reply[1], Is.EqualTo(0xFF));
        Assert.That(Encoding.ASCII.GetString(reply, 2, reply.Length - 3), Is.EqualTo("Bad filename length"));
        Assert.That(_harness.Configuration.ConfigBits, Is.EqualTo(0x82), "nothing changed");
        Assert.That(_harness.Configuration.FilenameLength, Is.EqualTo(10), "nothing changed");
    }

    [Test]
    public void PiBridgeGetParameters_NeedsAnAdministrator()
    {
        _harness.LoginAsUser();

        byte[] reply = _harness.Send(FileServerOpcodes.PiBridge, [17]).Payload;

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.InsufficientAccess));
    }

    [Test]
    public void SaveSetup_WithBigChunksClear_AdvertisesTheSmallWindow0x500()
    {
        // fs.c 9023 and 9024: the maximum transfer size is 0x500 with fs_bigchunks clear.
        _harness.LoginAsSyst();
        _harness.Configuration.ConfigBits = 0x82;

        byte[] reply = SaveRequest("NEWFILE", 32);

        Assert.That(reply, Has.Length.EqualTo(5));
        Assert.That(reply[3], Is.EqualTo(0x00));
        Assert.That(reply[4], Is.EqualTo(0x05));
    }

    [Test]
    public void SaveSetup_WithBigChunksSet_AdvertisesTheFourKilobyteWindow0x1000()
    {
        // fs.c 184, 9023 and 9024: FS_MAX_BULK_SIZE is 0x1000 and is sent, low byte first, with fs_bigchunks set.
        _harness.LoginAsSyst();
        _harness.Configuration.ConfigBits = 0x82 | FileServerConfiguration.FlagBigChunks;

        byte[] reply = SaveRequest("NEWFILE", 32);

        Assert.That(reply, Has.Length.EqualTo(5));
        Assert.That(reply[3], Is.EqualTo(0x00));
        Assert.That(reply[4], Is.EqualTo(0x10));
    }

    // Builds a SAVE request (fsop 0x01): [replyPort, opcode, ackPort, 0, 0, load(4), exec(4), length(3), name\r].
    private byte[] SaveRequest(string filename, int length)
    {
        byte[] name = Encoding.ASCII.GetBytes(filename + "\r");
        byte[] payload = new byte[FileServerBulkPacketLayout.SaveFilenameOffset + name.Length];
        payload[0] = 0x80;
        payload[1] = FileServerOpcodes.Save;
        payload[2] = 0x40;
        payload[13] = (byte)length;
        payload[14] = (byte)(length >> 8);
        payload[15] = (byte)(length >> 16);
        name.CopyTo(payload, FileServerBulkPacketLayout.SaveFilenameOffset);

        return _harness.Handler.Handle(EconetFsTestHarness.CreateMessage(payload))!.Payload;
    }
}
