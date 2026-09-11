//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RetroIO.Protocols.Econet.FileServer;
using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handlers;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Paths;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Econet.Server.Transport;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

[TestFixture]
[Category("Unit")]
public sealed class UdpAunTransportTests
{
    private static int _nextTestPort = 33768;

    private int _testPort;
    private IHost _host = null!;
    private UdpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _testPort = Interlocked.Increment(ref _nextTestPort);
        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(fileSystem);

        EconetUserDirectory users = EconetTestUsers.CreateDirectory(out _);
        DiscCatalog discs = new DiscCatalog(fileSystem, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(fileSystem, users, discs, "/econet-root");
        FileServerHandleManager handles = new FileServerHandleManager();
        PathResolver paths = new PathResolver(fileSystem, discs, "/econet-root");
        BulkTransferManager bulk = new BulkTransferManager();
        FileServerContext context = new FileServerContext(
            fileSystem, users, discs, sessions, handles, paths, bulk,
            new FileServerConfiguration(), new FileServerClock(),
            "/econet-root", "RetroFS", FileServerCommandHandler.DefaultVersionString);

        AunAddressMapper mapper = new AunAddressMapper(network: 1);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IFileSystemBackend>(fileSystem);
                services.AddSingleton(context);
                services.AddSingleton(sessions);
                services.AddSingleton<FileServerCommandHandler>();
                services.AddSingleton<BulkTransferHandler>();
                services.AddSingleton<ITransport>(new UdpAunTransport(
                    _testPort,
                    mapper,
                    NullLogger<UdpAunTransport>.Instance));
                services.AddHostedService(sp =>
                    new EconetFileServerService(
                        new ITransport[] { sp.GetRequiredService<ITransport>() },
                        sp.GetRequiredService<FileServerCommandHandler>(),
                        sp.GetRequiredService<BulkTransferHandler>(),
                        sp.GetRequiredService<FileServerSessionManager>(),
                        NullLogger<EconetFileServerService>.Instance));
            })
            .Build();

        await _host.StartAsync();

        _client = new UdpClient();
        _client.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.Dispose();

        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Test]
    public async Task ReadVersion_OverUdpAun_AcksThenReturnsVersion()
    {
        byte[] fsPayload =
        [
            0x80,
            FileServerOpcodes.ReadVersion,
            0, 0, 0,
        ];

        byte[] datagram = new byte[AunPacket.HeaderSize + fsPayload.Length];
        AunPacket.Encode(
            datagram,
            AunPacketType.Unicast,
            EconetPorts.FileServer,
            0x80,
            0x00000042,
            fsPayload);

        IPEndPoint server = new IPEndPoint(IPAddress.Loopback, _testPort);
        await _client.SendAsync(datagram, datagram.Length, server);

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        UdpReceiveResult ackResult = await _client.ReceiveAsync(timeout.Token);
        byte[] ackBuffer = ackResult.Buffer;
        Assert.That(ackBuffer.Length, Is.EqualTo(AunPacket.HeaderSize));
        Assert.That(AunPacket.TryDecode(
            ackBuffer,
            out AunPacketType ackType,
            out byte ackPort,
            out byte ackControl,
            out uint ackSequence,
            out int ackPayloadOffset), Is.True);
        Assert.That(ackType, Is.EqualTo(AunPacketType.Ack));
        Assert.That(ackPort, Is.EqualTo(EconetPorts.FileServer));
        Assert.That(ackSequence, Is.EqualTo(0x00000042u));
        Assert.That(ackPayloadOffset, Is.EqualTo(AunPacket.HeaderSize));

        UdpReceiveResult responseResult = await _client.ReceiveAsync(timeout.Token);
        byte[] responseBuffer = responseResult.Buffer;
        Assert.That(AunPacket.TryDecode(
            responseBuffer,
            out AunPacketType responseType,
            out byte responsePort,
            out byte _,
            out uint _,
            out int responsePayloadOffset), Is.True);
        Assert.That(responseType, Is.EqualTo(AunPacketType.Unicast));
        Assert.That(responsePort, Is.EqualTo(0x80));

        ReadOnlySpan<byte> responsePayload = responseBuffer.AsSpan(responsePayloadOffset);
        Assert.That(
            Encoding.ASCII.GetString(responsePayload.Slice(2, responsePayload.Length - 3)),
            Is.EqualTo(FileServerCommandHandler.DefaultVersionString));
    }

    [Test]
    public async Task GetObjectInfo_Cmd6_WireCwdZero_ReturnsNotFound()
    {
        // PiFS ground truth (utilities/fs.c):
        //  - fs.c:9944 applies FS_DIVHANDLE to request byte 3; DIVHANDLE(0)==0.
        //  - fs.c:9951 overwrites the session's current handle with that wire byte -> 0.
        //  - fs_get_object_info reads relative_to = *(data+3) == 0 (fs.c:5598).
        //  - fs_normalize_path_wildcard finds fhandles[0].handle == -1 (slot 0 is never an
        //    open handle) and returns FS_PATH_ERR_NODIR (fs.c:2341).
        //  - For command 6 that NODIR is reported as the hard error 0xD6 "Not found"
        //    (fs.c:5635-5638), NOT a success directory block.
        // BeebEm's *CAT sends wire CWD handle 0, so this not-found reply is exactly what keeps
        // *CAT working; a success dir block here made BeebEm loop forever -> "Net error".
        const byte replyPort = 0x90;
        const uint sequence = 0x0000000Cu;

        await LoginSystAsync(replyPort, sequence);

        byte[] fsPayload = [replyPort, FileServerOpcodes.GetObjectInfo, 0, 0, 0, 6, FileServerReply.CarriageReturn];
        byte[] datagram = new byte[AunPacket.HeaderSize + fsPayload.Length];
        AunPacket.Encode(
            datagram,
            AunPacketType.Unicast,
            EconetPorts.FileServer,
            0x80,
            sequence + 4,
            fsPayload);

        IPEndPoint server = new IPEndPoint(IPAddress.Loopback, _testPort);
        await _client.SendAsync(datagram, datagram.Length, server);

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _client.ReceiveAsync(timeout.Token);

        UdpReceiveResult responseResult = await _client.ReceiveAsync(timeout.Token);
        byte[] responseBuffer = responseResult.Buffer;
        Assert.That(responseBuffer.Length, Is.GreaterThanOrEqualTo(AunPacket.HeaderSize));
        Assert.That(responseBuffer[0], Is.EqualTo((byte)AunPacketType.Unicast));
        Assert.That(responseBuffer[1], Is.EqualTo(replyPort));

        ReadOnlySpan<byte> payload = responseBuffer.AsSpan(AunPacket.HeaderSize);
        // Error reply layout: [0x00, 0xD6, "Not found", 0x0D].
        Assert.That(payload[0], Is.EqualTo(0));
        Assert.That(payload[1], Is.EqualTo((byte)0xD6));
        Assert.That(
            Encoding.ASCII.GetString(payload.Slice(2, payload.Length - 3)),
            Is.EqualTo("Not found"));
    }

    [Test]
    public async Task FsReply_UsesFreshServerSequence_NotEchoedRequestSeq_IncrementingByFour()
    {
        // PiFS/PiEconetBridge ground truth: every FS reply DATA carries a FRESH server sequence from
        // get_local_seq() (+= 4), NOT the client's request seq (fs.c:1291; econet-hpbridge.c:9974/9981).
        // BeebEm ACKs the reply by that server seq so the bridge can stop retransmitting
        // (econet-hpbridge.c:3725, 5891). Independent confirmation: RISC OS PRM Vol 5a Ch.122 and
        // BeebWiki "Econet over IP" ("increments by 4 on each transaction; Acks/Naks quote back the seq").
        // Use fsop 0x19 (read version) — allowed without login, single self-contained reply.
        const byte replyPort = 0x91;
        const uint requestSeqA = 0x00001000u;
        const uint requestSeqB = 0x00002000u;

        uint replySeqA = await ReadVersionReplySequenceAsync(replyPort, requestSeqA);
        uint replySeqB = await ReadVersionReplySequenceAsync(replyPort, requestSeqB);

        // The reply seq must be a fresh server value, not an echo of either request seq.
        Assert.That(replySeqA, Is.Not.EqualTo(requestSeqA), "reply must not echo the request seq");
        Assert.That(replySeqB, Is.Not.EqualTo(requestSeqB), "reply must not echo the request seq");

        // Server counter advances by exactly 4 between consecutive replies (Econet convention).
        Assert.That(replySeqA % 4, Is.EqualTo(0));
        Assert.That(replySeqB, Is.EqualTo(replySeqA + 4));
    }

    private async Task<uint> ReadVersionReplySequenceAsync(byte replyPort, uint requestSequence)
    {
        byte[] fsPayload = [replyPort, FileServerOpcodes.ReadVersion, 0, 0, 0];
        byte[] datagram = new byte[AunPacket.HeaderSize + fsPayload.Length];
        AunPacket.Encode(datagram, AunPacketType.Unicast, EconetPorts.FileServer, 0x80, requestSequence, fsPayload);

        await _client.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, _testPort));

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // First inbound datagram is the server's ACK of our request; skip until we get the reply on replyPort.
        while (true)
        {
            UdpReceiveResult result = await _client.ReceiveAsync(timeout.Token);
            byte[] buffer = result.Buffer;
            if (buffer.Length < AunPacket.HeaderSize)
                continue;
            if (buffer[0] != (byte)AunPacketType.Unicast || buffer[1] != replyPort)
                continue; // skip the ACK (ptype 0x03) and anything not our reply

            // AUN header seq is a little-endian uint32 at offset 4.
            uint replySeq = (uint)(buffer[4] | (buffer[5] << 8) | (buffer[6] << 16) | (buffer[7] << 24));

            // ACK the reply by its server seq (as BeebEm would) so the server cancels retransmission —
            // otherwise a 1 s-later retransmit of this reply could be misread as the next reply.
            byte[] ack = new byte[AunPacket.HeaderSize];
            AunPacket.Encode(ack, AunPacketType.Ack, replyPort, 0x80, replySeq, ReadOnlySpan<byte>.Empty);
            await _client.SendAsync(ack, ack.Length, new IPEndPoint(IPAddress.Loopback, _testPort));

            return replySeq;
        }
    }

    private async Task LoginSystAsync(byte replyPort, uint sequence)
    {
        byte[] loginParams = Encoding.ASCII.GetBytes("I AM SYST \r");
        byte[] fsPayload = new byte[5 + loginParams.Length];
        fsPayload[0] = replyPort;
        fsPayload[1] = FileServerOpcodes.OsCli;
        loginParams.CopyTo(fsPayload, 5);

        byte[] datagram = new byte[AunPacket.HeaderSize + fsPayload.Length];
        AunPacket.Encode(datagram, AunPacketType.Unicast, EconetPorts.FileServer, 0x80, sequence, fsPayload);
        await _client.SendAsync(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, _testPort));

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _client.ReceiveAsync(timeout.Token);
        UdpReceiveResult loginReply = await _client.ReceiveAsync(timeout.Token);
        ReadOnlySpan<byte> loginPayload = loginReply.Buffer.AsSpan(AunPacket.HeaderSize);
        Assert.That(loginPayload[0], Is.EqualTo(FileServerOpcodes.LoginSuccessReturnCode));
    }
}
