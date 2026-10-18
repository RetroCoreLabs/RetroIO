//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Generic;
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

    [Test]
    public void DescribeCmd6Fields_NamesTheReplyBytesForWhatTheyAre()
    {
        // Regression: bytes 0 and 1 were reported as loadLo and loadHi. In a real command-6 reply they are
        // the return code and the result byte, always 0 on success; the load address is not in this form.
        byte[] name = Encoding.ASCII.GetBytes("USER      ");
        byte[] reply = FileServerReply.ObjectInfoForCommand(
            command: 6, fileType: 2, load: 0xFFFF1900, exec: 0, length: 0, access: 0,
            day: 0, monthYear: 0, isOwner: true, directoryName10: name, directoryEntryCount: 7);

        IReadOnlyDictionary<string, object?> fields = EconetProtocolDiagnostics.DescribeCmd6Fields(reply);

        Assert.That(fields["returnCode"], Is.EqualTo((byte)0));
        Assert.That(fields["resultCode"], Is.EqualTo((byte)0));
        Assert.That(fields["fileType"], Is.EqualTo((byte)2));
        Assert.That(fields["nameLength"], Is.EqualTo((byte)10));
        Assert.That(fields["name"], Is.EqualTo("USER"));
        Assert.That(fields["ownerFlag"], Is.EqualTo((byte)0x00));
        Assert.That(fields["entryCount"], Is.EqualTo((byte)7));
        Assert.That(fields.ContainsKey("loadLo"), Is.False, "the old, wrong label must be gone");
        Assert.That(fields.ContainsKey("loadHi"), Is.False);
    }
}
