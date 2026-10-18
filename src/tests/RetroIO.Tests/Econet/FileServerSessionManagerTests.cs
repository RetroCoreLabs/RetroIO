//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.IO;
using NUnit.Framework;
using RetroIO.Identity;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Handle numbers and session slot numbers handed out by <see cref="FileServerSessionManager"/>.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class FileServerSessionManagerTests
{
    private MockFileSystemBackend _fileSystem = null!;
    private FileServerSessionManager _sessions = null!;
    private FileServerHandleManager _handles = null!;

    [SetUp]
    public void SetUp()
    {
        _fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(_fileSystem);
        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out IRetroIDP _);
        DiscCatalog discs = new DiscCatalog(_fileSystem, "/econet-root", "RetroFS");
        _sessions = new FileServerSessionManager(_fileSystem, users, discs, "/econet-root");
        _handles = new FileServerHandleManager();
    }

    private FileServerSession Login(byte station, string user)
    {
        bool ok = _sessions.TryLogin(1, station, user, string.Empty, out FileServerSession? session, out _, out _);
        Assert.That(ok, Is.True, "login failed");
        return session!;
    }

    [Test]
    public void AllocateHandle_AfterWrapping_NeverReturnsAHandleThatIsStillOpen()
    {
        _fileSystem.AddFile("/econet-root/F", [1]);

        // Open 29 files: handles 4 to 32 are all in use.
        for (int i = 0; i < 29; i++)
        {
            byte handle = _handles.AllocateHandle(1, 1);
            FileStream stream = _fileSystem.OpenFile("/econet-root/F", FileMode.Open, FileAccess.Read);
            Assert.That(_handles.TryOpen(handle, "/econet-root/F", stream, true, 1, 1, out _), Is.True);
        }

        // The table is full: the next request must not hand out a handle that is in use.
        byte next = _handles.AllocateHandle(1, 1);

        Assert.That(next, Is.EqualTo(0), "no free handle: 0 is the 'none' answer");

        _handles.CloseAllForStation(1, 1);
    }

    [Test]
    public void AllocateHandle_TwoStations_EachGetTheirOwnHandleNumbers()
    {
        _fileSystem.AddFile("/econet-root/F", [1]);

        // Station 1 uses up all of 4 to 32; station 2 must still get a handle, starting at 4 too.
        for (int i = 0; i < 29; i++)
        {
            byte handle = _handles.AllocateHandle(1, 1);
            FileStream stream = _fileSystem.OpenFile("/econet-root/F", FileMode.Open, FileAccess.Read);
            Assert.That(_handles.TryOpen(handle, "/econet-root/F", stream, true, 1, 1, out _), Is.True);
        }

        Assert.That(_handles.AllocateHandle(1, 1), Is.EqualTo(0), "station 1 is full");
        Assert.That(_handles.AllocateHandle(1, 2), Is.EqualTo(4), "station 2 has its own table");

        _handles.CloseAllForStation(1, 1);
    }

    [Test]
    public void TryOpen_SameHandleNumberForTwoStations_BothSucceedAndStayApart()
    {
        _fileSystem.AddFile("/econet-root/F", [1]);
        FileStream a = _fileSystem.OpenFile("/econet-root/F", FileMode.Open, FileAccess.Read);
        FileStream b = _fileSystem.OpenFile("/econet-root/F", FileMode.Open, FileAccess.Read);

        Assert.That(_handles.TryOpen(4, "/econet-root/F", a, true, 1, 1, out _), Is.True);
        Assert.That(_handles.TryOpen(4, "/econet-root/F", b, true, 1, 2, out _), Is.True);

        Assert.That(_handles.Get(1, 1, 4)!.Stream, Is.SameAs(a));
        Assert.That(_handles.Get(1, 2, 4)!.Stream, Is.SameAs(b));
        Assert.That(_handles.Get(1, 3, 4), Is.Null, "a third station has no handle 4");

        _handles.CloseAllForStation(1, 1);
        _handles.CloseAllForStation(1, 2);
    }

    [Test]
    public void Close_OfAnotherStationsHandle_IsRefusedAndLeavesItOpen()
    {
        _fileSystem.AddFile("/econet-root/F", [1]);
        FileStream stream = _fileSystem.OpenFile("/econet-root/F", FileMode.Open, FileAccess.Read);
        Assert.That(_handles.TryOpen(4, "/econet-root/F", stream, true, 1, 1, out _), Is.True);

        Assert.That(_handles.Close(1, 2, 4), Is.False, "station 2 does not own station 1's handle 4");
        Assert.That(_handles.Get(1, 1, 4), Is.Not.Null);
        Assert.That(_handles.Close(1, 1, 4), Is.True);
    }

    [Test]
    public void AllocateHandle_SkipsHandlesThatAreOpen()
    {
        _fileSystem.AddFile("/econet-root/F", [1]);
        FileStream stream = _fileSystem.OpenFile("/econet-root/F", FileMode.Open, FileAccess.Read);
        Assert.That(_handles.TryOpen(5, "/econet-root/F", stream, true, 1, 1, out _), Is.True);

        Assert.That(_handles.AllocateHandle(1, 1), Is.EqualTo(4));
        Assert.That(_handles.AllocateHandle(1, 1), Is.EqualTo(6), "5 is open and is skipped");

        _handles.CloseAllForStation(1, 1);
    }

    [Test]
    public void TryGetSessionIndex_FirstSessionAndUnknownSession_AreToldApart()
    {
        FileServerSession first = Login(1, "SYST");
        FileServerSession second = Login(2, "USER");
        FileServerSession stranger = new FileServerSession();

        Assert.That(_sessions.TryGetSessionIndex(first, out ushort firstIndex), Is.True);
        Assert.That(firstIndex, Is.EqualTo(0));
        Assert.That(_sessions.TryGetSessionIndex(second, out ushort secondIndex), Is.True);
        Assert.That(secondIndex, Is.EqualTo(1));
        Assert.That(_sessions.TryGetSessionIndex(stranger, out _), Is.False);
    }
}
