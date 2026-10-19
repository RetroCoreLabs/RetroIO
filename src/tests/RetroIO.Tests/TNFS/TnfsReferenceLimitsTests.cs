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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
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
