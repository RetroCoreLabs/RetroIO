using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RetroIO.Protocols.X25.Monitor
{
    /// <summary>
    /// A reusable, passive monitoring server: it accepts TCP connections from viewer clients, applies each
    /// client's server-side <see cref="MonitorFilter"/>, and pushes decoded <see cref="CaptureRecord"/>s to
    /// the clients whose filter matches. A switch or a standalone tap hosts one of these and calls
    /// <see cref="Publish"/> for every captured frame.
    /// </summary>
    /// <remarks>
    /// The server is transport-only: producing capture records (decoding frames with
    /// <see cref="X25CaptureDecoder"/>) is the host's job, so the same server serves XOT, serial-LAPB, or any
    /// other capture source. It is passive — clients subscribe and filter, they do not place calls.
    /// </remarks>
    public sealed class MonitorServer : IDisposable
    {
        private readonly string _host;
        private readonly object _lock = new object();
        private readonly List<Client> _clients = new List<Client>();

        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private int _port;

        // Last topology frame, cached so a newly connected viewer can seed its network map immediately
        // instead of waiting for the next periodic publish.
        private volatile byte[]? _lastTopologyFrame;

        /// <summary>
        /// Creates a monitoring server bound to the given endpoint.
        /// </summary>
        /// <param name="host">
        /// The local address to bind (for example <c>127.0.0.1</c> or <c>0.0.0.0</c>).
        /// </param>
        /// <param name="port">
        /// The TCP port to listen on; 0 lets the OS assign one (read back from <see cref="ListenPort"/>).
        /// </param>
        public MonitorServer(string host, int port)
        {
            _host = host;
            _port = port;
        }

        /// <summary>
        /// The TCP port the server is bound to, valid after <see cref="Start"/> (the OS-assigned port when
        /// constructed with port 0).
        /// </summary>
        public int ListenPort => _port;

        /// <summary>
        /// The number of currently connected viewer clients.
        /// </summary>
        public int ClientCount
        {
            get
            {
                lock (_lock)
                {
                    return _clients.Count;
                }
            }
        }

        /// <summary>
        /// Starts listening and accepting viewer connections.
        /// </summary>
        /// <returns>
        /// True if the listener bound successfully.
        /// </returns>
        public bool Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Parse(_host), _port);
                _listener.Start();
                _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _running = true;

                _acceptThread = new Thread(AcceptLoop)
                {
                    Name = "MonitorServer-Accept",
                    IsBackground = true
                };
                _acceptThread.Start();
                return true;
            }
            catch (SocketException)
            {
                _running = false;
                return false;
            }
        }

        /// <summary>
        /// Pushes a capture record to every connected client whose filter matches it.
        /// </summary>
        /// <param name="record">
        /// The decoded capture record to publish.
        /// </param>
        public void Publish(CaptureRecord record)
        {
            Client[] snapshot;
            lock (_lock)
            {
                if (_clients.Count == 0)
                {
                    return;
                }
                snapshot = _clients.ToArray();
            }

            byte[]? framed = null;
            for (int i = 0; i < snapshot.Length; i++)
            {
                Client client = snapshot[i];
                // Capture carries the raw frame (not decoded fields), so the server can only filter by port;
                // the viewer applies the address/packet-type dimensions after decoding.
                if (!client.Filter.MatchesPort(record.Port))
                {
                    continue;
                }

                // Encode lazily: only build the frame once we know at least one client wants it.
                if (framed == null)
                {
                    framed = MonitorProtocol.Encode(new MonitorMessage
                    {
                        Type = MonitorProtocol.TypeCapture,
                        Capture = record
                    });
                }

                client.Send(framed);
            }
        }

        /// <summary>
        /// Pushes a link/port status snapshot to every connected client. Status is not capture-filtered.
        /// </summary>
        /// <param name="snapshot">
        /// The status snapshot to publish.
        /// </param>
        public void PublishStatus(StatusSnapshot snapshot)
        {
            Client[] snapshotClients;
            lock (_lock)
            {
                if (_clients.Count == 0)
                {
                    return;
                }
                snapshotClients = _clients.ToArray();
            }

            byte[] framed = MonitorProtocol.Encode(new MonitorMessage
            {
                Type = MonitorProtocol.TypeStatus,
                Status = snapshot
            });

            for (int i = 0; i < snapshotClients.Length; i++)
            {
                snapshotClients[i].Send(framed);
            }
        }

        /// <summary>
        /// Pushes a log record to every connected client. Logs are not capture-filtered.
        /// </summary>
        /// <param name="record">
        /// The log record to publish.
        /// </param>
        public void PublishLog(LogRecord record)
        {
            Client[] snapshotClients;
            lock (_lock)
            {
                if (_clients.Count == 0)
                {
                    return;
                }
                snapshotClients = _clients.ToArray();
            }

            byte[] framed = MonitorProtocol.Encode(new MonitorMessage
            {
                Type = MonitorProtocol.TypeLog,
                Log = record
            });

            for (int i = 0; i < snapshotClients.Length; i++)
            {
                snapshotClients[i].Send(framed);
            }
        }

        /// <summary>
        /// Pushes a topology snapshot to every connected client and caches it so future clients receive it on
        /// connect. Topology is not capture-filtered.
        /// </summary>
        /// <param name="snapshot">
        /// The topology snapshot to publish.
        /// </param>
        public void PublishTopology(TopologySnapshot snapshot)
        {
            byte[] framed = MonitorProtocol.Encode(new MonitorMessage
            {
                Type = MonitorProtocol.TypeTopology,
                Topology = snapshot
            });
            _lastTopologyFrame = framed;

            Client[] snapshotClients;
            lock (_lock)
            {
                if (_clients.Count == 0)
                {
                    return;
                }
                snapshotClients = _clients.ToArray();
            }

            for (int i = 0; i < snapshotClients.Length; i++)
            {
                snapshotClients[i].Send(framed);
            }
        }

        /// <summary>
        /// Stops the server and disconnects all clients.
        /// </summary>
        public void Stop()
        {
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
                // Listener already torn down.
            }

            Client[] snapshot;
            lock (_lock)
            {
                snapshot = _clients.ToArray();
                _clients.Clear();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].Close();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient tcp;
                try
                {
                    tcp = _listener!.AcceptTcpClient();
                }
                catch (Exception)
                {
                    // Listener stopped or faulted: exit the loop.
                    break;
                }

                Client client = new Client(tcp, this);
                lock (_lock)
                {
                    _clients.Add(client);
                }
                client.StartReading();

                // Seed the new viewer with the last known topology so its map appears immediately.
                byte[]? topology = _lastTopologyFrame;
                if (topology != null)
                {
                    client.Send(topology);
                }
            }
        }

        private void RemoveClient(Client client)
        {
            lock (_lock)
            {
                _clients.Remove(client);
            }
        }

        /// <summary>
        /// One connected viewer: holds its stream and its current filter, and reads inbound filter updates.
        /// </summary>
        private sealed class Client
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _stream;
            private readonly MonitorServer _owner;
            private readonly MonitorFrameReader _reader = new MonitorFrameReader();
            private readonly object _writeLock = new object();
            private Thread? _readThread;

            private volatile MonitorFilter _filter = new MonitorFilter();

            public Client(TcpClient tcp, MonitorServer owner)
            {
                _tcp = tcp;
                _owner = owner;
                _stream = tcp.GetStream();
                _reader.MessageReceived += OnMessage;
            }

            public MonitorFilter Filter => _filter;

            public void StartReading()
            {
                _readThread = new Thread(ReadLoop)
                {
                    Name = "MonitorServer-Client",
                    IsBackground = true
                };
                _readThread.Start();
            }

            public void Send(byte[] framed)
            {
                try
                {
                    lock (_writeLock)
                    {
                        _stream.Write(framed, 0, framed.Length);
                    }
                }
                catch (Exception)
                {
                    Close();
                    _owner.RemoveClient(this);
                }
            }

            public void Close()
            {
                try
                {
                    _stream.Close();
                    _tcp.Close();
                }
                catch (Exception)
                {
                    // Already closed.
                }
            }

            private void ReadLoop()
            {
                byte[] buffer = new byte[4096];
                while (true)
                {
                    int read;
                    try
                    {
                        read = _stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    _reader.ProcessBytes(buffer.AsSpan(0, read));
                }

                Close();
                _owner.RemoveClient(this);
            }

            private void OnMessage(MonitorMessage message)
            {
                if (string.Equals(message.Type, MonitorProtocol.TypeFilter, StringComparison.Ordinal)
                    && message.Filter != null)
                {
                    _filter = message.Filter;
                }
            }
        }
    }
}
