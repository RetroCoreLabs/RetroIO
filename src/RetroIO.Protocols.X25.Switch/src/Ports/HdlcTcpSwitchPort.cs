//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RetroIO.Protocols.X25.Switch.Ports
{
    /// <summary>
    /// A trunk port that carries X.25 over LAPB over 0x7E HDLC framing on one TCP connection: a leased line
    /// that happens to run over TCP. The configuration name is <c>hdlc-tcp</c>, and <c>hdlc-ppp</c> is an alias
    /// of it, as in the C emulator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything about the link, the framing and the X.25 restart is in <see cref="HdlcSwitchPortBase"/>. This
    /// class only gets the TCP connection, the way the XOT port does:
    /// </para>
    /// <list type="bullet">
    /// <item><description>A DCE port listens on <c>host:port</c> (the host is the address to bind; empty means every
    /// address; port 0 lets the system choose, which tests use) and takes the peer's connection when it comes. It
    /// carries one link, so a new connection replaces the old one: the old one may be half dead, and a peer that
    /// restarted must not be shut out by its own earlier connection.</description></item>
    /// <item><description>A DTE port dials <c>host:port</c>. When the dial fails, or the connection is lost, it
    /// dials again every <see cref="HdlcSwitchPortBase.RedialDelay"/> milliseconds, with the same log lines as the
    /// XOT port: a warning for the first failure and every fifteenth, a line when the connection is up, a warning
    /// when it goes down that says when the next try is.</description></item>
    /// <item><description>The end that dials sends the SABM. The end that listens waits for it.</description></item>
    /// </list>
    /// <para>
    /// Both ends of a trunk must use the same transport family: an <c>hdlc-tcp</c> port cannot talk to an
    /// <c>xot</c> port.
    /// </para>
    /// </remarks>
    public sealed class HdlcTcpSwitchPort : HdlcSwitchPortBase
    {
        /// <summary>
        /// How long a dial may take before it is given up, in milliseconds.
        /// </summary>
        private const int DialTimeoutMs = 5000;

        private TcpListener? _listener;

        /// <summary>
        /// Creates the port. Nothing happens on the network until <see cref="HdlcSwitchPortBase.Start"/>.
        /// </summary>
        /// <param name="config">
        /// The port's configuration. <c>Mode</c> decides whether it listens (DCE) or dials (DTE); <c>Host</c> and
        /// <c>Port</c> say where; the LAPB values and the channel range are read by the base class.
        /// </param>
        /// <param name="logger">
        /// Where the port reports what happens to its link. Null reports nothing.
        /// </param>
        public HdlcTcpSwitchPort(PortConfig config, ILogger? logger = null)
            : base(config, logger, "TCP")
        {
        }

        /// <summary>
        /// The port the listener actually bound to. Zero until a DCE port has started.
        /// </summary>
        public ushort BoundPort { get; private set; }

        /// <inheritdoc/>
        protected override bool SendsSabmOnConnect => PortSettings.Mode != PortMode.Dce;

        /// <inheritdoc/>
        protected override void OpenLine()
        {
            if (PortSettings.Mode == PortMode.Dce)
            {
                TcpListener listener = new TcpListener(ParseBindAddress(PortSettings.Host), PortSettings.Port);
                listener.Start();
                _listener = listener;
                BoundPort = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
                Logger.LogInformation("Port {Id} ({Mode} {Type}, HDLC over TCP): listening on {Address}:{Port}",
                    Id, PortSettings.Mode, PortSettings.Type, ((IPEndPoint)listener.LocalEndpoint).Address, BoundPort);
            }
            else
            {
                if (string.IsNullOrEmpty(PortSettings.Host))
                {
                    throw new InvalidDataException("a DTE hdlc-tcp port dials out, so it needs 'host =' in its configuration");
                }

                Logger.LogInformation("Port {Id} ({Mode} {Type}, HDLC over TCP): dialling {Host}:{Port}",
                    Id, PortSettings.Mode, PortSettings.Type, PortSettings.Host, PortSettings.Port);
            }
        }

        /// <inheritdoc/>
        protected override Stream? TakeOpenedStream()
        {
            return null;
        }

        /// <inheritdoc/>
        protected override void CloseLine()
        {
            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // Already down.
            }
        }

        /// <inheritdoc/>
        protected override string RetryText()
        {
            return PortSettings.Mode == PortMode.Dce ? string.Empty : "; dialling again in " + RedialDelay / 1000 + " seconds";
        }

        /// <inheritdoc/>
        protected override void RunSupervisor()
        {
            while (IsRunning)
            {
                try
                {
                    if (PortSettings.Mode == PortMode.Dce)
                    {
                        AcceptOne();
                    }
                    else
                    {
                        DialIfNeeded();
                    }
                }
                catch (Exception ex)
                {
                    // Nothing here is fatal to the port: the next turn accepts or dials again.
                    Logger.LogDebug("Port {Id}: connection supervisor: {Reason}", Id, ex.Message);
                    if (IsRunning)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
        }

        private void AcceptOne()
        {
            TcpListener? listener = _listener;
            if (listener == null)
            {
                Thread.Sleep(100);
                return;
            }

            Socket accepted;
            try
            {
                accepted = listener.AcceptSocket();
            }
            catch (Exception)
            {
                // Stop() closed the listener.
                return;
            }

            NoteConnectAttempt();
            accepted.NoDelay = true;
            AttachStream(new NetworkStream(accepted, ownsSocket: true), "accepted");
        }

        private void DialIfNeeded()
        {
            if (HasLine)
            {
                Thread.Sleep(100);
                return;
            }

            string what = "dial to " + PortSettings.Host + ":" + PortSettings.Port;
            TcpClient? client = null;
            try
            {
                NoteConnectAttempt();
                client = new TcpClient();
                Task connect = client.ConnectAsync(PortSettings.Host, PortSettings.Port);
                if (!connect.Wait(DialTimeoutMs))
                {
                    client.Close();
                    throw new TimeoutException("no answer in " + DialTimeoutMs + " ms");
                }
            }
            catch (Exception ex)
            {
                NoteConnectFailure(what, ex.InnerException?.Message ?? ex.Message);
                client?.Close();
                WaitRedialDelay();
                return;
            }

            if (!IsRunning)
            {
                client.Close();
                return;
            }

            client.NoDelay = true;
            AttachStream(new NetworkStream(client.Client, ownsSocket: true), "dialled");
        }

        /// <summary>
        /// Works out the local address a listening port binds to.
        /// </summary>
        /// <param name="host">
        /// The configured <c>Host</c>: empty or <c>0.0.0.0</c> for every address, otherwise an address.
        /// </param>
        /// <returns>
        /// The address to listen on.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <paramref name="host"/> is not an address. A host name is not accepted, because the C
        /// emulator takes the same setting as an address.
        /// </exception>
        private static IPAddress ParseBindAddress(string host)
        {
            if (string.IsNullOrEmpty(host) || host == "0.0.0.0")
            {
                return IPAddress.Any;
            }

            if (!IPAddress.TryParse(host, out IPAddress? address))
            {
                throw new FormatException("'" + host + "' is not an address the port can listen on");
            }

            return address;
        }
    }
}
