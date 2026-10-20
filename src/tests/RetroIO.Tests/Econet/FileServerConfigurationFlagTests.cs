//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;

namespace RetroIO.Tests.Econet;

/// <summary>
/// What each configuration flag bit changes in the file server, checked against the PiFS reference
/// (PiEconetBridge utilities/fs.c). One section per flag. The default flag value is 0x82 (MdfsFunctions
/// and AcornDir set), so each test sets the bits it needs.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class FileServerConfigurationFlagTests
{
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EconetFsTestHarness();

    private void SetFlags(byte bits) => _harness.Configuration.ConfigBits = bits;

    // Opens a file for reading with the OPEN call (fsop 6): [mustExist, readOnly, name\r]. Read only so one
    // file can be opened many times. Returns the whole reply.
    private byte[] Open(string acornName)
    {
        byte[] parameters = new byte[2 + acornName.Length + 1];
        parameters[0] = 1;
        parameters[1] = 1;
        Encoding.ASCII.GetBytes(acornName + "\r").CopyTo(parameters, 2);
        return _harness.Send(FileServerOpcodes.Open, parameters).Payload;
    }

    // ---------------------------------------------------------------------------------------------
    // ManyHandle 0x10. fs.c 71 and 72 (FS_DIVHANDLE and FS_MULHANDLE), 1464 and 1495 (handle limit),
    // 9329 (open reply), 9941 to 9946 (request header handles), 11367 to 11401 (channel handles).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Login_ManyHandleClear_RepliesWithBitMaskHandles()
    {
        // fs.c 4833 to 4835: FS_MULHANDLE of root, current and library. Handles 1, 2, 3 become 0x01, 0x02, 0x04.
        SetFlags(0x82);

        byte[] reply = _harness.LoginAsSyst().Payload;

        Assert.That(reply[2], Is.EqualTo(0x01));
        Assert.That(reply[3], Is.EqualTo(0x02));
        Assert.That(reply[4], Is.EqualTo(0x04));
    }

    [Test]
    public void Login_ManyHandleSet_RepliesWithPlainHandleNumbers()
    {
        // fs.c 72: with fs_manyhandle set FS_MULHANDLE gives the number itself.
        SetFlags(0x82 | FileServerConfiguration.FlagManyHandle);

        byte[] reply = _harness.LoginAsSyst().Payload;

        Assert.That(reply[2], Is.EqualTo(1));
        Assert.That(reply[3], Is.EqualTo(2));
        Assert.That(reply[4], Is.EqualTo(3));
    }

    [Test]
    public void Open_ManyHandleClear_RepliesWithTheHandleAsABitMask()
    {
        // fs.c 9329: the open reply carries FS_MULHANDLE(userhandle). The first file handle is 4, so 1 << 3 = 0x08.
        SetFlags(0x82);
        _harness.LoginAsSyst();

        byte[] reply = Open("HELLO");

        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(reply[2], Is.EqualTo(0x08));
    }

    [Test]
    public void Open_ManyHandleSet_RepliesWithThePlainHandleNumber()
    {
        SetFlags(0x82 | FileServerConfiguration.FlagManyHandle);
        _harness.LoginAsSyst();

        byte[] reply = Open("HELLO");

        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(reply[2], Is.EqualTo(4));
    }

    [Test]
    public void Open_ManyHandleClear_RefusesTheSixthFile()
    {
        // fs.c 1464: without manyhandle no handle above 8 is given out. Handles 1 to 3 are the directories,
        // so files get 4 to 8, which is five files.
        SetFlags(0x82);
        _harness.LoginAsSyst();
        for (int i = 0; i < 5; i++)
            Assert.That(Open("HELLO")[1], Is.EqualTo(0), "open " + (i + 1));

        byte[] sixth = Open("HELLO");

        Assert.That(sixth[1], Is.EqualTo(FileServerErrors.Locked));
    }

    [Test]
    public void Open_ManyHandleSet_AllowsMoreThanFiveFiles()
    {
        // fs.c 1464: with manyhandle the limit is FS_MAX_OPEN_FILES (33 slots, slot 0 unused).
        SetFlags(0x82 | FileServerConfiguration.FlagManyHandle);
        _harness.LoginAsSyst();
        for (int i = 0; i < 6; i++)
            Assert.That(Open("HELLO")[1], Is.EqualTo(0), "open " + (i + 1));
    }

    [Test]
    public void EndOfFile_ManyHandleClear_TakesTheHandleAsABitMask()
    {
        // fs.c 11401: FS_DIVHANDLE(data+5) turns the mask 0x08 back into handle 4.
        SetFlags(0x82);
        _harness.LoginAsSyst();
        byte handle = Open("HELLO")[2];

        byte[] reply = _harness.Send(FileServerOpcodes.EndOfFile, [handle]).Payload;

        Assert.That(reply[1], Is.EqualTo(0), "known channel");
    }

    [Test]
    public void EndOfFile_ManyHandleClear_RefusesAPlainNumberThatIsNotTheMask()
    {
        // The plain number 4 is not a power of two that names handle 4; it is divided as itself, which is
        // handle 4 only by chance. Use 0x10, which divides to 5, a handle that is not open.
        SetFlags(0x82);
        _harness.LoginAsSyst();
        Open("HELLO");

        byte[] reply = _harness.Send(FileServerOpcodes.EndOfFile, [0x10]).Payload;

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.BadChannel));
    }

    [Test]
    public void EndOfFile_ManyHandleSet_TakesThePlainHandleNumber()
    {
        SetFlags(0x82 | FileServerConfiguration.FlagManyHandle);
        _harness.LoginAsSyst();
        byte handle = Open("HELLO")[2];
        Assert.That(handle, Is.EqualTo(4));

        byte[] known = _harness.Send(FileServerOpcodes.EndOfFile, [4]).Payload;
        byte[] mask = _harness.Send(FileServerOpcodes.EndOfFile, [0x08]).Payload;

        Assert.That(known[1], Is.EqualTo(0), "plain 4 is handle 4");
        Assert.That(mask[1], Is.EqualTo(FileServerErrors.BadChannel), "0x08 is handle 8, not open");
    }

    [Test]
    public void GetByte_ManyHandleSet_TakesThePlainHandleNumberFromTheFirstHandleByte()
    {
        // fs.c 11373: FS_DIVHANDLE(data+2). With manyhandle set the byte is the handle itself.
        SetFlags(0x82 | FileServerConfiguration.FlagManyHandle);
        _harness.LoginAsSyst();
        byte handle = Open("HELLO")[2];

        EconetMessage request = EconetFsTestHarness.CreateLoggedInRequest(FileServerOpcodes.GetByte, [], urd: handle);
        byte[] reply = _harness.Handler.Handle(request)!.Payload;

        Assert.That(handle, Is.EqualTo(4));
        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(reply[2], Is.EqualTo((byte)'H'));
    }

    [Test]
    public void LibCommand_ManyHandleClear_RepliesWithTheLibraryHandleAsABitMask()
    {
        // fs.c 6804 to 6806: FS_MULHANDLE on the root, current and library handle in the reply.
        SetFlags(0x82);
        _harness.LoginAsSyst();

        byte[] reply = _harness.HandleOsCli("LIB \r").Payload;

        Assert.That(reply[0], Is.EqualTo(0x09));
        Assert.That(reply[2], Is.EqualTo(0x04));
    }

    [Test]
    public void LibCommand_ManyHandleSet_RepliesWithThePlainLibraryHandle()
    {
        SetFlags(0x82 | FileServerConfiguration.FlagManyHandle);
        _harness.LoginAsSyst();

        byte[] reply = _harness.HandleOsCli("LIB \r").Payload;

        Assert.That(reply[0], Is.EqualTo(0x09));
        Assert.That(reply[2], Is.EqualTo(3));
    }

    [Test]
    public void Close_ManyHandleClear_TakesTheHandleAsABitMask()
    {
        SetFlags(0x82);
        _harness.LoginAsSyst();
        byte handle = Open("HELLO")[2];

        byte[] reply = _harness.Send(FileServerOpcodes.Close, [handle]).Payload;
        byte[] again = _harness.Send(FileServerOpcodes.EndOfFile, [handle]).Payload;

        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(again[1], Is.EqualTo(FileServerErrors.BadChannel), "the file is closed");
    }
}
