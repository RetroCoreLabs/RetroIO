//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol fixture for <c>PutBytes</c> (fsop 0x0B), the bulk write half of *SAVE. Migration
/// guard for <c>MiscOpcodeHandler</c> -> <c>PutBytesOperation</c>. Covers auth + the error paths; the
/// full bulk data flow needs an open, writable channel and is exercised by the higher-level *SAVE tests.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class PutBytesFsopTests : FsopProtocolTestBase
{
    // 8-byte request: handle, offsetStatus, 24-bit length, 24-bit offset.
    private static readonly byte[] Request = [5, 0, 0, 1, 0, 0, 0, 0];

    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.PutBytes, Request));
    }

    [Test]
    public void ShortParameters_ReturnsBadCommand()
    {
        LoginAdmin();

        AssertErrorCode(Harness.Send(FileServerOpcodes.PutBytes, [5, 0, 0]), FileServerErrors.BadCommand);
    }

    [Test]
    public void UnknownChannel_ReturnsBadChannel()
    {
        LoginAdmin();

        AssertErrorCode(Harness.Send(FileServerOpcodes.PutBytes, Request), FileServerErrors.BadChannel);
    }
}
