//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol fixture for <c>GetBytes</c> (fsop 0x0A), the bulk read half of *LOAD. Migration
/// guard for <c>MiscOpcodeHandler</c> -> <c>GetBytesOperation</c>. Covers auth + the error paths; the
/// full bulk data flow needs an open channel and is exercised by the higher-level *LOAD tests.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class GetBytesFsopTests : FsopProtocolTestBase
{
    // 8-byte request: handle, offsetStatus, 24-bit length, 24-bit offset.
    private static readonly byte[] Request = [5, 0, 0, 1, 0, 0, 0, 0];

    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.GetBytes, Request));
    }

    [Test]
    public void ShortParameters_ReturnsBadCommand()
    {
        LoginAdmin();

        AssertErrorCode(Harness.Send(FileServerOpcodes.GetBytes, [5, 0, 0]), FileServerErrors.BadCommand);
    }

    [Test]
    public void UnknownChannel_ReturnsBadChannel()
    {
        LoginAdmin();

        AssertErrorCode(Harness.Send(FileServerOpcodes.GetBytes, Request), FileServerErrors.BadChannel);
    }
}
