//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Golden byte-fixture regression tests locking down the exact wire replies for the DNFS <c>*I AM</c> +
/// <c>*CAT</c> flow (login handshake, read-user-environment, catalogue header, examine arg-3 text). These
/// are the formats that previously broke against BeebEm; the fixtures ensure they can't silently regress.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class GoldenWireFormatTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EconetFsTestHarness();

    [Test]
    public void Login_Syst_GoldenSixByteReply()
    {
        byte[] payload = _harness.HandleOsCli("I AM SYST \r").Payload;

        Assert.That(payload, Is.EqualTo(new byte[]
        {
            FileServerOpcodes.LoginSuccessReturnCode, // 0x05
            0x00,                                     // no error
            AcornFileHandles.WireUrd,                 // 0x01
            AcornFileHandles.WireCwd,                 // 0x02
            AcornFileHandles.WireLib,                 // 0x04
            0x00,                                     // boot option (off)
        }));
    }

    [Test]
    public void ReadUserEnv_Syst_GoldenThirtyNineByteReply()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] env = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserEnv, []).Payload;

        Assert.That(env, Has.Length.EqualTo(39));
        Assert.That(env[0], Is.EqualTo(0));
        Assert.That(env[1], Is.EqualTo(0));
        Assert.That(env[2], Is.EqualTo(16), "disc name field length");
        Assert.That(Encoding.ASCII.GetString(env, 3, 16), Is.EqualTo("RetroFS         "), "16-char disc name");
        // GOLDEN BYTES — re-baselined 2026-07-17 to PiFS naming parity (fs.c:10388): the CSD/library
        // 10-byte leaf is "$" ONLY at the disc root (npath == 0). The logged-in home is $.USER, so
        // both leaves are "USER". The previous baseline reported home as "$", which had the side
        // effect of the disc root falling through to its HOST folder name ("0RetroFS") on the wire —
        // caught live in BeebEm. The 39-byte structure and field offsets are unchanged.
        Assert.That(Encoding.ASCII.GetString(env, 19, 10), Is.EqualTo("USER      "), "CSD leaf (home = $.USER)");
        Assert.That(Encoding.ASCII.GetString(env, 29, 10), Is.EqualTo("USER      "), "library leaf (home = $.USER)");
    }

    [Test]
    public void CatHeader_Root_GoldenThirtyFiveByteReply()
    {
        _harness.HandleOsCli("I AM SYST \r");

        byte[] header = _harness.HandleLoggedIn(
            FileServerOpcodes.CatHeader,
            Encoding.ASCII.GetBytes(".\r")).Payload;

        Assert.That(header, Has.Length.EqualTo(35));
        Assert.That(header[0], Is.EqualTo(0));
        Assert.That(header[1], Is.EqualTo(0));
        // GOLDEN BYTES — re-baselined 2026-07-17 to PiFS naming parity (fs.c:10388). The CSD after
        // login is the home dir $.USER, whose tail is "USER"; "$" would mean the DISC ROOT. Layout,
        // 35-byte length, padding width and every other field are unchanged.
        Assert.That(Encoding.ASCII.GetString(header, 2, 11), Is.EqualTo("USER       "), "directory tail padded to 11");
        Assert.That(header[13], Is.EqualTo((byte)'O'), "owner flag");
        Assert.That(Encoding.ASCII.GetString(header, 17, 16), Is.EqualTo("RetroFS         "), "disc name");
        Assert.That(header[33], Is.EqualTo(FileServerReply.CarriageReturn));
        Assert.That(header[34], Is.EqualTo(0x80));
    }

    [Test]
    public void Examine_Arg3_GoldenHumanReadableTextEntries()
    {
        _harness.HandleOsCli("I AM SYST \r");

        // arg=3, start=0, count=10, path="." — the human-readable *CAT listing.
        byte[] examine = _harness.HandleLoggedIn(
            FileServerOpcodes.Examine,
            [3, 0, 10, (byte)'.', FileServerReply.CarriageReturn]).Payload;

        Assert.That(examine[0], Is.EqualTo(0));
        Assert.That(examine[1], Is.EqualTo(0));
        Assert.That(examine[2], Is.EqualTo(3), "three catalogue entries examined");
        Assert.That(examine[^1], Is.EqualTo(0x80), "terminal byte");

        // Entries are NUL-separated PiFS "%-10s %4s/%-2s" records between the 4-byte header and 0x80.
        string body = Encoding.ASCII.GetString(examine, 4, examine.Length - 5);
        string[] entries = body.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        Assert.That(entries, Has.Length.EqualTo(3));
        Assert.That(entries, Is.EqualTo(new[]
        {
            $"{"BASIC",-10} {"WR",4}/{"WR",-2}",
            $"{"HELLO",-10} {"WR",4}/{"WR",-2}",
            $"{"README",-10} {"WR",4}/{"WR",-2}",
        }), "sorted files rendered as owner/public access text");
    }
}
