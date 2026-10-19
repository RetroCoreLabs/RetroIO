//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
using RetroIO.Protocols.Econet.Protocol;
using RetroIO.Protocols.Econet.Protocol.Aun;
using RetroIO.Protocols.Econet.Protocol.FileServer;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Econet.Server.Transport;
using RetroIO.Tests.TNFS;

namespace RetroIO.Tests.Econet;

/// <summary>
/// Tests two things about the Econet counters that the main statistics tests do not reach:
/// that the dependency injection setup hands the same counter object to the service and to
/// the Management API, and that the service counts a failed send as an error.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EconetStatisticsWiringTests
{
    /// <summary>
    /// A transport that delivers messages on request and fails every send. Used to drive the
    /// error paths of the service.
    /// </summary>
    private sealed class FailingSendTransport : ITransport
    {
        private readonly bool _autoAck;

        /// <summary>
        /// Creates the transport.
        /// </summary>
        /// <param name="autoAck">
        /// True to ask the service to acknowledge each inbound data packet, which is the first send it makes.
        /// </param>
        public FailingSendTransport(bool autoAck)
        {
            _autoAck = autoAck;
        }

        /// <summary>
        /// Gets the number of sends that were attempted and refused.
        /// </summary>
        public int SendAttempts => Volatile.Read(ref _sendAttempts);

        private int _sendAttempts;

        /// <inheritdoc />
        public string TransportType => "FailingSend";

        /// <inheritdoc />
        public event EconetMessageReceivedHandler? OnMessageReceived;

        /// <inheritdoc />
        public TransportCapabilities Capabilities => new()
        {
            SupportsMultipleClients = true,
            MaxConcurrentConnections = int.MaxValue,
            RequiresConnectionState = false,
            IsReliable = true,
            AutoAckInboundData = _autoAck,
        };

        /// <inheritdoc />
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Normal stop.
            }
        }

        /// <inheritdoc />
        public ValueTask SendAsync(EconetMessage message)
        {
            Interlocked.Increment(ref _sendAttempts);
            throw new InvalidOperationException("The send is refused on purpose.");
        }

        /// <summary>
        /// Delivers a message to the service as if it came off the wire.
        /// </summary>
        /// <param name="message">
        /// The message to deliver.
        /// </param>
        public async Task DeliverAsync(EconetMessage message)
        {
            message.Transport = this;
            EconetMessageReceivedHandler? handler = OnMessageReceived;
            if (handler != null)
                await handler(message);
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }

    private static EconetFileServerService BuildService(FailingSendTransport transport, EconetServerStatistics stats)
    {
        MockFileSystemBackend fileSystem = new MockFileSystemBackend();
        EconetSampleCatalogFixture.Seed(fileSystem);

        RetroIO.Protocols.Econet.FileServer.Users.EconetUserDirectory users = EconetTestUsers.CreateDirectory(out _);
        DiscCatalog discs = new DiscCatalog(fileSystem, "/econet-root", "RetroFS");
        FileServerSessionManager sessions = new FileServerSessionManager(fileSystem, users, discs, "/econet-root");
        FileServerContext context = new FileServerContext(
            fileSystem, users, discs, sessions, new FileServerHandleManager(),
            new PathResolver(fileSystem, discs, "/econet-root"), new BulkTransferManager(),
            new FileServerConfiguration(), new FileServerClock(),
            "/econet-root", "RetroFS", FileServerCommandHandler.DefaultVersionString);

        return new EconetFileServerService(
            new ITransport[] { transport },
            new FileServerCommandHandler(context, NullLogger<FileServerCommandHandler>.Instance),
            new BulkTransferHandler(context),
            sessions,
            NullLogger<EconetFileServerService>.Instance,
            statistics: stats);
    }

    private static EconetMessage ReadVersionRequest()
    {
        return new EconetMessage
        {
            PacketType = AunPacketType.Unicast,
            EconetPort = EconetPorts.FileServer,
            Control = 0x80,
            Sequence = 42,
            Payload = [0x80, FileServerOpcodes.ReadVersion, 0, 0, 0],
            ClientAddress = IPAddress.Loopback,
            ClientPort = 50000,
            ClientNet = 1,
            ClientStation = 1,
        };
    }

    /// <summary>
    /// The setup call registers one counter object that answers to both its own type (read by the
    /// Management API) and to the tracker interface (written by the service).
    /// </summary>
    [Test]
    public void AddEconetFileServer_RegistersOneSharedCounterObject()
    {
        ServiceCollection services = new ServiceCollection();
        services.AddEconetFileServer(options => options.RootDirectory = "/econet-root");
        using ServiceProvider provider = services.BuildServiceProvider();

        EconetServerStatistics? concrete = provider.GetService<EconetServerStatistics>();
        IEconetStatisticsTracker? tracker = provider.GetService<IEconetStatisticsTracker>();

        Assert.That(concrete, Is.Not.Null, "the Management API cannot read counters that are not registered");
        Assert.That(tracker, Is.Not.Null, "the service cannot write counters that are not registered");
        Assert.That(tracker, Is.SameAs(concrete), "the reader and the writer must see one object");
        Assert.That(provider.GetService<EconetServerStatistics>(), Is.SameAs(concrete), "singleton");
    }

    /// <summary>
    /// When the transport refuses the reply, the request is still counted, no bytes are counted
    /// as sent, and one error is recorded.
    /// </summary>
    [Test]
    public async Task Service_ReplySendFails_CountsOneErrorAndNoBytesSent()
    {
        FailingSendTransport transport = new FailingSendTransport(autoAck: false);
        EconetServerStatistics stats = new EconetServerStatistics();
        EconetFileServerService service = BuildService(transport, stats);
        await service.StartAsync(CancellationToken.None);
        try
        {
            EconetMessage request = ReadVersionRequest();
            await transport.DeliverAsync(request);

            Assert.That(stats.Errors, Is.EqualTo(1), "one failed send is one error");
            Assert.That(stats.TotalRequests, Is.EqualTo(1), "the request itself was handled");
            Assert.That(stats.BytesReceived, Is.EqualTo(request.Payload.Length));
            Assert.That(stats.BytesSent, Is.EqualTo(0), "a reply the transport refused is not sent bytes");
            Assert.That(transport.SendAttempts, Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    /// <summary>
    /// When the very first send fails outside the reply step (the automatic acknowledgement of the
    /// inbound packet), the request is abandoned: one error, and the request is not counted.
    /// </summary>
    [Test]
    public async Task Service_RequestThrowsBeforeTheReply_CountsOneErrorAndNoRequest()
    {
        FailingSendTransport transport = new FailingSendTransport(autoAck: true);
        EconetServerStatistics stats = new EconetServerStatistics();
        EconetFileServerService service = BuildService(transport, stats);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await transport.DeliverAsync(ReadVersionRequest());

            Assert.That(stats.Errors, Is.EqualTo(1));
            Assert.That(stats.TotalRequests, Is.EqualTo(0), "an abandoned request is not a handled request");
            Assert.That(stats.BytesSent, Is.EqualTo(0));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }
}
