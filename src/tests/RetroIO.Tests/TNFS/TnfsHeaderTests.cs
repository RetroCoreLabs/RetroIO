//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// The TNFS header exists once, in the protocol layer, and reads and writes the wire bytes the way the TNFS
/// specification says: session id little-endian, then sequence number, then command, then (in a reply) status.
/// </summary>
[TestFixture]
[Category("TNFS")]
public class TnfsHeaderTests
{
    /// <summary>
    /// BUGS.md found two public TnfsHeader structs in one package (Protocol and Server). Two copies drift apart,
    /// and a caller that imports both namespaces cannot name either. One must remain: the protocol layer one,
    /// which does not depend on the server core.
    /// </summary>
    [Test]
    public void TnfsHeader_ExistsOnce_InTheProtocolLayer()
    {
        // The Server namespace types live in the same assembly as TnfsMessage.
        List<string> found = new List<string>();
        Type[] types = typeof(TnfsMessage).Assembly.GetTypes();
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i].Name == "TnfsHeader")
                found.Add(types[i].FullName!);
        }

        Assert.That(found.ToArray(), Is.EqualTo(new[] { "RetroIO.Protocols.Tnfs.Protocol.TnfsHeader" }));
    }

    /// <summary>
    /// The session id is two bytes, least significant first.
    /// </summary>
    [Test]
    public void Parse_ReadsSessionIdLittleEndian_ThenSequenceAndCommand()
    {
        byte[] wire = { 0x34, 0x12, 0x07, 0x21 };

        var header = TnfsHeader.Parse(wire);

        Assert.Multiple(() =>
        {
            Assert.That(header.SessionId, Is.EqualTo((ushort)0x1234));
            Assert.That(header.SequenceNumber, Is.EqualTo((byte)0x07));
            Assert.That(header.Command, Is.EqualTo((byte)0x21));
            Assert.That(header.Status, Is.EqualTo((byte)0), "a request carries no status byte");
        });
    }

    /// <summary>
    /// A buffer shorter than the 4 byte header is refused.
    /// </summary>
    [Test]
    public void Parse_ShortBuffer_Throws()
    {
        Assert.Throws<ArgumentException>(() => TnfsHeader.Parse(new byte[3]));
    }

    /// <summary>
    /// A reply header is 5 bytes: the request header plus the status byte.
    /// </summary>
    [Test]
    public void SerializeResponse_WritesFiveBytes_AndParsesBack()
    {
        var header = new TnfsHeader { SessionId = 0xBEEF, SequenceNumber = 9, Command = 0x10, Status = 0x02 };
        var buffer = new byte[5];

        int written = header.SerializeResponse(buffer);
        var back = TnfsHeader.Parse(buffer);

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(5));
            Assert.That(buffer, Is.EqualTo(new byte[] { 0xEF, 0xBE, 9, 0x10, 0x02 }));
            Assert.That(back.SessionId, Is.EqualTo((ushort)0xBEEF));
            Assert.That(back.SequenceNumber, Is.EqualTo((byte)9));
            Assert.That(back.Command, Is.EqualTo((byte)0x10));
        });
    }

    /// <summary>
    /// A buffer shorter than the 5 byte reply header is refused.
    /// </summary>
    [Test]
    public void SerializeResponse_ShortBuffer_Throws()
    {
        var header = new TnfsHeader { SessionId = 1 };

        Assert.Throws<ArgumentException>(() => header.SerializeResponse(new byte[4]));
    }

    /// <summary>
    /// Parse reads only the first four bytes and ignores the rest of the buffer, so a whole received message
    /// (header plus body) can be passed in.
    /// </summary>
    [Test]
    public void Parse_LongerBuffer_ReadsOnlyTheFirstFourBytes()
    {
        byte[] wire = { 0x01, 0x80, 0x05, 0x11, 0xAA, 0xBB, 0xCC };

        TnfsHeader header = TnfsHeader.Parse(wire);

        Assert.Multiple(() =>
        {
            Assert.That(header.SessionId, Is.EqualTo((ushort)0x8001), "high byte 0x80 must not turn the id negative or sign-extend");
            Assert.That(header.SequenceNumber, Is.EqualTo((byte)0x05));
            Assert.That(header.Command, Is.EqualTo((byte)0x11));
            Assert.That(header.Status, Is.EqualTo((byte)0), "the byte after the command in a request is body, not status");
        });
    }

    /// <summary>
    /// Parse accepts a span that is exactly four bytes, the smallest request.
    /// </summary>
    [Test]
    public void Parse_ExactlyFourBytes_Succeeds()
    {
        TnfsHeader header = TnfsHeader.Parse(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

        Assert.Multiple(() =>
        {
            Assert.That(header.SessionId, Is.EqualTo((ushort)0xFFFF));
            Assert.That(header.SequenceNumber, Is.EqualTo((byte)0xFF));
            Assert.That(header.Command, Is.EqualTo((byte)0xFF));
        });
    }

    /// <summary>
    /// SerializeResponse writes exactly the five header bytes and touches nothing after them, so the body
    /// that the caller places behind the header is not overwritten.
    /// </summary>
    [Test]
    public void SerializeResponse_LargerBuffer_LeavesTheBytesAfterTheHeaderAlone()
    {
        TnfsHeader header = new TnfsHeader { SessionId = 0x0102, SequenceNumber = 3, Command = 4, Status = 5 };
        byte[] buffer = { 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0x77, 0x88 };

        int written = header.SerializeResponse(buffer);

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(5));
            Assert.That(buffer, Is.EqualTo(new byte[] { 0x02, 0x01, 3, 4, 5, 0x77, 0x88 }));
        });
    }

    /// <summary>
    /// A header written and read back keeps every session id, including the ones with a high byte of 0x80 or
    /// more and a low byte of 0x80 or more.
    /// </summary>
    [Test]
    public void SerializeResponse_ThenParse_RoundTripsEverySessionIdByteCombination()
    {
        ushort[] ids = { 0x0000, 0x00FF, 0xFF00, 0x7FFF, 0x8000, 0x80FF, 0xFFFF, 0x1234 };
        byte[] buffer = new byte[5];

        for (int i = 0; i < ids.Length; i++)
        {
            TnfsHeader header = new TnfsHeader { SessionId = ids[i], SequenceNumber = 0x81, Command = 0xF0, Status = 0xFF };
            header.SerializeResponse(buffer);
            TnfsHeader back = TnfsHeader.Parse(buffer);

            Assert.That(back.SessionId, Is.EqualTo(ids[i]), "session id 0x" + ids[i].ToString("X4"));
        }
    }
}
