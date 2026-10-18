//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using NUnit.Framework;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Tests that delete and rename on the Econet file server go through the file system backend, so they
/// work on any backend and not only on the local disk.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EconetDeleteRenameBackendTests
{
    private const string UserDirectory = "/econet-root/0RetroFS/USER";
    private EconetFsTestHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        _harness = new EconetFsTestHarness();
        _harness.LoginAsSyst();
    }

    private string InUser(string name) => _harness.FileSystem.CombinePath(UserDirectory, name);

    private EconetMessage Delete(string acornName) =>
        _harness.Send(FileServerOpcodes.Delete, Encoding.ASCII.GetBytes(acornName + "\r"));

    [Test]
    public void Delete_OfAFile_RemovesItFromTheBackend()
    {
        // Regression: the file was deleted with System.IO.File.Delete on a backend path. With any backend
        // that is not the local disk that deletes nothing, yet the reply said success.
        _harness.FileSystem.AddFile(InUser("TESTDEL"), new byte[] { 1, 2, 3 });

        EconetMessage reply = Delete("TESTDEL");

        Assert.That(reply.Payload[1], Is.EqualTo(0), "success");
        Assert.That(_harness.FileSystem.FileExists(InUser("TESTDEL")), Is.False, "the backend must no longer have the file");
    }

    [Test]
    public void Delete_OfAnEmptyDirectory_RemovesItFromTheBackend()
    {
        _harness.FileSystem.AddDirectory(InUser("TESTDIR"));

        EconetMessage reply = Delete("TESTDIR");

        Assert.That(reply.Payload[1], Is.EqualTo(0), "success");
        Assert.That(_harness.FileSystem.DirectoryExists(InUser("TESTDIR")), Is.False);
    }

    [Test]
    public void Delete_OfAFileThatIsNotThere_AnswersNotFound()
    {
        EconetMessage reply = Delete("NOSUCH");

        Assert.That(reply.Payload[1], Is.EqualTo(FileServerErrors.NotFound));
    }

    [Test]
    public void Rename_OfAFile_MovesItInTheBackend()
    {
        _harness.FileSystem.AddFile(InUser("OLDNAME"), new byte[] { 9, 8, 7 });

        _harness.HandleOsCli("RENAME OLDNAME NEWNAME\r");

        Assert.That(_harness.FileSystem.FileExists(InUser("NEWNAME")), Is.True, "the new name must exist in the backend");
        Assert.That(_harness.FileSystem.FileExists(InUser("OLDNAME")), Is.False, "the old name must be gone");
    }

    [Test]
    public void Rename_OfADirectory_MovesItInTheBackend()
    {
        _harness.FileSystem.AddDirectory(InUser("OLDDIR"));

        _harness.HandleOsCli("RENAME OLDDIR NEWDIR\r");

        Assert.That(_harness.FileSystem.DirectoryExists(InUser("NEWDIR")), Is.True);
        Assert.That(_harness.FileSystem.DirectoryExists(InUser("OLDDIR")), Is.False);
    }
}
