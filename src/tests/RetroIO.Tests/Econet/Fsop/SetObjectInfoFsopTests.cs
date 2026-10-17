//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol fixture for <c>SetObjectInfo</c> (fsop 0x13). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>SetObjectInfoOperation</c>. Covers auth and the two error paths
/// (short parameters, unknown object).
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class SetObjectInfoFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        // command byte + "X\r".
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.SetObjectInfo, [1, (byte)'X', 0x0D]));
    }

    [Test]
    public void ShortParameters_ReturnsBadPath()
    {
        LoginAdmin();

        // Fewer than 2 bytes (command + at least one path byte) is malformed.
        EconetMessage reply = Harness.Send(FileServerOpcodes.SetObjectInfo, [1]);

        AssertErrorCode(reply, FileServerErrors.BadPath);
    }

    [Test]
    public void UnknownObject_ReturnsNotFound()
    {
        LoginAdmin();

        // command 2 (set load/exec/access) on a file that does not exist: "NOPE\r".
        EconetMessage reply = Harness.Send(FileServerOpcodes.SetObjectInfo, [2, (byte)'N', (byte)'O', (byte)'P', (byte)'E', 0x0D]);

        AssertErrorCode(reply, FileServerErrors.NotFound);
    }
}
