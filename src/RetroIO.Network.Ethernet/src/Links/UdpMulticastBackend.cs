//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// Emulators on one local network sharing one Ethernet segment over UDP multicast
    /// (<c>udp</c>, <c>udp:GROUP:PORT</c>; default 239.3.9.4 port 3094), with no hub: each frame
    /// is one datagram to the group and every member gets it.
    /// </summary>
    /// <remarks>
    /// <para>The bytes are those of the old ND-100 code and nd100x (<see cref="EthernetLinkWire"/>):
    /// the bare Ethernet frame, nothing added. Multicast stays on the local network (time to live
    /// 1) and some managed switches filter it; across the internet use a TCP link or hub.</para>
    /// <para>Several emulators on one computer bind the same port (address reuse) and multicast
    /// loopback is on, so they hear each other - and each hears its own frames. The old code left
    /// that echo to the card's own-address filter; the shared manager has no such filter, so this
    /// backend drops a received frame whose source address is one this backend has sent from.</para>
    /// <para>The outgoing interface is picked on purpose (a wired or wireless adapter first, then
    /// anything up that can multicast), as the old code learned it must: left to the system, the
    /// frames can leave by a VPN or host-only adapter no other emulator is on.</para>
    /// </remarks>
    public sealed class UdpMulticastBackend : IEthernetBackend
    {
        // ── Constants ──

        /// <summary>
        /// The spec prefix.
        /// </summary>
        public const string Prefix = "udp";

        private const int OwnAddressSlots = 4;

        // Windows: stop an ICMP "port unreachable" turning into an error on the next receive.
        private const int SioUdpConnReset = unchecked((int)0x9800000C);

        // ── Fields ──

        private readonly IPAddress _group;
        private readonly int _port;
        private readonly IPAddress? _interfaceAddress;
        private readonly EthernetLinkReceiveQueue _queue = new EthernetLinkReceiveQueue(EthernetLinkWire.UdpMaxFrameBytes);
        private readonly byte[] _ownAddresses = new byte[OwnAddressSlots * 6];
        private int _ownAddressCount;
        private Socket? _socket;
        private IPEndPoint? _destination;
        private Thread? _thread;
        private volatile bool _stop;
        private long _framesSent;
        private long _framesReceived;
        private long _ownEchoesDropped;
        private long _sendFailures;

        // ── Events ──

        /// <inheritdoc/>
        public event EthernetFrameHandler? OnFrameReceived;

        // ── Constructors ──

        /// <summary>
        /// Initializes a new <see cref="UdpMulticastBackend"/>.
        /// </summary>
        /// <param name="group">
        /// The multicast group.
        /// </param>
        /// <param name="port">
        /// The UDP port.
        /// </param>
        /// <param name="interfaceAddress">
        /// The local address to send and join on; null picks one.
        /// </param>
        public UdpMulticastBackend(IPAddress group, int port, IPAddress? interfaceAddress = null)
        {
            _group = group ?? EthernetLinkWire.DefaultGroup;
            _port = port;
            _interfaceAddress = interfaceAddress;
        }

        // ── Properties ──

        /// <inheritdoc/>
        public bool IsRunning => _thread != null;

        /// <summary>
        /// Gets the multicast group.
        /// </summary>
        public IPAddress Group => _group;

        /// <summary>
        /// Gets the UDP port.
        /// </summary>
        public int Port => _port;

        /// <summary>
        /// Gets the local address the frames go out on, once started.
        /// </summary>
        public IPAddress? InterfaceAddress { get; private set; }

        /// <summary>
        /// Gets how many frames were sent.
        /// </summary>
        public long FramesSent => Interlocked.Read(ref _framesSent);

        /// <summary>
        /// Gets how many frames were received, own echoes not counted.
        /// </summary>
        public long FramesReceived => Interlocked.Read(ref _framesReceived);

        /// <summary>
        /// Gets how many of this backend's own frames came back and were dropped.
        /// </summary>
        public long OwnEchoesDropped => Interlocked.Read(ref _ownEchoesDropped);

        /// <summary>
        /// Gets how many sends failed.
        /// </summary>
        public long SendFailures => Interlocked.Read(ref _sendFailures);

        /// <summary>
        /// Gets how many received frames were dropped because the guest fell behind.
        /// </summary>
        public long FramesDroppedFull => _queue.FramesDroppedFull;

        // ── Public methods ──

        /// <summary>
        /// Adds this backend's spec reader to <see cref="EthernetBackends"/>.
        /// </summary>
        public static void RegisterFactory()
        {
            EthernetBackends.Register(CreateFromSpec);
        }

        /// <summary>
        /// Makes a multicast link from a spec, or gives <c>null</c> when it is not one:
        /// <c>udp</c>, <c>udp:GROUP</c>, <c>udp:PORT</c>, <c>udp:GROUP:PORT</c>. The old code's
        /// spellings <c>udp-mcast:...</c> and <c>udp:mcast:...</c> are accepted too.
        /// </summary>
        /// <param name="spec">
        /// The spec.
        /// </param>
        /// <returns>
        /// The backend, or <c>null</c>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown for a udp spec with a bad group or port.
        /// </exception>
        public static IEthernetBackend? CreateFromSpec(string spec)
        {
            if (spec == null)
            {
                return null;
            }
            string s = spec.Trim();
            string rest;
            if (s.Equals(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                rest = string.Empty;
            }
            else if (s.StartsWith("udp-mcast:", StringComparison.OrdinalIgnoreCase))
            {
                rest = s.Substring("udp-mcast:".Length);
            }
            else if (s.StartsWith(Prefix + ":", StringComparison.OrdinalIgnoreCase))
            {
                rest = s.Substring(Prefix.Length + 1);
            }
            else
            {
                return null;
            }
            rest = rest.Trim();
            if (rest.Equals("mcast", StringComparison.OrdinalIgnoreCase))
            {
                rest = string.Empty;
            }
            else if (rest.StartsWith("mcast:", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest.Substring("mcast:".Length).Trim();
            }
            IPAddress group = EthernetLinkWire.DefaultGroup;
            int port = EthernetLinkWire.DefaultPort;
            if (rest.Length > 0)
            {
                int colon = rest.LastIndexOf(':');
                string groupText = colon >= 0 ? rest.Substring(0, colon).Trim() : (rest.IndexOf('.') >= 0 ? rest : string.Empty);
                string portText = colon >= 0 ? rest.Substring(colon + 1).Trim() : (rest.IndexOf('.') >= 0 ? string.Empty : rest);
                if (groupText.Length > 0)
                {
                    if (!IPAddress.TryParse(groupText, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork
                        || (parsed.GetAddressBytes()[0] & 0xF0) != 0xE0)
                    {
                        throw new ArgumentException("'" + groupText + "' in '" + spec + "' is not a multicast group (224.0.0.0 to 239.255.255.255).", nameof(spec));
                    }
                    group = parsed;
                }
                if (portText.Length > 0 && (!int.TryParse(portText, out port) || port < 1 || port > 65535))
                {
                    throw new ArgumentException("The port in '" + spec + "' is not a number from 1 to 65535.", nameof(spec));
                }
            }
            return new UdpMulticastBackend(group, port);
        }

        /// <inheritdoc/>
        public void Start()
        {
            if (_thread != null)
            {
                return;
            }
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    socket.IOControl(SioUdpConnReset, new byte[4], null);
                }
                catch (SocketException)
                {
                    // Not fatal: the receive loop survives the error too.
                }
            }
            socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            IPAddress outgoing = _interfaceAddress ?? PickInterface();
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, outgoing.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_group, outgoing));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            socket.ReceiveTimeout = 500;
            InterfaceAddress = outgoing;
            _destination = new IPEndPoint(_group, _port);
            _socket = socket;
            _stop = false;
            _thread = new Thread(ReceiveLoop) { Name = "UdpMulticastLink", IsBackground = true };
            _thread.Start();
        }

        /// <inheritdoc/>
        public void Stop()
        {
            _stop = true;
            _thread?.Join(2000);
            _thread = null;
            Socket? socket = _socket;
            _socket = null;
            socket?.Close();
        }

        /// <inheritdoc/>
        public void Send(byte[] frame, int offset, int length)
        {
            Socket? socket = _socket;
            if (socket == null || _destination == null || length <= 0 || length > EthernetLinkWire.UdpMaxFrameBytes)
            {
                return;
            }
            if (length >= 12)
            {
                RememberOwnAddress(new ReadOnlySpan<byte>(frame, offset + 6, 6));
            }
            try
            {
                socket.SendTo(frame, offset, length, SocketFlags.None, _destination);
                Interlocked.Increment(ref _framesSent);
            }
            catch (SocketException)
            {
                // A lost datagram is what real Ethernet does too; counted so it shows.
                Interlocked.Increment(ref _sendFailures);
            }
        }

        /// <inheritdoc/>
        public void Poll()
        {
            _queue.Deliver(OnFrameReceived);
        }

        // ── Private methods ──

        // A wired or wireless adapter first, then anything up that can multicast, then any.
        private static IPAddress PickInterface()
        {
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            IPAddress? preferred = null;
            IPAddress? fallback = null;
            for (int i = 0; i < nics.Length; i++)
            {
                NetworkInterface nic = nics[i];
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || !nic.SupportsMulticast)
                {
                    continue;
                }
                UnicastIPAddressInformationCollection addresses = nic.GetIPProperties().UnicastAddresses;
                for (int a = 0; a < addresses.Count; a++)
                {
                    IPAddress address = addresses[a].Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }
                    bool real = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                        || nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet
                        || nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    if (real && preferred == null)
                    {
                        preferred = address;
                    }
                    else if (fallback == null)
                    {
                        fallback = address;
                    }
                }
            }
            return preferred ?? fallback ?? IPAddress.Any;
        }

        // The emulation thread is the only writer; the receive thread reads the count once.
        private void RememberOwnAddress(ReadOnlySpan<byte> source)
        {
            int count = Volatile.Read(ref _ownAddressCount);
            for (int i = 0; i < count; i++)
            {
                if (source.SequenceEqual(new ReadOnlySpan<byte>(_ownAddresses, i * 6, 6)))
                {
                    return;
                }
            }
            if (count >= OwnAddressSlots)
            {
                return;
            }
            source.CopyTo(new Span<byte>(_ownAddresses, count * 6, 6));
            Volatile.Write(ref _ownAddressCount, count + 1);
        }

        private bool IsOwnFrame(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 12)
            {
                return false;
            }
            ReadOnlySpan<byte> source = frame.Slice(6, 6);
            int count = Volatile.Read(ref _ownAddressCount);
            for (int i = 0; i < count; i++)
            {
                if (source.SequenceEqual(new ReadOnlySpan<byte>(_ownAddresses, i * 6, 6)))
                {
                    return true;
                }
            }
            return false;
        }

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[EthernetLinkWire.UdpMaxFrameBytes];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int errors = 0;
            while (!_stop)
            {
                Socket? socket = _socket;
                if (socket == null)
                {
                    return;
                }
                int received;
                try
                {
                    received = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from);
                    errors = 0;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    continue;
                }
                catch (SocketException)
                {
                    // A lasting error must not spin a core at full speed.
                    errors++;
                    Thread.Sleep(errors < 10 ? 1 : 50);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                ReadOnlySpan<byte> frame = new ReadOnlySpan<byte>(buffer, 0, received);
                if (received <= 0)
                {
                    continue;
                }
                if (IsOwnFrame(frame))
                {
                    Interlocked.Increment(ref _ownEchoesDropped);
                    continue;
                }
                Interlocked.Increment(ref _framesReceived);
                _queue.Put(frame);
            }
        }
    }
}
