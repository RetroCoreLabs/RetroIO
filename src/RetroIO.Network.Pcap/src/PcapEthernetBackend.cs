//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.Threading;

using RetroIO.Network.Ethernet;

using SharpPcap;
using SharpPcap.LibPcap;

namespace RetroIO.Network.Pcap
{
    /// <summary>
    /// An <see cref="IEthernetBackend"/> that bridges an emulated NIC to a REAL host
    /// network adapter via SharpPcap (libpcap / Npcap): transmitted frames are written
    /// onto the wire and frames captured from the wire are delivered to the NIC.
    /// </summary>
    /// <remarks>
    /// Capture runs on SharpPcap's own thread; this backend never raises
    /// <see cref="OnFrameReceived"/> from that thread. It buffers captured frames into a
    /// thread-safe queue and the host drains them on the emulation thread via
    /// <see cref="Poll"/> (once per frame), so the frame is injected into the NIC chip on
    /// the thread that runs it. Opening a capture device usually requires elevated
    /// privileges and a host pcap driver — this is why the dependency lives in this
    /// opt-in package, separate from the native-dependency-free core.
    /// <para>
    /// What happens to a captured frame, on the capture thread: frames the guest sent itself
    /// and unicast frames for other machines are dropped (<see cref="GuestMacFilter"/>; the
    /// same rule is also set as a kernel capture filter so most of them are never copied up);
    /// frames longer than <see cref="MaxFrameBytes"/> are dropped; the rest are copied into a
    /// bounded queue of buffers made once (<see cref="EthernetFrameRing"/>), and their IPv4,
    /// TCP and UDP checksums are repaired in place when the host's checksum offload left them
    /// unfinished (<see cref="IpChecksumRepair"/>). <see cref="Poll"/> hands at most
    /// <see cref="MaxFramesPerPoll"/> frames to the guest per call. Every drop is counted and
    /// shown by <see cref="DescribeStatus"/>. These are fixes the legacy pcap backend had and
    /// this one lost (host networking design, section 8). Padding short frames to 60 bytes is
    /// done for every backend in the machine's EthernetManager.
    /// </para>
    /// </remarks>
    public sealed class PcapEthernetBackend : IEthernetBackend, IEthernetBackendStatus, IEthernetGuestAddresses
    {
        // ── Constants ──

        /// <summary>
        /// How many received frames wait for the guest before new ones are dropped. A real card's
        /// receive ring drops when full and the sender retransmits; this is that ring.
        /// </summary>
        public const int ReceiveQueueFrames = 512;

        /// <summary>
        /// The most frames one <see cref="Poll"/> hands to the guest, so a busy host link cannot
        /// starve the guest CPU. Polled once per host video frame (about 60 times a second) that
        /// is about 3800 frames a second - more than a 10 Mbit/s wire carries in full-size frames.
        /// </summary>
        public const int MaxFramesPerPoll = 64;

        /// <summary>
        /// The longest frame delivered, without FCS: 1514 bytes plus a 4-byte 802.1Q tag. A real
        /// wire carries nothing longer, and the emulated cards cannot take more.
        /// </summary>
        public const int MaxFrameBytes = 1518;

        // ── Fields ──

        /// <summary>
        /// The requested adapter (see <see cref="PcapAdapterChoice"/>), or <c>null</c> for ND-Loopback when it exists, else the first adapter.
        /// </summary>
        private readonly string? _deviceName;

        /// <summary>
        /// Frames captured from the wire, awaiting delivery on the next <see cref="Poll"/>.
        /// </summary>
        private readonly EthernetFrameRing _received = new EthernetFrameRing(ReceiveQueueFrames, MaxFrameBytes);

        /// <summary>
        /// The guest's MAC address(es), learned from its transmits, and the receive rule.
        /// </summary>
        private readonly GuestMacFilter _filter = new GuestMacFilter();

        /// <summary>
        /// The opened host capture device, or <c>null</c> when stopped.
        /// </summary>
        private ILiveDevice? _device;

        /// <summary>
        /// Running state.
        /// </summary>
        private bool _running;

        /// <summary>
        /// The <see cref="GuestMacFilter.Version"/> the kernel filter was last built from.
        /// </summary>
        private int _kernelFilterVersion = -1;

        /// <summary>
        /// The kernel capture filter in force, or <c>null</c> when none is set.
        /// </summary>
        private string? _kernelFilter;

        /// <summary>
        /// Which adapter was chosen and why, in plain words (see <see cref="PcapAdapterChoice"/>),
        /// or <c>null</c> before <see cref="Start"/>.
        /// </summary>
        private string? _choiceReason;

        /// <summary>
        /// Why the kernel filter could not be set, or <c>null</c>; the software rule still applies.
        /// </summary>
        private string? _kernelFilterError;

        /// <summary>
        /// Frames handed to the guest.
        /// </summary>
        private long _framesDelivered;

        /// <summary>
        /// Frames whose IPv4, TCP or UDP checksums were repaired.
        /// </summary>
        private long _framesRepaired;

        /// <summary>
        /// Captured frames the guest sent itself.
        /// </summary>
        private long _droppedSelfEcho;

        /// <summary>
        /// Captured unicast frames for other machines.
        /// </summary>
        private long _droppedNotForGuest;

        /// <summary>
        /// Captured frames longer than <see cref="MaxFrameBytes"/>.
        /// </summary>
        private long _droppedTooLong;

        /// <summary>
        /// Captured frames shorter than an Ethernet header.
        /// </summary>
        private long _droppedTooShort;

        // ── Events ──

        /// <inheritdoc/>
        public event EthernetFrameHandler? OnFrameReceived;

        // ── Constructors ──

        /// <summary>
        /// Initializes a new <see cref="PcapEthernetBackend"/>.
        /// </summary>
        /// <param name="deviceName">
        /// The host adapter to bind to: its connection name (such as "ND-Loopback"), pcap name,
        /// part of its description, an IPv4 address, or <c>select:</c> and an IPv4 prefix (see
        /// <see cref="PcapAdapterChoice"/>); <c>null</c> binds ND-Loopback when it exists, else
        /// the first adapter.
        /// </param>
        public PcapEthernetBackend(string? deviceName = null)
        {
            _deviceName = deviceName;
        }

        // ── Properties ──

        /// <inheritdoc/>
        public bool IsRunning => _running;

        /// <summary>
        /// Gets how many frames were handed to the guest.
        /// </summary>
        public long FramesDelivered => Interlocked.Read(ref _framesDelivered);

        /// <summary>
        /// Gets how many frames had their checksums repaired (host checksum offload).
        /// </summary>
        public long FramesRepaired => Interlocked.Read(ref _framesRepaired);

        /// <summary>
        /// Gets how many captured frames were the guest's own transmits.
        /// </summary>
        public long FramesDroppedSelfEcho => Interlocked.Read(ref _droppedSelfEcho);

        /// <summary>
        /// Gets how many captured unicast frames were for other machines.
        /// </summary>
        public long FramesDroppedNotForGuest => Interlocked.Read(ref _droppedNotForGuest);

        /// <summary>
        /// Gets how many captured frames were dropped because the receive queue was full.
        /// </summary>
        public long FramesDroppedQueueFull => _received.FramesDroppedFull;

        /// <summary>
        /// Gets how many captured frames were longer than <see cref="MaxFrameBytes"/>.
        /// </summary>
        public long FramesDroppedTooLong => Interlocked.Read(ref _droppedTooLong);

        /// <summary>
        /// Gets how many captured frames were shorter than an Ethernet header.
        /// </summary>
        public long FramesDroppedTooShort => Interlocked.Read(ref _droppedTooShort);

        /// <summary>
        /// Gets the kernel capture filter the guest's addresses learned so far call for.
        /// </summary>
        public string KernelFilter => _filter.BuildKernelFilter();

        // ── Public methods ──

        /// <summary>
        /// Registers this backend with <see cref="EthernetBackends"/> so the generic config
        /// spec <c>"pcap"</c>, or <c>"pcap:"</c> followed by an adapter name, resolves to a
        /// <see cref="PcapEthernetBackend"/>. A host calls this ONCE at startup (it is the
        /// host that carries the pcap dependency); machines then select pcap purely through
        /// their <c>net</c> <c>[MachineOption]</c> with no reference to this package.
        /// </summary>
        public static void RegisterFactory()
        {
            EthernetBackends.Register(CreateFromSpec);
        }

        /// <summary>
        /// The <see cref="EthernetBackendFactory"/> for pcap specs: <c>"pcap"</c> (first
        /// adapter) or <c>"pcap:"</c> followed by an adapter name (that adapter); <c>null</c> otherwise.
        /// </summary>
        /// <param name="spec">
        /// The backend spec.
        /// </param>
        /// <returns>
        /// A <see cref="PcapEthernetBackend"/>, or <c>null</c> when the spec is not pcap.
        /// </returns>
        public static IEthernetBackend? CreateFromSpec(string spec)
        {
            if (string.IsNullOrEmpty(spec))
            {
                return null;
            }
            if (string.Equals(spec, "pcap", StringComparison.OrdinalIgnoreCase))
            {
                return new PcapEthernetBackend();
            }
            if (spec.StartsWith("pcap:", StringComparison.OrdinalIgnoreCase))
            {
                string device = spec.Substring("pcap:".Length).Trim();
                return new PcapEthernetBackend(device.Length == 0 ? null : device);
            }
            return null;
        }

        /// <summary>
        /// Lists the host capture adapters available to bind to, as <c>"Name — Description"</c> strings.
        /// </summary>
        /// <returns>
        /// The available adapters; empty when none / no pcap driver is present.
        /// </returns>
        public static IReadOnlyList<string> ListDevices()
        {
            List<string> names = new List<string>();
            try
            {
                CaptureDeviceList devices = CaptureDeviceList.Instance;
                for (int i = 0; i < devices.Count; i++)
                {
                    ILiveDevice d = devices[i];
                    names.Add(d.Name + " — " + d.Description);
                }
            }
            catch (Exception)
            {
                // No pcap driver / no permission — return what we have (possibly empty).
            }
            return names;
        }

        /// <summary>
        /// Lists the host capture adapters as user-friendly labels suitable for a config
        /// dropdown — the adapter's Description when it has one, else its Name. Each label is a
        /// value <see cref="ResolveDevice"/> accepts directly (it matches Name OR Description).
        /// </summary>
        /// <remarks>
        /// This is what a host registers as the value provider named <c>"ethernet-adapters"</c>
        /// (RetroCore's machine option value providers) so a machine's <c>net-device</c> option shows the
        /// REAL adapters present on the box.
        /// Best-effort: returns an empty list when no pcap driver is installed.
        /// </remarks>
        /// <returns>
        /// One friendly label per adapter; empty when none are available.
        /// </returns>
        public static IReadOnlyList<string> ListDeviceLabels()
        {
            List<string> labels = new List<string>();
            try
            {
                CaptureDeviceList devices = CaptureDeviceList.Instance;
                PcapAdapter[] adapters = DescribeAdapters(devices);
                for (int i = 0; i < adapters.Length; i++)
                {
                    PcapAdapter d = adapters[i];
                    string baseLabel = d.Description.Length == 0 ? d.PcapName : d.Description;
                    if (string.IsNullOrWhiteSpace(baseLabel))
                    {
                        continue;
                    }

                    // The system's connection name in front ("ND-Loopback: Microsoft KM-TEST
                    // Loopback Adapter"), and the bound IPv4 address(es) after, so the user can
                    // pick the right NIC by either. ResolveDevice accepts the whole label too.
                    string named = d.ConnectionName != null ? d.ConnectionName + ": " + baseLabel : baseLabel;
                    string label = d.Ipv4.Length == 0 ? named : named + "  [" + string.Join(", ", d.Ipv4) + "]";
                    labels.Add(label);
                }
            }
            catch (Exception)
            {
                // No pcap driver / no permission — offer no suggestions rather than throwing.
            }
            return labels;
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the adapters cannot be listed, no adapter matches, the adapter cannot be
        /// opened, or it is not an Ethernet adapter. The message says what this host system
        /// needs for pcap (<see cref="PcapSetupAdvice"/>).
        /// </exception>
        public void Start()
        {
            if (_running)
            {
                return;
            }

            ILiveDevice? device;
            try
            {
                device = ResolveDevice();
            }
            catch (Exception ex)
            {
                // No pcap library at all (DllNotFoundException / TypeInitializationException), or
                // no permission to list adapters. Say what this system needs instead of a bare
                // native error.
                throw new InvalidOperationException(
                    "PcapEthernetBackend: the host adapters could not be listed (" + ex.Message + "). "
                    + PcapSetupAdvice.ForThisSystem(), ex);
            }
            if (device == null)
            {
                throw new InvalidOperationException(
                    "PcapEthernetBackend: no capture adapter available" +
                    (string.IsNullOrEmpty(_deviceName) ? " (is a pcap driver installed?)." : " matching '" + _deviceName + "' (net.devices lists them).")
                    + " " + PcapSetupAdvice.ForThisSystem());
            }

            try
            {
                DeviceConfiguration config = new DeviceConfiguration { Mode = DeviceModes.Promiscuous, ReadTimeout = 100 };
                if (OperatingSystem.IsWindows())
                {
                    // Npcap holds captured frames until a minimum number of bytes has gathered or
                    // the read timeout runs out; 0 hands each frame up at once. This handle's own
                    // setting, not a host setting. Measured 2026-11-22 on ND-Loopback (ARP reply
                    // from this computer, average of 20, PcapMinToCopyMeasurementTests): 50.11 ms
                    // with the driver's own value, 0.22 ms with 0.
                    config.MinToCopy = 0;
                }
                device.Open(config);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "PcapEthernetBackend: could not open '" + device.Description + "' (" + ex.Message + "). "
                    + PcapSetupAdvice.ForThisSystem(), ex);
            }

            if (device.LinkType != PacketDotNet.LinkLayers.Ethernet)
            {
                // The guest cards speak Ethernet frames; a capture that delivers another link
                // layer (Wi-Fi radio headers, the Linux "any" device) would hand them garbage.
                PacketDotNet.LinkLayers linkType = device.LinkType;
                device.Close();
                throw new InvalidOperationException(
                    "PcapEthernetBackend: '" + device.Description + "' is not an Ethernet adapter (pcap reports link type " + linkType + "). "
                    + PcapSetupAdvice.Limits);
            }

            ApplyKernelFilter(device);
            device.OnPacketArrival += OnPacketArrival;
            device.StartCapture();

            _device = device;
            _running = true;
        }

        /// <inheritdoc/>
        public void Stop()
        {
            if (!_running || _device == null)
            {
                return;
            }

            ILiveDevice device = _device;
            _device = null;
            _running = false;

            try { device.StopCapture(); }
            catch (PcapException) { }

            device.OnPacketArrival -= OnPacketArrival;

            try { device.Close(); }
            catch (PcapException) { }
        }

        /// <inheritdoc/>
        public void Send(byte[] frame, int offset, int length)
        {
            if (frame == null || length <= 0 || offset < 0 || offset + length > frame.Length)
            {
                return;
            }

            // The source address of what the guest sends is the address it really uses: learn
            // it, so captured copies of our own frames and frames for other machines are dropped.
            // Learned even while stopped, so the rule is right from the first captured frame.
            ReadOnlySpan<byte> bytes = new ReadOnlySpan<byte>(frame, offset, length);
            _filter.Learn(bytes);

            ILiveDevice? device = _device;
            if (!_running || device == null)
            {
                return;
            }

            device.SendPacket(bytes);
        }

        /// <summary>
        /// Adds a card address the machine already knows, so frames for that card pass before the
        /// card has sent anything. The kernel filter follows on the next <see cref="Poll"/>.
        /// </summary>
        /// <param name="mac">
        /// The 6-byte address.
        /// </param>
        /// <returns>
        /// <c>true</c> when the address was new.
        /// </returns>
        public bool AddGuestAddress(ReadOnlySpan<byte> mac)
        {
            return _filter.Add(mac);
        }

        /// <summary>
        /// Removes a card address that is no longer used. The kernel filter follows on the next
        /// <see cref="Poll"/>.
        /// </summary>
        /// <param name="mac">
        /// The 6-byte address.
        /// </param>
        /// <returns>
        /// <c>true</c> when the address was held.
        /// </returns>
        public bool RemoveGuestAddress(ReadOnlySpan<byte> mac)
        {
            return _filter.Remove(mac);
        }

        /// <inheritdoc/>
        public void Poll()
        {
            // A new guest address since the last call: rebuild the kernel filter. Done here, on
            // the emulation thread, with the capture stopped around it - libpcap does not allow
            // one handle to be used from two threads at once, and the capture thread is inside
            // libpcap all the time it runs.
            ILiveDevice? device = _device;
            if (_running && device != null && _filter.Version != _kernelFilterVersion)
            {
                try { device.StopCapture(); }
                catch (PcapException) { }
                ApplyKernelFilter(device);
                device.StartCapture();
            }

            // Drain what was captured since the last frame, on the caller's (emulation) thread -
            // but at most MaxFramesPerPoll; the rest waits for the next call.
            int handed = 0;
            while (handed < MaxFramesPerPoll && _received.TryPeek(out byte[]? frame, out int length))
            {
                try
                {
                    OnFrameReceived?.Invoke(frame!, 0, length);
                }
                finally
                {
                    _received.Release();
                }
                handed++;
                Interlocked.Increment(ref _framesDelivered);
            }
        }

        /// <inheritdoc/>
        public string[] DescribeStatus()
        {
            string macs = _filter.DescribeMacs();
            ILiveDevice? device = _device;
            string filterLine = _kernelFilterError != null
                ? "pcap: the kernel capture filter could not be set (" + _kernelFilterError + "); the same rule is applied in software."
                : "pcap: kernel capture filter: " + (_kernelFilter ?? "(none yet - set when the adapter opens)");
            return new string[]
            {
                "pcap: adapter " + (device != null ? device.Description : (_deviceName ?? "(" + PcapAdapterChoice.DefaultConnectionName + " when it exists, else the first)"))
                    + ", guest MAC " + (macs.Length > 0 ? macs : "not learned yet (only broadcast and multicast reach the guest until it sends a frame)"),
                "pcap: chosen: " + (_choiceReason ?? "not yet (the adapter is chosen when the backend starts)"),
                filterLine,
                "pcap: delivered " + FramesDelivered + ", checksums repaired " + FramesRepaired + ", waiting " + _received.Count,
                "pcap: dropped: own frames " + FramesDroppedSelfEcho + ", for other machines " + FramesDroppedNotForGuest
                    + ", queue full " + FramesDroppedQueueFull + ", too long " + FramesDroppedTooLong + ", too short " + FramesDroppedTooShort,
            };
        }

        // ── Internal methods ──

        /// <summary>
        /// Capture-thread side of receive: decides whether the frame goes to the guest, then
        /// copies it into the receive queue and repairs its checksums. Called by
        /// <see cref="OnPacketArrival"/>; unit tests call it directly, because opening a real
        /// adapter is a hand test.
        /// </summary>
        /// <param name="data">
        /// The captured frame, from the destination MAC on, no FCS.
        /// </param>
        internal void AcceptCapturedFrame(ReadOnlySpan<byte> data)
        {
            switch (_filter.Classify(data))
            {
                case CapturedFrameVerdict.TooShort:
                    Interlocked.Increment(ref _droppedTooShort);
                    return;
                case CapturedFrameVerdict.SelfEcho:
                    Interlocked.Increment(ref _droppedSelfEcho);
                    return;
                case CapturedFrameVerdict.NotForGuest:
                    Interlocked.Increment(ref _droppedNotForGuest);
                    return;
            }

            if (data.Length > MaxFrameBytes)
            {
                // Longer than any wire carries. Not verified: whether Npcap hands over the host's
                // own large-send-offload frames (one TCP frame bigger than the wire allows, cut up
                // later by the card). If it does, they end up here and are counted.
                Interlocked.Increment(ref _droppedTooLong);
                return;
            }

            if (!_received.TryReserve(data.Length, out Span<byte> slot))
            {
                // Queue full: counted by the queue itself (FramesDroppedQueueFull).
                return;
            }

            data.CopyTo(slot);
            if (IpChecksumRepair.RepairInPlace(slot) != RepairedChecksums.None)
            {
                Interlocked.Increment(ref _framesRepaired);
            }
            _received.Commit();
        }

        // ── Private methods ──

        /// <summary>
        /// Sets the kernel capture filter built from the guest's addresses, so the kernel (or the
        /// Npcap driver) drops frames for other machines before they are copied up. When it
        /// cannot be set the reason is kept for <see cref="DescribeStatus"/>; the software rule in
        /// <see cref="AcceptCapturedFrame"/> applies either way, so the guest gets the same frames.
        /// </summary>
        private void ApplyKernelFilter(ILiveDevice device)
        {
            int version = _filter.Version;
            string filter = _filter.BuildKernelFilter();
            try
            {
                device.Filter = filter;
                _kernelFilter = filter;
                _kernelFilterError = null;
            }
            catch (PcapException ex)
            {
                _kernelFilter = null;
                _kernelFilterError = ex.Message;
            }
            _kernelFilterVersion = version;
        }

        /// <summary>
        /// Resolves the adapter to bind (see <see cref="PcapAdapterChoice"/>: by connection name,
        /// pcap name, description, address or <c>select:</c> prefix; with no name, ND-Loopback when
        /// it exists, else the first), keeping the reason for <see cref="DescribeStatus"/>.
        /// Returns <c>null</c> when none fits.
        /// </summary>
        private ILiveDevice? ResolveDevice()
        {
            CaptureDeviceList devices = CaptureDeviceList.Instance;
            PcapAdapter[] adapters = DescribeAdapters(devices);
            int index = PcapAdapterChoice.Choose(adapters, _deviceName, out string reason);
            _choiceReason = reason;
            return index < 0 ? null : devices[index];
        }

        /// <summary>
        /// The adapters as <see cref="PcapAdapterChoice"/> sees them: pcap's name, description and
        /// IPv4 addresses, and the operating system's own name for each, read from the system's
        /// adapter list (<see cref="System.Net.NetworkInformation.NetworkInterface"/>; reading
        /// only, nothing on the host is changed).
        /// </summary>
        private static PcapAdapter[] DescribeAdapters(CaptureDeviceList devices)
        {
            List<string> ids = new List<string>();
            List<string> names = new List<string>();
            try
            {
                System.Net.NetworkInformation.NetworkInterface[] nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nics.Length; i++)
                {
                    ids.Add(nics[i].Id);
                    names.Add(nics[i].Name);
                }
            }
            catch (System.Net.NetworkInformation.NetworkInformationException)
            {
                // No system list: adapters are then chosen by pcap's own names only.
            }
            PcapAdapter[] adapters = new PcapAdapter[devices.Count];
            for (int i = 0; i < devices.Count; i++)
            {
                ILiveDevice d = devices[i];
                adapters[i] = new PcapAdapter(d.Name, d.Description, PcapAdapterChoice.ConnectionNameFor(d.Name, ids, names), GetIPv4Addresses(d).ToArray());
            }
            return adapters;
        }

        /// <summary>
        /// Returns the IPv4 addresses bound to an adapter (empty when none / unavailable).
        /// </summary>
        private static List<string> GetIPv4Addresses(ILiveDevice device)
        {
            List<string> ips = new List<string>();
            try
            {
                LibPcapLiveDevice? live = device as LibPcapLiveDevice;
                if (live == null)
                {
                    return ips;
                }
                System.Collections.ObjectModel.ReadOnlyCollection<PcapAddress> addresses = live.Addresses;
                for (int i = 0; i < addresses.Count; i++)
                {
                    System.Net.IPAddress? ip = addresses[i]?.Addr?.ipAddress;
                    if (ip != null && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        ips.Add(ip.ToString());
                    }
                }
            }
            catch (Exception)
            {
                // best-effort
            }
            return ips;
        }

        /// <summary>
        /// SharpPcap capture-thread callback: copy the frame and queue it for the next <see cref="Poll"/>.
        /// </summary>
        private void OnPacketArrival(object sender, PacketCapture e)
        {
            // e.Data is the capture buffer itself (no copy, no allocation); it is only valid
            // during this call, and AcceptCapturedFrame copies what it keeps.
            AcceptCapturedFrame(e.Data);
        }
    }
}
