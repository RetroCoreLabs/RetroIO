//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Net.Sockets;

namespace RetroIO.Network.Nat
{
    /// <summary>
    /// The host sockets of one port forward rule: one listening socket per host port of the
    /// rule (a TCP listener, or a bound UDP socket), each with the guest port it goes to.
    /// </summary>
    /// <remarks>
    /// Made and bound on the thread that adds the rule, so a port already in use is reported to
    /// the caller at once (design 7.7, conflicts); after that only the NAT thread uses the
    /// sockets. <see cref="Close"/> may be called twice.
    /// </remarks>
    public sealed class NatForwardBinding
    {
        // ── Constants ──

        private const int Backlog = 16;

        // ── Constructors ──

        private NatForwardBinding(NatPortForward rule, Socket[] sockets)
        {
            Rule = rule;
            Sockets = sockets;
            GuestPorts = new ushort[sockets.Length];
            for (int i = 0; i < sockets.Length; i++)
            {
                GuestPorts[i] = (ushort)(rule.GuestPortFirst + i);
            }
        }

        // ── Properties ──

        /// <summary>
        /// Gets the rule.
        /// </summary>
        public NatPortForward Rule { get; }

        /// <summary>
        /// Gets the host sockets, one per host port, in port order.
        /// </summary>
        public Socket[] Sockets { get; }

        /// <summary>
        /// Gets the guest port for each socket.
        /// </summary>
        public ushort[] GuestPorts { get; }

        /// <summary>
        /// Gets a value indicating whether the sockets were closed.
        /// </summary>
        public bool Closed { get; private set; }

        // ── Public methods ──

        /// <summary>
        /// Opens every host port of a rule. On any failure the ports already opened are closed again.
        /// </summary>
        /// <param name="rule">
        /// The rule.
        /// </param>
        /// <param name="binding">
        /// The open sockets, or <c>null</c>.
        /// </param>
        /// <param name="error">
        /// Why a port could not be opened, or empty.
        /// </param>
        /// <returns>
        /// <c>true</c> when every port is open.
        /// </returns>
        public static bool TryOpen(NatPortForward rule, out NatForwardBinding? binding, out string error)
        {
            binding = null;
            error = string.Empty;
            Socket[] sockets = new Socket[rule.PortCount];
            int opened = 0;
            try
            {
                for (int i = 0; i < sockets.Length; i++)
                {
                    bool tcp = rule.Protocol == NatForwardProtocol.Tcp;
                    Socket s = new Socket(AddressFamily.InterNetwork, tcp ? SocketType.Stream : SocketType.Dgram, tcp ? ProtocolType.Tcp : ProtocolType.Udp);
                    sockets[i] = s;
                    opened++;
                    if (OperatingSystem.IsWindows())
                    {
                        // Without this Windows lets a second program take the same port, and
                        // the conflict would never be reported.
                        s.ExclusiveAddressUse = true;
                    }
                    s.Bind(new IPEndPoint(rule.ListenAddress, rule.HostPortFirst + i));
                    if (tcp)
                    {
                        s.Listen(Backlog);
                    }
                    s.Blocking = false;
                }
            }
            catch (SocketException e)
            {
                for (int i = 0; i < opened; i++)
                {
                    sockets[i].Close();
                }
                int port = rule.HostPortFirst + opened - 1;
                error = e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? "host port " + port + " on " + rule.ListenAddress + " is already in use by another program"
                    : e.SocketErrorCode == SocketError.AccessDenied
                        ? "host port " + port + " on " + rule.ListenAddress + " is not allowed for this user (ports below 1024 need admin rights on some systems)"
                        : "host port " + port + " on " + rule.ListenAddress + " cannot be opened: " + e.Message;
                return false;
            }
            binding = new NatForwardBinding(rule, sockets);
            return true;
        }

        /// <summary>
        /// Finds the index of a socket of this rule.
        /// </summary>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <returns>
        /// The index, or -1.
        /// </returns>
        public int IndexOf(Socket socket)
        {
            for (int i = 0; i < Sockets.Length; i++)
            {
                if (ReferenceEquals(Sockets[i], socket))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Closes every socket of the rule. Connections already accepted are not touched.
        /// </summary>
        public void Close()
        {
            Closed = true;
            for (int i = 0; i < Sockets.Length; i++)
            {
                try
                {
                    Sockets[i].Close();
                }
                catch (ObjectDisposedException)
                {
                    // Already closed.
                }
            }
        }
    }
}
