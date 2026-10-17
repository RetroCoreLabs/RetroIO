//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class EconetProtocolDiagnosticsTests
{
    [Test]
    public void GetObjectInfo_Cmd6Cr_RequestTraceShowsImplicitParse()
    {
        byte[] raw = [0x50, FileServerOpcodes.GetObjectInfo, 0x01, 0x02, 0x04, 6, FileServerReply.CarriageReturn];
        Assert.That(FileServerRequest.TryParse(raw, out FileServerRequest request), Is.True);

        string trace = EconetProtocolDiagnostics.FormatRequestTrace(request, null);

        Assert.That(trace, Does.Contain("cmd=6"));
        Assert.That(trace, Does.Contain("expectReply=17"));
        Assert.That(trace, Does.Contain("06 0D"));
    }

    [Test]
    public void Examine_PathOnly_RequestTraceShowsPathOnlyMode()
    {
        byte[] raw = [0x50, FileServerOpcodes.Examine, 0x01, 0x02, 0x04, (byte)'.', FileServerReply.CarriageReturn];
        Assert.That(FileServerRequest.TryParse(raw, out FileServerRequest request), Is.True);

        string trace = EconetProtocolDiagnostics.FormatRequestTrace(request, null);

        Assert.That(trace, Does.Contain("path-only"));
        Assert.That(trace, Does.Contain("arg=0"));
    }

    [Test]
    public void GetObjectInfo_Cmd6Reply_DecodesDirNameAndEntryCount()
    {
        byte[] requestRaw = [0x50, FileServerOpcodes.GetObjectInfo, 0, 0, 0, 6, FileServerReply.CarriageReturn];
        Assert.That(FileServerRequest.TryParse(requestRaw, out FileServerRequest request), Is.True);
        byte[] reply = new byte[17];
        reply[2] = 0x02;
        reply[4] = 10;
        Encoding.ASCII.GetBytes("USER      ").CopyTo(reply, 5);
        reply[16] = 3;

        string result = EconetProtocolDiagnostics.FormatReplyTrace(request, reply, true);

        Assert.That(result, Does.Contain("cmd6 dir=\"USER\""));
        Assert.That(result, Does.Contain("entries=3"));
        Assert.That(result, Does.Contain("bytes=17"));
        Assert.That(result, Does.Contain("hexPairs=17"));
    }

    [Test]
    public void SoftNotFound_MarkedAsFailure()
    {
        byte[] requestRaw = [0x50, FileServerOpcodes.GetObjectInfo, 0, 0, 0, 1, (byte)'X', FileServerReply.CarriageReturn];
        Assert.That(FileServerRequest.TryParse(requestRaw, out FileServerRequest request), Is.True);
        byte[] reply = [0, 0, 0];

        Assert.That(EconetProtocolDiagnostics.EvaluateReplySuccess(request, reply), Is.False);
        Assert.That(EconetProtocolDiagnostics.FormatReplyTrace(request, reply, false), Does.Contain("soft-not-found"));
    }
}
