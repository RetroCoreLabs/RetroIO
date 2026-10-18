//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet.Fsop;

/// <summary>
/// <c>SetObjectInfo</c> (0x13) with a parameter block that is too short for the length field.
/// </summary>
[TestFixture]
[Category("Unit")]
internal sealed class SetObjectInfoShortBlockTests : FsopProtocolTestBase
{
    // Block = command byte + name + CR, so a name of 8 characters gives 10 bytes and 9 gives 11.
    [TestCase("FILENAME")]
    [TestCase("FILENAME1")]
    public void WritingCommand_WithBlockShorterThanTheAttributes_RepliesSuccessWithoutThrowing(string name)
    {
        LoginAdmin();
        Harness.FileSystem.AddFile(EconetSampleCatalogFixture.UserDirectory + "/" + name, [1, 2, 3]);

        byte[] path = Encoding.ASCII.GetBytes(name + "\r");
        byte[] parameters = new byte[1 + path.Length];
        parameters[0] = 2;
        path.CopyTo(parameters, 1);

        EconetMessage reply = null!;
        Assert.DoesNotThrow(() => reply = Harness.Send(FileServerOpcodes.SetObjectInfo, parameters));

        Assert.That(StatusOf(reply), Is.EqualTo(0x00));
    }
}
