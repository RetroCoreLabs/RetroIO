//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//


using System;
using System.Collections.Generic;

using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Ndfs;
using RetroIO.Protocols.Xmsg.Node;
using RetroIO.Protocols.Xmsg.Node.Seam;
using RetroIO.Protocols.Xmsg.Node.Services;
using RetroIO.Protocols.Xmsg.Servers.Fa;
using RetroIO.Protocols.Xmsg.Servers.Tad;

namespace RetroIO.Protocols.Xmsg.Hosting
{
    /// <summary>
    /// Builds an XMSG node on a link: its stores, its TAD terminal server, its optional file
    /// server and the <see cref="XmsgNodeHost"/>, wired the way the runner has always wired them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was written out twice inside the runner, once for HDLC and once for Ethernet, and the
    /// two copies drifted: the seed store, the server-host log and <c>--announce-restart</c> were
    /// each wired on one path and silently missing from the other. One builder, used by the runner
    /// and by a hosted service, is how that stops happening.
    /// </para>
    /// <para>
    /// What is NOT here is anything a particular host decides for itself: frame dumps, LAPB
    /// storm reporting, one-shot transfers, the pump loop. Those stay with the host.
    /// </para>
    /// </remarks>
    public static class XmsgNodeBuilder
    {
        /// <summary>
        /// Builds a node on one link.
        /// </summary>
        /// <param name="link">
        /// The link the node speaks over (an HDLC adapter or an Ethernet link).
        /// </param>
        /// <param name="nodeNumber">
        /// Our own ND system number.
        /// </param>
        /// <param name="routingEntries">
        /// The routes this node advertises.
        /// </param>
        /// <param name="options">
        /// The state folder, TAD accounts, banner, file server and log.
        /// </param>
        /// <returns>
        /// The node and the parts a host needs to reach later.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="link"/>, <paramref name="routingEntries"/> or
        /// <paramref name="options"/> is null, or <see cref="XmsgNodeOptions.StateDirectory"/> is
        /// null.
        /// </exception>
        public static XmsgHostedNode Build(
            ILink link, ushort nodeNumber, IReadOnlyList<RoutingTableEntry> routingEntries, XmsgNodeOptions options)
        {
            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            if (routingEntries == null)
            {
                throw new ArgumentNullException(nameof(routingEntries));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.StateDirectory == null)
            {
                throw new ArgumentNullException(nameof(options), "StateDirectory is required.");
            }

            XmsgLogHandler? log = options.Log;

            // Persist our outgoing datagram sequence per remote node across restarts, so we
            // continue in step with the peer's persistent expected-from-us instead of resetting to
            // 0x0000 and being silently dropped. See XMSG-SEQUENCE-RESTART-ANSWER doc.
            string sequencePath = XmsgStateFiles.PathOf(options.StateDirectory, XmsgStateFiles.Sequence);
            FileResponderSequenceStore sequenceStore = new FileResponderSequenceStore(sequencePath);
            log?.Invoke($"[node] datagram-sequence state file: {sequencePath}");

            // The MOTD banner middle line comes from the caller when set; otherwise the server uses
            // its built-in "Emulated TAD server version vN.N.N". The host-id line is generated from
            // our own node.
            TadUserDirectory users = options.TadUsers ?? new TadUserDirectory();
            TadServer tadServer = new TadServer(options.Clock ?? DefaultClock, users: users, motdLine: options.Motd);
            log?.Invoke($"[tad] {users.Count} login account(s) loaded");
            if (log != null)
            {
                tadServer.SessionOpened += (tadNumber, clientSystem) =>
                    log($"[tad] session opened: tty{tadNumber} from node {clientSystem}");
                tadServer.SessionClosed += (tadNumber, clientSystem) =>
                    log($"[tad] session closed: tty{tadNumber} from node {clientSystem}");
            }

            XmsgNodeHost host = new XmsgNodeHost(
                link, nodeNumber, routingEntries, sequenceStore,
                AddServers(BuildServerList(tadServer, options.FileServer), options.ExtraServers));
            host.DeliverInboundOnPump = options.DeliverInboundOnPump;

            // BOTH logs, on every transport. The server host logs the sequence a link starts from,
            // every Flags 1 it stamps and every store advance; the layer logs why a peer rejected a
            // frame. Each was once wired on one transport and not the other, and an afternoon of
            // sequence work was done on inference because of it. A diagnostic that is not plugged
            // in is not a diagnostic.
            host.ServerHost.Log = log;
            host.Layer.Log = log;

            // Wire the introspection commands: "list servers" reads the host's registered servers;
            // "list route" formats the routing table this node advertises.
            tadServer.ServerDirectory = host.ServerHost.DescribeServers;
            tadServer.RouteReport = host.FormatRouteReport;

            // REMEMBER THE ENVELOPE SEED. It is a per-link constant that survives the peer
            // rebooting, and remembering it is what lets this process address a machine it has met
            // before without waiting to be spoken to first.
            string seedPath = XmsgStateFiles.PathOf(options.StateDirectory, XmsgStateFiles.LinkSeed);
            host.ServerHost.SeedStore = new FileLinkSeedStore(seedPath);
            log?.Invoke($"[node] link-seed state file: {seedPath}");

            return new XmsgHostedNode(host, tadServer, options.FileServer, sequenceStore);
        }

        /// <summary>
        /// Creates the COSMOS file server (<c>*FA-SERVER</c>) over the configured Windows folder.
        /// </summary>
        /// <param name="config">
        /// The <c>fileServer</c> section of the topology, or null when there is none.
        /// </param>
        /// <param name="baseDirectory">
        /// The folder a relative <c>root</c> is taken from.
        /// </param>
        /// <param name="stateDirectory">
        /// The folder that holds <c>fa-connection.state</c>.
        /// </param>
        /// <param name="log">
        /// Where the server's own lines, and why it is off, are written. Null discards them.
        /// </param>
        /// <returns>
        /// The file server, or <c>null</c> when no folder is configured, the block is disabled, or the
        /// folder cannot be opened.
        /// </returns>
        /// <remarks>
        /// <b>No folder configured means NO file server</b>, and a line in the log saying so. Falling
        /// back to a folder next to the executable would share the runner's own logs, state files and
        /// DLLs with any machine that asked, which nobody chose. Sharing files is an explicit act.
        /// </remarks>
        public static FaServer? CreateFileServer(
            FileServerConfig? config, string baseDirectory, string stateDirectory, XmsgLogHandler? log)
        {
            if (baseDirectory == null)
            {
                throw new ArgumentNullException(nameof(baseDirectory));
            }

            if (stateDirectory == null)
            {
                throw new ArgumentNullException(nameof(stateDirectory));
            }

            if (config == null || string.IsNullOrWhiteSpace(config.Root))
            {
                log?.Invoke(
                    "[fa] file server OFF: no \"fileServer\": { \"root\": \"...\" } in the topology file. "
                    + "Nothing is served. Set a folder to turn it on.");
                return null;
            }

            if (!config.Enabled)
            {
                log?.Invoke($"[fa] file server OFF: \"enabled\" is false (the configured folder was {config.Root}).");
                return null;
            }

            // A relative folder is taken from the base folder (the runner passes its own folder); an absolute path is honoured as-is - the
            // same rule the log file uses, so one convention covers both.
            string root = System.IO.Path.IsPathRooted(config.Root)
                ? config.Root
                : System.IO.Path.Combine(baseDirectory, config.Root);

            try
            {
                FolderFileStore store = new FolderFileStore(root);
                FaServer server = new FaServer(store, config.UserIndex);
                server.NextConnectionNumber = FaConnectionNumbers.Reserve(
                    XmsgStateFiles.PathOf(stateDirectory, XmsgStateFiles.FaConnection), log);
                if (log != null)
                {
                    server.Log += line => log(line);
                }
                log?.Invoke($"[fa] file server ON as {FaServer.ServerName} (logical port {FaServer.ServerLogicalPort}, wire port 0x{FaServer.FaServerWirePort:X4})");
                log?.Invoke($"[fa] serving folder: {store.RootFolder}");
                log?.Invoke($"[fa] files visible now: {store.ListFiles().Count}");
                return server;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[fa] file server OFF: folder '{root}' could not be opened ({ex.Message}).");
                return null;
            }
        }

        /// <summary>
        /// Appends extra servers to a server list.
        /// </summary>
        /// <param name="servers">
        /// The list so far.
        /// </param>
        /// <param name="extra">
        /// The servers to add, or null.
        /// </param>
        /// <returns>
        /// <paramref name="servers"/> when there is nothing to add, otherwise a new array with both.
        /// </returns>
        private static IXmsgServer[] AddServers(IXmsgServer[] servers, IXmsgServer[]? extra)
        {
            if (extra == null || extra.Length == 0)
            {
                return servers;
            }

            IXmsgServer[] all = new IXmsgServer[servers.Length + extra.Length];
            Array.Copy(servers, all, servers.Length);
            Array.Copy(extra, 0, all, servers.Length, extra.Length);
            return all;
        }

        /// <summary>
        /// Builds the server list handed to the node host: the TAD server always, plus the file server
        /// when one is configured.
        /// </summary>
        /// <param name="tadServer">
        /// The TAD terminal server.
        /// </param>
        /// <param name="fileServer">
        /// The COSMOS file server, or <c>null</c> when it is off.
        /// </param>
        /// <returns>
        /// The servers to register, in order.
        /// </returns>
        public static IXmsgServer[] BuildServerList(TadServer tadServer, FaServer? fileServer)
        {
            if (fileServer == null)
            {
                return new IXmsgServer[] { tadServer };
            }

            return new IXmsgServer[] { tadServer, fileServer };
        }

        /// <summary>
        /// Builds the two node hosts a relay runs on - one per link - with the SAME servers registered
        /// on both.
        /// </summary>
        /// <param name="inbound">
        /// The link the relay listens on.
        /// </param>
        /// <param name="outbound">
        /// The link the relay dials out on.
        /// </param>
        /// <param name="node">
        /// Our own node number.
        /// </param>
        /// <param name="routingEntries">
        /// The routing table both hosts answer reachability questions from.
        /// </param>
        /// <param name="sequenceStore">
        /// The persisted responder sequence, shared by both hosts as it always has been.
        /// </param>
        /// <param name="servers">
        /// The servers to register on both hosts.
        /// </param>
        /// <returns>
        /// Two hosts: the inbound one at index 0, the outbound one at index 1.
        /// </returns>
        /// <remarks>
        /// <para><b>Why this is a method and not two lines at the call site</b></para>
        /// <para>
        /// Because the two lines disagreed, and nothing noticed for as long as the relay was only ever
        /// asked to FORWARD. The outbound host was built with no server list at all, so a datagram a
        /// peer on that link addressed TO US - as opposed to asking us to pass it on - reached
        /// <c>XmsgServerHost.Route</c>, found no server to hand it to, and produced nothing.
        /// </para>
        /// <para>
        /// What that looked like from the outside, on 2026-08-08: typing
        /// <c>LI-FI D19999(SYSTEM).,,</c> on D100 hung the terminal until SINTRAN gave up with
        /// "NO ANSWER FROM REMOTE SYSTEM; FILE-ACCESS CONNECTION ABORTED". The capture
        /// (<c>DOC/captures/FA-OPERATIONS-2026-08-08</c>) shows the reachability request answered
        /// normally, then the *FA-SERVER connect letter - body <c>1B41 0014 FF0A</c> "*FA-SERVER"
        /// <c>FE06</c> "D19999" - answered with a BARE ACK and nothing else. The relay counters never
        /// move during any of it, because a datagram addressed to our own node is not transit, so the
        /// log was silent on both sides.
        /// </para>
        /// <para>
        /// ONE set of server instances, not one per link. A session is keyed by its wire port and
        /// records the node and system it belongs to, and every reply is built through whichever host
        /// routed the request - so answers still leave by the link they arrived on, and a client that
        /// reaches us over either link talks to the same served folder.
        /// </para>
        /// </remarks>
        public static XmsgNodeHost[] BuildRelayHosts(
            ILink inbound,
            ILink outbound,
            ushort node,
            IReadOnlyList<RoutingTableEntry> routingEntries,
            IResponderSequenceStore sequenceStore,
            IXmsgServer[] servers)
        {
            return new XmsgNodeHost[]
            {
                new XmsgNodeHost(inbound, node, routingEntries, sequenceStore, servers),
                new XmsgNodeHost(outbound, node, routingEntries, sequenceStore, servers),
            };
        }

        /// <summary>
        /// The clock the TAD server stamps its banner and session times with by default.
        /// </summary>
        /// <returns>
        /// The local time now.
        /// </returns>
        private static DateTime DefaultClock()
        {
            return DateTime.Now;
        }
    }
}
