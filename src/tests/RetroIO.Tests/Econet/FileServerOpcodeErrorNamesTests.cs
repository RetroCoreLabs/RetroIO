//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class FileServerOpcodeErrorNamesTests
{
    [Test]
    public void OpcodeNames_KnownOpcode_ReturnsReadableName()
    {
        Assert.That(FileServerOpcodeNames.GetName(FileServerOpcodes.Examine), Is.EqualTo("Examine directory"));
        Assert.That(FileServerOpcodeNames.GetDisplayName(FileServerOpcodes.Load), Is.EqualTo("Load file (0x02)"));
    }

    [Test]
    public void OpcodeNames_EconetPortAsOpcode_ReturnsBadFsopLabel()
    {
        Assert.That(FileServerOpcodeNames.GetName(0x99), Is.EqualTo("Bad fsop (Econet port 0x99)"));
        Assert.That(
            FileServerOpcodeNames.GetDisplayName(0x99),
            Is.EqualTo("Bad fsop: used Econet port 0x99 as opcode"));
        Assert.That(FileServerOpcodeNames.GetMonitorCommandLabel(0x99), Is.EqualTo("Bad fsop 0x99"));
    }

    [Test]
    public void OpcodeNames_UnknownOpcode_ReturnsUnknownLabel()
    {
        Assert.That(FileServerOpcodeNames.GetName(0x90), Is.EqualTo("Unknown opcode"));
        Assert.That(FileServerOpcodeNames.GetDisplayName(0x90), Is.EqualTo("Unknown opcode (0x90)"));
        Assert.That(FileServerOpcodeNames.GetMonitorCommandLabel(0x90), Is.EqualTo("Unknown 0x90"));
    }

    [Test]
    public void ErrorNames_Unsupported_ReturnsReadableName()
    {
        Assert.That(FileServerErrorNames.GetName(FileServerErrors.Unsupported), Is.EqualTo("Unsupported"));
        Assert.That(
            FileServerErrorNames.GetDisplay(FileServerErrors.Unsupported, "Unsupported"),
            Is.EqualTo("Unsupported"));
        Assert.That(
            FileServerErrorNames.GetDisplay(FileServerErrors.NotFound, "Not found"),
            Is.EqualTo("Not found"));
    }

    [Test]
    public void OsCliNames_IAm_ReturnsStarIAm()
    {
        Assert.That(FileServerOpcodeNames.GetOsCliDisplayName("I AM SYST "), Is.EqualTo("*I AM"));
        Assert.That(FileServerOpcodeNames.GetOsCliDisplayName("CAT $."), Is.EqualTo("*CAT"));
    }
}
