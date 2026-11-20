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
/// What each configuration flag bit changes in the file server, checked against the reference
/// behaviour. One section per flag. The default flag value is 0x82 (MdfsFunctions
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
    // ManyHandle 0x10. Wire handle bytes (mask or plain number), the handle limit,
    // the open reply, the request header handles and the channel handles.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Login_ManyHandleClear_RepliesWithBitMaskHandles()
    {
        // Root, current and library go out as wire bytes. Handles 1, 2, 3 become 0x01, 0x02, 0x04.
        SetFlags(0x82);

        byte[] reply = _harness.LoginAsSyst().Payload;

        Assert.That(reply[2], Is.EqualTo(0x01));
        Assert.That(reply[3], Is.EqualTo(0x02));
        Assert.That(reply[4], Is.EqualTo(0x04));
    }

    [Test]
    public void Login_ManyHandleSet_RepliesWithPlainHandleNumbers()
    {
        // With ManyHandle set the wire byte is the number itself.
        SetFlags(0x82 | FileServerConfiguration.FlagManyHandle);

        byte[] reply = _harness.LoginAsSyst().Payload;

        Assert.That(reply[2], Is.EqualTo(1));
        Assert.That(reply[3], Is.EqualTo(2));
        Assert.That(reply[4], Is.EqualTo(3));
    }

    [Test]
    public void Open_ManyHandleClear_RepliesWithTheHandleAsABitMask()
    {
        // The open reply carries the handle as a wire byte. The first file handle is 4, so 1 << 3 = 0x08.
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
        // Without ManyHandle no handle above 8 is given out. Handles 1 to 3 are the directories,
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
        // With ManyHandle the limit is 33 slots, slot 0 unused.
        SetFlags(0x82 | FileServerConfiguration.FlagManyHandle);
        _harness.LoginAsSyst();
        for (int i = 0; i < 6; i++)
            Assert.That(Open("HELLO")[1], Is.EqualTo(0), "open " + (i + 1));
    }

    [Test]
    public void EndOfFile_ManyHandleClear_TakesTheHandleAsABitMask()
    {
        // Request byte 5: the mask 0x08 is turned back into handle 4.
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
        // Request byte 2. With ManyHandle set the byte is the handle itself.
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
        // The root, current and library handle in the reply are wire bytes.
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
    // AcornDir 0x80. Access conversion, EXAMINE text and *INFO.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Examine_AcornDirSet_HidesTheImpliedAccessLettersOfADirectory()
    {
        // The default directory access is owner write, owner read, other read (0x13). With AcornDir set and
        // other write clear, the server clears those three bits before it builds the letters, so the directory shows D/.
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
        // The test includes other write, so a directory that others may write is shown in full.
        _harness.FileSystem.AddDirectory("/econet-root/0RetroFS/USER/SUBDIR");
        _harness.Configuration.DefaultDirPerm = 0x33;
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornDir);
        _harness.LoginAsSyst();

        Assert.That(ExamineLine("SUBDIR"), Is.EqualTo("SUBDIR      DWR/WR"));
    }

    [Test]
    public void Examine_AcornDirSet_KeepsTheLettersWhenNotAllThreeBitsAreSet()
    {
        // The mask only applies when owner write, owner read and other read are all set.
        _harness.FileSystem.AddDirectory("/econet-root/0RetroFS/USER/SUBDIR");
        _harness.Configuration.DefaultDirPerm = 0x03;
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornDir);
        _harness.LoginAsSyst();

        Assert.That(ExamineLine("SUBDIR"), Is.EqualTo("SUBDIR      DWR/  "));
    }

    [Test]
    public void Examine_AcornDirSet_DoesNotChangeAFileWithTheSameAccessBits()
    {
        // The object type is tested: only directories are masked. The file has access 0x13, the
        // same bits that a directory shows as D/.
        AcornAttributeStore.WriteAttributes(_harness.FileSystem, "/econet-root/0RetroFS/USER/HELLO", 0, 0, 0x13);
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornDir);
        _harness.LoginAsSyst();

        Assert.That(ExamineLine("HELLO"), Is.EqualTo("HELLO        WR/R "));
    }

    // ---------------------------------------------------------------------------------------------
    // MdfsFunctions 0x02. Operations from 64 up, get object info command 0x40,
    // library search on load, library search on open.
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
        // Fsop 64 and up with MdfsFunctions clear answers error 0xFF "Unsupported".
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
        // Every fsop from 64 up is tested, and the PiBridge calls (fsop 0x60) come after that test.
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
        // With MdfsFunctions set a load that finds nothing in the current directory looks in the library.
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
        // The library is searched when the file must exist.
        SetFlags(0x82);
        LoginWithCurrentDirectoryElsewhere();

        Assert.That(Open("HELLO")[1], Is.EqualTo(0));
    }

    [Test]
    public void Open_FileThatNeedNotExist_MdfsFunctionsSet_DoesNotOpenTheLibraryFile()
    {
        // The library is searched only when the file must exist. With must-exist clear the library file is
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
