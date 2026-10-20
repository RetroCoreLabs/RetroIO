//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Metadata;
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

    // Reads the text catalogue (EXAMINE argument 3) of the current directory and returns one string per entry.
    private List<string> ExamineText()
    {
        byte[] reply = _harness.Send(FileServerOpcodes.Examine, [3, 0, 255, 13]).Payload;
        int count = reply[2];
        List<string> entries = new List<string>();
        int offset = 4;
        for (int i = 0; i < count; i++)
        {
            int end = Array.IndexOf(reply, (byte)0, offset);
            entries.Add(Encoding.ASCII.GetString(reply, offset, end - offset));
            offset = end + 1;
        }

        return entries;
    }

    // Returns the catalogue line of the entry with the given name, or fails the test.
    private string ExamineLine(string name)
    {
        List<string> entries = ExamineText();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].StartsWith(name, StringComparison.Ordinal))
                return entries[i];
        }

        Assert.Fail("no catalogue entry for " + name + " in: " + string.Join(" | ", entries));
        return string.Empty;
    }

    // ---------------------------------------------------------------------------------------------
    // AcornDir 0x80. fs.c 1110 (fs_perm_to_acorn), 5117 and 5174 (EXAMINE text), 7191 (*INFO), 216 (FS_ACORN_DIR_MASK).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Examine_AcornDirSet_HidesTheImpliedAccessLettersOfADirectory()
    {
        // The default directory access is owner write, owner read, other read (0x13). With fs_mask_dir_wrr set and
        // other write clear, fs.c clears those three bits before it builds the letters, so the directory shows D/.
        _harness.FileSystem.AddDirectory("/econet-root/0RetroFS/USER/SUBDIR");
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornDir);
        _harness.LoginAsSyst();

        Assert.That(ExamineLine("SUBDIR"), Is.EqualTo("SUBDIR        D/  "));
    }

    [Test]
    public void Examine_AcornDirClear_ShowsTheRealAccessLettersOfADirectory()
    {
        _harness.FileSystem.AddDirectory("/econet-root/0RetroFS/USER/SUBDIR");
        SetFlags(FileServerConfiguration.FlagMdfsFunctions);
        _harness.LoginAsSyst();

        Assert.That(ExamineLine("SUBDIR"), Is.EqualTo("SUBDIR      DWR/R "));
    }

    [Test]
    public void Examine_AcornDirSet_KeepsTheLettersWhenOtherWriteIsSet()
    {
        // fs.c 1110: the test includes FS_PERM_OTH_W, so a directory that others may write is shown in full.
        _harness.FileSystem.AddDirectory("/econet-root/0RetroFS/USER/SUBDIR");
        _harness.Configuration.DefaultDirPerm = 0x33;
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornDir);
        _harness.LoginAsSyst();

        Assert.That(ExamineLine("SUBDIR"), Is.EqualTo("SUBDIR      DWR/WR"));
    }

    [Test]
    public void Examine_AcornDirSet_KeepsTheLettersWhenNotAllThreeBitsAreSet()
    {
        // fs.c 1110: the mask only applies when owner write, owner read and other read are all set.
        _harness.FileSystem.AddDirectory("/econet-root/0RetroFS/USER/SUBDIR");
        _harness.Configuration.DefaultDirPerm = 0x03;
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornDir);
        _harness.LoginAsSyst();

        Assert.That(ExamineLine("SUBDIR"), Is.EqualTo("SUBDIR      DWR/  "));
    }

    [Test]
    public void Examine_AcornDirSet_DoesNotChangeAFileWithTheSameAccessBits()
    {
        // fs.c 1110 and 5174 test the object type: only directories are masked. The file has access 0x13, the
        // same bits that a directory shows as D/.
        AcornInfStore.WriteAttributes(_harness.FileSystem, "/econet-root/0RetroFS/USER/HELLO", 0, 0, 10, 0x13);
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornDir);
        _harness.LoginAsSyst();

        Assert.That(ExamineLine("HELLO"), Is.EqualTo("HELLO        WR/R "));
    }

    // ---------------------------------------------------------------------------------------------
    // MdfsFunctions 0x02. fs.c 9911 (operations from 64 up), 5392 (get object info command 0x40),
    // 8176 to 8178 (library search on load), 9195 to 9197 (library search on open).
    // ---------------------------------------------------------------------------------------------

    // Makes the current directory a directory that holds no files, so a file name only resolves in the library
    // (the library directory of a new login is the user directory, where the sample files are).
    private void LoginWithCurrentDirectoryElsewhere()
    {
        _harness.FileSystem.AddDirectory("/econet-root/0RetroFS/USER/EMPTYDIR");
        _harness.LoginAsSyst();
        byte[] reply = _harness.HandleOsCli("DIR EMPTYDIR\r").Payload;
        Assert.That(reply[0], Is.EqualTo(0x07), "the directory change worked");
    }

    // Builds a LOAD request (fsop 0x02) relative to the current directory.
    private byte[] LoadRequest(string filename)
    {
        byte[] name = Encoding.ASCII.GetBytes(filename + "\r");
        byte[] payload = new byte[FileServerBulkPacketLayout.LoadFilenameOffset + name.Length];
        payload[0] = 0x80;
        payload[1] = FileServerOpcodes.Load;
        payload[2] = 0x40;
        payload[3] = AcornFileHandles.WireCwd;
        name.CopyTo(payload, FileServerBulkPacketLayout.LoadFilenameOffset);
        return _harness.Handler.Handle(EconetFsTestHarness.CreateMessage(payload))!.Payload;
    }

    [Test]
    public void SjOperation_MdfsFunctionsClear_IsRefusedAsUnsupported()
    {
        // fs.c 9911 to 9915: fsop 64 and up with fs_sjfunc clear answers error 0xFF "Unsupported".
        SetFlags(0x80);
        _harness.LoginAsSyst();

        byte[] reply = _harness.Send(FileServerOpcodes.SjOperation, []).Payload;

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.Unsupported));
        Assert.That(Encoding.ASCII.GetString(reply, 2, reply.Length - 3), Is.EqualTo("Unsupported"));
    }

    [Test]
    public void SjOperation_MdfsFunctionsSet_IsAnswered()
    {
        SetFlags(0x82);
        _harness.LoginAsSyst();

        byte[] reply = _harness.Send(FileServerOpcodes.SjOperation, []).Payload;

        Assert.That(reply[1], Is.EqualTo(0));
    }

    [Test]
    public void PiBridgeCall_MdfsFunctionsClear_IsRefusedAsUnsupportedToo()
    {
        // fs.c 9911 tests every fsop from 64 up, and the PiBridge calls (fsop 0x60, fs.c 11786) come after that test.
        SetFlags(0x80);
        _harness.LoginAsSyst();

        byte[] reply = _harness.Send(FileServerOpcodes.PiBridge, [17]).Payload;

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.Unsupported));
        Assert.That(Encoding.ASCII.GetString(reply, 2, reply.Length - 3), Is.EqualTo("Unsupported"));
    }

    [Test]
    public void PiBridgeCall_MdfsFunctionsSet_IsAnswered()
    {
        SetFlags(0x82);
        _harness.LoginAsSyst();

        byte[] reply = _harness.Send(FileServerOpcodes.PiBridge, [17]).Payload;

        Assert.That(reply[1], Is.EqualTo(0));
    }

    [Test]
    public void GetObjectInfo_Command0x40_MdfsFunctionsClear_IsRefusedAsMdfsUnsupported()
    {
        // fs.c 5392 to 5396.
        SetFlags(0x80);
        _harness.LoginAsSyst();
        byte[] parameters = Encoding.ASCII.GetBytes("@HELLO\r");

        byte[] reply = _harness.Send(FileServerOpcodes.GetObjectInfo, parameters).Payload;

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.Unsupported));
        Assert.That(Encoding.ASCII.GetString(reply, 2, reply.Length - 3), Is.EqualTo("MDFS Unsupported"));
    }

    [Test]
    public void Load_MdfsFunctionsSet_SearchesTheLibraryWhenTheCurrentDirectoryHasNoSuchFile()
    {
        // fs.c 8176 to 8178: with fs_sjfunc set a load that finds nothing in the current directory looks in the library.
        SetFlags(0x82);
        LoginWithCurrentDirectoryElsewhere();

        byte[] reply = LoadRequest("HELLO");

        Assert.That(reply[1], Is.EqualTo(0), "found in the library");
    }

    [Test]
    public void Load_MdfsFunctionsClear_DoesNotSearchTheLibrary()
    {
        SetFlags(0x80);
        LoginWithCurrentDirectoryElsewhere();

        byte[] reply = LoadRequest("HELLO");

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.NotFound));
    }

    [Test]
    public void Open_ExistingFile_MdfsFunctionsSet_SearchesTheLibraryWhenTheCurrentDirectoryHasNoSuchFile()
    {
        // fs.c 9195 to 9197: the library is searched when the file must exist.
        SetFlags(0x82);
        LoginWithCurrentDirectoryElsewhere();

        Assert.That(Open("HELLO")[1], Is.EqualTo(0));
    }

    [Test]
    public void Open_FileThatNeedNotExist_MdfsFunctionsSet_DoesNotOpenTheLibraryFile()
    {
        // fs.c 9197 searches the library only when existingfile is true. With must-exist clear the library file is
        // not used. (This server cannot make a new file through OPEN, so the answer is Not found, as it is with the
        // flag clear.)
        SetFlags(0x82);
        LoginWithCurrentDirectoryElsewhere();
        byte[] parameters = new byte[] { 0, 0, (byte)'H', (byte)'E', (byte)'L', (byte)'L', (byte)'O', 13 };

        byte[] reply = _harness.Send(FileServerOpcodes.Open, parameters).Payload;

        Assert.That(reply[1], Is.EqualTo(FileServerErrors.NotFound));
    }

    [Test]
    public void Open_ExistingFile_MdfsFunctionsClear_DoesNotSearchTheLibrary()
    {
        SetFlags(0x80);
        LoginWithCurrentDirectoryElsewhere();

        Assert.That(Open("HELLO")[1], Is.EqualTo(FileServerErrors.NotFound));
    }
}
