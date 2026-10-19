//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Linq;
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
        var found = typeof(TnfsMessage).Assembly
            .GetTypes()
            .Where(t => t.Name == "TnfsHeader")
            .Select(t => t.FullName)
            .ToArray();

        Assert.That(found, Is.EqualTo(new[] { "RetroIO.Protocols.Tnfs.Protocol.TnfsHeader" }));
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
}
