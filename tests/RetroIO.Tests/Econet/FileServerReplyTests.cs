//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class FileServerReplyTests
{
    [Test]
    public void Version_IncludesStatusBytesAndCrTerminatedString()
    {
        byte[] reply = FileServerReply.Version("3 RetroFS Econet FS 1.0");

        Assert.That(reply[0], Is.EqualTo(0));
        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(reply[^1], Is.EqualTo(FileServerReply.CarriageReturn));
        Assert.That(Encoding.ASCII.GetString(reply, 2, reply.Length - 3), Is.EqualTo("3 RetroFS Econet FS 1.0"));
    }

    [Test]
    public void Error_PutsCodeInSecondStatusByte()
    {
        byte[] reply = FileServerReply.Error(FileServerErrors.WhoAreYou, "Who are you?");

        Assert.That(reply[0], Is.EqualTo(0));
        Assert.That(reply[1], Is.EqualTo(FileServerErrors.WhoAreYou));
        Assert.That(reply[^1], Is.EqualTo(FileServerReply.CarriageReturn));
    }

    [Test]
    public void SaveSetup_UsesBulkPortAndChunkSize1280()
    {
        byte[] reply = FileServerReply.SaveSetup(42);

        Assert.That(reply, Is.EqualTo(new byte[] { 0, 0, 42, 0x00, 0x05 }));
    }

    [Test]
    public void SaveComplete_MatchesPiFsClosePacketShape()
    {
        byte[] reply = FileServerReply.SaveComplete(0x33, 15, 0x46, "MYTEST");

        Assert.That(reply[0], Is.EqualTo(3));
        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(reply[2], Is.EqualTo(0x33));
        Assert.That(reply[3], Is.EqualTo(15));
        Assert.That(reply[4], Is.EqualTo(0x46));
        Assert.That(reply[17], Is.EqualTo(0x80));
        Assert.That(reply[18], Is.EqualTo(0x20));
        Assert.That(reply[19], Is.EqualTo(0xA9));
        Assert.That(reply[20], Is.EqualTo(0x24));
    }

    [Test]
    public void LoadMetadata_WritesLengthAtOffset10()
    {
        byte[] reply = FileServerReply.LoadMetadata(0x8000, 0x8000, 1234, 0x33, 10, 0x46);

        int length = reply[10] | (reply[11] << 8) | (reply[12] << 16);
        Assert.That(length, Is.EqualTo(1234));
    }

    [Test]
    public void DiscNames_WritesSeventeenByteEntries()
    {
        DiscNameEntry[] discs = [new DiscNameEntry(0, "RetroFS")];
        byte[] reply = FileServerReply.DiscNames(discs);

        Assert.That(reply[0], Is.EqualTo(10));
        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(reply[2], Is.EqualTo(1));
        Assert.That(reply[3], Is.EqualTo((byte)0));
        Assert.That(Encoding.ASCII.GetString(reply, 4, 7), Is.EqualTo("RetroFS"));
    }

    [Test]
    public void ObjectInfoForCommand_Command1_ReturnsFiveByteReply()
    {
        byte[] reply = FileServerReply.ObjectInfoForCommand(
            1, 0x01, 0, 0, 0, 0, 12, 0x46, true, ReadOnlySpan<byte>.Empty, 0);

        Assert.That(reply, Has.Length.EqualTo(5));
        Assert.That(reply[0], Is.EqualTo(0));
        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(reply[2], Is.EqualTo(0x01));
        Assert.That(reply[3], Is.EqualTo(12));
        Assert.That(reply[4], Is.EqualTo(0x46));
    }

    [Test]
    public void ObjectInfoForCommand_Command6_ReturnsDirectoryHeader()
    {
        byte[] dirName = Encoding.ASCII.GetBytes("$         ");
        byte[] reply = FileServerReply.ObjectInfoForCommand(
            6, 0x02, 0, 0, 0, 0, 0, 0, true, dirName, 3);

        Assert.That(reply, Has.Length.EqualTo(17));
        Assert.That(reply[2], Is.EqualTo(0x02));
        Assert.That(reply[3], Is.EqualTo(0));
        Assert.That(reply[4], Is.EqualTo(10));
        Assert.That(Encoding.ASCII.GetString(reply, 5, 10), Is.EqualTo("$         "));
        Assert.That(reply[15], Is.EqualTo(0));
        Assert.That(reply[16], Is.EqualTo(3));
    }

    [Test]
    public void ObjectInfoForCommand_Command5_ReturnsFullBlock()
    {
        byte[] reply = FileServerReply.ObjectInfoForCommand(
            5, 0xFF, 0x8000, 0x8020, 100, 0x33, 5, 0x46, true, ReadOnlySpan<byte>.Empty, 0);

        Assert.That(reply, Has.Length.EqualTo(18));
    }
}
