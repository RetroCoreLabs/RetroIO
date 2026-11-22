//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// A Linux TAP interface as the guest's network (<c>tap:IFNAME</c>): every frame the guest
    /// sends comes out of the interface into this computer's own network stack, and every
    /// frame the stack sends to the interface reaches the guest. The host can then ping,
    /// telnet or ftp the guest directly, with no NAT in between.
    /// </summary>
    /// <remarks>
    /// <para>The interface must already exist and belong to the user running the emulator; this
    /// backend only opens it, it never creates one or changes the host's network settings (the
    /// owner's decision, host-networking-design.md section 11 item 6 and its Decisions list). An
    /// admin makes it once, for example:
    /// <c>sudo ip tuntap add dev rc0 mode tap user $USER</c>, then
    /// <c>sudo ip addr add 192.168.210.1/24 dev rc0</c> and <c>sudo ip link set rc0 up</c>.</para>
    /// <para>As nd100x does it (src/devices/ethernet/net/eth_net.c, read as a reference):
    /// open <c>/dev/net/tun</c>, the TUNSETIFF request with IFF_TAP (Ethernet frames) and
    /// IFF_NO_PI (no packet-information header, so one read or write is exactly one frame), a
    /// thread that waits with poll() and reads frames into a bounded queue, and one write() per
    /// frame sent. An interface another program already has open is refused (EBUSY), and the
    /// error says so. Linux only: on other systems <see cref="Start"/> throws.</para>
    /// </remarks>
    public sealed class TapEthernetBackend : IEthernetBackend
    {
        // ── Constants ──

        /// <summary>
        /// The spec prefix.
        /// </summary>
        public const string Prefix = "tap";

        /// <summary>
        /// The longest interface name Linux takes (IFNAMSIZ is 16 with the terminator).
        /// </summary>
        public const int MaxNameLength = 15;

        private const int MaxFrameBytes = 2048;
        private const int OpenReadWrite = 2;
        private const int OpenCloseOnExec = 0x80000;
        private const uint TunSetIff = 0x400454CA;
        private const short IffTap = 0x0002;
        private const short IffNoPi = 0x1000;
        private const short PollIn = 0x0001;
        private const int PollMs = 200;
        private const int ErrorBusy = 16;
        private const int IfreqBytes = 40;

        // ── Fields ──

        private readonly string _name;
        private readonly EthernetLinkReceiveQueue _queue = new EthernetLinkReceiveQueue(MaxFrameBytes);
        private int _fd = -1;
        private Thread? _thread;
        private volatile bool _stop;
        private long _framesSent;
        private long _framesReceived;
        private long _sendFailures;
        private long _receiveErrors;

        // ── Events ──

        /// <inheritdoc/>
        public event EthernetFrameHandler? OnFrameReceived;

        // ── Constructors ──

        /// <summary>
        /// Initializes a new <see cref="TapEthernetBackend"/>.
        /// </summary>
        /// <param name="interfaceName">
        /// The TAP interface, for example <c>rc0</c>.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown for an empty name or one over 15 characters.
        /// </exception>
        public TapEthernetBackend(string interfaceName)
        {
            string name = (interfaceName ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > MaxNameLength)
            {
                throw new ArgumentException("A TAP interface name is 1 to 15 characters: '" + name + "'.", nameof(interfaceName));
            }
            _name = name;
        }

        // ── Properties ──

        /// <inheritdoc/>
        public bool IsRunning => _thread != null;

        /// <summary>
        /// Gets the interface name.
        /// </summary>
        public string InterfaceName => _name;

        /// <summary>
        /// Gets how many frames were written to the interface.
        /// </summary>
        public long FramesSent => Interlocked.Read(ref _framesSent);

        /// <summary>
        /// Gets how many frames were read from the interface.
        /// </summary>
        public long FramesReceived => Interlocked.Read(ref _framesReceived);

        /// <summary>
        /// Gets how many writes failed.
        /// </summary>
        public long SendFailures => Interlocked.Read(ref _sendFailures);

        /// <summary>
        /// Gets how many reads failed.
        /// </summary>
        public long ReceiveErrors => Interlocked.Read(ref _receiveErrors);

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
        /// Makes a TAP backend from <c>tap:IFNAME</c>, or gives <c>null</c>.
        /// </summary>
        /// <param name="spec">
        /// The spec.
        /// </param>
        /// <returns>
        /// The backend, or <c>null</c>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// Thrown for <c>tap:</c> with a bad name.
        /// </exception>
        public static IEthernetBackend? CreateFromSpec(string spec)
        {
            if (spec == null)
            {
                return null;
            }
            string s = spec.Trim();
            if (!s.StartsWith(Prefix + ":", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return new TapEthernetBackend(s.Substring(Prefix.Length + 1));
        }

        /// <inheritdoc/>
        /// <exception cref="PlatformNotSupportedException">
        /// Thrown on a system other than Linux.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the interface cannot be opened.
        /// </exception>
        public void Start()
        {
            if (_thread != null)
            {
                return;
            }
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("tap:" + _name + ": TAP interfaces are built for Linux only.");
            }
            int fd = open("/dev/net/tun", OpenReadWrite | OpenCloseOnExec);
            if (fd < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException("tap:" + _name + ": cannot open /dev/net/tun (error " + err + ").");
            }
            byte[] ifreq = new byte[IfreqBytes];
            for (int i = 0; i < _name.Length; i++)
            {
                ifreq[i] = (byte)_name[i];
            }
            short flags = IffTap | IffNoPi;
            ifreq[16] = (byte)flags;
            ifreq[17] = (byte)(flags >> 8);
            if (ioctl(fd, TunSetIff, ifreq) != 0)
            {
                int err = Marshal.GetLastPInvokeError();
                close(fd);
                throw new InvalidOperationException(err == ErrorBusy
                    ? "tap:" + _name + ": cannot attach - another program already has it open."
                    : "tap:" + _name + ": cannot attach (error " + err + "). It must exist and belong to you: sudo ip tuntap add dev "
                        + _name + " mode tap user $USER");
            }
            _fd = fd;
            _stop = false;
            _thread = new Thread(ReceiveLoop) { Name = "TapLink", IsBackground = true };
            _thread.Start();
        }

        /// <inheritdoc/>
        public void Stop()
        {
            _stop = true;
            _thread?.Join(2000);
            _thread = null;
            int fd = Interlocked.Exchange(ref _fd, -1);
            if (fd >= 0)
            {
                close(fd);
            }
        }

        /// <inheritdoc/>
        public void Send(byte[] frame, int offset, int length)
        {
            int fd = Volatile.Read(ref _fd);
            if (fd < 0 || length <= 0 || length > MaxFrameBytes)
            {
                return;
            }
            if (write(fd, ref frame[offset], (nint)length) == length)
            {
                Interlocked.Increment(ref _framesSent);
            }
            else
            {
                Interlocked.Increment(ref _sendFailures);
            }
        }

        /// <inheritdoc/>
        public void Poll()
        {
            _queue.Deliver(OnFrameReceived);
        }

        // ── Private methods ──

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int ioctl(int fd, nuint request, byte[] argument);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern nint read(int fd, ref byte buffer, nint count);

        [DllImport("libc", SetLastError = true)]
        private static extern nint write(int fd, ref byte buffer, nint count);

        [DllImport("libc", SetLastError = true)]
        private static extern int poll(ref PollFd fds, nuint count, int timeoutMs);

        private void ReceiveLoop()
        {
            byte[] buffer = new byte[MaxFrameBytes];
            while (!_stop)
            {
                int fd = Volatile.Read(ref _fd);
                if (fd < 0)
                {
                    return;
                }
                PollFd p = new PollFd { Fd = fd, Events = PollIn };
                int r = poll(ref p, 1, PollMs);
                if (r == 0)
                {
                    continue;
                }
                if (r > 0)
                {
                    nint got = read(fd, ref buffer[0], buffer.Length);
                    if (got > 0)
                    {
                        Interlocked.Increment(ref _framesReceived);
                        _queue.Put(new ReadOnlySpan<byte>(buffer, 0, (int)got));
                        continue;
                    }
                }
                if (_stop)
                {
                    return;
                }
                // A lasting error must not spin a core.
                Interlocked.Increment(ref _receiveErrors);
                Thread.Sleep(50);
            }
        }

        // ── Nested types ──

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }
    }
}
