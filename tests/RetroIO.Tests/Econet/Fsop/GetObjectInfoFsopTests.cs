//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol fixture for <c>GetObjectInfo</c> (fsop 0x12) — the last opcode of the strangler
/// refactor. Migration guard for <c>MiscOpcodeHandler</c> -> <c>GetObjectInfoOperation</c>.
/// </summary>
/// <remarks>
/// GetObjectInfo's byte-level behaviour (access/date fields, the canonical path form, the command-6
/// catalogue-directory block, and the ctrl=0 AUN framing) is covered in depth by the OSCLI
/// <c>*CAT</c>/<c>*INFO</c>/<c>*EX</c> fixtures that drive it. Here we only guard the routing and the
/// login gate that the migration is responsible for.
/// </remarks>
[TestFixture]
[Category("Unit")]
internal sealed class GetObjectInfoFsopTests : FsopProtocolTestBase
{
    // command byte 1 (info: access + dates) on the disc root "$".
    private static byte[] InfoRequest() => Combine([1], Encoding.ASCII.GetBytes("$\r"));

    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.GetObjectInfo, InfoRequest()));
    }

    [Test]
    public void LoggedIn_IsServedByTheOperation()
    {
        LoginAdmin();

        // Whatever the object-info verdict, a logged-in caller must NOT get the not-logged-in reply:
        // that proves the request routed into GetObjectInfoOperation past the dispatcher's login gate.
        AssertNotWhoAreYou(Harness.Send(FileServerOpcodes.GetObjectInfo, InfoRequest()));
    }

    private static byte[] Combine(byte[] a, byte[] b)
    {
        byte[] result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}
