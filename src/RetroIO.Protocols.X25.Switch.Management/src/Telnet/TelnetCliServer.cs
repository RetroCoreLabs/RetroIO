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

namespace RetroIO.Protocols.X25.Switch.Management.Telnet
{
    /// <summary>
    /// A raw-TCP management console server: it accepts connections on the management port and drives each
    /// through a <see cref="TelnetLineEditor"/>, dispatching completed lines to a <see cref="SwitchCli"/>
    /// and writing the responses back with a <c>> </c> prompt. No authentication.
    /// </summary>
    public sealed class TelnetCliServer
    {
        private const string Banner = "\r\nX.25 Switch Management Console\r\n\r\n> ";
        private const string Prompt = "> ";

        private readonly SwitchCli _cli;
        private readonly string _bindHost;
        private readonly int _port;
        private readonly object _lock = new object();
        private readonly List<TcpClient> _clients = new List<TcpClient>();

        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        /// <summary>
        /// Creates a management console server.
        /// </summary>
        /// <param name="cli">
        /// The command processor completed lines are dispatched to.
        /// </param>
        /// <param name="bindHost">
        /// The bind address (for example <c>0.0.0.0</c>).
        /// </param>
        /// <param name="port">
        /// The TCP port to listen on.
        /// </param>
        public TelnetCliServer(SwitchCli cli, string bindHost, int port)
        {
            _cli = cli ?? throw new ArgumentNullException(nameof(cli));
            _bindHost = bindHost;
            _port = port;
        }

        /// <summary>
        /// The port the server is listening on (useful when the requested port was 0).
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// Starts listening and accepting connections.
        /// </summary>
        /// <returns>
        /// True if the listener started.
        /// </returns>
        public bool Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Parse(_bindHost), _port);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "SwitchCli-Accept" };
                _acceptThread.Start();
                return true;
            }
            catch (Exception)
            {
                _running = false;
                return false;
            }
        }

        /// <summary>
        /// Stops the server and closes all connections.
        /// </summary>
        public void Stop()
        {
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // best effort
            }

            lock (_lock)
            {
                for (int i = 0; i < _clients.Count; i++)
                {
                    try
                    {
                        _clients[i].Close();
                    }
                    catch (Exception)
                    {
                        // best effort
                    }
                }

                _clients.Clear();
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (Exception)
                {
                    break; // listener stopped
                }

                client.NoDelay = true;
                lock (_lock)
                {
                    _clients.Add(client);
                }

                Thread t = new Thread(() => Serve(client)) { IsBackground = true, Name = "SwitchCli-Session" };
                t.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                Write(stream, Banner);

                TelnetLineEditor editor = new TelnetLineEditor();
                while (_running)
                {
                    int read = stream.ReadByte();
                    if (read < 0)
                    {
                        break; // connection closed
                    }

                    TelnetInput input = editor.Feed((byte)read);
                    if (input.Echo.Length > 0)
                    {
                        Write(stream, input.Echo);
                    }

                    if (!input.LineComplete)
                    {
                        continue;
                    }

                    string line = input.Line;
                    if (line.StartsWith("quit", StringComparison.Ordinal) || line.StartsWith("exit", StringComparison.Ordinal))
                    {
                        break;
                    }

                    if (line.Length > 0)
                    {
                        Write(stream, _cli.Process(line));
                    }

                    Write(stream, Prompt);
                }
            }
            catch (Exception)
            {
                // connection error; drop the session
            }
            finally
            {
                lock (_lock)
                {
                    _clients.Remove(client);
                }

                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                    // best effort
                }
            }
        }

        private static void Write(NetworkStream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
