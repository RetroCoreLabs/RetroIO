//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Task 0.4 — the two frame-shape wire-format divergences gated by <see cref="WireProfile"/>:
/// read-logged-on-users (0x0F, variable vs fixed 13-byte) and disc free-space (0x1A, 8-byte vs
/// 3-byte-free-only). The LaterFS default is the verified shape; Classic1983 is the 1983 AUG shape.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WireProfileFrameShapeTests
{
    private static EconetFsTestHarness Later()
    {
        EconetFsTestHarness h = new EconetFsTestHarness();
        h.LoginAsSyst();
        return h;
    }

    private static EconetFsTestHarness Classic()
    {
        EconetFsTestHarness h = new EconetFsTestHarness(new WireProfileResolver(WireProfile.Classic1983, null));
        h.LoginAsSyst();
        return h;
    }

    // ---- Read logged-on users (0x0F) -----------------------------------------------------------

    [Test]
    public void LoggedOnUsers_LaterFs_IsVariableFrame()
    {
        // One session logged on (SYST @ net 1 / station 1). Variable entry =
        // [station, net, "SYST", 0x0D, priv] = 8 bytes -> reply length 3 + 8.
        byte[] p = Later().Send(FileServerOpcodes.ReadLoggedOnUsers).Payload;

        Assert.That(p[0], Is.EqualTo(0));
        Assert.That(p[1], Is.EqualTo(0));
        Assert.That(p[2], Is.EqualTo(1), "one logged-on user");
        Assert.That(p.Length, Is.EqualTo(3 + 8), "variable frame: 4 + strlen(\"SYST\")");
        Assert.That(p[3], Is.EqualTo(1), "station");
        Assert.That(p[4], Is.EqualTo(1), "net");
        Assert.That(p[9], Is.EqualTo(0x0D), "variable frame ends the name with a CR");
    }

    [Test]
    public void LoggedOnUsers_Classic1983_IsFixed13ByteFrame()
    {
        byte[] p = Classic().Send(FileServerOpcodes.ReadLoggedOnUsers).Payload;

        Assert.That(p[2], Is.EqualTo(1), "one logged-on user");
        Assert.That(p.Length, Is.EqualTo(3 + 13), "fixed 13-byte frame per user");
        Assert.That(p[3], Is.EqualTo(1), "station");
        Assert.That(p[4], Is.EqualTo(1), "net");

        // Username field: 10 bytes, "SYST" then spaces.
        Assert.That(p[5], Is.EqualTo((byte)'S'));
        Assert.That(p[6], Is.EqualTo((byte)'Y'));
        Assert.That(p[7], Is.EqualTo((byte)'S'));
        Assert.That(p[8], Is.EqualTo((byte)'T'));
        for (int i = 9; i <= 14; i++)
            Assert.That(p[i], Is.EqualTo((byte)' '), "username field is space-padded to 10");

        Assert.That(p[15], Is.EqualTo(1), "SYST is System-privileged (1)");
        // The fixed frame carries NO CR terminator inside the name field.
        Assert.That(System.Array.IndexOf(p, (byte)0x0D), Is.EqualTo(-1), "no CR in the fixed frame");
    }

    // ---- Disc free space (0x1A) ----------------------------------------------------------------

    [Test]
    public void FreeSpace_LaterFs_IsEightBytes()
    {
        byte[] p = Later().Send(FileServerOpcodes.FreeSpace).Payload;

        Assert.That(p[0], Is.EqualTo(0));
        Assert.That(p[1], Is.EqualTo(0));
        Assert.That(p.Length, Is.EqualTo(8), "later FS: free(3) + total-extent(3)");
    }

    [Test]
    public void FreeSpace_Classic1983_IsFreeOnlyThreeBytes()
    {
        byte[] later = Later().Send(FileServerOpcodes.FreeSpace).Payload;
        byte[] classic = Classic().Send(FileServerOpcodes.FreeSpace).Payload;

        Assert.That(classic.Length, Is.EqualTo(5), "1983: 3-byte free-block count only, no extent");
        // The free count itself (bytes 2..4) is identical in both profiles — only the extent is dropped.
        Assert.That(classic[2], Is.EqualTo(later[2]));
        Assert.That(classic[3], Is.EqualTo(later[3]));
        Assert.That(classic[4], Is.EqualTo(later[4]));
    }
}
