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
/// number for message size, path length, file name length or I/O size, so the values are the package's own choice
/// (the tnfsd config.h values differ and are not the spec). Two names for one limit with two values drift apart.
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
}
