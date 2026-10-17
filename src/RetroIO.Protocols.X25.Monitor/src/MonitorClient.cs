//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net.Sockets;
using System.Threading;

namespace RetroIO.Protocols.X25.Monitor
{
    /// <summary>
    /// A client of a <see cref="MonitorServer"/>: it connects over TCP, receives decoded
    /// <see cref="CaptureRecord"/>s, and sends <see cref="MonitorFilter"/> updates to narrow the stream
    /// server-side. UI-agnostic — a view-model subscribes to its events and marshals to the UI thread.
    /// </summary>
    public sealed class MonitorClient : IDisposable
    {
        private readonly MonitorFrameReader _reader = new MonitorFrameReader();
        private readonly object _lock = new object();

        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private Thread? _readThread;
        private volatile bool _connected;

        /// <summary>
        /// Raised for each decoded capture record received from the server.
        /// </summary>
        /// <param name="record">
        /// The received capture record.
        /// </param>
        public delegate void CaptureHandler(CaptureRecord record);

        /// <summary>
        /// Raised when the connection state changes.
        /// </summary>
        /// <param name="connected">
        /// True when connected, false when disconnected.
        /// </param>
        public delegate void ConnectionHandler(bool connected);

        /// <summary>
        /// Raised for each link/port status snapshot received from the server.
        /// </summary>
        /// <param name="snapshot">
        /// The received status snapshot.
        /// </param>
        public delegate void StatusHandler(StatusSnapshot snapshot);

        /// <summary>
        /// Raised for each decoded capture record received from the server.
        /// </summary>
        public event CaptureHandler? CaptureReceived;

        /// <summary>
        /// Raised for each log record received from the server.
        /// </summary>
        /// <param name="record">
        /// The received log record.
        /// </param>
        public delegate void LogHandler(LogRecord record);

        /// <summary>
        /// Raised for each topology snapshot received from the server.
        /// </summary>
        /// <param name="snapshot">
        /// The received topology snapshot.
        /// </param>
        public delegate void TopologyHandler(TopologySnapshot snapshot);

        /// <summary>
        /// Raised for each link/port status snapshot received from the server.
        /// </summary>
        public event StatusHandler? StatusReceived;

        /// <summary>
        /// Raised for each log record received from the server.
        /// </summary>
        public event LogHandler? LogReceived;

        /// <summary>
        /// Raised for each topology snapshot received from the server (self identity plus neighbours).
        /// </summary>
        public event TopologyHandler? TopologyReceived;

        /// <summary>
        /// Raised when the connection state changes.
        /// </summary>
        public event ConnectionHandler? ConnectionChanged;

        /// <summary>
        /// Creates a monitor client and wires the frame reader.
        /// </summary>
        public MonitorClient()
        {
            _reader.MessageReceived += OnMessage;
        }

        /// <summary>
        /// True while the client is connected to a server.
        /// </summary>
        public bool IsConnected => _connected;

        /// <summary>
        /// Connects to a monitoring server and begins receiving captures.
        /// </summary>
        /// <param name="host">
        /// The server host.
        /// </param>
        /// <param name="port">
        /// The server TCP port.
        /// </param>
        /// <returns>
        /// True if the connection was established.
        /// </returns>
        public bool Connect(string host, int port)
        {
            Disconnect();

            try
            {
                TcpClient tcp = new TcpClient();
                tcp.Connect(host, port);

                lock (_lock)
                {
                    _tcp = tcp;
                    _stream = tcp.GetStream();
                    _connected = true;
                }

                _readThread = new Thread(ReadLoop)
                {
                    Name = "MonitorClient-Read",
                    IsBackground = true
                };
                _readThread.Start();

                ConnectionChanged?.Invoke(true);
                return true;
            }
            catch (Exception)
            {
                Disconnect();
                return false;
            }
        }

        /// <summary>
        /// Sends a filter update to the server to narrow the stream.
        /// </summary>
        /// <param name="filter">
        /// The filter to apply server-side.
        /// </param>
        public void SendFilter(MonitorFilter filter)
        {
            NetworkStream? stream = _stream;
            if (stream == null)
            {
                return;
            }

            byte[] framed = MonitorProtocol.Encode(new MonitorMessage
            {
                Type = MonitorProtocol.TypeFilter,
                Filter = filter
            });

            try
            {
                stream.Write(framed, 0, framed.Length);
            }
            catch (Exception)
            {
                Disconnect();
            }
        }

        /// <summary>
        /// Disconnects from the server, if connected.
        /// </summary>
        public void Disconnect()
        {
            bool wasConnected;
            TcpClient? tcp;
            NetworkStream? stream;
            lock (_lock)
            {
                wasConnected = _connected;
                tcp = _tcp;
                stream = _stream;
                _connected = false;
                _tcp = null;
                _stream = null;
            }

            try
            {
                stream?.Close();
                tcp?.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }

            if (wasConnected)
            {
                ConnectionChanged?.Invoke(false);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Disconnect();
        }

        private void ReadLoop()
        {
            NetworkStream? stream = _stream;
            if (stream == null)
            {
                return;
            }

            byte[] buffer = new byte[8192];
            while (_connected)
            {
                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
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

            Disconnect();
        }

        private void OnMessage(MonitorMessage message)
        {
            if (string.Equals(message.Type, MonitorProtocol.TypeCapture, StringComparison.Ordinal)
                && message.Capture != null)
            {
                CaptureReceived?.Invoke(message.Capture);
            }
            else if (string.Equals(message.Type, MonitorProtocol.TypeStatus, StringComparison.Ordinal)
                && message.Status != null)
            {
                StatusReceived?.Invoke(message.Status);
            }
            else if (string.Equals(message.Type, MonitorProtocol.TypeLog, StringComparison.Ordinal)
                && message.Log != null)
            {
                LogReceived?.Invoke(message.Log);
            }
            else if (string.Equals(message.Type, MonitorProtocol.TypeTopology, StringComparison.Ordinal)
                && message.Topology != null)
            {
                TopologyReceived?.Invoke(message.Topology);
            }
        }
    }
}
