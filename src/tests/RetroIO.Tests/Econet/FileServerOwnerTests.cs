//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Common;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Handlers;
using RetroIO.Protocols.Econet.FileServer.Metadata;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// The owner kept for each object in its property bag, checked against the PiFS reference (PiEconetBridge utilities/fs.c):
/// struct objattr at line 318, fs_read_attr_from_file and fs_write_attr_to_file at lines 1608 to 1648, fs_read_xattr at line 1675.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AcornAttributeStoreOwnerTests
{
    private const string FilePath = "/disc/FILE";

    private MockFileSystemBackend _fileSystem = null!;

    [SetUp]
    public void SetUp()
    {
        _fileSystem = new MockFileSystemBackend();
        _fileSystem.AddDirectory("/disc");
        _fileSystem.AddFile(FilePath, Encoding.ASCII.GetBytes("0123456789"));
    }

    [Test]
    public void ReadOwnership_NoPropertyBag_ReadsOwnerZero()
    {
        AcornAttributeStore.ReadOwnership(_fileSystem, FilePath, out ushort owner, out ushort homeOf);

        Assert.That(owner, Is.EqualTo(0));
        Assert.That(homeOf, Is.EqualTo(0));
    }

    [Test]
    public void WriteOwnership_KeepsLoadExecAndAccessOfThePropertyBag()
    {
        AcornAttributeStore.WriteAttributes(_fileSystem, FilePath, 0x1900, 0x8023, 0x13);

        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 0x42, 0);

        AcornAttributeStore.TryReadAttributes(_fileSystem, FilePath, out uint load, out uint exec, out int length, out byte access);
        Assert.That(load, Is.EqualTo(0x1900u));
        Assert.That(exec, Is.EqualTo(0x8023u));
        Assert.That(length, Is.EqualTo(10));
        Assert.That(access, Is.EqualTo(0x13));
        AcornAttributeStore.ReadOwnership(_fileSystem, FilePath, out ushort owner, out _);
        Assert.That(owner, Is.EqualTo(0x42));
    }

    [Test]
    public void WriteOwnership_WithNoPropertyBag_MakesOneAndTheAttributesStayAtTheirDefaults()
    {
        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 7, 0);

        AcornAttributeStore.TryReadAttributes(_fileSystem, FilePath, out uint load, out uint exec, out int length, out byte access);
        Assert.That(load, Is.EqualTo(0u));
        Assert.That(exec, Is.EqualTo(0u));
        Assert.That(length, Is.EqualTo(10), "the real file length");
        Assert.That(access, Is.EqualTo(0x33));
        AcornAttributeStore.ReadOwnership(_fileSystem, FilePath, out ushort owner, out _);
        Assert.That(owner, Is.EqualTo(7));
    }

    [Test]
    public void WriteOwnership_CalledTwice_KeepsTheLastOwner()
    {
        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 1, 0);
        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 2, 9);

        AcornAttributeStore.ReadOwnership(_fileSystem, FilePath, out ushort owner, out ushort homeOf);
        Assert.That(owner, Is.EqualTo(2));
        Assert.That(homeOf, Is.EqualTo(9));
    }

    [Test]
    public void WriteAttributes_WithoutOwner_KeepsTheOwnerAndHomeOwnerThatWereStored()
    {
        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 0x42, 0x55);

        AcornAttributeStore.WriteAttributes(_fileSystem, FilePath, 1, 2, 0x03);

        AcornAttributeStore.ReadOwnership(_fileSystem, FilePath, out ushort owner, out ushort homeOf);
        Assert.That(owner, Is.EqualTo(0x42));
        Assert.That(homeOf, Is.EqualTo(0x55));
    }

    [Test]
    public void WriteAttributes_WithOwner_ReplacesTheOwnerAndKeepsTheHomeOwner()
    {
        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 0x42, 0x55);

        AcornAttributeStore.WriteAttributes(_fileSystem, FilePath, 1, 2, 0x03, 0x77);

        AcornAttributeStore.ReadOwnership(_fileSystem, FilePath, out ushort owner, out ushort homeOf);
        Assert.That(owner, Is.EqualTo(0x77));
        Assert.That(homeOf, Is.EqualTo(0x55));
    }

    [Test]
    public void WriteAttributes_BagWithoutOwner_StaysWithoutAnOwnerProperty()
    {
        // A file that never had an owner keeps reading as the default owner, not as an owner somebody wrote.
        AcornAttributeStore.WriteAttributes(_fileSystem, FilePath, 1, 2, 0x03);

        IDictionary<string, object>? bag = _fileSystem.GetObjectProperties(FilePath);
        Assert.That(bag, Is.Not.Null);
        Assert.That(bag!.ContainsKey(AcornProperties.Owner), Is.False);
    }

    [Test]
    public void MoveAttributes_CarriesTheOwnerToTheNewName()
    {
        _fileSystem.AddFile("/disc/NEW", Encoding.ASCII.GetBytes("x"));
        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 0x42, 0);

        AcornAttributeStore.MoveAttributes(_fileSystem, FilePath, "/disc/NEW");

        AcornAttributeStore.ReadOwnership(_fileSystem, "/disc/NEW", out ushort owner, out _);
        Assert.That(owner, Is.EqualTo(0x42));
        Assert.That(_fileSystem.FileExists(XatSidecar.GetXatPath(FilePath)), Is.False);
        Assert.That(_fileSystem.FileExists(XatSidecar.GetXatPath("/disc/NEW")), Is.True);
    }

    [Test]
    public void DeleteAttributes_RemovesTheOwner()
    {
        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 0x42, 0);

        AcornAttributeStore.DeleteAttributes(_fileSystem, FilePath);

        AcornAttributeStore.ReadOwnership(_fileSystem, FilePath, out ushort owner, out _);
        Assert.That(owner, Is.EqualTo(0));
        Assert.That(_fileSystem.FileExists(XatSidecar.GetXatPath(FilePath)), Is.False);
    }

    [Test]
    public void IsStoreName_XatName_IsTheStoreOfAnObject()
    {
        Assert.That(AcornAttributeStore.IsStoreName("FILE.xat"), Is.True);
        Assert.That(AcornAttributeStore.IsStoreName("FILE.XAT"), Is.True);
    }

    [Test]
    public void IsStoreName_DamagedXatName_IsHiddenToo()
    {
        Assert.That(AcornAttributeStore.IsStoreName("FILE.xat.bad"), Is.True);
        Assert.That(AcornAttributeStore.IsStoreName("FILE.XAT.BAD"), Is.True);
        Assert.That(AcornAttributeStore.IsStoreName("FILE.bad"), Is.False);
    }

    [Test]
    public void IsStoreName_AcornNameAndCommaInfName_AreNotStores()
    {
        // An Acorn name has no dot, so it can never end in .xat. A name with a comma and inf is an ordinary name.
        Assert.That(AcornAttributeStore.IsStoreName("FILE"), Is.False);
        Assert.That(AcornAttributeStore.IsStoreName("FILE,inf"), Is.False);
        Assert.That(AcornAttributeStore.IsStoreName("XAT"), Is.False);
    }

    [Test]
    public void WriteOwnership_KeepsThePropertyBagInTheXatFileBesideTheObject()
    {
        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 0x42, 0);

        Assert.That(_fileSystem.FileExists("/disc/FILE.xat"), Is.True);
        Assert.That(_fileSystem.FileExists(FilePath + ",inf"), Is.False, "no ,inf file is written");
    }

    // ---------------------------------------------------------------------------------------------
    // The property bag goes through the same serializer and file name rules as NDFS.
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void XatFile_HoldsTheJsonOfTheNdfsSerializer()
    {
        AcornAttributeStore.WriteAttributes(_fileSystem, FilePath, 0x1900, 0x8023, 0x13, 5);

        using FileStream stream = _fileSystem.OpenFile("/disc/FILE.xat", FileMode.Open, FileAccess.Read);
        byte[] buffer = new byte[stream.Length];
        int read = stream.Read(buffer, 0, buffer.Length);
        IDictionary<string, object> bag = RetroIO.Storage.Ndfs.PropertyBags.NdfsXatSerializer.DeserializeFromJson(Encoding.UTF8.GetString(buffer, 0, read));
        Assert.That(Convert.ToInt64(bag["acorn.load"]), Is.EqualTo(0x1900));
        Assert.That(Convert.ToInt64(bag["acorn.exec"]), Is.EqualTo(0x8023));
        Assert.That(Convert.ToInt64(bag["acorn.access"]), Is.EqualTo(0x13));
        Assert.That(Convert.ToInt64(bag["acorn.owner"]), Is.EqualTo(5));
    }

    [Test]
    public void XatPath_DotInTheDirectoryPart_DoesNotCutTheNameShort()
    {
        // NdfsXatSerializer.GetXatFileName cuts at the last dot of whatever it is given, so the path must not be given to it whole.
        Assert.That(XatSidecar.GetXatPath("/disc.one/FILE"), Is.EqualTo("/disc.one/FILE.xat"));
        Assert.That(XatSidecar.GetXatPath("/disc/SUB/"), Is.EqualTo("/disc/SUB.xat"));
    }

    [Test]
    public void XatPath_NameWithADot_KeepsItsOwnStore()
    {
        Assert.That(XatSidecar.GetXatPath("/disc/A.B"), Is.EqualTo("/disc/A.B.xat"));
        Assert.That(XatSidecar.GetXatPath("/disc/A.B"), Is.Not.EqualTo(XatSidecar.GetXatPath("/disc/A")));
    }

    [Test]
    public void XatRead_DamagedFile_ReadsAsNoBagAndIsMovedToBad()
    {
        _fileSystem.AddFile("/disc/FILE.xat", Encoding.UTF8.GetBytes("this is not json"));

        AcornAttributeStore.ReadOwnership(_fileSystem, FilePath, out ushort owner, out ushort homeOf);

        Assert.That(owner, Is.EqualTo(0));
        Assert.That(homeOf, Is.EqualTo(0));
        Assert.That(XatSidecar.Read(_fileSystem, FilePath), Is.Null);
        Assert.That(_fileSystem.FileExists("/disc/FILE.xat"), Is.False, "the damaged file is moved away");
        Assert.That(_fileSystem.FileExists("/disc/FILE.xat.bad"), Is.True, "and kept under the .bad name");
    }

    [Test]
    public void XatRead_EmptyFile_ReadsAsNoBagAndIsMovedToBad()
    {
        _fileSystem.AddFile("/disc/FILE.xat", new byte[0]);

        IDictionary<string, object>? bag = XatSidecar.Read(_fileSystem, FilePath);

        Assert.That(bag, Is.Null);
        Assert.That(_fileSystem.FileExists("/disc/FILE.xat"), Is.False);
        Assert.That(_fileSystem.FileExists("/disc/FILE.xat.bad"), Is.True);
    }

    [Test]
    public void XatRead_DamagedFile_KeepsTheDamagedBytesInTheBadFile()
    {
        _fileSystem.AddFile("/disc/FILE.xat", Encoding.UTF8.GetBytes("this is not json"));

        XatSidecar.Read(_fileSystem, FilePath);

        using FileStream stream = _fileSystem.OpenFile("/disc/FILE.xat.bad", FileMode.Open, FileAccess.Read);
        byte[] buffer = new byte[stream.Length];
        int read = stream.Read(buffer, 0, buffer.Length);
        Assert.That(Encoding.UTF8.GetString(buffer, 0, read), Is.EqualTo("this is not json"));
    }

    [Test]
    public void XatRead_DamagedFileAndAnOlderBadFile_ReplacesTheOlderBadFile()
    {
        _fileSystem.AddFile("/disc/FILE.xat.bad", Encoding.UTF8.GetBytes("old damage"));
        _fileSystem.AddFile("/disc/FILE.xat", Encoding.UTF8.GetBytes("new damage"));

        XatSidecar.Read(_fileSystem, FilePath);

        Assert.That(_fileSystem.FileExists("/disc/FILE.xat"), Is.False);
        using FileStream stream = _fileSystem.OpenFile("/disc/FILE.xat.bad", FileMode.Open, FileAccess.Read);
        byte[] buffer = new byte[stream.Length];
        int read = stream.Read(buffer, 0, buffer.Length);
        Assert.That(Encoding.UTF8.GetString(buffer, 0, read), Is.EqualTo("new damage"));
    }

    [Test]
    public void XatRead_GoodFile_IsNotMoved()
    {
        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 5, 0);

        IDictionary<string, object>? bag = XatSidecar.Read(_fileSystem, FilePath);

        Assert.That(bag, Is.Not.Null);
        Assert.That(_fileSystem.FileExists("/disc/FILE.xat"), Is.True);
        Assert.That(_fileSystem.FileExists("/disc/FILE.xat.bad"), Is.False);
    }

    [Test]
    public void XatRead_DamagedFile_ANextWriteMakesAFreshXatFile()
    {
        _fileSystem.AddFile("/disc/FILE.xat", Encoding.UTF8.GetBytes("this is not json"));

        AcornAttributeStore.WriteOwnership(_fileSystem, FilePath, 0x42, 0);

        Assert.That(_fileSystem.FileExists("/disc/FILE.xat.bad"), Is.True);
        Assert.That(_fileSystem.FileExists("/disc/FILE.xat"), Is.True);
        AcornAttributeStore.ReadOwnership(_fileSystem, FilePath, out ushort owner, out _);
        Assert.That(owner, Is.EqualTo(0x42));
    }

    [Test]
    public void XatRead_EmptyFile_ANextWriteMakesAFreshXatFile()
    {
        _fileSystem.AddFile("/disc/FILE.xat", new byte[0]);

        AcornAttributeStore.WriteAttributes(_fileSystem, FilePath, 0x1900, 0x8023, 0x13);

        Assert.That(_fileSystem.FileExists("/disc/FILE.xat.bad"), Is.True);
        AcornAttributeStore.TryReadAttributes(_fileSystem, FilePath, out uint load, out uint exec, out _, out byte access);
        Assert.That(load, Is.EqualTo(0x1900u));
        Assert.That(exec, Is.EqualTo(0x8023u));
        Assert.That(access, Is.EqualTo(0x13));
    }

    [Test]
    public void TryReadAttributes_LengthFollowsTheFileWhenItGrows()
    {
        // fs.c struct objattr has no length: the length is the size of the file, never a stored number.
        AcornAttributeStore.WriteAttributes(_fileSystem, FilePath, 0x1900, 0x8023, 0x13);
        AcornAttributeStore.TryReadAttributes(_fileSystem, FilePath, out _, out _, out int before, out _);

        _fileSystem.AddFile(FilePath, Encoding.ASCII.GetBytes("0123456789ABCDEF"));
        AcornAttributeStore.TryReadAttributes(_fileSystem, FilePath, out _, out _, out int after, out _);

        Assert.That(before, Is.EqualTo(10));
        Assert.That(after, Is.EqualTo(16));
    }

    [Test]
    public void Properties_HaveNoLengthKey()
    {
        for (int i = 0; i < AcornProperties.All.Length; i++)
            Assert.That(AcornProperties.All[i].Key, Is.Not.EqualTo("acorn.length"));

        AcornAttributeStore.WriteAttributes(_fileSystem, FilePath, 1, 2, 0x03);
        IDictionary<string, object>? bag = _fileSystem.GetObjectProperties(FilePath);
        Assert.That(bag, Is.Not.Null);
        Assert.That(bag!.ContainsKey("acorn.length"), Is.False);
    }

    [Test]
    public void Properties_DescriptorsUseFilesystemSafeShortNames()
    {
        for (int i = 0; i < AcornProperties.All.Length; i++)
        {
            string shortName = AcornProperties.All[i].ShortName;
            Assert.That(shortName, Does.Match("^[A-Za-z0-9]{1,8}$"), AcornProperties.All[i].Key);
            Assert.That(AcornProperties.All[i].Key, Does.StartWith("acorn."));
        }
    }
}

/// <summary>
/// What the file server does with the owner: who gets 0 in the owner byte, who becomes the owner of what is made,
/// the AcornHome flag (fs.c lines 2784, 2928, 3061, 4677 and 10715 to 10751), the MdfsInfo flag (lines 5180 to 5187)
/// and the owner bytes of the MdfsFunctions flag (lines 5089 to 5094).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class FileServerOwnerTests
{
    private const string UserDirectory = EconetSampleCatalogFixture.UserDirectory;
    private const string HelloPath = UserDirectory + "/HELLO";
    private const string DiscRoot = "/econet-root/0RetroFS";
    private const ushort OtherUser = 0x1234;

    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _harness.Configuration.SaveCloseDelayMs = 0;
    }

    private void SetFlags(byte bits) => _harness.Configuration.ConfigBits = bits;

    // The user number of the user who is logged in at the test station.
    private ushort LoggedInUserIndex() => _harness.Sessions.GetSession(1, 1)!.UserIndex;

    // The user number of a named account, which the owner of the objects the account makes.
    private ushort UserIndexOf(string name)
    {
        Assert.That(_harness.Context.Users.TryGetProfileByUsername(name, out EconetUserProfile? profile), Is.True);
        return profile!.UserIndex;
    }

    // Makes HELLO a file with the given access byte and owner.
    private void SeedHello(byte access, ushort owner)
    {
        AcornAttributeStore.WriteAttributes(_harness.FileSystem, HelloPath, 0, 0, access);
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, HelloPath, owner, 0);
    }

    private static byte[] InfoRequest(byte command, string name)
    {
        byte[] encoded = Encoding.ASCII.GetBytes(name + "\r");
        byte[] request = new byte[1 + encoded.Length];
        request[0] = command;
        encoded.CopyTo(request, 1);
        return request;
    }

    // The owner byte of get object info command 4, which comes last in the reply (fs.c line 5694).
    private byte OwnerByteOfHello()
    {
        byte[] reply = _harness.Send(FileServerOpcodes.GetObjectInfo, InfoRequest(4, "HELLO")).Payload;
        Assert.That(reply, Has.Length.EqualTo(5));
        return reply[4];
    }

    // ---------------------------------------------------------------------------------------------
    // Owner byte of get object info. fs.c 5694 (commands 4, 5 and 96) and 5735 (command 6).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void GetObjectInfo_Cmd4_FileOwnedByTheCaller_OwnerByteIsZero()
    {
        _harness.LoginAsUser();
        SeedHello(0x13, LoggedInUserIndex());

        Assert.That(OwnerByteOfHello(), Is.EqualTo(0x00));
    }

    [Test]
    public void GetObjectInfo_Cmd4_FileOwnedBySomeoneElse_OwnerByteIsFF()
    {
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        Assert.That(OwnerByteOfHello(), Is.EqualTo(0xFF));
    }

    [Test]
    public void GetObjectInfo_Cmd4_FileOwnedBySomeoneElse_SystemUserStillGetsZero()
    {
        // fs.c 5694 tests the owner or FS_ACTIVE_SYST.
        _harness.LoginAsSyst();
        SeedHello(0x13, OtherUser);

        Assert.That(OwnerByteOfHello(), Is.EqualTo(0x00));
    }

    [Test]
    public void GetObjectInfo_Cmd4_FileWithNoStoredOwner_BelongsToTheSystemManager()
    {
        // fs.c 1678: owner 0 when nothing is stored, and user 0 is SYST (lines 3538 to 3544).
        _harness.LoginAsUser();
        Assert.That(OwnerByteOfHello(), Is.EqualTo(0xFF), "a normal user is not the owner");

        _harness.LoginAsSyst();
        Assert.That(OwnerByteOfHello(), Is.EqualTo(0x00), "the system manager is");
    }

    [Test]
    public void GetObjectInfo_Cmd6_DirectoryOwnedBySomeoneElse_SystemUserGetsFF()
    {
        // fs.c 5735 tests the owner only, a system user is not counted for the directory header.
        _harness.LoginAsSyst();
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory, OtherUser, 0);

        byte[] reply = _harness.Send(FileServerOpcodes.GetObjectInfo, [6, 13]).Payload;

        Assert.That(reply[15], Is.EqualTo(0xFF));
    }

    [Test]
    public void GetObjectInfo_Cmd6_DirectoryOwnedByTheCaller_OwnerByteIsZero()
    {
        _harness.LoginAsUser();
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory, LoggedInUserIndex(), 0);

        byte[] reply = _harness.Send(FileServerOpcodes.GetObjectInfo, [6, 13]).Payload;

        Assert.That(reply[15], Is.EqualTo(0x00));
    }

    // ---------------------------------------------------------------------------------------------
    // Who becomes the owner. fs.c 5873 (save), 7131 (cdir), 6474 (copy), 6981 to 6986 (rename), 7087 (delete).
    // ---------------------------------------------------------------------------------------------

    private void SaveRequest(string filename, int length)
    {
        byte[] name = Encoding.ASCII.GetBytes(filename + "\r");
        byte[] payload = new byte[FileServerBulkPacketLayout.SaveFilenameOffset + name.Length];
        payload[0] = 0x80;
        payload[1] = FileServerOpcodes.Save;
        payload[2] = 0x40;
        payload[13] = (byte)length;
        payload[14] = (byte)(length >> 8);
        payload[15] = (byte)(length >> 16);
        name.CopyTo(payload, FileServerBulkPacketLayout.SaveFilenameOffset);
        _lastSaveReply = _harness.Handler.Handle(EconetFsTestHarness.CreateMessage(payload))!.Payload;
    }

    private byte[] _lastSaveReply = [];

    private ushort OwnerOf(string unixPath)
    {
        AcornAttributeStore.ReadOwnership(_harness.FileSystem, unixPath, out ushort owner, out _);
        return owner;
    }

    [Test]
    public void Save_ZeroLength_MakesTheSavingUserTheOwner()
    {
        _harness.LoginAsUser();

        SaveRequest("EMPTY", 0);

        Assert.That(OwnerOf(UserDirectory + "/EMPTY"), Is.EqualTo(LoggedInUserIndex()));
        Assert.That(LoggedInUserIndex(), Is.Not.EqualTo(0));
    }

    [Test]
    public void Save_WithData_MakesTheSavingUserTheOwnerAtTheEndOfTheTransfer()
    {
        _harness.LoginAsUser();
        SaveRequest("DATA", 5);
        byte bulkPort = _lastSaveReply[2];

        EconetMessage data = new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = bulkPort,
            Control = 0x80,
            Sequence = 2,
            Payload = Encoding.ASCII.GetBytes("hello"),
            ClientAddress = IPAddress.Loopback,
            ClientPort = 50000,
            ClientNet = 1,
            ClientStation = 1,
        };
        EconetMessage? final = new BulkTransferHandler(_harness.Context).HandleData(data);

        Assert.That(final, Is.Not.Null);
        Assert.That(OwnerOf(UserDirectory + "/DATA"), Is.EqualTo(LoggedInUserIndex()));
        AcornAttributeStore.TryReadAttributes(_harness.FileSystem, UserDirectory + "/DATA", out _, out _, out int length, out _);
        Assert.That(length, Is.EqualTo(5));
    }

    [Test]
    public void Save_OverAnExistingFile_TheSaverBecomesTheOwner()
    {
        // fs.c 5873 writes the user number of the saver whether or not the file was there.
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        SaveRequest("HELLO", 0);

        Assert.That(OwnerOf(HelloPath), Is.EqualTo(LoggedInUserIndex()));
    }

    [Test]
    public void Cdir_MakesTheUserTheOwnerOfTheNewDirectory()
    {
        _harness.LoginAsUser();

        _harness.HandleOsCli("CDIR NEWDIR\r");

        Assert.That(OwnerOf(UserDirectory + "/NEWDIR"), Is.EqualTo(LoggedInUserIndex()));
    }

    [Test]
    public void CreateDirCall_MakesTheUserTheOwnerOfTheNewDirectory()
    {
        _harness.LoginAsUser();

        byte[] reply = _harness.Send(FileServerOpcodes.CreateDir, [0, (byte)'N', (byte)'E', (byte)'W', 13]).Payload;

        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(OwnerOf(UserDirectory + "/NEW"), Is.EqualTo(LoggedInUserIndex()));
    }

    [Test]
    public void CreateDirCall_DirectoryThatWasThere_KeepsItsOwner()
    {
        _harness.LoginAsUser();
        _harness.FileSystem.AddDirectory(UserDirectory + "/OLD");
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory + "/OLD", OtherUser, 0);

        _harness.Send(FileServerOpcodes.CreateDir, [0, (byte)'O', (byte)'L', (byte)'D', 13]);

        Assert.That(OwnerOf(UserDirectory + "/OLD"), Is.EqualTo(OtherUser));
    }

    [Test]
    public void Copy_MakesTheUserTheOwnerOfTheCopy()
    {
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        _harness.HandleOsCli("COPY HELLO HELLO2\r");

        Assert.That(OwnerOf(UserDirectory + "/HELLO2"), Is.EqualTo(LoggedInUserIndex()));
    }

    [Test]
    public void Rename_TheOwnerMovesWithTheObject()
    {
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        _harness.HandleOsCli("RENAME HELLO MOVED\r");

        Assert.That(OwnerOf(UserDirectory + "/MOVED"), Is.EqualTo(OtherUser));
        Assert.That(OwnerOf(HelloPath), Is.EqualTo(0), "nothing is left behind under the old name");
    }

    [Test]
    public void Delete_TheOwnerIsNotGivenToANewObjectOfTheSameName()
    {
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        byte[] reply = _harness.Send(FileServerOpcodes.Delete, Encoding.ASCII.GetBytes("HELLO\r")).Payload;

        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(_harness.FileSystem.FileExists(XatSidecar.GetXatPath(HelloPath)), Is.False);
        _harness.FileSystem.AddFile(HelloPath, Encoding.ASCII.GetBytes("new"));
        Assert.That(OwnerOf(HelloPath), Is.EqualTo(0));
    }

    [Test]
    public void AccessCommand_KeepsTheOwner()
    {
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        _harness.HandleOsCli("ACCESS HELLO WR/R\r");

        Assert.That(OwnerOf(HelloPath), Is.EqualTo(OtherUser));
    }

    // ---------------------------------------------------------------------------------------------
    // Catalogue lists do not show the sidecar files.
    // ---------------------------------------------------------------------------------------------

    private List<string> ExamineTextEntries()
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

    private string ExamineTextLine(string name)
    {
        List<string> entries = ExamineTextEntries();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].StartsWith(name + " ", StringComparison.Ordinal))
                return entries[i];
        }

        Assert.Fail("no catalogue entry for " + name + " in: " + string.Join(" | ", entries));
        return string.Empty;
    }

    [Test]
    public void Examine_SidecarFilesAreNotListed()
    {
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);
        _harness.HandleOsCli("CDIR NEWDIR\r");

        List<string> entries = ExamineTextEntries();

        Assert.That(_harness.FileSystem.FileExists(XatSidecar.GetXatPath(HelloPath)), Is.True, "the .xat file is there to be hidden");
        for (int i = 0; i < entries.Count; i++)
            Assert.That(entries[i], Does.Not.Contain(".xat"), entries[i]);
        Assert.That(ExamineTextLine("HELLO"), Is.Not.Empty);
        Assert.That(ExamineTextLine("NEWDIR"), Is.Not.Empty);
    }

    [Test]
    public void Examine_NameEndingInCommaInfWithNoObjectBehindIt_IsAnOrdinaryFile()
    {
        _harness.LoginAsUser();
        _harness.FileSystem.AddFile(UserDirectory + "/LONELY,inf", Encoding.ASCII.GetBytes("x"));

        Assert.That(ExamineTextLine("LONELY,inf"), Is.Not.Empty);
    }

    [Test]
    public void Examine_CommaInfFileBesideAnObject_IsListedAndNotReadAsAttributes()
    {
        _harness.LoginAsUser();
        _harness.FileSystem.AddFile(HelloPath, Encoding.ASCII.GetBytes("0123456789"));
        byte[] text = Encoding.ASCII.GetBytes("LOAD=00001900\nEXEC=00008023\nACCESS=13\nOWNER=0042\n");
        _harness.FileSystem.AddFile(HelloPath + ",inf", text);

        Assert.That(ExamineTextLine("HELLO,inf"), Is.Not.Empty, "an ordinary file, listed");
        Assert.That(_harness.FileSystem.FileExists(HelloPath + ",inf"), Is.True, "and not removed");
        AcornAttributeStore.TryReadAttributes(_harness.FileSystem, HelloPath, out uint load, out uint exec, out _, out byte access);
        Assert.That(load, Is.EqualTo(0u));
        Assert.That(exec, Is.EqualTo(0u));
        Assert.That(access, Is.EqualTo(AcornAttributeStore.DefaultAccess));
        Assert.That(OwnerOf(HelloPath), Is.EqualTo(0));
    }

    [Test]
    public void Examine_DamagedXatFileMovedAside_IsNotListed()
    {
        _harness.LoginAsUser();
        _harness.FileSystem.AddFile(HelloPath, Encoding.ASCII.GetBytes("0123456789"));
        _harness.FileSystem.AddFile(HelloPath + ".xat.bad", Encoding.ASCII.GetBytes("damaged"));

        List<string> entries = ExamineTextEntries();

        for (int i = 0; i < entries.Count; i++)
            Assert.That(entries[i], Does.Not.Contain(".bad"), entries[i]);
        Assert.That(ExamineTextLine("HELLO"), Is.Not.Empty);
    }

    // ---------------------------------------------------------------------------------------------
    // MdfsInfo 0x20. fs.c 5170 (is_owner) and 5180 to 5187 (letters of EXAMINE argument 3).
    // ---------------------------------------------------------------------------------------------

    [Test]
    public void Examine_MdfsInfoClear_AllLettersAreUpperCase()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions);
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        Assert.That(ExamineTextLine("HELLO"), Is.EqualTo("HELLO        WR/R "));
    }

    [Test]
    public void Examine_MdfsInfoSet_ViewerIsNotTheOwner_OwnerLettersAreLowerCase()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagMdfsInfo);
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        Assert.That(ExamineTextLine("HELLO"), Is.EqualTo("HELLO        wr/R "));
    }

    [Test]
    public void Examine_MdfsInfoSet_ViewerIsTheOwner_OtherUserLettersAreLowerCase()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagMdfsInfo);
        _harness.LoginAsUser();
        SeedHello(0x13, LoggedInUserIndex());

        Assert.That(ExamineTextLine("HELLO"), Is.EqualTo("HELLO        WR/r "));
    }

    [Test]
    public void Examine_MdfsInfoSet_SystemUserCountsAsTheOwner()
    {
        // fs.c 5170 uses FS_PERM_EFFOWNER, so a system user is the owner of every object.
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagMdfsInfo);
        _harness.LoginAsSyst();
        SeedHello(0x13, OtherUser);

        Assert.That(ExamineTextLine("HELLO"), Is.EqualTo("HELLO        WR/r "));
    }

    [Test]
    public void Examine_MdfsInfoSet_OtherWriteLettersFollowTheSameRule()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagMdfsInfo);
        _harness.LoginAsUser();
        SeedHello(0x33, LoggedInUserIndex());
        Assert.That(ExamineTextLine("HELLO"), Is.EqualTo("HELLO        WR/wr"));

        SeedHello(0x33, OtherUser);
        Assert.That(ExamineTextLine("HELLO"), Is.EqualTo("HELLO        wr/WR"));
    }

    [Test]
    public void Examine_MdfsInfoSet_LockedLetterStaysUpperCase()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagMdfsInfo);
        _harness.LoginAsUser();
        SeedHello(0x17, OtherUser);

        Assert.That(ExamineTextLine("HELLO"), Is.EqualTo("HELLO       Lwr/R "));
    }

    // ---------------------------------------------------------------------------------------------
    // MdfsFunctions 0x02, owner bytes of the binary catalogue entry. fs.c 5089 to 5094.
    // ---------------------------------------------------------------------------------------------

    // Returns the 27 bytes of the binary catalogue entry of a name.
    private byte[] BinaryEntry(string name)
    {
        byte[] reply = _harness.Send(FileServerOpcodes.Examine, [0, 0, 255, 13]).Payload;
        int count = reply[2];
        string padded = name.PadRight(10);
        for (int i = 0; i < count; i++)
        {
            int offset = 4 + i * 27;
            if (Encoding.ASCII.GetString(reply, offset, 10) == padded)
            {
                byte[] entry = new byte[27];
                Array.Copy(reply, offset, entry, 0, 27);
                return entry;
            }
        }

        Assert.Fail("no binary catalogue entry for " + name);
        return [];
    }

    [Test]
    public void Examine_Binary_MdfsFunctionsSet_EntryCarriesTheOwnerBytes()
    {
        // fs.c 5091 to 5093: owner low byte, bits 8 to 10 shifted down by 3, then the auxiliary account 0.
        SetFlags(FileServerConfiguration.FlagMdfsFunctions);
        _harness.LoginAsUser();
        SeedHello(0x13, 0x0523);

        byte[] entry = BinaryEntry("HELLO");

        Assert.That(entry[21], Is.EqualTo(0x23));
        Assert.That(entry[22], Is.EqualTo(0xA0));
        Assert.That(entry[23], Is.EqualTo(0x00));
    }

    [Test]
    public void Examine_Binary_MdfsFunctionsSet_DirectoryEntryCarriesItsOwner()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions);
        _harness.LoginAsUser();
        _harness.FileSystem.AddDirectory(UserDirectory + "/SUB");
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory + "/SUB", 0x0009, 0);

        byte[] entry = BinaryEntry("SUB");

        Assert.That(entry[21], Is.EqualTo(0x09));
    }

    [Test]
    public void Examine_Binary_MdfsFunctionsClear_OwnerBytesAreNotWritten()
    {
        // With the flag clear PiFS puts the internal number there (line 5097 onward), which this server has not, so 0.
        SetFlags(0x00);
        _harness.LoginAsUser();
        SeedHello(0x13, 0x0523);

        byte[] entry = BinaryEntry("HELLO");

        Assert.That(entry[21], Is.EqualTo(0x00));
        Assert.That(entry[22], Is.EqualTo(0x00));
        Assert.That(entry[23], Is.EqualTo(0x00));
    }

    // ---------------------------------------------------------------------------------------------
    // AcornHome 0x01. fs.c 4677 to 4685 (login), 2781 to 2800, 2928 to 2932 and 3061 to 3065 (owner), 10715 to 10751 (*SETHOME).
    // ---------------------------------------------------------------------------------------------

    private ushort HomeOwnerOf(string unixPath)
    {
        AcornAttributeStore.ReadOwnership(_harness.FileSystem, unixPath, out _, out ushort homeOf);
        return homeOf;
    }

    [Test]
    public void Login_AcornHomeSet_HomeDirectoryGetsTheUserAsHomeOwner()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornHome);

        _harness.LoginAsUser();

        Assert.That(HomeOwnerOf(UserDirectory), Is.EqualTo(LoggedInUserIndex()));
        Assert.That(LoggedInUserIndex(), Is.Not.EqualTo(0));
    }

    [Test]
    public void Login_AcornHomeClear_HomeDirectoryGetsNoHomeOwner()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions);

        _harness.LoginAsUser();

        Assert.That(HomeOwnerOf(UserDirectory), Is.EqualTo(0));
    }

    [Test]
    public void Login_AcornHomeSet_ExistingHomeOwnerIsKept()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornHome);
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory, 0x0007, OtherUser);

        _harness.LoginAsUser();

        Assert.That(HomeOwnerOf(UserDirectory), Is.EqualTo(OtherUser));
        Assert.That(OwnerOf(UserDirectory), Is.EqualTo(0x0007), "the owner of the directory is not touched");
    }

    [Test]
    public void GetOwner_AcornHomeSet_ObjectBelowAHomeDirectoryIsOwnedByTheHomeUser()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornHome);
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory, 0, 0x0077);
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, HelloPath, 0x1111, 0);

        Assert.That(ObjectOwnership.GetOwner(_harness.Context, HelloPath), Is.EqualTo(0x0077));
        Assert.That(ObjectOwnership.GetOwner(_harness.Context, UserDirectory), Is.EqualTo(0x0077), "the home directory itself too");
    }

    [Test]
    public void GetOwner_AcornHomeClear_TheStoredOwnerStands()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions);
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory, 0, 0x0077);
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, HelloPath, 0x1111, 0);

        Assert.That(ObjectOwnership.GetOwner(_harness.Context, HelloPath), Is.EqualTo(0x1111));
    }

    [Test]
    public void GetOwner_AcornHomeSet_DeepObjectAndObjectOutsideAnyHome()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornHome);
        _harness.FileSystem.AddDirectory(UserDirectory + "/A");
        _harness.FileSystem.AddDirectory(UserDirectory + "/A/B");
        _harness.FileSystem.AddFile(UserDirectory + "/A/B/DEEP", Encoding.ASCII.GetBytes("x"));
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory + "/A", 0, 0x0055);
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory + "/A/B/DEEP", 0x2222, 0);

        Assert.That(ObjectOwnership.GetOwner(_harness.Context, UserDirectory + "/A/B/DEEP"), Is.EqualTo(0x0055));
        Assert.That(ObjectOwnership.GetOwner(_harness.Context, HelloPath), Is.EqualTo(0), "HELLO is not below A and USER has no home owner");
    }

    [Test]
    public void GetOwner_AcornHomeSet_TheHomeDirectoryNearestTheRootWins()
    {
        // fs.c 2781 and 3061: homeof_found is set by the first directory with a home owner on the way down and is not replaced.
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornHome);
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, DiscRoot, 0, 0x0011);
        AcornAttributeStore.WriteOwnership(_harness.FileSystem, UserDirectory, 0, 0x0022);

        Assert.That(ObjectOwnership.GetOwner(_harness.Context, HelloPath), Is.EqualTo(0x0011));
    }

    [Test]
    public void GetObjectInfo_Cmd4_AcornHomeSet_UserIsOwnerOfEverythingInTheirHome()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagAcornHome);
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        Assert.That(OwnerByteOfHello(), Is.EqualTo(0x00), "the home of USER was marked at login");
    }

    [Test]
    public void GetObjectInfo_Cmd4_AcornHomeClear_OwnerStaysTheStoredOwner()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions);
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        Assert.That(OwnerByteOfHello(), Is.EqualTo(0xFF));
    }

    [Test]
    public void Examine_MdfsInfoSet_AcornHomeSet_LettersFollowTheHomeOwner()
    {
        SetFlags(FileServerConfiguration.FlagMdfsFunctions | FileServerConfiguration.FlagMdfsInfo | FileServerConfiguration.FlagAcornHome);
        _harness.LoginAsUser();
        SeedHello(0x13, OtherUser);

        Assert.That(ExamineTextLine("HELLO"), Is.EqualTo("HELLO        WR/r "));
    }

    [Test]
    public void SetHome_MarksTheNewHomeEvenWhenAcornHomeIsClear()
    {
        // fs.c 10747 to 10752 do not look at fs_acorn_home.
        SetFlags(FileServerConfiguration.FlagMdfsFunctions);
        _harness.LoginAsSyst();
        _harness.FileSystem.AddDirectory(UserDirectory + "/HOME1");

        byte[] reply = _harness.HandleOsCli("SETHOME USER HOME1\r").Payload;

        Assert.That(reply[1], Is.EqualTo(0));
        Assert.That(HomeOwnerOf(UserDirectory + "/HOME1"), Is.EqualTo(UserIndexOf("USER")));
    }

    [Test]
    public void SetHome_TakesTheMarkOffTheOldHome()
    {
        // fs.c 10715 to 10724.
        _harness.LoginAsSyst();
        _harness.FileSystem.AddDirectory(UserDirectory + "/HOME1");
        _harness.FileSystem.AddDirectory(UserDirectory + "/HOME2");
        _harness.HandleOsCli("SETHOME USER HOME1\r");

        _harness.HandleOsCli("SETHOME USER HOME2\r");

        Assert.That(HomeOwnerOf(UserDirectory + "/HOME1"), Is.EqualTo(0));
        Assert.That(HomeOwnerOf(UserDirectory + "/HOME2"), Is.EqualTo(UserIndexOf("USER")));
    }

    [Test]
    public void SetHome_Percent_TakesTheMarkOffTheOldHome()
    {
        // fs.c 10715 to 10724 run before the "%" case at line 10726.
        _harness.LoginAsSyst();
        _harness.FileSystem.AddDirectory(UserDirectory + "/HOME1");
        _harness.HandleOsCli("SETHOME USER HOME1\r");

        _harness.HandleOsCli("SETHOME USER %\r");

        Assert.That(HomeOwnerOf(UserDirectory + "/HOME1"), Is.EqualTo(0));
    }

    [Test]
    public void SetHome_DiscRoot_IsNotMarked()
    {
        // fs.c 10745 to 10746: "Don't set it on root!".
        _harness.LoginAsSyst();

        _harness.HandleOsCli("SETHOME USER $\r");

        Assert.That(HomeOwnerOf(DiscRoot), Is.EqualTo(0));
    }

    [Test]
    public void SetHome_DirectoryThatDoesNotExist_MarksNothing()
    {
        _harness.LoginAsSyst();

        byte[] reply = _harness.HandleOsCli("SETHOME USER NOSUCH\r").Payload;

        Assert.That(reply[1], Is.Not.EqualTo(0));
        Assert.That(_harness.FileSystem.FileExists(XatSidecar.GetXatPath(UserDirectory + "/NOSUCH")), Is.False);
    }
}
