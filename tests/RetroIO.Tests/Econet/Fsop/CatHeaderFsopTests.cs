//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// Per-opcode protocol fixture for <c>CatHeader</c> (fsop 0x04). Migration guard for
/// <c>MiscOpcodeHandler</c> -> <c>CatHeaderOperation</c>. Covers auth + the not-found error path.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class CatHeaderFsopTests : FsopProtocolTestBase
{
    [Test]
    public void WithoutLogin_ReturnsWhoAreYou()
    {
        AssertWhoAreYou(Harness.Send(FileServerOpcodes.CatHeader, Encoding.ASCII.GetBytes("$\r")));
    }

    [Test]
    public void UnknownDirectory_ReturnsNotFound()
    {
        LoginAdmin();

        EconetMessage reply = Harness.Send(FileServerOpcodes.CatHeader, Encoding.ASCII.GetBytes("NOSUCHDIR\r"));

        AssertErrorCode(reply, FileServerErrors.NotFound);
    }
}
