//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class AcornFileHandlesTests
{
    [Test]
    public void MulHandle_MapsInternalHandlesToWireBitmasks()
    {
        Assert.That(AcornFileHandles.MulHandle(1), Is.EqualTo(0x01));
        Assert.That(AcornFileHandles.MulHandle(2), Is.EqualTo(0x02));
        Assert.That(AcornFileHandles.MulHandle(3), Is.EqualTo(0x04));
    }

    [Test]
    public void DivHandle_MapsWireBitmasksToInternalHandles()
    {
        Assert.That(AcornFileHandles.DivHandle(0x01), Is.EqualTo(1));
        Assert.That(AcornFileHandles.DivHandle(0x02), Is.EqualTo(2));
        Assert.That(AcornFileHandles.DivHandle(0x04), Is.EqualTo(3));
        Assert.That(AcornFileHandles.DivHandle(0x00), Is.EqualTo(0));
    }
}
