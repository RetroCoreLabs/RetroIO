//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Api.Node;
using RetroIO.Protocols.Xmsg.Ethernet;
using RetroIO.Protocols.Xmsg.Hosting;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Live.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;

namespace RetroIO.Protocols.Xmsg.Plugins.Server
{
    /// <summary>
    /// Runs an XMSG node inside a server program: it joins an Ethernet segment (normally the hub the
    /// same program runs), and takes connections from client and server programs over gRPC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This makes RetroServer an XMSG machine. A SINTRAN machine on the segment reaches RetroServer's
    /// TAD terminal server and every name a connected program claimed (<c>*CHAT</c> from
    /// <c>retro-chat-server</c>, for example). A C# client program (<c>retro-chat</c>) opens its
    /// port on this node and is never an XMSG machine of its own (Ronny, 2026-11-10).
    /// </para>
    /// <para>
    /// ONE LOOP TOUCHES THE NODE. Packets from the link are queued and handled inside
    /// <c>Pump()</c> (<see cref="XmsgNodeOptions.DeliverInboundOnPump"/>), and gRPC requests are
    /// queued and done inside <see cref="PluginServerHost.Pump"/>. Both run here, one after the
    /// other, every 20 ms - the tick the runner uses, short enough for the TAD server's paired
    /// replies (TAD-Message-Formats.md 22.16).
    /// </para>
    /// <para>
    /// LIMITS, named in RetroIO <c>src/BUGS.md</c>: one peer only (the Ethernet link binds one), and
    /// the bridge's ports come from a second port allocator beside the TAD server's.
    /// </para>
    /// </remarks>
    public sealed class XmsgNodeService : BackgroundService
    {
        /// <summary>
        /// How often the node is pumped, in milliseconds.
        /// </summary>
        private const int TickMs = 20;

        /// <summary>
        /// The name the bridge server registers under on the node.
        /// </summary>
        private const string BridgeName = "*PLUGINS";

        /// <summary>
        /// How long the logged-out TAD sessions' frames get to leave at shutdown.
        /// </summary>
        private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

        private readonly XmsgNodeServiceOptions _options;
        private readonly ILogger _logger;
        private readonly ILoggerFactory _loggerFactory;

        /// <summary>
        /// Initialises the service.
        /// </summary>
        /// <param name="options">
        /// The node's settings.
        /// </param>
        /// <param name="loggerFactory">
        /// Where log lines go, or null to discard them.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> is null.
        /// </exception>
        public XmsgNodeService(XmsgNodeServiceOptions options, ILoggerFactory? loggerFactory)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            _logger = _loggerFactory.CreateLogger<XmsgNodeService>();
        }

        /// <summary>
        /// Gets a value indicating whether the link has heard from the peer.
        /// </summary>
        public bool PeerHeard { get; private set; }

        /// <summary>
        /// Gets the number of programs connected right now.
        /// </summary>
        public int ConnectedPrograms { get; private set; }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.SystemNumber == 0 || _options.PeerSystemNumber == 0)
            {
                _logger.LogError("XMSG node: the system number and the peer's system number are both required; the node is not started.");
                return;
            }

            if (string.IsNullOrEmpty(_options.StateDirectory))
            {
                _logger.LogError("XMSG node: no state folder configured; the node is not started.");
                return;
            }

            IEthernetBackend? backend = EthernetBackendFactory.FromSpec(_options.Ethernet);
            if (backend == null)
            {
                _logger.LogError("XMSG node: the Ethernet setting '{Spec}' is not understood; the node is not started.", _options.Ethernet);
                return;
            }

            ushort self = _options.SystemNumber;
            ushort peer = _options.PeerSystemNumber;
            using EthernetLink link = new EthernetLink("eth:" + peer, self, peer, backend);
            link.Log = Log;

            // The bridge: every connected program's ports and names live in its kernel and name table.
            XmsgKernelServer bridge = XmsgKernelServer.Create(self, (ushort)Environment.TickCount, BridgeName, 4);
            PluginServerHost programs = new PluginServerHost(bridge, new XroutDirectory())
            {
                Token = _options.ProgramToken,
                // The programs' log lines go into the same logging as RetroServer's own lines.
                Logs = _loggerFactory,
                LogMinimum = _options.ProgramLogMinimum,
            };

            TopologyConfig topology = new TopologyConfig { Self = self };
            topology.Nodes.Add(new TopologyNode { Id = self, Reach = "local" });
            topology.Nodes.Add(new TopologyNode { Id = peer, Reach = "neighbour", Link = 1 });
            List<RoutingTableEntry> routes = topology.BuildRoutingEntries(self);

            XmsgHostedNode node = XmsgNodeBuilder.Build(link, self, routes, new XmsgNodeOptions
            {
                StateDirectory = _options.StateDirectory,
                Motd = _options.Motd,
                Log = Log,
                DeliverInboundOnPump = true,
                ExtraServers = new IXmsgServer[] { bridge },
            });

            if (!link.Start())
            {
                _logger.LogError("XMSG node {Self}: the Ethernet link to {Spec} did not start; the node is not running.", self, _options.Ethernet);
                return;
            }

            WebApplication web;
            try
            {
                web = await StartProgramPortAsync(programs, stoppingToken);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is InvalidOperationException || ex is FormatException)
            {
                _logger.LogError(ex, "XMSG node {Self}: the port for programs ({Address}:{Port}) could not be opened.",
                    self, _options.PluginBindAddress, _options.PluginPort);
                return;
            }

            _logger.LogInformation(
                "XMSG node {Self} is up on {Spec}, peer {Peer}. Programs connect to http://{Address}:{Port}.",
                self, _options.Ethernet, peer, _options.PluginBindAddress, _options.PluginPort);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    programs.Pump();
                    node.Host.Pump();

                    if (!PeerHeard && link.HasLearnedPeer)
                    {
                        PeerHeard = true;
                        _logger.LogInformation("XMSG node {Self}: heard from {Peer}; the link is up.", self, peer);
                    }

                    ConnectedPrograms = programs.CopySessions().Length;

                    try
                    {
                        await Task.Delay(TickMs, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                // LOG THE TAD USERS OUT WHILE WE CAN STILL SEND. Stopping with sessions open left
                // D100's XMSG holding half a session and killed it (2026-08-17). Ethernet has no
                // LAPB, so there is no DISC to send after.
                if (XmsgNodeShutdown.LogOutTadSessions(node, Log) > 0)
                {
                    await XmsgNodeShutdown.WaitForEthernetQueueAsync(link, ShutdownGrace, Log);
                }

                await web.StopAsync(CancellationToken.None);
                await web.DisposeAsync();
                _logger.LogInformation("XMSG node {Self} stopped.", self);
            }
        }

        /// <summary>
        /// Opens the gRPC port client and server programs connect to.
        /// </summary>
        /// <param name="programs">
        /// The host that does their requests.
        /// </param>
        /// <param name="token">
        /// Cancels the start.
        /// </param>
        /// <returns>
        /// The running web host.
        /// </returns>
        /// <remarks>
        /// Its own small web host, HTTP/2 only and without TLS: gRPC needs HTTP/2, and an endpoint
        /// that also offers HTTP/1 without TLS can only ever give HTTP/1. Kept apart from the
        /// management API so neither one's address settings change the other.
        /// </remarks>
        private async Task<WebApplication> StartProgramPortAsync(PluginServerHost programs, CancellationToken token)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Parse(_options.PluginBindAddress), _options.PluginPort,
                    listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(programs);

            WebApplication web = builder.Build();
            web.MapGrpcService<PluginConnectionService>();
            await web.StartAsync(token);
            return web;
        }

        /// <summary>
        /// Sends one of the node's own lines to the log.
        /// </summary>
        /// <param name="message">
        /// The line.
        /// </param>
        private void Log(string message)
        {
            _logger.LogInformation("{Line}", message);
        }
    }
}
