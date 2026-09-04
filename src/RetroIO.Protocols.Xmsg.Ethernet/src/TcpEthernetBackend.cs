using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// Carries Ethernet frames over a TCP connection - either a point-to-point link to one peer
    /// emulator, or a dial-out to a central relay that joins many nodes into one segment.
    /// </summary>
    /// <remarks>
    /// <para><b>Wire format</b> (read from RetroCore's <c>TcpEthernetBackend</c>, 2026-08-01)</para>
    /// <para>
    /// A one-time <b>5-byte handshake</b> <c>['R','E','T','H', version]</c> - <c>52 45 54 48 01</c>,
    /// magic "RETH" for RetroCore ETHernet bridge - sent by BOTH ends and validated on the four
    /// magic bytes. Then a stream of <c>[u16 big-endian length][frame]</c>.
    /// </para>
    /// <para>
    /// Both the handshake and the length prefix belong to the <b>emulator's transport</b>, not to
    /// COSMOS. Neither exists on real hardware. A client that skips the handshake is dropped.
    /// </para>
    /// <para>
    /// The peer writes the length prefix as its own small write, so a reader must reassemble the
    /// stream and must never assume one frame arrives per read.
    /// </para>
    /// <para><b>Point-to-point versus hub</b></para>
    /// <para>
    /// A listening endpoint accepts ONE peer - this is a link, not a segment. For many nodes
    /// (the 10-to-100 machine "over the internet" topology) do not build a server here: RetroCore's
    /// <c>TcpEthernetRelay</c> is already a central Ethernet hub that accepts many clients and
    /// forwards each frame to all the others. Every node, including this one, simply dials it with
    /// <c>tcp:relay-host:port</c>. Because clients dial OUT, that works through NAT
    /// with no port forwarding.
    /// </para>
    /// <para>
    /// Unlike the UDP backend, this transport IS visible to packet capture, which is why it is the
    /// right choice when an external Wireshark trace is wanted.
    /// </para>
    /// </remarks>
    public sealed class TcpEthernetBackend : IEthernetBackend
    {
        /// <summary>
        /// Default TCP port - the ND Ethernet II PCB number.
        /// </summary>
        public const int DefaultPort = 3094;

        /// <summary>
        /// Handshake protocol version byte sent after the magic.
        /// </summary>
        public const byte HandshakeVersion = 0x01;

        /// <summary>
        /// Handshake magic 'R','E','T','H' - RetroCore ETHernet bridge.
        /// </summary>
        private static readonly byte[] Magic = { 0x52, 0x45, 0x54, 0x48 };

        /// <summary>
        /// Largest frame accepted from the peer, guarding against a corrupt length prefix.
        /// </summary>
        private const int MaxFrameLength = 2048;

        private readonly bool _listen;
        private readonly string _host;
        private readonly int _port;
        private readonly object _stateLock = new object();

        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Thread? _worker;
        private volatile bool _stopRequested;

        /// <summary>
        /// Initialises the backend.
        /// </summary>
        /// <param name="listen">
        /// True to wait for a peer to connect; false to dial out.
        /// </param>
        /// <param name="host">
        /// The host to bind to when listening, or to dial when connecting.
        /// </param>
        /// <param name="port">
        /// The TCP port.
        /// </param>
        public TcpEthernetBackend(bool listen, string host, int port)
        {
            _listen = listen;
            _host = host ?? "0.0.0.0";
            _port = port;
        }

        /// <inheritdoc/>
        public event EthernetPacketReceived? OnPacketReceived;

        /// <inheritdoc/>
        public bool IsActive { get; private set; }

        /// <inheritdoc/>
        public string Description => _listen ? $"tcp-listen:{_port}" : $"tcp:{_host}:{_port}";

        /// <summary>
        /// Gets a value indicating whether a peer is currently connected and past the handshake.
        /// </summary>
        public bool IsConnected => _stream != null;

        /// <summary>
        /// Builds a backend from a spec string, matching RetroCore's forms.
        /// </summary>
        /// <param name="spec">
        /// <c>listen[:port]</c> or <c>tcp-listen[:port]</c> to wait for a peer;
        /// <c>tcp:host[:port]</c> or <c>host:port</c> to dial out.
        /// </param>
        /// <returns>
        /// The backend, or <c>null</c> when the spec cannot be parsed.
        /// </returns>
        public static TcpEthernetBackend? FromSpec(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
            {
                return null;
            }

            string s = spec.Trim();

            if (s.Equals("listen", StringComparison.OrdinalIgnoreCase)
                || s.Equals("tcp-listen", StringComparison.OrdinalIgnoreCase))
            {
                return new TcpEthernetBackend(true, "0.0.0.0", DefaultPort);
            }

            if (s.StartsWith("listen:", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("tcp-listen:", StringComparison.OrdinalIgnoreCase))
            {
                int colon = s.IndexOf(':');
                string portText = s.Substring(colon + 1).Trim();
                if (portText.Length == 0)
                {
                    return new TcpEthernetBackend(true, "0.0.0.0", DefaultPort);
                }

                if (!int.TryParse(portText, out int listenPort) || listenPort < 1 || listenPort > 65535)
                {
                    return null;
                }

                return new TcpEthernetBackend(true, "0.0.0.0", listenPort);
            }

            string body = s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
                ? s.Substring("tcp:".Length).Trim()
                : s;

            if (body.Length == 0)
            {
                return null;
            }

            int lastColon = body.LastIndexOf(':');
            if (lastColon < 0)
            {
                return new TcpEthernetBackend(false, body, DefaultPort);
            }

            string hostPart = body.Substring(0, lastColon).Trim();
            string portPart = body.Substring(lastColon + 1).Trim();
            if (hostPart.Length == 0 || !int.TryParse(portPart, out int dialPort) || dialPort < 1 || dialPort > 65535)
            {
                return null;
            }

            return new TcpEthernetBackend(false, hostPart, dialPort);
        }

        /// <inheritdoc/>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_worker != null)
                {
                    return;
                }

                _stopRequested = false;
                IsActive = true;
                _worker = new Thread(_listen ? AcceptLoop : DialLoop)
                {
                    Name = "XmsgTcpEthernet",
                    IsBackground = true
                };
                _worker.Start();
            }
        }

        /// <inheritdoc/>
        public void Stop()
        {
            Thread? worker;
            lock (_stateLock)
            {
                if (_worker == null)
                {
                    return;
                }

                _stopRequested = true;
                IsActive = false;
                CloseConnection();

                try
                {
                    _listener?.Stop();
                }
                catch (Exception)
                {
                    // Shutting down; nothing useful to report.
                }

                _listener = null;
                worker = _worker;
                _worker = null;
            }

            worker?.Join(2000);
        }

        /// <inheritdoc/>
        public void SendPacket(byte[] data, int offset, int length)
        {
            NetworkStream? stream = _stream;
            if (stream == null || length <= 0 || data == null || length > MaxFrameLength)
            {
                return;
            }

            try
            {
                // The prefix is written separately, exactly as the peer does it.
                Span<byte> prefix = stackalloc byte[2];
                prefix[0] = (byte)(length >> 8);
                prefix[1] = (byte)(length & 0xFF);
                lock (_stateLock)
                {
                    stream.Write(prefix);
                    stream.Write(data, offset, length);
                    stream.Flush();
                }
            }
            catch (Exception)
            {
                // Peer went away mid-write; the reader thread will notice EOF and reconnect.
            }
        }

        /// <summary>
        /// Waits for a peer, serves it, and waits again until stopped.
        /// </summary>
        private void AcceptLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    TcpListener listener = new TcpListener(IPAddress.Any, _port);
                    _listener = listener;
                    listener.Start();

                    while (!_stopRequested)
                    {
                        TcpClient peer = listener.AcceptTcpClient();
                        ServeConnection(peer);
                    }
                }
                catch (Exception)
                {
                    if (_stopRequested)
                    {
                        return;
                    }

                    Thread.Sleep(500);
                }
            }
        }

        /// <summary>
        /// Dials the peer, serves the connection, and retries after it drops until stopped.
        /// </summary>
        private void DialLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    TcpClient peer = new TcpClient();
                    peer.Connect(_host, _port);
                    ServeConnection(peer);
                }
                catch (Exception)
                {
                    // Peer not up yet, or the link dropped. Retry.
                }

                if (!_stopRequested)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>
        /// Runs one established connection: handshake, then read frames until it closes.
        /// </summary>
        /// <param name="peer">
        /// The connected client.
        /// </param>
        private void ServeConnection(TcpClient peer)
        {
            NetworkStream stream;
            try
            {
                peer.NoDelay = true;
                stream = peer.GetStream();
            }
            catch (Exception)
            {
                try
                {
                    peer.Dispose();
                }
                catch (Exception)
                {
                    // Nothing to do.
                }

                return;
            }

            if (!Handshake(stream))
            {
                try
                {
                    peer.Dispose();
                }
                catch (Exception)
                {
                    // Nothing to do.
                }

                return;
            }

            _client = peer;
            _stream = stream;

            try
            {
                byte[] frame = new byte[MaxFrameLength];
                while (!_stopRequested)
                {
                    if (!TryReadFrame(stream, frame, out int length))
                    {
                        break;
                    }

                    if (length > 0)
                    {
                        OnPacketReceived?.Invoke(frame, length);
                    }
                }
            }
            catch (Exception)
            {
                // Connection failed; fall through and clean up so the loop can retry.
            }
            finally
            {
                CloseConnection();
            }
        }

        /// <summary>
        /// Sends our handshake and validates the peer's.
        /// </summary>
        /// <param name="stream">
        /// The connected stream.
        /// </param>
        /// <returns>
        /// True when the peer's magic matched.
        /// </returns>
        private static bool Handshake(NetworkStream stream)
        {
            try
            {
                Span<byte> hello = stackalloc byte[5];
                hello[0] = Magic[0];
                hello[1] = Magic[1];
                hello[2] = Magic[2];
                hello[3] = Magic[3];
                hello[4] = HandshakeVersion;
                stream.Write(hello);
                stream.Flush();

                byte[] peer = new byte[5];
                if (!ReadExactly(stream, peer, 5))
                {
                    return false;
                }

                for (int i = 0; i < Magic.Length; i++)
                {
                    if (peer[i] != Magic[i])
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads one length-prefixed frame.
        /// </summary>
        /// <param name="stream">
        /// The connected stream.
        /// </param>
        /// <param name="frame">
        /// Buffer receiving the frame bytes.
        /// </param>
        /// <param name="length">
        /// Receives the frame length, or 0 on failure.
        /// </param>
        /// <returns>
        /// True when a complete frame was read.
        /// </returns>
        private static bool TryReadFrame(NetworkStream stream, byte[] frame, out int length)
        {
            length = 0;

            byte[] prefix = new byte[2];
            if (!ReadExactly(stream, prefix, 2))
            {
                return false;
            }

            int declared = (prefix[0] << 8) | prefix[1];
            if (declared <= 0 || declared > frame.Length)
            {
                // A corrupt prefix would desynchronise the stream; drop the connection instead of
                // guessing where the next frame starts.
                return false;
            }

            if (!ReadExactly(stream, frame, declared))
            {
                return false;
            }

            length = declared;
            return true;
        }

        /// <summary>
        /// Reads exactly the requested number of bytes, reassembling across reads.
        /// </summary>
        /// <param name="stream">
        /// The stream to read.
        /// </param>
        /// <param name="buffer">
        /// The destination buffer.
        /// </param>
        /// <param name="count">
        /// The number of bytes required.
        /// </param>
        /// <returns>
        /// True when all bytes were read; false at end of stream.
        /// </returns>
        private static bool ReadExactly(NetworkStream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int got = stream.Read(buffer, read, count - read);
                if (got <= 0)
                {
                    return false;
                }

                read += got;
            }

            return true;
        }

        /// <summary>
        /// Closes the current connection, if any.
        /// </summary>
        private void CloseConnection()
        {
            try
            {
                _stream?.Dispose();
            }
            catch (Exception)
            {
                // Shutting down.
            }

            try
            {
                _client?.Dispose();
            }
            catch (Exception)
            {
                // Shutting down.
            }

            _stream = null;
            _client = null;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Stop();
        }
    }
}
