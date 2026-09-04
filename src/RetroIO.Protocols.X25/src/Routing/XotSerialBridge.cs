using System;
using System.Buffers;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Transport
{
    /// <summary>
    /// Bridge mode enumeration
    /// </summary>
    public enum BridgeMode
    {
        /// <summary>
        /// XOT to Serial bridge
        /// </summary>
        XotToSerial,

        /// <summary>
        /// Serial to XOT bridge (reverse direction)
        /// </summary>
        SerialToXot,

        /// <summary>
        /// XOT to XOT bridge (both interfaces are XOT)
        /// </summary>
        XotToXot,

        /// <summary>
        /// Serial to Serial bridge (both interfaces are Serial)
        /// </summary>
        SerialToSerial
    }

    /// <summary>
    /// High-performance transport bridge for transparent pass-through mode
    /// Supports XOT-to-Serial, XOT-to-XOT, and Serial-to-Serial bridging
    /// Bypasses routing layer for maximum throughput with zero-allocation design
    /// Uses Span&lt;T&gt;, ArrayPool, and Interlocked for optimal performance
    /// </summary>
    public class XotSerialBridge : IDisposable
    {
        // Zero-allocation buffer pool (shared across all bridges)
        private static readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;

        // Transport references
        private readonly ITransportInterface _interface1;
        private readonly ITransportInterface _interface2;
        private readonly IProtocolRuntime _runtime;

        // Bridge mode (determined by transport types)
        private readonly BridgeMode _mode;

        // Configuration
        private readonly XotSerialBridgeConfig _config;
        private readonly ILogger _logger;

        // Statistics (using Interlocked for thread-safety without locks)
        private long _interface1To2Bytes;
#pragma warning disable CS0649 // Statistics counters for future monitoring features
        private long _interface2To1Bytes;
        private long _interface1To2Packets;
        private long _interface2To1Packets;
#pragma warning restore CS0649
        private long _interface1To2Frames;
        private long _interface2To1Frames;
        private long _errors;
        private readonly DateTime _startTime;

        // Active flag
        private volatile bool _isActive;
        private bool _disposed;

        /// <summary>
        /// Creates a new high-performance transport bridge
        /// </summary>
        public XotSerialBridge(
            ILogger logger,
            ITransportInterface interface1,
            ITransportInterface interface2,
            IProtocolRuntime runtime,
            XotSerialBridgeConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _interface1 = interface1 ?? throw new ArgumentNullException(nameof(interface1));
            _interface2 = interface2 ?? throw new ArgumentNullException(nameof(interface2));
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _startTime = DateTime.UtcNow;

            // Determine bridge mode based on transport types
            _mode = DetermineBridgeMode(interface1.Type, interface2.Type);

            _logger.LogInformation("Transport bridge created: {Interface1} <-> {Interface2} (Mode: {Mode})",
                interface1.Name, interface2.Name, _mode);
        }

        /// <summary>
        /// Determine bridge mode from transport types
        /// </summary>
        private static BridgeMode DetermineBridgeMode(TransportType type1, TransportType type2)
        {
            bool is1XOT = type1 == TransportType.XOT_Client || type1 == TransportType.XOT_Server;
            bool is2XOT = type2 == TransportType.XOT_Client || type2 == TransportType.XOT_Server;
            bool is1Serial = type1 == TransportType.Serial;
            bool is2Serial = type2 == TransportType.Serial;

            if (is1XOT && is2XOT)
                return BridgeMode.XotToXot;
            else if (is1Serial && is2Serial)
                return BridgeMode.SerialToSerial;
            else if (is1XOT && is2Serial)
                return BridgeMode.XotToSerial;
            else if (is1Serial && is2XOT)
                return BridgeMode.SerialToXot;
            else
                throw new InvalidOperationException($"Unsupported transport combination: {type1} to {type2}");
        }

        /// <summary>
        /// Start the bridge and begin relaying data
        /// </summary>
        public void Start()
        {
            if (_isActive)
            {
                _logger.LogWarning("Transport bridge already active");
                return;
            }

            _isActive = true;

            // Subscribe to events based on bridge mode
            switch (_mode)
            {
                case BridgeMode.XotToSerial:
                    // Subscribe to XOT data received event (interface1 = XOT, interface2 = Serial)
                    _interface1.DataReceived += OnXotDataReceived;
                    // Also subscribe to Serial frames for reverse direction
                    // Stack A serial LAPB bridging retired; serial is handled by Stack B links now.
                    break;

                case BridgeMode.SerialToXot:
                    // Subscribe to Serial LAPB frames (interface1 = Serial, interface2 = XOT)
                    // Stack A serial LAPB bridging retired; serial is handled by Stack B links now.
                    // Also subscribe to XOT data for reverse direction
                    _interface2.DataReceived += OnXotDataReceived;
                    break;

                case BridgeMode.XotToXot:
                    // Subscribe to both XOT interfaces
                    _interface1.DataReceived += OnXot1DataReceived;
                    _interface2.DataReceived += OnXot2DataReceived;
                    break;

                case BridgeMode.SerialToSerial:
                    // Subscribe to LAPB frames (will need to distinguish which serial port)
                    // Stack A serial LAPB bridging retired; serial is handled by Stack B links now.
                    break;
            }

            _logger.LogInformation("Transport bridge started: {Interface1} <-> {Interface2} (Mode: {Mode}, Buffer: {BufferSize} bytes)",
                _interface1.Name, _interface2.Name, _mode, _config.BufferSize);
        }

        /// <summary>
        /// Stop the bridge and cease relaying
        /// </summary>
        public void Stop()
        {
            if (!_isActive)
                return;

            _isActive = false;

            // Unsubscribe from events based on bridge mode
            switch (_mode)
            {
                case BridgeMode.XotToSerial:
                    _interface1.DataReceived -= OnXotDataReceived;
                    // Stack A serial LAPB bridging retired.
                    break;

                case BridgeMode.SerialToXot:
                    // Stack A serial LAPB bridging retired.
                    _interface2.DataReceived -= OnXotDataReceived;
                    break;

                case BridgeMode.XotToXot:
                    _interface1.DataReceived -= OnXot1DataReceived;
                    _interface2.DataReceived -= OnXot2DataReceived;
                    break;

                case BridgeMode.SerialToSerial:
                    // Stack A serial LAPB bridging retired.
                    break;
            }

            _logger.LogInformation("Transport bridge stopped: {Interface1} <-> {Interface2}",
                _interface1.Name, _interface2.Name);
        }

        /// <summary>
        /// Get current bridge statistics
        /// </summary>
        public BridgeStatistics GetStatistics()
        {
            return new BridgeStatistics
            {
                Interface1Name = _interface1.Name,
                Interface2Name = _interface2.Name,
                BridgeMode = _mode.ToString(),
                IsActive = _isActive,
                Interface1To2Bytes = Interlocked.Read(ref _interface1To2Bytes),
                Interface1To2Packets = Interlocked.Read(ref _interface1To2Packets),
                Interface1To2Frames = Interlocked.Read(ref _interface1To2Frames),
                Interface2To1Bytes = Interlocked.Read(ref _interface2To1Bytes),
                Interface2To1Packets = Interlocked.Read(ref _interface2To1Packets),
                Interface2To1Frames = Interlocked.Read(ref _interface2To1Frames),
                Errors = Interlocked.Read(ref _errors),
                Uptime = DateTime.UtcNow - _startTime
            };
        }

        /// <summary>
        /// Handle Serial frame received (LAPB I-frame containing X.25 packet)
        /// Extracts X.25 packet and relays based on bridge mode
        /// </summary>
        private void OnSerialFrameReceived(object? sender, LapbFrameEventArgs e)
        {
            if (!_isActive || e.FrameData == null || e.Length < 3)
                return;

            try
            {
                // Only process I-frames (data frames)
                // I-frame check: bit 0 of control byte = 0
                byte control = e.FrameData[1];
                bool isIFrame = (control & 0x01) == 0;

                if (!isIFrame)
                    return; // Skip supervisory and unnumbered frames

                // Extract X.25 packet from LAPB I-frame
                // I-frame format: Address(1) + Control(1) + X.25 Packet(n)
                ReadOnlySpan<byte> frameSpan = new ReadOnlySpan<byte>(e.FrameData, 0, e.Length);
                ReadOnlySpan<byte> x25Packet = frameSpan.Slice(2); // Skip address + control

                // Relay based on bridge mode
                switch (_mode)
                {
                    case BridgeMode.SerialToXot:
                        RelaySerialToXot(x25Packet);
                        Interlocked.Increment(ref _interface1To2Frames);
                        break;

                    case BridgeMode.SerialToSerial:
                        RelaySerialToSerial(x25Packet);
                        Interlocked.Increment(ref _interface1To2Frames);
                        break;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "Error relaying frame");
            }
        }

        /// <summary>
        /// Handle Serial frame transmitted (for statistics only)
        /// </summary>
        private void OnSerialFrameTransmitted(object? sender, LapbFrameEventArgs e)
        {
            if (!_isActive)
                return;

            // Update frame count statistics
            byte control = e.FrameData?[1] ?? 0;
            bool isIFrame = (control & 0x01) == 0;

            if (isIFrame)
            {
                Interlocked.Increment(ref _interface2To1Frames);
            }
        }

        /// <summary>
        /// Handle XOT data received (for XotToSerial or SerialToXot modes)
        /// </summary>
        private void OnXotDataReceived(object? sender, TransportDataEventArgs e)
        {
            if (!_isActive || e.Data == null || e.Length == 0)
                return;

            try
            {
                // Extract X.25 packet from received data
                ReadOnlySpan<byte> x25Packet = new ReadOnlySpan<byte>(e.Data, 0, e.Length);

                // Relay based on bridge mode
                switch (_mode)
                {
                    case BridgeMode.XotToSerial:
                        // XOT → Serial: wrap in LAPB and send to serial
                        RelayXotToSerial(x25Packet);
                        Interlocked.Increment(ref _interface1To2Frames);
                        break;

                    case BridgeMode.SerialToXot:
                        // XOT → Serial: wrap in LAPB and send to serial
                        RelayXotToSerial(x25Packet);
                        Interlocked.Increment(ref _interface2To1Frames);
                        break;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "Error relaying XOT data");
            }
        }

        /// <summary>
        /// Handle data received from XOT interface 1 (for XotToXot mode)
        /// </summary>
        private void OnXot1DataReceived(object? sender, TransportDataEventArgs e)
        {
            if (!_isActive || e.Data == null || e.Length == 0)
                return;

            try
            {
                ReadOnlySpan<byte> xotPacket = new ReadOnlySpan<byte>(e.Data, 0, e.Length);
                RelayXotToXot(xotPacket);
                Interlocked.Increment(ref _interface1To2Frames);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "Error relaying XOT1 data");
            }
        }

        /// <summary>
        /// Handle data received from XOT interface 2 (for XotToXot mode)
        /// </summary>
        private void OnXot2DataReceived(object? sender, TransportDataEventArgs e)
        {
            if (!_isActive || e.Data == null || e.Length == 0)
                return;

            try
            {
                ReadOnlySpan<byte> xotPacket = new ReadOnlySpan<byte>(e.Data, 0, e.Length);
                RelayXotToXot(xotPacket);
                Interlocked.Increment(ref _interface2To1Frames);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug(ex, "Error relaying XOT2 data");
            }
        }

        /// <summary>
        /// Relay X.25 packet from Serial to XOT with zero allocations
        /// Wraps packet in XOT header (4 bytes: version + length)
        /// </summary>
        private void RelaySerialToXot(ReadOnlySpan<byte> x25Packet)
        {
            if (x25Packet.Length == 0 || x25Packet.Length > 4096)
            {
                Interlocked.Increment(ref _errors);
                return;
            }

            // Rent buffer from pool (no allocation if available)
            byte[] buffer = _bufferPool.Rent(_config.BufferSize);
            try
            {
                // Build XOT header (4 bytes: version + length) on stack
                Span<byte> header = stackalloc byte[4];
                header[0] = 0x00; // Version high byte (XOT version 0)
                header[1] = 0x00; // Version low byte
                header[2] = (byte)(x25Packet.Length >> 8); // Length high byte
                header[3] = (byte)(x25Packet.Length & 0xFF); // Length low byte

                // Copy header and packet to buffer
                int offset = 0;
                header.CopyTo(new Span<byte>(buffer, offset, 4));
                offset += 4;
                x25Packet.CopyTo(new Span<byte>(buffer, offset, x25Packet.Length));
                offset += x25Packet.Length;

                // TODO: Send to XOT transport (TCP stream write)
                // This requires adding a SendData method to ITransportInterface
                // For now, log the relay
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Serial->XOT relay: {Length} bytes", x25Packet.Length);

                // Update statistics (thread-safe increment)
                Interlocked.Increment(ref _interface1To2Packets);
                Interlocked.Add(ref _interface1To2Bytes, x25Packet.Length);
            }
            finally
            {
                // Return buffer to pool (critical for zero-allocation)
                _bufferPool.Return(buffer, clearArray: false);
            }
        }

        /// <summary>
        /// Relay X.25 packet from XOT to Serial with zero allocations
        /// Wraps packet in LAPB I-frame and sends via LAPB layer
        /// </summary>
        private void RelayXotToSerial(ReadOnlySpan<byte> xotPacket)
        {
            if (xotPacket.Length == 0 || xotPacket.Length > 4096)
            {
                Interlocked.Increment(ref _errors);
                return;
            }

            // Rent buffer from pool
            byte[] buffer = _bufferPool.Rent(_config.BufferSize);
            try
            {
                // X.25 packet will be wrapped by LAPB layer automatically
                // Just copy the packet data
                xotPacket.CopyTo(new Span<byte>(buffer, 0, xotPacket.Length));

                // Stack A serial LAPB relay retired; serial output is handled by Stack B links now.
                _logger.LogWarning("XOT->Serial relay: legacy LAPB send retired ({Length} bytes)", xotPacket.Length);

                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("XOT->Serial relay: {Length} bytes", xotPacket.Length);

                // Update statistics
                Interlocked.Increment(ref _interface1To2Packets);
                Interlocked.Add(ref _interface1To2Bytes, xotPacket.Length);
            }
            finally
            {
                _bufferPool.Return(buffer, clearArray: false);
            }
        }

        /// <summary>
        /// Relay X.25 packet from Serial to Serial with zero allocations
        /// Extracts from one LAPB connection and re-wraps for another
        /// </summary>
        private void RelaySerialToSerial(ReadOnlySpan<byte> x25Packet)
        {
            if (x25Packet.Length == 0 || x25Packet.Length > 4096)
            {
                Interlocked.Increment(ref _errors);
                return;
            }

            // Rent buffer from pool
            byte[] buffer = _bufferPool.Rent(_config.BufferSize);
            try
            {
                // Copy X.25 packet to buffer
                x25Packet.CopyTo(new Span<byte>(buffer, 0, x25Packet.Length));

                // Stack A serial LAPB relay retired; serial output is handled by Stack B links now.
                _logger.LogWarning("Serial->Serial relay: legacy LAPB send retired ({Length} bytes)", x25Packet.Length);

                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Serial->Serial relay: {Length} bytes", x25Packet.Length);

                // Update statistics
                Interlocked.Increment(ref _interface1To2Packets);
                Interlocked.Add(ref _interface1To2Bytes, x25Packet.Length);
            }
            finally
            {
                _bufferPool.Return(buffer, clearArray: false);
            }
        }

        /// <summary>
        /// Relay XOT packet from XOT to XOT with zero allocations
        /// Direct packet forwarding (already has XOT header)
        /// </summary>
        private void RelayXotToXot(ReadOnlySpan<byte> xotPacket)
        {
            if (xotPacket.Length < 4 || xotPacket.Length > 4096)
            {
                Interlocked.Increment(ref _errors);
                return;
            }

            // Rent buffer from pool
            byte[] buffer = _bufferPool.Rent(_config.BufferSize);
            try
            {
                // Copy XOT packet directly (already has header)
                xotPacket.CopyTo(new Span<byte>(buffer, 0, xotPacket.Length));

                // TODO: Send to other XOT transport (TCP stream write)
                // This requires adding a SendData method to ITransportInterface
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("XOT->XOT relay: {Length} bytes", xotPacket.Length);

                // Update statistics
                Interlocked.Increment(ref _interface1To2Packets);
                Interlocked.Add(ref _interface1To2Bytes, xotPacket.Length);
            }
            finally
            {
                _bufferPool.Return(buffer, clearArray: false);
            }
        }

        /// <summary>
        /// Dispose bridge resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;

            _logger.LogInformation("Transport bridge disposed");
        }
    }

    /// <summary>
    /// Statistics for transport bridge
    /// </summary>
    public class BridgeStatistics
    {
        public string Interface1Name { get; set; } = string.Empty;
        public string Interface2Name { get; set; } = string.Empty;
        public string BridgeMode { get; set; } = string.Empty;
        public bool IsActive { get; set; }

        // Interface1 → Interface2 direction
        public long Interface1To2Bytes { get; set; }
        public long Interface1To2Packets { get; set; }
        public long Interface1To2Frames { get; set; }

        // Interface2 → Interface1 direction
        public long Interface2To1Bytes { get; set; }
        public long Interface2To1Packets { get; set; }
        public long Interface2To1Frames { get; set; }

        // Errors
        public long Errors { get; set; }

        // Performance metrics
        public TimeSpan Uptime { get; set; }

        // Legacy property names for backward compatibility
        [Obsolete("Use Interface1Name instead")]
        public string XotInterfaceName
        {
            get => Interface1Name;
            set => Interface1Name = value;
        }

        [Obsolete("Use Interface2Name instead")]
        public string SerialInterfaceName
        {
            get => Interface2Name;
            set => Interface2Name = value;
        }

        [Obsolete("Use Interface1To2Bytes instead")]
        public long XotToSerialBytes
        {
            get => Interface1To2Bytes;
            set => Interface1To2Bytes = value;
        }

        [Obsolete("Use Interface2To1Bytes instead")]
        public long SerialToXotBytes
        {
            get => Interface2To1Bytes;
            set => Interface2To1Bytes = value;
        }

        [Obsolete("Use Interface1To2Packets instead")]
        public long XotToSerialPackets
        {
            get => Interface1To2Packets;
            set => Interface1To2Packets = value;
        }

        [Obsolete("Use Interface2To1Packets instead")]
        public long SerialToXotPackets
        {
            get => Interface2To1Packets;
            set => Interface2To1Packets = value;
        }

        [Obsolete("Use Interface1To2Frames instead")]
        public long XotToSerialFrames
        {
            get => Interface1To2Frames;
            set => Interface1To2Frames = value;
        }

        [Obsolete("Use Interface2To1Frames instead")]
        public long SerialToXotFrames
        {
            get => Interface2To1Frames;
            set => Interface2To1Frames = value;
        }

        /// <summary>
        /// Calculate total throughput in bytes per second
        /// </summary>
        public double BytesPerSecond
        {
            get
            {
                if (Uptime.TotalSeconds == 0) return 0;
                return (Interface1To2Bytes + Interface2To1Bytes) / Uptime.TotalSeconds;
            }
        }

        /// <summary>
        /// Calculate total packets per second
        /// </summary>
        public double PacketsPerSecond
        {
            get
            {
                if (Uptime.TotalSeconds == 0) return 0;
                return (Interface1To2Packets + Interface2To1Packets) / Uptime.TotalSeconds;
            }
        }

        /// <summary>
        /// Get formatted throughput string
        /// </summary>
        public string GetThroughputString()
        {
            double bps = BytesPerSecond;
            if (bps >= 1048576) // >= 1 MB/s
                return $"{bps / 1048576:F2} MB/s";
            else if (bps >= 1024) // >= 1 KB/s
                return $"{bps / 1024:F2} KB/s";
            else
                return $"{bps:F2} B/s";
        }
    }
}
