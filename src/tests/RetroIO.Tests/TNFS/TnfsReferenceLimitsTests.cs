//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Protocols.Tnfs.Protocol;
using RetroIO.Protocols.Tnfs.Server;
using RetroIO.Protocols.Tnfs.Server.Handlers;
using RetroIO.Protocols.Tnfs.Server.Transport;

namespace RetroIO.Tests.TNFS;

/// <summary>
/// Behaviour that follows from the limits taken from the reference daemon tnfsd (config.h): the number of open
/// directories, the size of one READ reply, the size of the buffers, and the TCP connection limit.
/// </summary>
[TestFixture]
[Category("TNFS")]
public class TnfsReferenceLimitsTests
{
    private MockFileSystemBackend _mockFS = null!;
    private SessionManager _sessionManager = null!;
    private DirectoryCommandHandler _directoryHandler = null!;
    private FileCommandHandler _fileHandler = null!;
    private TnfsSession _session = null!;

    [SetUp]
    public void Setup()
    {
        _mockFS = new MockFileSystemBackend();
        _mockFS.AddDirectory("/tmp/tnfs");
        _mockFS.AddDirectory("/tmp/tnfs/test");
        _mockFS.AddFile("/tmp/tnfs/big.bin", new byte[4000]);

        _sessionManager = new SessionManager(NullLogger<SessionManager>.Instance, TimeSpan.FromMinutes(10), maxSessionsPerIP: 8);
        _directoryHandler = new DirectoryCommandHandler(_sessionManager, NullLogger<DirectoryCommandHandler>.Instance, _mockFS);
        _fileHandler = new FileCommandHandler(_sessionManager, NullLogger<FileCommandHandler>.Instance, _mockFS);
        _session = _sessionManager.CreateSession(IPAddress.Loopback, "/tmp/tnfs");
    }

    [TearDown]
    public void TearDown()
    {
        _sessionManager.Dispose();
    }

    /// <summary>
    /// config.h MAX_DHND_PER_CONN is 8: eight directories open, the ninth is refused with EMFILE.
    /// </summary>
    [Test]
    public void NinthOpenDirectory_OnOneConnection_IsRefusedWithEmfile()
    {
        for (int i = 0; i < 8; i++)
        {
            TnfsMessage ok = _directoryHandler.HandleOpenDir(OpenDir(_session.SessionId, "/test"));
            Assert.That((TnfsReturnCode)ok.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS), "open directory number " + (i + 1));
        }

        TnfsMessage ninth = _directoryHandler.HandleOpenDir(OpenDir(_session.SessionId, "/test"));
        Assert.That((TnfsReturnCode)ninth.Header.Status, Is.EqualTo(TnfsReturnCode.EMFILE));
        Assert.That(_session.DirectoryHandles.Length, Is.EqualTo(8), "config.h MAX_DHND_PER_CONN");
    }

    /// <summary>
    /// The session keeps the last reply in a buffer of MAXMSGSZ bytes.
    /// </summary>
    [Test]
    public void SessionLastMessageBuffer_HasMaxMessageSize()
    {
        Assert.That(_session.LastMessage.Length, Is.EqualTo(532), "config.h MAXMSGSZ");
    }

    /// <summary>
    /// A READ that asks for more than fits in one reply returns at most MAX_IOSZ bytes, and the whole reply
    /// (header, status, count, data) fits in MAXMSGSZ.
    /// </summary>
    [Test]
    public void Read_AskingForMoreThanFits_ReturnsAtMostTheIoLimit()
    {
        TnfsMessage open = _fileHandler.HandleOpen(OpenFile(_session.SessionId, "/big.bin"));
        Assert.That((TnfsReturnCode)open.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        byte fd = open.Data.Span[0];

        TnfsMessage read = _fileHandler.HandleRead(ReadRequest(_session.SessionId, fd, 4000));
        Assert.That((TnfsReturnCode)read.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));

        int count = read.Data.Span[0] | (read.Data.Span[1] << 8);
        Assert.That(count, Is.EqualTo(TnfsConstants.MaxIOSize), "bytes read");
        Assert.That(read.Data.Length, Is.EqualTo(2 + count), "count field plus data");
        Assert.That(read.Data.Length, Is.LessThanOrEqualTo(TnfsConstants.TNFS_MAX_PAYLOAD), "reply payload fits one datagram");
        Assert.That(TnfsConstants.TNFS_HEADERSZ + 1 + read.Data.Length, Is.LessThanOrEqualTo(TnfsConstants.MAXMSGSZ));
    }

    /// <summary>
    /// A new TCP connection over the configured limit is closed, and the transport reports the same limit.
    /// </summary>
    [Test]
    public async Task TcpTransport_ConnectionOverTheLimit_IsClosed_AndCapabilitiesReportTheSameLimit()
    {
        int port = GetFreePort();
        using TcpTransport transport = new TcpTransport(port, NullLogger<TcpTransport>.Instance, maxConnections: 2);
        Assert.That(transport.Capabilities.MaxConcurrentConnections, Is.EqualTo(2));

        using CancellationTokenSource cts = new CancellationTokenSource();
        Task run = transport.RunAsync(cts.Token);

        using TcpClient first = await ConnectAsync(port);
        using TcpClient second = await ConnectAsync(port);
        await Task.Delay(300);

        using TcpClient third = await ConnectAsync(port);
        byte[] one = new byte[1];
        using CancellationTokenSource readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int n;
        try
        {
            n = await third.GetStream().ReadAsync(one, 0, 1, readTimeout.Token);
        }
        catch (System.IO.IOException)
        {
            n = 0;
        }
        Assert.That(n, Is.EqualTo(0), "the third connection must be closed by the server");

        // The first two are still open: a refused connection must not close the ones inside the limit.
        Assert.That(first.Connected && second.Connected, Is.True);

        cts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The default limit is config.h MAX_TCP_CONN and the report matches it.
    /// </summary>
    [Test]
    public void TcpTransport_DefaultLimit_IsMaxTcpConn()
    {
        using TcpTransport transport = new TcpTransport(GetFreePort(), NullLogger<TcpTransport>.Instance);
        Assert.That(transport.Capabilities.MaxConcurrentConnections, Is.EqualTo(4096), "config.h MAX_TCP_CONN");
    }

    /// <summary>
    /// A closed directory handle frees its slot: with all eight open the next OPENDIR is refused, and after one
    /// CLOSEDIR the next OPENDIR succeeds and gets the freed handle number.
    /// </summary>
    [Test]
    public void ClosingADirectory_FreesItsSlot_ForTheNextOpenDir()
    {
        byte first = 0;
        for (int i = 0; i < 8; i++)
        {
            TnfsMessage ok = _directoryHandler.HandleOpenDir(OpenDir(_session.SessionId, "/test"));
            Assert.That((TnfsReturnCode)ok.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
            if (i == 0)
                first = ok.Data.Span[0];
        }

        TnfsMessage refused = _directoryHandler.HandleOpenDir(OpenDir(_session.SessionId, "/test"));
        Assert.That((TnfsReturnCode)refused.Header.Status, Is.EqualTo(TnfsReturnCode.EMFILE), "the ninth is refused");

        TnfsMessage closed = _directoryHandler.HandleCloseDir(Build(_session.SessionId, TnfsConstants.TNFS_CLOSEDIR, new byte[] { first }));
        Assert.That((TnfsReturnCode)closed.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));

        TnfsMessage again = _directoryHandler.HandleOpenDir(OpenDir(_session.SessionId, "/test"));
        Assert.That((TnfsReturnCode)again.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS), "a slot was freed");
        Assert.That(again.Data.Span[0], Is.EqualTo(first), "the freed handle number is reused");

        TnfsMessage refusedAgain = _directoryHandler.HandleOpenDir(OpenDir(_session.SessionId, "/test"));
        Assert.That((TnfsReturnCode)refusedAgain.Header.Status, Is.EqualTo(TnfsReturnCode.EMFILE), "only one slot was freed");
    }

    /// <summary>
    /// Handle numbers from 8 up are outside the table of eight; READDIR and CLOSEDIR answer EBADF and do not
    /// throw an index error.
    /// </summary>
    /// <param name="handle">
    /// The directory handle number sent.
    /// </param>
    [TestCase((byte)8)]
    [TestCase((byte)9)]
    [TestCase((byte)255)]
    public void DirectoryHandleNumber_OutsideTheTable_IsBadFileHandle(byte handle)
    {
        TnfsMessage read = _directoryHandler.HandleReadDir(Build(_session.SessionId, TnfsConstants.TNFS_READDIR, new byte[] { handle }));
        TnfsMessage close = _directoryHandler.HandleCloseDir(Build(_session.SessionId, TnfsConstants.TNFS_CLOSEDIR, new byte[] { handle }));

        Assert.That((TnfsReturnCode)read.Header.Status, Is.EqualTo(TnfsReturnCode.EBADF), "READDIR");
        Assert.That((TnfsReturnCode)close.Header.Status, Is.EqualTo(TnfsReturnCode.EBADF), "CLOSEDIR");
    }

    /// <summary>
    /// A READ is cut to MAX_IOSZ (512) bytes and never more, whatever the client asks for, and a request for less
    /// is not cut. UDP and TCP both pass the request to this one handler.
    /// </summary>
    /// <param name="asked">
    /// The length the client asks for.
    /// </param>
    /// <param name="expected">
    /// The number of bytes the reply must carry.
    /// </param>
    [TestCase((ushort)1, 1)]
    [TestCase((ushort)100, 100)]
    [TestCase((ushort)512, 512)]
    [TestCase((ushort)513, 512)]
    [TestCase((ushort)1000, 512)]
    [TestCase((ushort)65535, 512)]
    public void Read_Length_IsCutToTheIoLimit(ushort asked, int expected)
    {
        TnfsMessage open = _fileHandler.HandleOpen(OpenFile(_session.SessionId, "/big.bin"));
        byte fd = open.Data.Span[0];

        TnfsMessage read = _fileHandler.HandleRead(ReadRequest(_session.SessionId, fd, asked));

        Assert.That((TnfsReturnCode)read.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        int count = read.Data.Span[0] | (read.Data.Span[1] << 8);
        Assert.That(count, Is.EqualTo(expected), "bytes in the reply");
        Assert.That(read.Data.Length, Is.EqualTo(2 + expected));
    }

    /// <summary>
    /// The largest READ reply, serialised, fits a buffer of MAXMSGSZ bytes (header 5 bytes, count 2 bytes, data 512
    /// bytes). That is the size of the receive and send buffers, so a full reply cannot overflow them.
    /// </summary>
    [Test]
    public void Read_LargestReply_SerialisesInsideMaxMessageSize()
    {
        TnfsMessage open = _fileHandler.HandleOpen(OpenFile(_session.SessionId, "/big.bin"));
        TnfsMessage read = _fileHandler.HandleRead(ReadRequest(_session.SessionId, open.Data.Span[0], 65535));

        byte[] wire = new byte[TnfsConstants.MAXMSGSZ];
        int length = read.SerializeResponse(wire);

        Assert.That(length, Is.EqualTo(TnfsConstants.TNFS_HEADERSZ + 1 + 2 + TnfsConstants.MaxIOSize));
        Assert.That(length, Is.LessThanOrEqualTo(TnfsConstants.MAXMSGSZ));
    }

    /// <summary>
    /// A file shorter than the limit gives a short read of the file length, not a full 512 bytes of zeros.
    /// </summary>
    [Test]
    public void Read_FileShorterThanTheLimit_ReturnsOnlyTheFileBytes()
    {
        _mockFS.AddFile("/tmp/tnfs/small.bin", new byte[100]);
        TnfsMessage open = _fileHandler.HandleOpen(OpenFile(_session.SessionId, "/small.bin"));

        TnfsMessage read = _fileHandler.HandleRead(ReadRequest(_session.SessionId, open.Data.Span[0], 4000));

        int count = read.Data.Span[0] | (read.Data.Span[1] << 8);
        Assert.That(count, Is.EqualTo(100));
        Assert.That(read.Data.Length, Is.EqualTo(102));
    }

    /// <summary>
    /// When the backend knows no file times (it reports null), STAT answers with the time of the call for the
    /// access, modification and creation times, and still reports the true size.
    /// </summary>
    [Test]
    public void Stat_FileTimesUnknown_FallBackToNow_AndSizeIsKept()
    {
        _mockFS.FileTimesMode = MockFileSystemBackend.MockFileTimes.Unknown;
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        TnfsMessage stat = _fileHandler.HandleStat(StatRequest(_session.SessionId, "/big.bin"));

        long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.That((TnfsReturnCode)stat.Header.Status, Is.EqualTo(TnfsReturnCode.SUCCESS));
        byte[] d = stat.Data.ToArray();
        Assert.That(ReadU32(d, 6), Is.EqualTo(4000u), "size");
        Assert.That((long)ReadU32(d, 10), Is.InRange(before, after), "atime");
        Assert.That((long)ReadU32(d, 14), Is.InRange(before, after), "mtime");
        Assert.That((long)ReadU32(d, 18), Is.InRange(before, after), "ctime");
    }

    /// <summary>
    /// When the backend reports file times, STAT passes them through unchanged (atime, mtime, ctime in that order).
    /// </summary>
    [Test]
    public void Stat_FileTimesKnown_AreReportedAsGiven()
    {
        _mockFS.FileTimesMode = MockFileSystemBackend.MockFileTimes.Fixed;
        _mockFS.FixedAccessTime = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        _mockFS.FixedWriteTime = new DateTime(2002, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        _mockFS.FixedCreationTime = new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc);

        TnfsMessage stat = _fileHandler.HandleStat(StatRequest(_session.SessionId, "/big.bin"));

        byte[] d = stat.Data.ToArray();
        Assert.That(ReadU32(d, 10), Is.EqualTo((uint)new DateTimeOffset(_mockFS.FixedAccessTime).ToUnixTimeSeconds()), "atime");
        Assert.That(ReadU32(d, 14), Is.EqualTo((uint)new DateTimeOffset(_mockFS.FixedWriteTime).ToUnixTimeSeconds()), "mtime");
        Assert.That(ReadU32(d, 18), Is.EqualTo((uint)new DateTimeOffset(_mockFS.FixedCreationTime).ToUnixTimeSeconds()), "ctime");
    }

    /// <summary>
    /// A TCP connection that sends a length prefix over MAXMSGSZ is closed, and nothing is passed on. A message of
    /// exactly MAXMSGSZ is still delivered, with all of its bytes.
    /// </summary>
    [Test]
    public async Task TcpTransport_MessageOverMaxMessageSize_IsDropped_AndExactlyMaxIsDelivered()
    {
        int port = GetFreePort();
        using TcpTransport transport = new TcpTransport(port, NullLogger<TcpTransport>.Instance);
        TransportProbe probe = new TransportProbe();
        transport.OnMessageReceived += probe.OnMessage;
        using CancellationTokenSource cts = new CancellationTokenSource();
        Task run = transport.RunAsync(cts.Token);

        using (TcpClient exact = await ConnectAsync(port))
        {
            await SendFrameAsync(exact, TnfsConstants.MAXMSGSZ);
            Assert.That(await probe.WaitForAsync(1), Is.True, "a message of MAXMSGSZ bytes must arrive");
            Assert.That(probe.Lengths[0], Is.EqualTo(TnfsConstants.MAXMSGSZ - TnfsConstants.TNFS_HEADERSZ), "body bytes after the 4 byte header");
        }

        using (TcpClient tooBig = await ConnectAsync(port))
        {
            byte[] prefix = BitConverter.GetBytes((ushort)(TnfsConstants.MAXMSGSZ + 1));
            await tooBig.GetStream().WriteAsync(prefix, 0, 2);
            Assert.That(await IsClosedByServerAsync(tooBig), Is.True, "an oversized length prefix closes the connection");
        }

        Assert.That(probe.Count, Is.EqualTo(1), "the oversized message was not passed on");

        cts.Cancel();
        await StopAsync(run);
    }

    /// <summary>
    /// Refused connections do not use up a slot, and a slot freed by a client that goes away is given to the next
    /// client: after several refusals and one disconnect, a new client is served.
    /// </summary>
    [Test]
    public async Task TcpTransport_SlotIsFreed_WhenAClientDisconnects_AndRefusalsDoNotLeakSlots()
    {
        int port = GetFreePort();
        using TcpTransport transport = new TcpTransport(port, NullLogger<TcpTransport>.Instance, maxConnections: 1);
        TransportProbe probe = new TransportProbe();
        transport.OnMessageReceived += probe.OnMessage;
        using CancellationTokenSource cts = new CancellationTokenSource();
        Task run = transport.RunAsync(cts.Token);

        TcpClient holder = await ConnectAsync(port);
        await SendFrameAsync(holder, 8);
        Assert.That(await probe.WaitForAsync(1), Is.True, "the first client is served");

        for (int i = 0; i < 4; i++)
        {
            using TcpClient refused = await ConnectAsync(port);
            Assert.That(await IsClosedByServerAsync(refused), Is.True, "refusal number " + (i + 1));
        }

        holder.Dispose();

        // The server notices the close in its own time; try a new client until one is served or the deadline passes.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        bool served = false;
        while (!served && DateTime.UtcNow < deadline)
        {
            using TcpClient next = await ConnectAsync(port);
            try
            {
                await SendFrameAsync(next, 8);
            }
            catch (System.IO.IOException)
            {
                continue;
            }
            served = await probe.WaitForAsync(2, 300);
        }

        Assert.That(served, Is.True, "a new client is served after the first one left");

        cts.Cancel();
        await StopAsync(run);
    }

    /// <summary>
    /// A connection limit below 1 is refused when the transport is built.
    /// </summary>
    /// <param name="limit">
    /// The limit given to the constructor.
    /// </param>
    [TestCase(0)]
    [TestCase(-1)]
    public void TcpTransport_MaxConnectionsBelowOne_Throws(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TcpTransport(GetFreePort(), NullLogger<TcpTransport>.Instance, maxConnections: limit));
    }

    /// <summary>
    /// The limit given to the constructor is the limit the property and the capabilities report.
    /// </summary>
    [Test]
    public void TcpTransport_MaxConnections_MatchesTheConstructorArgument()
    {
        using TcpTransport transport = new TcpTransport(GetFreePort(), NullLogger<TcpTransport>.Instance, maxConnections: 7);

        Assert.That(transport.MaxConnections, Is.EqualTo(7));
        Assert.That(transport.Capabilities.MaxConcurrentConnections, Is.EqualTo(7));
    }

    /// <summary>
    /// TnfsServerOptions.MaxTcpConnections defaults to MAX_TCP_CONN.
    /// </summary>
    [Test]
    public void ServerOptions_MaxTcpConnections_DefaultsToMaxTcpConn()
    {
        Assert.That(new TnfsServerOptions().MaxTcpConnections, Is.EqualTo(TnfsConstants.MAX_TCP_CONN));
    }

    /// <summary>
    /// The limit set in the options reaches the TCP transport that AddTnfsServer registers.
    /// </summary>
    [Test]
    public void AddTnfsServer_MaxTcpConnections_ReachesTheTcpTransport()
    {
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFileSystemBackend>(_mockFS);
        services.AddTnfsServer(o =>
        {
            o.EnableUdp = false;
            o.EnableTcp = true;
            o.TcpPort = GetFreePort();
            o.MaxTcpConnections = 3;
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        TcpTransport tcp = FindTcpTransport(provider);

        Assert.That(tcp.MaxConnections, Is.EqualTo(3));
        Assert.That(tcp.Capabilities.MaxConcurrentConnections, Is.EqualTo(3));
    }

    /// <summary>
    /// With no setting, AddTnfsServer gives the TCP transport the default limit.
    /// </summary>
    [Test]
    public void AddTnfsServer_NoLimitSet_GivesTheTcpTransportTheDefaultLimit()
    {
        ServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFileSystemBackend>(_mockFS);
        services.AddTnfsServer(o =>
        {
            o.EnableUdp = false;
            o.TcpPort = GetFreePort();
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.That(FindTcpTransport(provider).MaxConnections, Is.EqualTo(TnfsConstants.MAX_TCP_CONN));
    }

    /// <summary>
    /// A UDP datagram over MAXMSGSZ is never passed on as a message larger than the buffer, and the receive loop
    /// keeps running: a normal datagram sent afterwards is delivered.
    /// </summary>
    [Test]
    public async Task UdpTransport_DatagramOverMaxMessageSize_IsNotPassedOnWhole_AndTheLoopSurvives()
    {
        int port = GetFreeUdpPort();
        using UdpTransport transport = new UdpTransport(port, NullLogger<UdpTransport>.Instance);
        TransportProbe probe = new TransportProbe();
        transport.OnMessageReceived += probe.OnMessage;
        using CancellationTokenSource cts = new CancellationTokenSource();
        Task run = transport.RunAsync(cts.Token);

        using UdpClient client = new UdpClient();
        client.Connect(IPAddress.Loopback, port);
        byte[] big = new byte[TnfsConstants.MAXMSGSZ + 100];
        byte[] normal = new byte[TnfsConstants.TNFS_HEADERSZ + 6];

        // The socket binds a moment after RunAsync starts; send until a normal datagram arrives.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (probe.Count == 0 && DateTime.UtcNow < deadline)
        {
            await client.SendAsync(big, big.Length);
            await client.SendAsync(normal, normal.Length);
            await probe.WaitForAsync(1, 200);
        }

        Assert.That(probe.Count, Is.GreaterThanOrEqualTo(1), "the loop must still receive after an oversized datagram");
        List<int> seen = probe.Lengths;
        for (int i = 0; i < seen.Count; i++)
        {
            Assert.That(seen[i], Is.LessThanOrEqualTo(TnfsConstants.MAXMSGSZ - TnfsConstants.TNFS_HEADERSZ), "message " + i);
        }

        cts.Cancel();
        await StopAsync(run);
    }

    private static TcpTransport FindTcpTransport(IServiceProvider provider)
    {
        IEnumerable<ITransport> all = provider.GetServices<ITransport>();
        IEnumerator<ITransport> e = all.GetEnumerator();
        while (e.MoveNext())
        {
            if (e.Current is TcpTransport tcp)
                return tcp;
        }
        throw new InvalidOperationException("AddTnfsServer registered no TcpTransport");
    }

    private static uint ReadU32(byte[] d, int offset)
    {
        return (uint)(d[offset] | (d[offset + 1] << 8) | (d[offset + 2] << 16) | (d[offset + 3] << 24));
    }

    private static int GetFreeUdpPort()
    {
        using UdpClient u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    private static async Task SendFrameAsync(TcpClient client, int messageLength)
    {
        // Two length bytes, then a message of all zeros: the 4 header bytes are session 0, sequence 0, command 0.
        byte[] frame = new byte[2 + messageLength];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 2), (ushort)messageLength);
        await client.GetStream().WriteAsync(frame, 0, frame.Length);
    }

    private static async Task<bool> IsClosedByServerAsync(TcpClient client)
    {
        byte[] one = new byte[1];
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return await client.GetStream().ReadAsync(one, 0, 1, timeout.Token) == 0;
        }
        catch (System.IO.IOException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task StopAsync(Task run)
    {
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static TnfsMessage StatRequest(ushort sessionId, string path)
    {
        List<byte> body = new List<byte>(Encoding.ASCII.GetBytes(path));
        body.Add(0x00);
        return Build(sessionId, TnfsConstants.TNFS_STAT, body.ToArray());
    }

    /// <summary>
    /// Collects the body length of every message a transport passes on, and lets a test wait for a count.
    /// </summary>
    private sealed class TransportProbe
    {
        private readonly object _lock = new object();
        private readonly List<int> _lengths = new List<int>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _lengths.Count;
                }
            }
        }

        public List<int> Lengths
        {
            get
            {
                lock (_lock)
                {
                    return new List<int>(_lengths);
                }
            }
        }

        public ValueTask OnMessage(TnfsMessage message)
        {
            lock (_lock)
            {
                _lengths.Add(message.Data.Length);
            }
            _signal.Release();
            return ValueTask.CompletedTask;
        }

        public async Task<bool> WaitForAsync(int count, int timeoutMs = 5000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Count < count)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !await _signal.WaitAsync(left))
                    return Count >= count;
            }
            return true;
        }
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
        for (int attempt = 0; attempt < 50; attempt++)
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

    private static TnfsMessage Build(ushort sessionId, byte command, byte[] body)
    {
        List<byte> packet = new List<byte>();
        packet.AddRange(BitConverter.GetBytes(sessionId));
        packet.Add(0x00);
        packet.Add(command);
        packet.AddRange(body);
        return TnfsMessage.ParseRequest(packet.ToArray(), IPAddress.Loopback, 0);
    }

    private static TnfsMessage OpenDir(ushort sessionId, string path)
    {
        List<byte> body = new List<byte>(Encoding.ASCII.GetBytes(path));
        body.Add(0x00);
        return Build(sessionId, TnfsConstants.TNFS_OPENDIR, body.ToArray());
    }

    private static TnfsMessage OpenFile(ushort sessionId, string path)
    {
        List<byte> body = new List<byte>();
        // Open flags 0: the handler maps the low two bits 0 to read access (see ConvertToFileAccess).
        body.AddRange(BitConverter.GetBytes((ushort)0));
        body.AddRange(BitConverter.GetBytes((ushort)0));
        body.AddRange(Encoding.ASCII.GetBytes(path));
        body.Add(0x00);
        return Build(sessionId, TnfsConstants.TNFS_OPEN, body.ToArray());
    }

    private static TnfsMessage ReadRequest(ushort sessionId, byte fd, ushort length)
    {
        List<byte> body = new List<byte>();
        body.Add(fd);
        body.AddRange(BitConverter.GetBytes(length));
        return Build(sessionId, TnfsConstants.TNFS_READ, body.ToArray());
    }
}
