//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Byte-for-byte parity checks for miscellaneous file-server opcodes against PiFS (utilities/fs.c).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class MiscOpcodeParityTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _harness.HandleOsCli("I AM SYST \r");
    }

    [Test]
    public void ReadTime_ReturnsSevenBytePiFsLayout()
    {
        // PiFS fs_read_time (utilities/fs.c:7513-7543) returns EXACTLY 7 bytes:
        //   [0]=0 [1]=0 [2]=day [3]=monthyear [4]=hour [5]=min [6]=sec
        // (fs_aun_send(..., 7, ...)). The previous RetroFS build wrongly returned 9 bytes in a
        // RISC-OS-style order ([0,0,sec,min,hour,day,month,year,centis]). This test locks the length,
        // the two status bytes, and the day-before-hour field order so the regression cannot return.
        byte[] payload = _harness.HandleLoggedIn(FileServerOpcodes.ReadTime, Array.Empty<byte>()).Payload;

        Assert.That(payload.Length, Is.EqualTo(7), "PiFS read-time reply is 7 bytes");
        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(0));

        // Byte 2 is the Acorn day byte: low 5 bits = day-of-month, top 3 bits carry high year bits
        // (fs_date_to_two_bytes, sevenbitbodge on), so mask before range-checking the day.
        int day = payload[2] & 0x1F;
        byte hour = payload[4];
        byte minute = payload[5];
        byte second = payload[6];

        // Field-order sanity (day at [2], time fields at [4..6]) — impossible to satisfy with the old
        // [0,0,sec,min,hour,...] ordering for most clock values, and rules out a 9-byte reply outright.
        Assert.That(day, Is.InRange(1, 31), "byte 2 low 5 bits are the day-of-month");
        Assert.That(hour, Is.InRange(0, 23), "byte 4 is the hour");
        Assert.That(minute, Is.InRange(0, 59), "byte 5 is the minute");
        Assert.That(second, Is.InRange(0, 59), "byte 6 is the second");
    }

    // Expected values computed directly from PiFS fs_date_to_two_bytes (utilities/fs.c:1143) with the
    // DEFAULT fs_sevenbitbodge = 1 (econet-hpbridge.c:9321): yearBits = year - 1981;
    // day = (day & 0x1f) | ((yearBits & 0x70) << 1); monthyear = (month & 0x0f) | ((yearBits & 0x0f) << 4).
    [TestCase(2026, 7, 15, 0x4F, 0xD7)] // yearBits 45 (0x2D): day 15|0x40; my 7|0xD0
    [TestCase(1993, 12, 25, 0x19, 0xCC)] // yearBits 12 (0x0C): day 25|0; my 12|0xC0
    [TestCase(1981, 7, 1, 0x01, 0x07)] // yearBits 0: day 1; my 7
    public void EncodeFileDate_MatchesPiFsSevenBitBodge(int year, int month, int dayOfMonth, int expectedDay, int expectedMonthYear)
    {
        AcornPath.EncodeFileDate(new DateTime(year, month, dayOfMonth), out byte day, out byte monthYear);

        Assert.That(day, Is.EqualTo((byte)expectedDay), "day byte (with high year bits)");
        Assert.That(monthYear, Is.EqualTo((byte)expectedMonthYear), "month (low nibble) + year (high nibble)");
    }

    [Test]
    public void ReadClientId_ReturnsRawUsernameCrTerminated()
    {
        // PiFS "read client ID" (utilities/fs.c:11482) returns the caller's username as raw ASCII + 0x0D,
        // with NO [0,0] status prefix. The harness logs in as SYST.
        byte[] payload = _harness.HandleLoggedIn(FileServerOpcodes.ReadClientId, Array.Empty<byte>()).Payload;

        Assert.That(payload.Length, Is.EqualTo(5), "'SYST' + 0x0D");
        Assert.That(Encoding.ASCII.GetString(payload, 0, 4), Is.EqualTo("SYST"));
        Assert.That(payload[4], Is.EqualTo(FileServerReply.CarriageReturn));
    }

    [Test]
    public void ReadUserInfo_ReturnsFiveByteNetworkLocation()
    {
        // PiFS fs_read_user_info (utilities/fs.c:7622): [0, 0, flag, station, net] for the session where
        // the named user is logged on. The harness client is net 1, station 1.
        byte[] payload = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserInfo, Encoding.ASCII.GetBytes("SYST\r")).Payload;

        Assert.That(payload.Length, Is.EqualTo(5));
        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(0));
        Assert.That(payload[2], Is.AnyOf((byte)0x00, (byte)0x40), "0x40 for a System user, else 0");
        Assert.That(payload[3], Is.EqualTo(1), "station");
        Assert.That(payload[4], Is.EqualTo(1), "net");
    }

    [Test]
    public void ReadUserInfo_UnknownUser_ReturnsNoSuchUser()
    {
        byte[] payload = _harness.HandleLoggedIn(FileServerOpcodes.ReadUserInfo, Encoding.ASCII.GetBytes("NOBODY\r")).Payload;

        Assert.That(payload[1], Is.EqualTo((byte)0xBC), "0xBC no such user or not logged on");
    }

    [Test]
    public void ReadLoggedOnUsers_UsesPiFsVariableEntryLayout()
    {
        // PiFS fs_read_logged_on_users (utilities/fs.c:7546): [0,0,count] then per user
        // [station][net][username chars][0x0D][priv]. Only SYST is logged on (net 1, station 1).
        byte[] payload = _harness.HandleLoggedIn(FileServerOpcodes.ReadLoggedOnUsers, [(byte)0, (byte)8]).Payload;

        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo(0));
        Assert.That(payload[2], Is.EqualTo(1), "one logged-on user");
        Assert.That(payload[3], Is.EqualTo(1), "station before net");
        Assert.That(payload[4], Is.EqualTo(1), "net");
        Assert.That(Encoding.ASCII.GetString(payload, 5, 4), Is.EqualTo("SYST"));
        Assert.That(payload[9], Is.EqualTo(FileServerReply.CarriageReturn), "0x0D after username");
        Assert.That(payload[10], Is.AnyOf((byte)0, (byte)1), "priv byte (1 for System)");
        Assert.That(payload.Length, Is.EqualTo(11), "3 header + [stn,net,'SYST',0x0D,priv] = 3+8");
    }

    [Test]
    public void GetRandomAccessInfo_InvalidHandle_ReturnsChannelError()
    {
        // fs_get_random_access_info returns 0xDE "Channel ?" for an unopened handle (utilities/fs.c:8467).
        byte[] payload = _harness.HandleLoggedIn(FileServerOpcodes.GetRandomAccessInfo, [(byte)0x99, (byte)0]).Payload;

        Assert.That(payload[1], Is.EqualTo(FileServerErrors.BadChannel));
    }
}
