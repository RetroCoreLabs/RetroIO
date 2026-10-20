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
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Protocols.Tnfs.Server.Transport;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Session limits, TCP keepalive, the open flag values and long directory entry names. The numbers come from the
/// reference daemon tnfsd (config.h and tnfs_file.h) and from the TNFS protocol text (open flag table).
/// </summary>
[TestFixture]
[Category("TNFS")]
public class TnfsSessionFlagsAndNamesTests
{
    private MockFileSystemBackend _mockFS = null!;

    [SetUp]
    public void Setup()
    {
        _mockFS = new MockFileSystemBackend();
        _mockFS.AddDirectory("/tmp/tnfs");
        _mockFS.AddDirectory("/tmp/tnfs/test");
        _mockFS.AddFile("/tmp/tnfs/big.bin", new byte[100]);
    }

    // ---- Row 1: session limits -------------------------------------------------------------------------------

    /// <summary>
    /// config.h MAX_SESSIONS and MAX_SESSIONS_PER_IP are both 4096.
    /// </summary>
    [Test]
    public void SessionConstants_MatchConfigH()
    {
        Assert.That(TnfsConstants.MAX_SESSIONS, Is.EqualTo(4096), "config.h MAX_SESSIONS");
        Assert.That(TnfsConstants.MAX_SESSIONS_PER_IP, Is.EqualTo(4096), "config.h MAX_SESSIONS_PER_IP");
    }

    /// <summary>
    /// A manager built with no limits given reads both limits from the constants.
    /// </summary>
    [Test]
    public void SessionManager_DefaultLimits_AreTheConstants()
    {
        using SessionManager manager = new SessionManager(NullLogger<SessionManager>.Instance);

        Assert.That(manager.MaxSessions, Is.EqualTo(TnfsConstants.MAX_SESSIONS));
        Assert.That(manager.MaxSessionsPerIP, Is.EqualTo(TnfsConstants.MAX_SESSIONS_PER_IP));
    }

    /// <summary>
    /// With the total limit reached, a session from a new address is refused, and a slot given back is reused.
    /// </summary>
    [Test]
    public void SessionManager_TotalLimitReached_RefusesTheNextSession_UntilOneIsRemoved()
    {
        using SessionManager manager = new SessionManager(NullLogger<SessionManager>.Instance, null, 100, 3);
        TnfsSession a = manager.CreateSession(IPAddress.Parse("10.0.0.1"), "/tmp/tnfs");
        manager.CreateSession(IPAddress.Parse("10.0.0.2"), "/tmp/tnfs");
        manager.CreateSession(IPAddress.Parse("10.0.0.3"), "/tmp/tnfs");

        Assert.Throws<TnfsSessionLimitException>(() => manager.CreateSession(IPAddress.Parse("10.0.0.4"), "/tmp/tnfs"));
        Assert.That(manager.SessionCount, Is.EqualTo(3), "a refused session leaves no trace");

        manager.RemoveSession(a.SessionId);
        TnfsSession again = manager.CreateSession(IPAddress.Parse("10.0.0.4"), "/tmp/tnfs");
        Assert.That(again, Is.Not.Null);
        Assert.That(manager.SessionCount, Is.EqualTo(3));
    }

    /// <summary>
    /// At the per-address limit the oldest session of that address is recycled (as tnfsd does), so the count stays
    /// at the limit and the oldest ID is gone.
    /// </summary>
    [Test]
    public void SessionManager_PerIpLimitReached_RecyclesTheOldestSessionOfThatAddress()
    {
        using SessionManager manager = new SessionManager(NullLogger<SessionManager>.Instance, null, 2, 100);
        IPAddress ip = IPAddress.Parse("10.0.0.1");
        TnfsSession first = manager.CreateSession(ip, "/tmp/tnfs");
        first.LastContact = DateTime.UtcNow.AddMinutes(-5);
        TnfsSession second = manager.CreateSession(ip, "/tmp/tnfs");

        TnfsSession third = manager.CreateSession(ip, "/tmp/tnfs");

        Assert.That(manager.SessionCount, Is.EqualTo(2));
        Assert.That(manager.FindSession(first.SessionId), Is.Null, "the oldest was recycled");
        Assert.That(manager.FindSession(second.SessionId), Is.Not.Null);
        Assert.That(manager.FindSession(third.SessionId), Is.Not.Null);
    }

    /// <summary>
    /// A MOUNT that cannot get a session because the table is full is answered with ENFILE, and does not throw.
    /// </summary>
    [Test]
    public void Mount_WhenTheSessionTableIsFull_AnswersEnfile()
    {
        using SessionManager manager = new SessionManager(NullLogger<SessionManager>.Instance, null, 100, 1);
        SessionCommandHandler handler = new SessionCommandHandler(manager, NullLogger<SessionCommandHandler>.Instance, "/tmp/tnfs", _mockFS);
        manager.CreateSession(IPAddress.Parse("10.0.0.9"), "/tmp/tnfs");

        List<byte> body = new List<byte> { 0x02, 0x01, (byte)'/', 0x00 };
        TnfsMessage reply = handler.HandleMount(Build(0, TnfsConstants.TNFS_MOUNT, body.ToArray()));

        Assert.That((TnfsReturnCode)reply.Header.Status, Is.EqualTo(TnfsReturnCode.ENFILE));
    }

    /// <summary>
    /// The limits set in the options reach the session manager that AddTnfsServer registers, and the defaults are
    /// the constants.
    /// </summary>
    [Test]
    public void AddTnfsServer_SessionLimits_ReachTheSessionManager()
    {
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFileSystemBackend>(_mockFS);
        services.AddTnfsServer(o =>
        {
            o.EnableUdp = false;
            o.EnableTcp = false;
            o.MaxSessions = 11;
            o.MaxSessionsPerIP = 5;
        });
        using ServiceProvider provider = services.BuildServiceProvider();
        SessionManager manager = provider.GetRequiredService<SessionManager>();
        Assert.That(manager.MaxSessions, Is.EqualTo(11));
        Assert.That(manager.MaxSessionsPerIP, Is.EqualTo(5));

        Assert.That(new TnfsServerOptions().MaxSessions, Is.EqualTo(TnfsConstants.MAX_SESSIONS));
        Assert.That(new TnfsServerOptions().MaxSessionsPerIP, Is.EqualTo(TnfsConstants.MAX_SESSIONS_PER_IP));
    }

    // ---- Row 2: TCP keepalive --------------------------------------------------------------------------------

    /// <summary>
    /// An accepted TCP socket carries the config.h keepalive settings: idle 30, interval 1, count 60.
    /// </summary>
    [Test]
    public async Task TcpTransport_AcceptedSocket_HasTheKeepAliveSettingsFromConfigH()
    {
        int port = GetFreePort();
        using TcpTransport transport = new TcpTransport(port, NullLogger<TcpTransport>.Instance);
        using CancellationTokenSource cts = new CancellationTokenSource();
        Task run = transport.RunAsync(cts.Token);

        using TcpClient client = await ConnectAsync(port);

        // The first accepted connection gets ID 1.
        int idle = 0, interval = 0, count = 0;
        bool read = false;
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!read && DateTime.UtcNow < deadline)
        {
            read = transport.TryGetKeepAlive(1, out idle, out interval, out count);
            if (!read)
                await Task.Delay(50);
        }

        Assert.That(read, Is.True, "the connection must be known to the transport");
        Assert.That(idle, Is.EqualTo(TnfsConstants.TCP_KA_IDLE), "idle seconds");
        Assert.That(interval, Is.EqualTo(TnfsConstants.TCP_KA_INTVL), "probe interval seconds");
        Assert.That(count, Is.EqualTo(TnfsConstants.TCP_KA_COUNT), "probe count");

        cts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- Row 3: open flags -----------------------------------------------------------------------------------

    /// <summary>
    /// The access mode bits follow the protocol table: 1 read, 2 write, 3 read and write. Zero (no access bits) is
    /// read, as tnfsd passes it to open(2) as O_RDONLY.
    /// </summary>
    /// <param name="flags">
    /// The open flags sent.
    /// </param>
    /// <param name="expected">
    /// The access the server must open the file with.
    /// </param>
    [TestCase((ushort)0x0001, FileAccess.Read)]
    [TestCase((ushort)0x0002, FileAccess.Write)]
    [TestCase((ushort)0x0003, FileAccess.ReadWrite)]
    [TestCase((ushort)0x0000, FileAccess.Read)]
    [TestCase((ushort)0x0101, FileAccess.Read)]
    [TestCase((ushort)0x0302, FileAccess.Write)]
    public void ConvertToFileAccess_FollowsTheProtocolTable(ushort flags, FileAccess expected)
    {
        Assert.That(FileCommandHandler.ConvertToFileAccess(flags), Is.EqualTo(expected));
    }

    /// <summary>
    /// The create flags follow the protocol table: CREAT 0x0100, TRUNC 0x0200, EXCL 0x0400.
    /// </summary>
    /// <param name="flags">
    /// The open flags sent.
    /// </param>
    /// <param name="expected">
    /// The mode the server must open the file with.
    /// </param>
    [TestCase((ushort)0x0002, FileMode.Open)]
    [TestCase((ushort)0x0102, FileMode.OpenOrCreate)]
    [TestCase((ushort)0x0302, FileMode.Create)]
    [TestCase((ushort)0x0502, FileMode.CreateNew)]
    [TestCase((ushort)0x0702, FileMode.CreateNew)]
    [TestCase((ushort)0x0202, FileMode.Truncate)]
    public void ConvertToFileMode_FollowsTheProtocolTable(ushort flags, FileMode expected)
    {
        Assert.That(FileCommandHandler.ConvertToFileMode(flags), Is.EqualTo(expected));
    }

    /// <summary>
    /// The enum the client uses has the protocol values, the same as TnfsConstants.
    /// </summary>
    [Test]
    public void TnfsOpenFlags_HaveTheProtocolValues()
    {
        Assert.That((ushort)TnfsOpenFlags.O_RDONLY, Is.EqualTo(TnfsConstants.TNFS_O_RDONLY));
        Assert.That((ushort)TnfsOpenFlags.O_WRONLY, Is.EqualTo(TnfsConstants.TNFS_O_WRONLY));
        Assert.That((ushort)TnfsOpenFlags.O_RDWR, Is.EqualTo(TnfsConstants.TNFS_O_RDWR));
        Assert.That((ushort)TnfsOpenFlags.O_APPEND, Is.EqualTo(TnfsConstants.TNFS_O_APPEND));
        Assert.That((ushort)TnfsOpenFlags.O_CREAT, Is.EqualTo(TnfsConstants.TNFS_O_CREAT));
        Assert.That((ushort)TnfsOpenFlags.O_TRUNC, Is.EqualTo(TnfsConstants.TNFS_O_TRUNC));
        Assert.That((ushort)TnfsOpenFlags.O_EXCL, Is.EqualTo(TnfsConstants.TNFS_O_EXCL));
    }

    /// <summary>
    /// A file opened with the TNFS_O_RDONLY constant can be read; before the fix the open succeeded as write only
    /// and the read failed with EIO.
    /// </summary>
    [Test]
    public void FileOpenedWithTheReadOnlyConstant_CanBeRead()
    {
        using SessionManager manager = new SessionManager(NullLogger<SessionManager>.Instance);
        FileCommandHandler files = new FileCommandHandler(manager, NullLogger<FileCommandHandler>.Instance, _mockFS);
        TnfsSession session = manager.CreateSession(IPAddress.Loopback, "/tmp/tnfs");

        List<byte> body = new List<byte>();
        body.AddRange(BitConverter.GetBytes(TnfsConstants.TNFS_O_RDONLY));
        body.AddRange(BitConverter.GetBytes((ushort)0));
        body.AddRange(Encoding.ASCII.GetBytes("/big.bin"));
        body.Add(0x00);
        TnfsMessage open = files.HandleOpen(Build(session.SessionId, TnfsConstants.TNFS_OPEN, body.ToArray()));
        Assert.That((TnfsReturnCode)open.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS), "open");
        byte fd = open.Data.Span[0];

        List<byte> readBody = new List<byte> { fd };
        readBody.AddRange(BitConverter.GetBytes((ushort)50));
        TnfsMessage read = files.HandleRead(Build(session.SessionId, TnfsConstants.TNFS_READ, readBody.ToArray()));

        Assert.That((TnfsReturnCode)read.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS), "read");
        Assert.That(read.Data.Span[0] | (read.Data.Span[1] << 8), Is.EqualTo(50), "bytes read");
    }

    // ---- Row 4: long directory entry names -------------------------------------------------------------------

    /// <summary>
    /// READDIR returns a name of 255, 256 or 300 characters whole, followed by one terminator byte. Before the fix
    /// the name plus terminator did not fit the 256 byte buffer and the reply was EIO.
    /// </summary>
    /// <param name="length">
    /// The length of the entry name.
    /// </param>
    [TestCase(255)]
    [TestCase(256)]
    [TestCase(300)]
    public void ReadDir_LongName_IsReturnedWhole(int length)
    {
        string name = new string('n', length);
        _mockFS.AddFile("/tmp/tnfs/test/" + name, new byte[1]);
        using SessionManager manager = new SessionManager(NullLogger<SessionManager>.Instance);
        DirectoryCommandHandler dirs = new DirectoryCommandHandler(manager, NullLogger<DirectoryCommandHandler>.Instance, _mockFS);
        TnfsSession session = manager.CreateSession(IPAddress.Loopback, "/tmp/tnfs");

        List<byte> openBody = new List<byte>(Encoding.ASCII.GetBytes("/test"));
        openBody.Add(0x00);
        TnfsMessage open = dirs.HandleOpenDir(Build(session.SessionId, TnfsConstants.TNFS_OPENDIR, openBody.ToArray()));
        Assert.That((TnfsReturnCode)open.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS), "opendir");

        TnfsMessage read = dirs.HandleReadDir(Build(session.SessionId, TnfsConstants.TNFS_READDIR, new byte[] { open.Data.Span[0] }));

        Assert.That((TnfsReturnCode)read.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS), "readdir");
        Assert.That(read.Data.Length, Is.EqualTo(length + 1), "name plus terminator");
        Assert.That(Encoding.ASCII.GetString(read.Data.Span.Slice(0, length)), Is.EqualTo(name));
        Assert.That(read.Data.Span[length], Is.EqualTo(0), "terminator");
    }

    /// <summary>
    /// READDIRX returns a name of 300 characters whole too: the reply is 13 fixed bytes, the name and a terminator.
    /// </summary>
    [Test]
    public void ReadDirX_LongName_IsReturnedWhole()
    {
        string name = new string('x', 300);
        _mockFS.AddFile("/tmp/tnfs/test/" + name, new byte[1]);
        using SessionManager manager = new SessionManager(NullLogger<SessionManager>.Instance);
        DirectoryCommandHandler dirs = new DirectoryCommandHandler(manager, NullLogger<DirectoryCommandHandler>.Instance, _mockFS);
        TnfsSession session = manager.CreateSession(IPAddress.Loopback, "/tmp/tnfs");

        List<byte> openBody = new List<byte>(Encoding.ASCII.GetBytes("/test"));
        openBody.Add(0x00);
        TnfsMessage open = dirs.HandleOpenDir(Build(session.SessionId, TnfsConstants.TNFS_OPENDIR, openBody.ToArray()));
        Assert.That((TnfsReturnCode)open.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS), "opendir");

        TnfsMessage read = dirs.HandleReadDirX(Build(session.SessionId, TnfsConstants.TNFS_READDIRX, new byte[] { open.Data.Span[0] }));

        Assert.That((TnfsReturnCode)read.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS), "readdirx");
        Assert.That(read.Data.Length, Is.EqualTo(13 + 300 + 1));
        Assert.That(Encoding.ASCII.GetString(read.Data.Span.Slice(13, 300)), Is.EqualTo(name));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static TnfsMessage Build(ushort sessionId, byte command, byte[] body)
    {
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(sessionId));
        packet.Add(0x00);
        packet.Add(command);
        packet.AddRange(body);
        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 0);
    }

    private static int GetFreePort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        TcpClient c = new TcpClient();
        for (int i = 0; i < 100; i++)
        {
            try
            {
                await c.ConnectAsync(IPAddress.Loopback, port);
                return c;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
                c.Dispose();
                c = new TcpClient();
            }
        }
        throw new InvalidOperationException("could not connect to the transport under test");
    }
}
