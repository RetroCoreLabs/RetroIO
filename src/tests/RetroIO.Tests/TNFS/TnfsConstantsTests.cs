//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Reflection;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Protocol;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Each TNFS limit exists once in <see cref="TnfsConstants"/>. The written protocol (tnfs-protocol.md) states no
/// number for message size, path length, file name length or I/O size, so the values are a named choice: the user
/// chose the reference daemon tnfsd (config.h). Two names for one limit with two values drift apart.
/// </summary>
[TestFixture]
[Category("TNFS")]
public class TnfsConstantsTests
{
    /// <summary>
    /// Names that hold the same limit under another spelling must not exist. MaxMessageSize (532) disagreed with
    /// MAXMSGSZ (1024), MaxPathLength (256) with MAX_TNFSPATH (1024), and MAX_IOSZ (8192) with MaxIOSize (512).
    /// </summary>
    [Test]
    public void NoSecondName_ExistsFor_MessagePathFileNameOrIoLimits()
    {
        string[] duplicates = { "MaxMessageSize", "MaxPathLength", "MaxFilenameLength", "MAX_IOSZ" };
        Type type = typeof(TnfsConstants);
        for (int i = 0; i < duplicates.Length; i++)
        {
            FieldInfo? field = type.GetField(duplicates[i], BindingFlags.Public | BindingFlags.Static);
            Assert.That(field, Is.Null, duplicates[i] + " is a second name for a limit that already has one");
        }
    }

    /// <summary>
    /// The payload limit is derived from the message size, never typed in separately.
    /// </summary>
    [Test]
    public void MaxPayload_IsMessageSizeMinusHeaderAndStatusByte()
    {
        Assert.That(TnfsConstants.TNFS_MAX_PAYLOAD, Is.EqualTo(TnfsConstants.MAXMSGSZ - TnfsConstants.TNFS_HEADERSZ - 1));
    }

    /// <summary>
    /// Every limit the user chose to take from the reference daemon tnfsd (config.h) is pinned here by its
    /// config.h name. The written TNFS spec states no number for any of them.
    /// </summary>
    [Test]
    public void Limits_MatchReferenceDaemon_ConfigH()
    {
        Assert.That(TnfsConstants.MAXMSGSZ, Is.EqualTo(532), "config.h MAXMSGSZ");
        Assert.That(TnfsConstants.TNFS_MAX_PAYLOAD, Is.EqualTo(532 - 4 - 1), "config.h TNFS_MAX_PAYLOAD (MAXMSGSZ - TNFS_HEADERSZ - 1)");
        Assert.That(TnfsConstants.MAX_DHND_PER_CONN, Is.EqualTo(8), "config.h MAX_DHND_PER_CONN");
        Assert.That(TnfsConstants.MAX_FILEPATH, Is.EqualTo(384), "config.h MAX_FILEPATH");
        Assert.That(TnfsConstants.MAX_TNFSPATH, Is.EqualTo(256), "config.h MAX_TNFSPATH");
        Assert.That(TnfsConstants.CONN_TIMEOUT, Is.EqualTo(600), "config.h CONN_TIMEOUT");
        Assert.That(TnfsConstants.TCP_KA_IDLE, Is.EqualTo(30), "config.h TCP_KA_IDLE");
        Assert.That(TnfsConstants.TCP_KA_INTVL, Is.EqualTo(1), "config.h TCP_KA_INTVL");
        Assert.That(TnfsConstants.TCP_KA_COUNT, Is.EqualTo(60), "config.h TCP_KA_COUNT");
        Assert.That(TnfsConstants.MaxIOSize, Is.EqualTo(512), "config.h MAX_IOSZ");
        Assert.That(TnfsConstants.MAX_TCP_CONN, Is.EqualTo(4096), "config.h MAX_TCP_CONN");
        Assert.That(TnfsConstants.TIMEOUT_LSB, Is.EqualTo(0xE8), "config.h TIMEOUT_LSB");
        Assert.That(TnfsConstants.TIMEOUT_MSB, Is.EqualTo(0x03), "config.h TIMEOUT_MSB");
    }
}
