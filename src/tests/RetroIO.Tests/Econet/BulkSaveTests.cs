//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Validates SAVE / CREATE (fsop 0x01 / 0x1D) reply formats against the PiFS reference
/// (utilities/fs.c fs_save + bulk close): setup = [00 00 port 00 05], create/zero-length close =
/// 5-byte [00 00 access day monthyear], data-transfer close = 21-byte FS3 packet with trailing junk.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class BulkSaveReplyTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EconetFsTestHarness();

    [Test]
    public void SaveSetup_MatchesPiFsBulkPortAnd1280Window()
    {
        byte[] reply = FileServerReply.SaveSetup(0x93);
        Assert.That(reply, Is.EqualTo(new byte[] { 0, 0, 0x93, 0x00, 0x05 }));
    }

    [Test]
    public void SaveCreateComplete_IsFiveByteReturnCodeZero()
    {
        byte[] reply = FileServerReply.SaveCreateComplete(0x33, 0x0A, 0x5C);
        Assert.That(reply, Is.EqualTo(new byte[] { 0, 0, 0x33, 0x0A, 0x5C }));
    }

    [Test]
    public void SaveComplete_Is21ByteFs3PacketWithTrailingJunk()
    {
        byte[] reply = FileServerReply.SaveComplete(0x33, 0x0A, 0x5C, "TEST");

        Assert.That(reply, Has.Length.EqualTo(21));
        Assert.That(reply[0], Is.EqualTo(3), "FS3 return code");
        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(reply[2], Is.EqualTo(0x33));
        Assert.That(reply[3], Is.EqualTo(0x0A));
        Assert.That(reply[4], Is.EqualTo(0x5C));
        Assert.That(Encoding.ASCII.GetString(reply, 5, 4), Is.EqualTo("TEST"));
        Assert.That(reply[17], Is.EqualTo(0x80), "negative terminator byte");
        Assert.That(reply[18], Is.EqualTo(0x20));
        Assert.That(reply[19], Is.EqualTo(0xA9));
        Assert.That(reply[20], Is.EqualTo(0x24));
    }

    [Test]
    public void Save_WithData_ReturnsSetupWithBulkPortAnd1280Window()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] reply = SaveRequest("NEWFILE", load: 0, exec: 0, length: 32);

        Assert.That(reply, Has.Length.EqualTo(5));
        Assert.That(reply[0], Is.EqualTo(0));
        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(reply[2], Is.Not.EqualTo(0), "bulk port allocated");
        Assert.That(reply[3], Is.EqualTo(0x00));
        Assert.That(reply[4], Is.EqualTo(0x05));
    }

    [Test]
    public void Save_ZeroLength_ReturnsFiveByteCreateCompletion()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] reply = SaveRequest("EMPTY", load: 0, exec: 0, length: 0);

        Assert.That(reply, Has.Length.EqualTo(5), "PiFS create/zero-length close is 5 bytes");
        Assert.That(reply[0], Is.EqualTo(0), "return code 0 (not the FS3 code 3)");
        Assert.That(reply[1], Is.EqualTo(0));
    }

    [Test]
    public void Save_WithoutLogin_ReturnsWhoAreYou()
    {
        byte[] reply = SaveRequest("NEWFILE", 0, 0, 16);
        Assert.That(reply[1], Is.EqualTo(FileServerErrors.WhoAreYou));
    }

    // Build a SAVE request (fsop 0x01): [replyPort, opcode, ackPort, 0, 0, load(4), exec(4), length(3), name\r].
    private byte[] SaveRequest(string filename, uint load, uint exec, int length)
    {
        byte[] name = Encoding.ASCII.GetBytes(filename + "\r");
        byte[] payload = new byte[FileServerBulkPacketLayout.SaveFilenameOffset + name.Length];
        payload[0] = 0x80;
        payload[1] = FileServerOpcodes.Save;
        payload[2] = 0x40; // ack port
        payload[5] = (byte)load;
        payload[6] = (byte)(load >> 8);
        payload[7] = (byte)(load >> 16);
        payload[8] = (byte)(load >> 24);
        payload[9] = (byte)exec;
        payload[10] = (byte)(exec >> 8);
        payload[11] = (byte)(exec >> 16);
        payload[12] = (byte)(exec >> 24);
        payload[13] = (byte)length;
        payload[14] = (byte)(length >> 8);
        payload[15] = (byte)(length >> 16);
        name.CopyTo(payload, FileServerBulkPacketLayout.SaveFilenameOffset);

        EconetMessage request = EconetFsTestHarness.CreateMessage(payload);
        return _harness.Handler.Handle(request)!.Payload;
    }
}

/// <summary>
/// End-to-end SAVE over UDP AUN: save a file through the full bulk-inbound path, then load it back
/// and confirm the bytes round-trip (exercises the 21-byte FS3 close packet).
/// </summary>
[TestFixture]
[Category("Unit")]
[NonParallelizable]
public sealed class BulkSaveUdpTests
{
    private EconetUdpTestServer _server = null!;
    private EconetAunTestClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => _server = await EconetUdpTestServer.StartAsync();

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await _server.DisposeAsync();

    [SetUp]
    public void SetUp() => _client = new EconetAunTestClient(serverPort: _server.UdpPort);

    [TearDown]
    public void TearDown() => _client.Dispose();

    [Test]
    public void SaveThenLoad_RoundTripsFileContents()
    {
        _client.Login("SYST");

        byte[] payload = Encoding.ASCII.GetBytes("RetroFS SAVE round-trip payload\r");
        byte[] completion = _client.SaveFile("SAVED", payload, load: 0x1900, exec: 0x8023);

        Assert.That(completion, Has.Length.EqualTo(21), "FS3 close packet");
        Assert.That(completion[0], Is.EqualTo(3));

        byte[] loaded = _client.LoadFile("SAVED");
        Assert.That(loaded, Is.EqualTo(payload));
    }
}
