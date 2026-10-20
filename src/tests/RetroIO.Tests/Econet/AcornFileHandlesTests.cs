//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class AcornFileHandlesTests
{
    [Test]
    public void MulHandle_BitMaskMode_MapsInternalHandlesToWireBitmasks()
    {
        // fs.c 72: with fs_manyhandle clear the wire handle is 1 shifted left by handle - 1.
        Assert.That(AcornFileHandles.MulHandle(1, false), Is.EqualTo(0x01));
        Assert.That(AcornFileHandles.MulHandle(2, false), Is.EqualTo(0x02));
        Assert.That(AcornFileHandles.MulHandle(3, false), Is.EqualTo(0x04));
        Assert.That(AcornFileHandles.MulHandle(4, false), Is.EqualTo(0x08));
        Assert.That(AcornFileHandles.MulHandle(8, false), Is.EqualTo(0x80));
    }

    [Test]
    public void MulHandle_ManyHandleMode_KeepsThePlainNumber()
    {
        Assert.That(AcornFileHandles.MulHandle(1, true), Is.EqualTo(1));
        Assert.That(AcornFileHandles.MulHandle(4, true), Is.EqualTo(4));
        Assert.That(AcornFileHandles.MulHandle(32, true), Is.EqualTo(32));
    }

    [Test]
    public void DivHandle_BitMaskMode_MapsWireBitmasksToInternalHandles()
    {
        // fs.c 71: a power of two from 1 to 128 gives its bit number plus one; any other byte passes through.
        Assert.That(AcornFileHandles.DivHandle(0x01, false), Is.EqualTo(1));
        Assert.That(AcornFileHandles.DivHandle(0x02, false), Is.EqualTo(2));
        Assert.That(AcornFileHandles.DivHandle(0x04, false), Is.EqualTo(3));
        Assert.That(AcornFileHandles.DivHandle(0x08, false), Is.EqualTo(4));
        Assert.That(AcornFileHandles.DivHandle(0x80, false), Is.EqualTo(8));
        Assert.That(AcornFileHandles.DivHandle(0x00, false), Is.EqualTo(0));
        Assert.That(AcornFileHandles.DivHandle(0x03, false), Is.EqualTo(3), "not a power of two: unchanged");
    }

    [Test]
    public void DivHandle_ManyHandleMode_KeepsThePlainNumber()
    {
        Assert.That(AcornFileHandles.DivHandle(0x08, true), Is.EqualTo(8));
        Assert.That(AcornFileHandles.DivHandle(0x04, true), Is.EqualTo(4));
        Assert.That(AcornFileHandles.DivHandle(0x00, true), Is.EqualTo(0));
    }
}
