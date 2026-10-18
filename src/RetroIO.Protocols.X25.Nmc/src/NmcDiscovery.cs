//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Protocols.X25.Client;

namespace RetroIO.Protocols.X25.Nmc
{
    /// <summary>
    /// Notifies a listener that a discovered node's state changed (identity, neighbours, routes, or stats).
    /// </summary>
    /// <param name="node">
    /// The node whose state changed.
    /// </param>
    public delegate void NmcNodeChanged(NmcNode node);

    /// <summary>
    /// Options for a discovery run.
    /// </summary>
    public sealed class NmcDiscoveryOptions
    {
        /// <summary>
        /// The local switch access host to connect the X.25 client to (XOT).
        /// </summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>
        /// The local switch access port.
        /// </summary>
        public int Port { get; set; } = 2200;

        /// <summary>
        /// The X.121 address the NMC calls from.
        /// </summary>
        public string LocalAddress { get; set; } = "99990002";

        /// <summary>
        /// The management username presented at login.
        /// </summary>
        public string Username { get; set; } = "admin";

        /// <summary>
        /// The management password presented at login.
        /// </summary>
        public string Password { get; set; } = "admin";

        /// <summary>
        /// The maximum walk depth from the seed.
        /// </summary>
        public int MaxDepth { get; set; } = 5;

        /// <summary>
        /// The interval at which authenticated nodes are re-polled for live stats and topology.
        /// </summary>
        public int PollIntervalMs { get; set; } = 3000;
    }

    /// <summary>
    /// Walks a network of switches over the in-band X.25 management API: it connects to one local switch,
    /// calls the seed's management address, logs in, and reads identity, topology and routes; for each
    /// neighbour that advertises a management address it recurses, building a live model of the whole mesh.
    /// Discovery rides real X.25 — the calls route across the actual network, including cross-environment
    /// trunks (see docs/NMC_API_V1.md).
    /// </summary>
    public sealed class NmcDiscovery : IDisposable
    {
        private readonly NmcDiscoveryOptions _options;
        private readonly ILogger _log;
        private readonly object _lock = new object();

        // Node model, keyed by management address; the walk frontier and per-VC session state.
        private readonly Dictionary<string, NmcNode> _nodesByKey = new Dictionary<string, NmcNode>();
        private readonly Dictionary<X25Vc, Session> _sessionByVc = new Dictionary<X25Vc, Session>();
        private readonly HashSet<string> _visited = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<DialRequest> _dialQueue = new Queue<DialRequest>();

        private X25Client? _client;
        private Thread? _worker;
        private volatile bool _running;

        /// <summary>
        /// Raised when a node's state changes so a viewer can update. Fired on a background thread.
        /// </summary>
        public event NmcNodeChanged? NodeChanged;

        /// <summary>
        /// Creates a discovery engine.
        /// </summary>
        /// <param name="options">
        /// The connection, credential and walk options.
        /// </param>
        /// <param name="logger">
        /// Optional logger; defaults to a no-op.
        /// </param>
        public NmcDiscovery(NmcDiscoveryOptions options, ILogger? logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Opens the X.25 client and starts walking from the seed management address.
        /// </summary>
        /// <param name="seedMgmtAddr">
        /// The management address of the switch to start discovery from.
        /// </param>
        /// <returns>
        /// True when the client opened and the seed was queued; false when the client failed to open.
        /// </returns>
        public bool Start(string seedMgmtAddr)
        {
            X25ClientConfig cfg = new X25ClientConfig
            {
                Host = _options.Host,
                Port = (ushort)_options.Port,
                Framing = X25ClientFraming.Xot,
                IsDte = true,
                PacketSize = 128,
                WindowSize = 7,
                NumVcs = 32,
                LocalAddress = _options.LocalAddress
            };

            X25Client client = new X25Client { ServiceName = "Nmc" };
            client.SetOnData(OnData);
            client.SetOnClear(OnClear);

            try
            {
                client.Open(cfg, _log);
            }
            catch (Exception ex)
            {
                _log.LogWarning("NMC discovery: connect failed: {Message}", ex.Message);
                client.Dispose();
                return false;
            }

            _client = client;
            _running = true;

            lock (_lock)
            {
                _dialQueue.Enqueue(new DialRequest(seedMgmtAddr, 0));
            }

            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Nmc-Discover" };
            _worker.Start();
            return true;
        }

        /// <summary>
        /// Stops discovery: tells the worker thread to finish, disposes the X.25 client, and
        /// forgets every discovered node, session, visited address and queued dial.
        /// </summary>
        /// <remarks>
        /// It does not wait for the worker thread to end.
        /// </remarks>
        public void Dispose()
        {
            _running = false;
            X25Client? client = _client;
            _client = null;
            if (client != null)
            {
                client.Dispose();
            }

            lock (_lock)
            {
                _nodesByKey.Clear();
                _sessionByVc.Clear();
                _visited.Clear();
                _dialQueue.Clear();
            }
        }

        // Drains the dial frontier and periodically re-polls authenticated nodes for live state.
        private void WorkerLoop()
        {
            long lastPoll = 0;
            while (_running)
            {
                DialRequest? next = null;
                lock (_lock)
                {
                    if (_dialQueue.Count > 0)
                    {
                        next = _dialQueue.Dequeue();
                    }
                }

                if (next != null)
                {
                    Dial(next.Value.MgmtAddr, next.Value.Depth);
                }

                long now = Environment.TickCount64;
                if (now - lastPoll >= _options.PollIntervalMs)
                {
                    PollAll();
                    lastPoll = now;
                }

                Thread.Sleep(50);
            }
        }

        // Places a management call to a not-yet-visited address, creating its node and session.
        private void Dial(string mgmtAddr, int depth)
        {
            if (string.IsNullOrEmpty(mgmtAddr))
            {
                return;
            }

            X25Client? client = _client;
            if (client == null)
            {
                return;
            }

            NmcNode node;
            lock (_lock)
            {
                if (_visited.Contains(mgmtAddr))
                {
                    return;
                }
                _visited.Add(mgmtAddr);

                node = new NmcNode(mgmtAddr) { Depth = depth };
                _nodesByKey[mgmtAddr] = node;
            }

            RaiseChanged(node);

            X25Vc? vc = client.Call(mgmtAddr);
            if (vc == null)
            {
                _log.LogWarning("NMC discovery: call to {Addr} failed", mgmtAddr);
                return;
            }

            lock (_lock)
            {
                _sessionByVc[vc] = new Session(vc, mgmtAddr, depth, node);
            }
        }

        // Re-polls each authenticated node for stats and topology so up/down and counters stay live.
        private void PollAll()
        {
            X25Client? client = _client;
            if (client == null)
            {
                return;
            }

            List<Session> sessions = new List<Session>();
            lock (_lock)
            {
                foreach (KeyValuePair<X25Vc, Session> kv in _sessionByVc)
                {
                    if (kv.Value.Authenticated)
                    {
                        sessions.Add(kv.Value);
                    }
                }
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                Send(client, sessions[i].Vc, "GET_STATS", true);
                Send(client, sessions[i].Vc, "GET_TOPOLOGY", true);
            }
        }

        private void OnData(X25Client client, X25Vc vc, byte[] data, int length, bool qBit, bool mBit, bool dBit)
        {
            Session? session;
            lock (_lock)
            {
                _sessionByVc.TryGetValue(vc, out session);
            }

            if (session == null)
            {
                return;
            }

            string text = Encoding.ASCII.GetString(data, 0, length);
            if (!session.Authenticated)
            {
                session.AuthBuffer.Append(text);
                RunAuth(client, session);
            }
            else
            {
                session.ApiBuffer.Append(text);
                DrainApiLines(session);
            }
        }

        private void OnClear(X25Client client, X25Vc vc, byte cause, byte diag)
        {
            Session? session;
            lock (_lock)
            {
                if (_sessionByVc.TryGetValue(vc, out session))
                {
                    _sessionByVc.Remove(vc);
                }
            }

            if (session != null)
            {
                session.Node.Reachable = false;
                RaiseChanged(session.Node);
            }
        }

        // Drives the login handshake on the interactive (Q=0) stream, then kicks off the initial requests.
        private void RunAuth(X25Client client, Session session)
        {
            string buf = session.AuthBuffer.ToString();

            if (session.AuthPhase == 0 && buf.Contains("Username:"))
            {
                Send(client, session.Vc, _options.Username, false);
                session.AuthPhase = 1;
                session.AuthBuffer.Clear();
            }
            else if (session.AuthPhase == 1 && buf.Contains("Password:"))
            {
                Send(client, session.Vc, _options.Password, false);
                session.AuthPhase = 2;
                session.AuthBuffer.Clear();
            }
            else if (session.AuthPhase == 2)
            {
                if (buf.Contains("Welcome"))
                {
                    session.Authenticated = true;
                    session.AuthBuffer.Clear();
                    session.Node.Reachable = true;
                    RaiseChanged(session.Node);

                    // One-shot identity + topology + routes; stats/topology are re-polled for liveness.
                    Send(client, session.Vc, "GET_INFO", true);
                    Send(client, session.Vc, "GET_TOPOLOGY", true);
                    Send(client, session.Vc, "GET_ROUTES", true);
                }
                else if (buf.Contains("incorrect"))
                {
                    session.AuthPhase = 0;
                    session.AuthBuffer.Clear();
                    _log.LogWarning("NMC discovery: login incorrect at {Addr}", session.MgmtAddr);
                }
            }
        }

        // Splits the API buffer into complete lines and applies each to the session's node.
        private void DrainApiLines(Session session)
        {
            string all = session.ApiBuffer.ToString();
            int nl;
            while ((nl = all.IndexOf('\n')) >= 0)
            {
                string line = all.Substring(0, nl);
                all = all.Substring(nl + 1);
                ApplyLine(session, line);
            }

            session.ApiBuffer.Clear();
            session.ApiBuffer.Append(all);
        }

        private void ApplyLine(Session session, string line)
        {
            NmcParsedLine parsed = NmcLineParser.Parse(line);
            NmcNode node = session.Node;

            switch (parsed.Kind)
            {
                case NmcLineKind.Info:
                    node.Info = parsed.Info;
                    RaiseChanged(node);
                    break;

                case NmcLineKind.Stats:
                    node.Stats = parsed.Stats;
                    RaiseChanged(node);
                    break;

                case NmcLineKind.Route:
                    if (parsed.Route != null)
                    {
                        MergeRoute(node, parsed.Route);
                        RaiseChanged(node);
                    }
                    break;

                case NmcLineKind.Neighbour:
                    if (parsed.Neighbour != null)
                    {
                        MergeNeighbour(node, parsed.Neighbour);
                        EnqueueNeighbour(parsed.Neighbour, session.Depth);
                        RaiseChanged(node);
                    }
                    break;

                default:
                    break;
            }
        }

        // Updates an existing neighbour (matched by via port) in place, or adds a new one — avoids flicker
        // when topology is re-polled.
        private static void MergeNeighbour(NmcNode node, NmcNeighbour nb)
        {
            for (int i = 0; i < node.Neighbours.Count; i++)
            {
                if (node.Neighbours[i].Via == nb.Via)
                {
                    node.Neighbours[i].Name = nb.Name;
                    node.Neighbours[i].MgmtAddr = nb.MgmtAddr;
                    node.Neighbours[i].Up = nb.Up;
                    return;
                }
            }

            node.Neighbours.Add(nb);
        }

        // Adds a route the first time it is seen (by type+addr+port), so re-fetches do not duplicate it.
        private static void MergeRoute(NmcNode node, NmcRoute route)
        {
            for (int i = 0; i < node.Routes.Count; i++)
            {
                NmcRoute r = node.Routes[i];
                if (r.Type == route.Type && r.Address == route.Address && r.Port == route.Port)
                {
                    return;
                }
            }

            node.Routes.Add(route);
        }

        // Queues an unvisited neighbour with a management address for dialing, within the depth limit.
        private void EnqueueNeighbour(NmcNeighbour nb, int parentDepth)
        {
            if (nb.MgmtAddr.Length == 0 || parentDepth + 1 > _options.MaxDepth)
            {
                return;
            }

            lock (_lock)
            {
                if (!_visited.Contains(nb.MgmtAddr))
                {
                    _dialQueue.Enqueue(new DialRequest(nb.MgmtAddr, parentDepth + 1));
                }
            }
        }

        private void Send(X25Client client, X25Vc vc, string text, bool qBit)
        {
            // Interactive (auth) lines are CR-terminated; machine API requests are LF-terminated and Q=1.
            string framed = qBit ? text + "\n" : text + "\r";
            byte[] bytes = Encoding.ASCII.GetBytes(framed);
            client.Send(vc, bytes, bytes.Length, qBit);
        }

        private void RaiseChanged(NmcNode node)
        {
            NmcNodeChanged? handler = NodeChanged;
            if (handler != null)
            {
                handler(node);
            }
        }

        // A pending management call: the address to dial and the depth it was discovered at.
        private readonly struct DialRequest
        {
            public readonly string MgmtAddr;
            public readonly int Depth;

            public DialRequest(string mgmtAddr, int depth)
            {
                MgmtAddr = mgmtAddr;
                Depth = depth;
            }
        }

        // Per-VC session state: the login phase, receive buffers, and the node being populated.
        private sealed class Session
        {
            public readonly X25Vc Vc;
            public readonly string MgmtAddr;
            public readonly int Depth;
            public readonly NmcNode Node;
            public int AuthPhase;
            public bool Authenticated;
            public readonly StringBuilder AuthBuffer = new StringBuilder();
            public readonly StringBuilder ApiBuffer = new StringBuilder();

            public Session(X25Vc vc, string mgmtAddr, int depth, NmcNode node)
            {
                Vc = vc;
                MgmtAddr = mgmtAddr;
                Depth = depth;
                Node = node;
            }
        }
    }
}
