//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RetroIO.Protocols.Xmsg.Ethernet
{
    /// <summary>
    /// Carries Ethernet frames over a UDP multicast group, so any number of nodes on the same
    /// network form one virtual Ethernet segment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interoperates with RetroCore's <c>--net=udp</c> backend. Each UDP datagram payload is
    /// one raw Ethernet frame with no wrapper - no length prefix, no handshake, nothing.
    /// Default group <c>239.3.9.4</c> (administratively scoped; the digits echo 3-0-9-4) and port
    /// <c>3094</c>, the ND Ethernet II PCB number.
    /// </para>
    /// <para><b>Same-host peers are invisible to packet capture</b></para>
    /// <para>
    /// VERIFIED 2026-08-01: when two peers run on one Windows host, the kernel loops the multicast
    /// back internally and it reaches no capturable adapter - Wireshark sees nothing on loopback or
    /// on any interface carrying a multicast route, even with traffic demonstrably flowing. That is
    /// a property of the host network stack, not a filter mistake. Use this backend when the node
    /// itself should log the traffic (it is an endpoint, so it sees everything); use
    /// <see cref="TcpEthernetBackend"/> when an external capture is needed.
    /// </para>
    /// <para>
    /// Multicast loopback is enabled so peers on the same host reach each other; a
    /// node's own frames come back to it and must be discarded by the layer above on source-address
    /// match.
    /// </para>
    /// </remarks>
    public sealed class UdpEthernetBackend : IEthernetBackend
    {
        /// <summary>
        /// Default multicast group, administratively scoped (<c>239.0.0.0/8</c>).
        /// </summary>
        public static readonly IPAddress DefaultGroup = IPAddress.Parse("239.3.9.4");

        /// <summary>
        /// Default UDP port - the ND Ethernet II PCB number.
        /// </summary>
        public const int DefaultPort = 3094;

        private readonly IPAddress _group;
        private readonly int _port;
        private readonly object _stateLock = new object();

        private UdpClient? _udp;
        private IPEndPoint? _sendEndpoint;
        private Thread? _receiveThread;
        private volatile bool _stopRequested;

        /// <summary>
        /// Initialises the backend for a group and port.
        /// </summary>
        /// <param name="group">
        /// The multicast group to join.
        /// </param>
        /// <param name="port">
        /// The UDP port to bind and send to.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="group"/> is null.
        /// </exception>
        public UdpEthernetBackend(IPAddress group, int port)
        {
            _group = group ?? throw new ArgumentNullException(nameof(group));
            _port = port;
        }

        /// <summary>
        /// Occurs when a non-empty datagram arrives from the multicast group.
        /// </summary>
        /// <remarks>
        /// Raised on the receive thread, with a new buffer for each datagram. Frames this node sent
        /// itself also come back here, because multicast loopback is on.
        /// </remarks>
        public event EthernetPacketReceived? OnPacketReceived;

        /// <summary>
        /// Gets a value indicating whether the backend is started and able to carry frames.
        /// </summary>
        /// <value>
        /// <c>true</c> after a successful <see cref="Start"/>, until <see cref="Stop"/>.
        /// </value>
        public bool IsActive { get; private set; }

        /// <summary>
        /// Gets a short human-readable description of the transport, for logs.
        /// </summary>
        /// <value>
        /// <c>udp:mcast:</c> followed by the group, a colon and the port.
        /// </value>
        public string Description => $"udp:mcast:{_group}:{_port}";

        /// <summary>
        /// Builds a backend from a spec string, matching RetroCore's forms.
        /// </summary>
        /// <param name="spec">
        /// One of <c>udp</c>, <c>udp:port</c>, <c>udp:group</c> or
        /// <c>udp:group:port</c>. An optional <c>mcast:</c> or <c>udp-mcast:</c>
        /// prefix is accepted.
        /// </param>
        /// <returns>
        /// The backend, or <c>null</c> when the spec cannot be parsed.
        /// </returns>
        public static UdpEthernetBackend? FromSpec(string spec)
        {
            if (spec == null)
            {
                return null;
            }

            string s = spec.Trim();
            if (s.StartsWith("udp-mcast:", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring("udp-mcast:".Length);
            }
            else if (s.StartsWith("udp:", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring("udp:".Length);
            }
            else if (s.Equals("udp", StringComparison.OrdinalIgnoreCase))
            {
                s = string.Empty;
            }

            if (s.StartsWith("mcast:", StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring("mcast:".Length);
            }

            s = s.Trim();
            if (s.Length == 0)
            {
                return new UdpEthernetBackend(DefaultGroup, DefaultPort);
            }

            IPAddress group = DefaultGroup;
            int port = DefaultPort;

            int colon = s.LastIndexOf(':');
            if (colon >= 0)
            {
                string groupText = s.Substring(0, colon).Trim();
                string portText = s.Substring(colon + 1).Trim();
                if (groupText.Length > 0 && !IPAddress.TryParse(groupText, out group!))
                {
                    return null;
                }

                if (!int.TryParse(portText, out port) || port < 1 || port > 65535)
                {
                    return null;
                }
            }
            else if (s.IndexOf('.') >= 0)
            {
                if (!IPAddress.TryParse(s, out group!))
                {
                    return null;
                }
            }
            else
            {
                if (!int.TryParse(s, out port) || port < 1 || port > 65535)
                {
                    return null;
                }
            }

            return new UdpEthernetBackend(group ?? DefaultGroup, port);
        }

        /// <summary>
        /// Starts the backend: binds the UDP port with address reuse, joins the multicast group with
        /// loopback on, and starts the background receive thread.
        /// </summary>
        /// <remarks>
        /// Calling it while started does nothing. When the setup fails, the socket is closed, the
        /// backend stays inactive and the exception is passed on.
        /// </remarks>
        /// <exception cref="SocketException">
        /// Thrown when binding the port or joining the group fails.
        /// </exception>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_udp != null)
                {
                    return;
                }

                UdpClient udp = new UdpClient();
                try
                {
                    // ReuseAddress so several nodes on the SAME host can bind the same port.
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
                    udp.JoinMulticastGroup(_group);
                    udp.MulticastLoopback = true;

                    _udp = udp;
                    _sendEndpoint = new IPEndPoint(_group, _port);
                    _stopRequested = false;
                    IsActive = true;

                    _receiveThread = new Thread(ReceiveLoop)
                    {
                        Name = "XmsgUdpEthernet",
                        IsBackground = true
                    };
                    _receiveThread.Start();
                }
                catch (Exception)
                {
                    IsActive = false;
                    _udp = null;
                    try
                    {
                        udp.Dispose();
                    }
                    catch (Exception)
                    {
                        // Nothing useful to do while cleaning up a failed start.
                    }

                    throw;
                }
            }
        }

        /// <summary>
        /// Stops the backend: leaves the multicast group, closes the socket and waits up to 2 seconds
        /// for the receive thread to end.
        /// </summary>
        /// <remarks>
        /// Calling it when the backend is not started does nothing. Errors while leaving the group or
        /// closing the socket are ignored.
        /// </remarks>
        public void Stop()
        {
            Thread? thread;
            lock (_stateLock)
            {
                if (_udp == null)
                {
                    return;
                }

                _stopRequested = true;
                try
                {
                    _udp.DropMulticastGroup(_group);
                }
                catch (Exception)
                {
                    // Already gone - not worth reporting during shutdown.
                }

                try
                {
                    _udp.Close(); // unblocks the receive loop
                }
                catch (Exception)
                {
                    // Same.
                }

                thread = _receiveThread;
                _receiveThread = null;
                _udp = null;
                _sendEndpoint = null;
                IsActive = false;
            }

            thread?.Join(2000);
        }

        /// <summary>
        /// Sends one raw Ethernet frame as a single datagram to the multicast group.
        /// </summary>
        /// <remarks>
        /// The frame is dropped without notice when the backend is not active, <paramref name="data"/>
        /// is null, or <paramref name="length"/> is 0 or less. Any exception during the send, including
        /// an <paramref name="offset"/> or <paramref name="length"/> outside the buffer, is caught and
        /// the frame is lost.
        /// </remarks>
        /// <param name="data">
        /// The buffer holding the frame.
        /// </param>
        /// <param name="offset">
        /// The offset of the first frame byte within <paramref name="data"/>.
        /// </param>
        /// <param name="length">
        /// The number of bytes to send.
        /// </param>
        public void SendPacket(byte[] data, int offset, int length)
        {
            UdpClient? udp = _udp;
            IPEndPoint? endpoint = _sendEndpoint;
            if (!IsActive || udp == null || endpoint == null || length <= 0 || data == null)
            {
                return;
            }

            try
            {
                if (offset == 0 && length == data.Length)
                {
                    udp.Send(data, length, endpoint);
                }
                else
                {
                    byte[] slice = new byte[length];
                    Array.Copy(data, offset, slice, 0, length);
                    udp.Send(slice, length, endpoint);
                }
            }
            catch (Exception)
            {
                // Datagram dropped. COSMOS retransmits; do not tear the link down.
            }
        }

        /// <summary>
        /// Reads datagrams until stopped, raising <see cref="OnPacketReceived"/> for each.
        /// </summary>
        private void ReceiveLoop()
        {
            IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (!_stopRequested)
            {
                UdpClient? udp = _udp;
                if (udp == null)
                {
                    return;
                }

                byte[] datagram;
                try
                {
                    datagram = udp.Receive(ref from);
                }
                catch (Exception)
                {
                    // Closed during Stop, or a transient receive error. Either way, leave quietly
                    // if we are shutting down and retry otherwise.
                    if (_stopRequested)
                    {
                        return;
                    }

                    continue;
                }

                if (datagram.Length > 0)
                {
                    OnPacketReceived?.Invoke(datagram, datagram.Length);
                }
            }
        }

        /// <summary>
        /// Disposes the backend by calling <see cref="Stop"/>.
        /// </summary>
        public void Dispose()
        {
            Stop();
        }
    }
}
