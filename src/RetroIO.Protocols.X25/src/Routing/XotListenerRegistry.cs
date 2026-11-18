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
using System.Threading;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// Something that wants the connections that arrive on a shared XOT listening port: an XOT
    /// server entry or an XOT peer entry.
    /// </summary>
    public interface IXotListenerClient
    {
        /// <summary>
        /// Gets the entry name, for log lines.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the IP address of the one peer this entry stands for, or null when the entry takes
        /// every address (an XOT server entry does; so does a peer entry with no address).
        /// </summary>
        IPAddress? PeerAddress { get; }

        /// <summary>
        /// Gets whether this entry wants connections from addresses nobody lists to be refused.
        /// </summary>
        bool RefuseUnlisted { get; }

        /// <summary>
        /// Gets whether the entry has a free connection slot (one slot per X.25 channel of its line).
        /// The shared socket accepts only while at least one entry on it has one; with every entry full
        /// the next connection waits in the TCP backlog, as on x25emu's XOT port. An entry that is
        /// full when a connection is matched to it keeps the connection open and unread until a slot
        /// frees, and calls <see cref="XotListenerRegistry.SlotFreed"/> whenever one does.
        /// </summary>
        bool HasFreeSlot { get; }

        /// <summary>
        /// Called on the listener's thread with a connection that was matched to this entry.
        /// </summary>
        /// <param name="client">The accepted TCP connection. The entry owns it from now on.</param>
        /// <param name="remote">The source address of the connection.</param>
        /// <param name="listed">
        /// True when the source address is this entry's peer address (or the entry takes every
        /// address); false when no entry lists the address and this entry was handed the connection
        /// as the default owner.
        /// </param>
        void OnConnection(TcpClient client, IPAddress remote, bool listed);
    }

    /// <summary>
    /// Keeps ONE listening socket per local address and port, however many XOT entries use that port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An operating system lets only one socket listen on a port. RFC 1613 (section 5) sends every
    /// XOT connection to the same TCP port, 1998, so several entries (one per peer) that all want
    /// port 1998 have to share the one socket. The registry hands each incoming connection to the
    /// entry whose peer address equals the connection's source address.
    /// </para>
    /// <para>
    /// Cisco matches incoming XOT calls by the source IP address too (the optional xot access-group
    /// lists); RFC 1613 itself says nothing about who may connect (no handshake, no peer identity, no
    /// security). Behind a NAT router every device shares the translated address, so one address can
    /// stand for several machines.
    /// </para>
    /// <para>
    /// A connection from an address that no entry lists goes to the first entry that takes every
    /// address (an old-style server entry), or else to the first entry on the port as an unlisted
    /// peer, unless some entry on the port asks for unlisted addresses to be refused.
    /// </para>
    /// </remarks>
    public sealed class XotListenerRegistry : IDisposable
    {
        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private readonly Dictionary<string, SharedListener> _listeners = new Dictionary<string, SharedListener>();

        /// <summary>
        /// Creates an empty registry.
        /// </summary>
        /// <param name="logger">Diagnostics logger; the refusal warnings go here.</param>
        public XotListenerRegistry(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets how many listening sockets are open right now.
        /// </summary>
        public int ListeningSocketCount
        {
            get
            {
                lock (_lock)
                {
                    return _listeners.Count;
                }
            }
        }

        /// <summary>
        /// Adds an entry to the listener for an address and port, opening the socket if it is the
        /// first entry there.
        /// </summary>
        /// <param name="address">The local address to listen on, for example 0.0.0.0.</param>
        /// <param name="port">The local TCP port.</param>
        /// <param name="client">The entry.</param>
        /// <exception cref="SocketException">Thrown when the socket cannot be opened (for example the port is in use by another program).</exception>
        public void Join(string address, int port, IXotListenerClient client)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            string key = address + ":" + port;
            lock (_lock)
            {
                if (!_listeners.TryGetValue(key, out SharedListener? listener))
                {
                    listener = new SharedListener(_logger, IPAddress.Parse(address), port);
                    listener.Open();
                    _listeners[key] = listener;
                }

                listener.Add(client);
            }
        }

        /// <summary>
        /// Takes an entry off whichever listener it joined; the socket closes with the last entry.
        /// </summary>
        /// <param name="client">The entry.</param>
        public void Leave(IXotListenerClient client)
        {
            lock (_lock)
            {
                List<string> empty = new List<string>();
                foreach (KeyValuePair<string, SharedListener> kv in _listeners)
                {
                    kv.Value.Remove(client);
                    if (kv.Value.ClientCount == 0)
                    {
                        kv.Value.Close();
                        empty.Add(kv.Key);
                    }
                }

                for (int i = 0; i < empty.Count; i++)
                {
                    _listeners.Remove(empty[i]);
                }
            }
        }

        /// <summary>
        /// Tells every listening socket that an entry has given a connection slot back, so a socket
        /// that stopped accepting because every entry on it was full looks again.
        /// </summary>
        public void SlotFreed()
        {
            lock (_lock)
            {
                foreach (KeyValuePair<string, SharedListener> kv in _listeners)
                {
                    kv.Value.WakeForRoom();
                }
            }
        }

        /// <summary>
        /// Closes every listening socket.
        /// </summary>
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (KeyValuePair<string, SharedListener> kv in _listeners)
                {
                    kv.Value.Close();
                }

                _listeners.Clear();
            }
        }

        /// <summary>
        /// One open listening socket and the entries that share it.
        /// </summary>
        private sealed class SharedListener
        {
            private readonly ILogger _logger;
            private readonly IPAddress _address;
            private readonly int _port;
            private readonly object _clientLock = new object();
            private readonly List<IXotListenerClient> _clients = new List<IXotListenerClient>();
            private TcpListener? _tcp;
            private volatile bool _closing;

            // Set when a slot may have freed (or an entry joined, or the socket is closing).
            private readonly AutoResetEvent _room = new AutoResetEvent(false);

            public void WakeForRoom()
            {
                _room.Set();
            }

            /// <summary>
            /// True when some entry on this socket has a free slot, or there is no entry at all (the
            /// connection is then accepted and closed by Dispatch, "no entry is listening here").
            /// </summary>
            private bool AnyRoom()
            {
                lock (_clientLock)
                {
                    if (_clients.Count == 0)
                    {
                        return true;
                    }

                    for (int i = 0; i < _clients.Count; i++)
                    {
                        if (_clients[i].HasFreeSlot)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public SharedListener(ILogger logger, IPAddress address, int port)
            {
                _logger = logger;
                _address = address;
                _port = port;
            }

            public int ClientCount
            {
                get
                {
                    lock (_clientLock)
                    {
                        return _clients.Count;
                    }
                }
            }

            public void Open()
            {
                _tcp = new TcpListener(_address, _port);
                _tcp.Start();
                _logger.LogInformation("XOT listener opened on {Address}:{Port}", _address, _port);
                TcpListener tcp = _tcp;
                new Thread(() => AcceptLoop(tcp)) { IsBackground = true, Name = "XotListener:" + _port }.Start();
            }

            public void Add(IXotListenerClient client)
            {
                lock (_clientLock)
                {
                    // Two entries for the same peer address on one port cannot both be told apart by
                    // the source address (this is what a NAT router does to several machines). The
                    // first one gets the connections; say so now rather than let it surprise someone.
                    if (client.PeerAddress != null)
                    {
                        for (int i = 0; i < _clients.Count; i++)
                        {
                            if (_clients[i].PeerAddress != null && _clients[i].PeerAddress!.Equals(client.PeerAddress))
                            {
                                _logger.LogWarning("XOT entries {First} and {Second} both list peer {Address} on port {Port}: the source address cannot tell them apart, so the first one gets every connection from it",
                                    _clients[i].Name, client.Name, client.PeerAddress, _port);
                            }
                        }
                    }

                    _clients.Add(client);
                }

                _room.Set();
            }

            public void Remove(IXotListenerClient client)
            {
                lock (_clientLock)
                {
                    _clients.Remove(client);
                }
            }

            public void Close()
            {
                _closing = true;
                _room.Set();
                try
                {
                    _tcp?.Stop();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error closing XOT listener on port {Port}", _port);
                }
            }

            private void AcceptLoop(TcpListener tcp)
            {
                while (!_closing)
                {
                    // Copied from x25emu's XOT port: the listener is not polled while no slot is free,
                    // so the next connection waits in the TCP backlog until a connection closes
                    // (tools/x25switch.c, xot_port_io_loop and conn_slot_free). Accepting it and then
                    // closing it is what a caller sees as its call lost (cause 0x09).
                    if (!AnyRoom())
                    {
                        _room.WaitOne();
                        continue;
                    }

                    TcpClient client;
                    try
                    {
                        client = tcp.AcceptTcpClient();
                    }
                    catch (Exception ex)
                    {
                        if (!_closing)
                        {
                            _logger.LogError(ex, "Error accepting an XOT connection on port {Port}", _port);
                            if (ex is ObjectDisposedException || ex is InvalidOperationException)
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }

                        continue;
                    }

                    try
                    {
                        Dispatch(client);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error handing out an XOT connection on port {Port}", _port);
                        client.Close();
                    }
                }
            }

            private void Dispatch(TcpClient client)
            {
                IPEndPoint? remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                if (remoteEndPoint == null)
                {
                    client.Close();
                    return;
                }

                IPAddress remote = remoteEndPoint.Address.IsIPv4MappedToIPv6 ? remoteEndPoint.Address.MapToIPv4() : remoteEndPoint.Address;

                IXotListenerClient? target = null;
                bool listed = true;
                bool refuse = false;
                lock (_clientLock)
                {
                    // 1. The entry whose peer address is the source address.
                    for (int i = 0; i < _clients.Count && target == null; i++)
                    {
                        IPAddress? peer = _clients[i].PeerAddress;
                        if (peer != null && peer.Equals(remote))
                        {
                            target = _clients[i];
                        }
                    }

                    if (target == null)
                    {
                        // 2. Nobody lists this address. An entry that takes every address gets it
                        // (listed: it asked for everything); else the first entry as an unlisted peer.
                        for (int i = 0; i < _clients.Count; i++)
                        {
                            if (_clients[i].RefuseUnlisted)
                            {
                                refuse = true;
                            }
                        }

                        for (int i = 0; i < _clients.Count && target == null; i++)
                        {
                            if (_clients[i].PeerAddress == null)
                            {
                                target = _clients[i];
                            }
                        }

                        if (target == null && _clients.Count > 0)
                        {
                            target = _clients[0];
                            listed = false;
                        }
                    }
                }

                if (target == null)
                {
                    _logger.LogWarning("XOT connection from {Remote} on port {Port}: no entry is listening here, closed", remote, _port);
                    client.Close();
                    return;
                }

                if (refuse && (!listed || target.PeerAddress == null))
                {
                    _logger.LogWarning("XOT connection refused: {Remote} is not a listed peer (port {Port})", remote, _port);
                    CountRefusal(target, remote);
                    client.Close();
                    return;
                }

                target.OnConnection(client, remote, listed);
            }

            private static void CountRefusal(IXotListenerClient target, IPAddress remote)
            {
                if (target is IXotRefusalCounter counter)
                {
                    counter.NoteRefused(remote);
                }
            }
        }
    }

    /// <summary>
    /// Implemented by an entry that wants to be told when the listener refused a connection on its
    /// behalf, so it can count it.
    /// </summary>
    public interface IXotRefusalCounter
    {
        /// <summary>
        /// Called when a connection from an unlisted address was refused.
        /// </summary>
        /// <param name="remote">The source address.</param>
        void NoteRefused(IPAddress remote);
    }
}
