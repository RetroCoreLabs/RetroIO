using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25;
using RetroIO.Protocols.X25.Common;
using RetroIO.Protocols.X25.Diagnostics;
using RetroIO.Protocols.X25.Layers;
using RetroIO.Protocols.X25.Runtime.Layers;
using RetroIO.Link;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Protocols;
using RetroIO.Protocols.X25.Runtime.Transport;
using RetroIO.Protocols.X25.Transport;
using RetroIO.Protocols.X25.Services;
using RetroIO.Protocols.X25.Sintran.Services;

namespace RetroIO.Protocols.X25.Runtime
{

    /// <summary>
    /// Central runtime for X.25 protocol stack with unified timer management, routing, and history
    /// </summary>
    public partial class ProtocolRuntime : IDisposable, IProtocolRuntime, ILapbTimerManager, ILapbFramePreHandler
    {
        // Configuration
        private readonly RuntimeConfig _config;
        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        // Layer instances (public concrete classes). Stack A (LinkLayer/HdlcLayer/LapbLayer/XOTLayer) has
        // been retired to _retired/; the multi-link Stack B (LinkManager) is the sole link path now.
        public Layers.PhysicalLayer PhysicalLayer { get; private set; }  // Actual physical layer for serial/XOT
        /// <summary>
        /// The retired process-wide engine. Removed 2026-09-06: every connection - each XOT
        /// connection and each LinkManager link - owns an X25Layer of its own in an X25Endpoint,
        /// and the packet history took over its other job of being the bus the views listen on.
        /// </summary>

        /// <inheritdoc/>
        public Services.X25PacketHistory PacketHistory { get; } = new Services.X25PacketHistory();
        public X25RoutingLayer RoutingLayer { get; private set; }

        // New multi-link architecture with per-link LAPB instances
        private readonly LinkManager _linkManager;

        // Classifies opaque link payloads as X.25 or SINTRAN above the link layer (created lazily).
        private ProtocolDetector? _linkProtocolDetector;
        private TestLink? _defaultTestLink;

        /// <summary>
        /// Access to the multi-link manager (new architecture)
        /// </summary>
        public LinkManager Links => _linkManager;

        /// <summary>
        /// Default test link for frame injection (new architecture).
        /// Use this instead of LAPBLayer for frame injection.
        /// </summary>
        public TestLink DefaultTestLink
        {
            get
            {
                if (_defaultTestLink == null)
                {
                    // Create TestLink with same LAPB config as LAPBLayer
                    // isPrimary=false means DCE mode (respond to SABM with UA)
                    // isPrimary=true means DTE mode (send SABM, expect UA)
                    bool isPrimary = !_config.Lapb.IsDCE; // Invert: IsDCE=true means we respond (isPrimary=false)

                    _defaultTestLink = _linkManager.CreateTestLink(
                        "_default_test",
                        _config.Lapb.WindowSize,
                        (byte)_config.Lapb.MaxRetries,
                        (int)_config.Lapb.T1Timeout.TotalMilliseconds,
                        (int)_config.Lapb.T2Timeout.TotalMilliseconds,
                        (int)_config.Lapb.T3Timeout.TotalMilliseconds,
                        isPrimary);
                    _defaultTestLink.Start();

                    // Subscribe to events from the default test link
                    _defaultTestLink.FrameReceived += OnDefaultTestLinkFrameReceived;
                    _defaultTestLink.FrameTransmitted += OnDefaultTestLinkFrameTransmitted;
                    _defaultTestLink.PayloadReceived += OnDefaultTestLinkPayloadReceived;
                    _defaultTestLink.LapbStateChanged += OnDefaultTestLinkLapbStateChanged;
                }
                return _defaultTestLink;
            }
        }

        // Central timer management (delegated to TimerManager)
        private readonly Services.TimerManager _timerManager;

        // X.25 routing system
        private readonly X25Router _x25Router;
        private readonly Dictionary<string, ITransportInterface> _transportInterfaces = new();

        // XOT-Serial bridge for high-performance pass-through mode
        private XotSerialBridge? _bridge;

        // Local TAD protocol handler
        private readonly LocalTadProtocol _localTadProtocol;

        /// <summary>The in-process TAD terminal service local calls are delivered to.</summary>
        private LocalTadProtocol LocalTad => _localTadProtocol;

        // Local XMSG protocol handler
        private readonly LocalXMSGProtocol _localXmsgProtocol;

        // SINTRAN PVC server for SINTRAN-specific protocols
        private readonly SintranPvcServer _sintranPvcServer;

        // Protocol detection service
        private readonly Services.X25ProtocolDetectionService _protocolDetection;
        private readonly Services.ProtocolServiceRegistry _protocolServices;

        // Module routing and binding (using new centralized routing)
        private readonly Dictionary<ushort, IProtocolModule> _lcnBindings = new();
        private readonly Dictionary<string, IProtocolModule> _registeredModules = new();
        private readonly object _routingLock = new();


        // XOT connection tracking: maps local LCN to (connection, remote LCN) for bidirectional data
        // Reverse lookup: remote LCN -> local LCN (avoids linear scan in receive path)
        // LCN to service mapping for XOT calls
        private readonly Dictionary<ushort, Services.IProtocolService> _lcnServiceBindings = new();
        // Per-LCN X.25 flow control state for XOT calls

        // LCN to Link mapping for multi-link TX routing
        private readonly Dictionary<ushort, string> _lcnToLink = new();

        // Pending link context for associating incoming calls with their source link
        // Set before X25Layer.ReceiveX25Packet(), cleared after OnX25IncomingCall() uses it
        private string? _pendingLinkForNextCall;

        // PAD client lifecycle management
        private readonly Dictionary<string, Modules.PadClient> _padClients = new();
        private int _padClientIdCounter = 0;

        // SINTRAN TAD client lifecycle management
        private readonly Dictionary<string, RetroIO.Protocols.X25.Sintran.SintranTadClient> _sintranTadClients = new();
        private int _sintranTadClientIdCounter = 0;

        // History ring buffer
        private readonly Queue<HistoryItem> _historyBuffer = new();
        private readonly object _historyLock = new();
        private int _historyCapacity = 100;

        // Unified packet tracking system - single ID per packet with layer enrichment
        private readonly Dictionary<long, UnifiedHistoryItem> _unifiedPackets = new();
        private readonly object _unifiedPacketsLock = new();

        // Track most recent X.25 items for linking with LAPB encapsulation
        private readonly Dictionary<long, X25HistoryItem> _recentX25Items = new();
        private readonly Dictionary<string, LapbHistoryItem> _recentLapbFrames = new();
        private readonly HashSet<string> _pendingLapbFrameKeys = new(); // Keys for LAPB frames awaiting X.25
        private readonly Dictionary<string, X25HistoryItem> _pendingX25LinksToLapb = new(); // X.25 items waiting for LAPB link
        private readonly object _x25LinkingLock = new();

        // Simple relationship tracking (user requested simple dictionary approach)
        private LapbHistoryItem? _lastLapbIFrame = null;

        // Shared LAPB frame counter
        private long _sharedFrameId = 0;
        private readonly object _frameIdLock = new();

        // Statistics
        private StatisticsSnapshot _currentStats = new();
        private readonly object _statsLock = new();

        // Runtime state
        private bool _disposed = false;
        private readonly object _stateLock = new();
        private DateTime _startTime;

        /// <summary>
        /// Initialize ProtocolRuntime with configuration
        /// </summary>
        public ProtocolRuntime(Microsoft.Extensions.Logging.ILogger logger, RuntimeConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // Initialize timer manager
            _timerManager = new Services.TimerManager(logger);

            // Validate required configuration settings
            if (string.IsNullOrWhiteSpace(config.X25.LocalMachineId))
            {
                throw new ArgumentException("LocalMachineId must be configured in X25 configuration", nameof(config));
            }

            // Initialize X.25 routing system
            _x25Router = new X25Router(logger);

            // Initialize local TAD protocol handler
            _localTadProtocol = new LocalTadProtocol(logger, config.X25.LocalMachineId);

            // Initialize local XMSG protocol handler
            _localXmsgProtocol = new LocalXMSGProtocol(logger, config.X25.LocalMachineId);

            // Initialize SINTRAN PVC server
            _sintranPvcServer = new SintranPvcServer(logger);
            _sintranPvcServer.Initialize();  // Initialize terminal ID pool and handlers

            // Initialize protocol detection service
            _protocolDetection = new Services.X25ProtocolDetectionService(logger);

            // Initialize protocol service registry
            _protocolServices = new Services.ProtocolServiceRegistry(logger);

            // Initialize new multi-link manager
            _linkManager = new LinkManager(logger);

            // Initialize the actual PhysicalLayer (kept for status/serial config surface). Serial RX/TX is
            // handled by Stack B links (LinkManager) created from config; the legacy singleton stack is gone.
            var physicalConfig = CreatePhysicalLayerConfig(config);
            PhysicalLayer = new Layers.PhysicalLayer(logger, physicalConfig);

            RoutingLayer = new X25RoutingLayer(logger, _x25Router, this, _protocolServices);

            // Subscribe to layer events
            SubscribeToLayerEvents();

            // Initialize transport interfaces and routes if routing is enabled
            InitializeRouting();

            // Register protocol services for dynamic routing
            RegisterProtocolServices();

            // Load PVC bindings from configuration
            LoadPvcBindings();

            // Initialize built-in PAD/TAD services
            InitializeBuiltinServices();

            // Update initial statistics after all layers are initialized
            UpdateStatistics();

            // Central timer management is enabled but timers are started on-demand by LAPB layer
            if (_config.Timers?.CentralTimerManagement == true)
            {
                _logger.LogInformation("[Timers] Central timer management enabled - timers will be started on-demand");
            }

            // Start statistics update timer (every 5 seconds)
            StartTimer("STATS_UPDATE", TimeSpan.FromSeconds(5), () =>
            {
                if (!_disposed)
                {
                    UpdateStatistics();
                }
            });

            _startTime = DateTime.UtcNow;
            _logger.LogInformation("[ProtocolRuntime] Protocol runtime initialized and started successfully");

            StateChanged?.Invoke(this, new RuntimeStateChangedEventArgs(
                RuntimeState.Stopped,
                RuntimeState.Running,
                "Runtime started"
            ));
        }

        #region Public API - Runtime Control

        /// <summary>
        /// Runtime configuration
        /// </summary>
        public RuntimeConfig Config => _config;

        /// <summary>
        /// Check if runtime is currently running
        /// </summary>
        public bool IsRunning => !_disposed;

        /// <summary>
        /// Access to the X.25 routing system
        /// </summary>
        public X25Router Router => _x25Router;

        /// <summary>
        /// Get all configured transport interfaces
        /// </summary>
        public IReadOnlyDictionary<string, ITransportInterface> TransportInterfaces =>
            _transportInterfaces.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        /// <summary>
        /// Start all configured transport interfaces
        /// </summary>
        public bool StartTransportInterfaces()
        {
            _logger.LogInformation("Starting all configured transport interfaces");
            bool allStarted = true;

            foreach (var transport in _transportInterfaces.Values)
            {
                try
                {
                    if (transport.Status == TransportStatus.Stopped)
                    {
                        _logger.LogInformation("Starting transport interface: {Name}", transport.Name);
                        var started = transport.Start();
                        if (!started)
                        {
                            _logger.LogError("Failed to start transport interface: {Name}", transport.Name);
                            allStarted = false;
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Transport interface {Name} already started (status: {Status})",
                                       transport.Name, transport.Status);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error starting transport interface: {Name}", transport.Name);
                    allStarted = false;
                }
            }

            _logger.LogInformation("Transport interfaces startup completed - success: {AllStarted}", allStarted);
            return allStarted;
        }

        /// <summary>
        /// Stop all configured transport interfaces
        /// </summary>
        public void StopTransportInterfaces()
        {
            _logger.LogInformation("Stopping all transport interfaces");

            foreach (var transport in _transportInterfaces.Values)
            {
                try
                {
                    if (transport.Status != TransportStatus.Stopped)
                    {
                        _logger.LogInformation("Stopping transport interface: {Name}", transport.Name);
                        transport.Stop();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping transport interface: {Name}", transport.Name);
                }
            }

            _logger.LogInformation("All transport interfaces stopped");
        }

        #endregion

        #region Public API - Link Management (New Multi-Link Architecture)

        /// <summary>
        /// Create a serial link with per-link LAPB state machine
        /// </summary>
        public SerialLink CreateSerialLink(SerialLinkConfig config)
        {
            return _linkManager.CreateSerialLink(config);
        }

        /// <summary>
        /// Create a TCP-LAPB link (HDLC byte-stuffing over TCP) with per-link LAPB state machine
        /// </summary>
        public TcpLapbLink CreateTcpLapbLink(TcpLapbLinkConfig config)
        {
            return _linkManager.CreateTcpLapbLink(config);
        }

        /// <summary>
        /// Create an XOT link (X.25 over TCP, RFC 1613 - no LAPB)
        /// </summary>
        public XotLink CreateXotLink(XotLinkConfig config)
        {
            return _linkManager.CreateXotLink(config);
        }

        /// <summary>
        /// Get a link by name
        /// </summary>
        public ILink? GetLink(string linkName)
        {
            return _linkManager.GetLink(linkName);
        }

        /// <summary>
        /// Get a link by remote machine ID
        /// </summary>
        public ILink? GetLinkByMachineId(string machineId)
        {
            return _linkManager.GetLinkByMachineId(machineId);
        }

        /// <summary>
        /// Get all configured links
        /// </summary>
        public ILink[] GetAllLinks()
        {
            return _linkManager.GetAllLinks();
        }

        /// <summary>
        /// Remove a link by name
        /// </summary>
        public bool RemoveLink(string linkName)
        {
            return _linkManager.RemoveLink(linkName);
        }

        /// <summary>
        /// Start a specific link
        /// </summary>
        public bool StartLink(string linkName)
        {
            return _linkManager.StartLink(linkName);
        }

        /// <summary>
        /// Stop a specific link
        /// </summary>
        public void StopLink(string linkName)
        {
            _linkManager.StopLink(linkName);
        }

        /// <summary>
        /// Start all configured links
        /// </summary>
        public void StartAllLinks()
        {
            _linkManager.StartAll();
        }

        /// <summary>
        /// Stop all links
        /// </summary>
        public void StopAllLinks()
        {
            _linkManager.StopAll();
        }

        /// <summary>
        /// Send an opaque L3 payload to a specific machine via the appropriate link
        /// </summary>
        public bool SendToMachine(string machineId, ReadOnlySpan<byte> payload)
        {
            return _linkManager.SendToMachine(machineId, payload);
        }

        /// <summary>
        /// Send an opaque L3 payload via a specific link
        /// </summary>
        public bool SendViaLink(string linkName, ReadOnlySpan<byte> payload)
        {
            return _linkManager.SendViaLink(linkName, payload);
        }

        /// <summary>
        /// Get link manager statistics
        /// </summary>
        public LinkManagerStatistics GetLinkManagerStatistics()
        {
            return _linkManager.GetStatistics();
        }

        /// <summary>
        /// Initialize links from RuntimeConfig.
        /// Creates SerialLink, TcpLapbLink, and XotLink instances from configuration.
        /// </summary>
        public void InitializeLinksFromConfig()
        {
            var routing = _config.Routing;
            if (routing == null)
            {
                _logger.LogInformation("LinkManager: No routing configuration - skipping link initialization");
                return;
            }

            // Create Serial links
            if (routing.SerialInterfaces != null)
            {
                for (int i = 0; i < routing.SerialInterfaces.Count; i++)
                {
                    var serialConfig = routing.SerialInterfaces[i];
                    if (!serialConfig.Enabled)
                    {
                        _logger.LogDebug("LinkManager: Skipping disabled serial interface '{Name}'", serialConfig.Name);
                        continue;
                    }

                    try
                    {
                        if (serialConfig.Framing == FramingType.Slip)
                        {
                            // SLIP over serial: codec-based link with LAPB (FCS owned by the bridge).
                            var transportConfig = new SerialLinkConfig
                            {
                                Name = serialConfig.Name,
                                PortName = serialConfig.PortName,
                                BaudRate = serialConfig.BaudRate,
                                DataBits = serialConfig.DataBits,
                                Parity = serialConfig.Parity,
                                StopBits = serialConfig.StopBits
                            };
                            var transport = new SerialLinkTransport(_logger, transportConfig);
                            var codecConfig = new CodecLinkConfig
                            {
                                Name = serialConfig.Name,
                                Framing = FramingType.Slip,
                                RemoteMachineId = serialConfig.RemoteMachineId,
                                WindowSize = serialConfig.LapbWindowSize,
                                MaxRetries = serialConfig.LapbMaxRetries,
                                T1TimeoutMs = serialConfig.LapbT1TimeoutMs,
                                T2TimeoutMs = serialConfig.LapbT2TimeoutMs,
                                T3TimeoutMs = serialConfig.LapbT3TimeoutMs,
                                IsPrimary = serialConfig.IsPrimary,
                                LapbAutoConnect = serialConfig.LapbAutoConnect,
                                ProtocolMode = new ProtocolModeConfig { Mode = serialConfig.ProtocolMode }
                            };
                            _linkManager.CreateCodecLink(codecConfig, transport);
                            _logger.LogInformation("LinkManager: Created SLIP serial link '{Name}' on {Port}",
                                serialConfig.Name, serialConfig.PortName);
                        }
                        else
                        {
                            var linkConfig = new SerialLinkConfig
                            {
                                Name = serialConfig.Name,
                                PortName = serialConfig.PortName,
                                BaudRate = serialConfig.BaudRate,
                                DataBits = serialConfig.DataBits,
                                Parity = serialConfig.Parity,
                                StopBits = serialConfig.StopBits,
                                RemoteMachineId = serialConfig.RemoteMachineId,
                                WindowSize = serialConfig.LapbWindowSize,
                                MaxRetries = serialConfig.LapbMaxRetries,
                                T1TimeoutMs = serialConfig.LapbT1TimeoutMs,
                                T2TimeoutMs = serialConfig.LapbT2TimeoutMs,
                                T3TimeoutMs = serialConfig.LapbT3TimeoutMs,
                                IsPrimary = serialConfig.IsPrimary,
                                ProtocolMode = new ProtocolModeConfig { Mode = serialConfig.ProtocolMode }
                            };

                            var link = _linkManager.CreateSerialLink(linkConfig);
                            _logger.LogInformation("LinkManager: Created serial link '{Name}' on {Port}",
                                serialConfig.Name, serialConfig.PortName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "LinkManager: Failed to create serial link '{Name}'", serialConfig.Name);
                    }
                }
            }

            // Create TCP-LAPB links
            if (routing.TcpLapbInterfaces != null)
            {
                for (int i = 0; i < routing.TcpLapbInterfaces.Count; i++)
                {
                    var tcpConfig = routing.TcpLapbInterfaces[i];
                    if (!tcpConfig.Enabled)
                    {
                        _logger.LogDebug("LinkManager: Skipping disabled TCP-LAPB interface '{Name}'", tcpConfig.Name);
                        continue;
                    }

                    try
                    {
                        if (tcpConfig.Framing == FramingType.Slip)
                        {
                            // SLIP over TCP: codec-based link with LAPB (FCS owned by the bridge).
                            bool isServer = tcpConfig.Mode == TcpMode.Server;
                            string host = isServer ? "0.0.0.0" : tcpConfig.RemoteAddress;
                            var transport = new TcpLinkTransport(_logger, tcpConfig.Name, isServer, host, tcpConfig.Port);
                            var codecConfig = new CodecLinkConfig
                            {
                                Name = tcpConfig.Name,
                                Framing = FramingType.Slip,
                                RemoteMachineId = tcpConfig.RemoteMachineId,
                                WindowSize = tcpConfig.LapbWindowSize,
                                MaxRetries = tcpConfig.LapbMaxRetries,
                                T1TimeoutMs = tcpConfig.LapbT1TimeoutMs,
                                T2TimeoutMs = tcpConfig.LapbT2TimeoutMs,
                                T3TimeoutMs = tcpConfig.LapbT3TimeoutMs,
                                IsPrimary = tcpConfig.IsPrimary,
                                LapbAutoConnect = tcpConfig.LapbAutoConnect,
                                ProtocolMode = new ProtocolModeConfig { Mode = tcpConfig.ProtocolMode }
                            };
                            _linkManager.CreateCodecLink(codecConfig, transport);
                            _logger.LogInformation("LinkManager: Created SLIP TCP link '{Name}' ({Mode} {Address}:{Port})",
                                tcpConfig.Name, tcpConfig.Mode, tcpConfig.RemoteAddress, tcpConfig.Port);
                        }
                        else
                        {
                            var linkConfig = new TcpLapbLinkConfig
                            {
                                Name = tcpConfig.Name,
                                Mode = tcpConfig.Mode,
                                RemoteAddress = tcpConfig.RemoteAddress,
                                Port = tcpConfig.Port,
                                RemoteMachineId = tcpConfig.RemoteMachineId,
                                ConnectTimeoutMs = tcpConfig.ConnectTimeoutMs,
                                ReconnectDelayMs = tcpConfig.ReconnectDelayMs,
                                WindowSize = tcpConfig.LapbWindowSize,
                                MaxRetries = tcpConfig.LapbMaxRetries,
                                T1TimeoutMs = tcpConfig.LapbT1TimeoutMs,
                                T2TimeoutMs = tcpConfig.LapbT2TimeoutMs,
                                T3TimeoutMs = tcpConfig.LapbT3TimeoutMs,
                                IsPrimary = tcpConfig.IsPrimary,
                                ProtocolMode = new ProtocolModeConfig { Mode = tcpConfig.ProtocolMode }
                            };

                            var link = _linkManager.CreateTcpLapbLink(linkConfig);
                            _logger.LogInformation("LinkManager: Created TCP-LAPB link '{Name}' ({Mode} {Address}:{Port})",
                                tcpConfig.Name, tcpConfig.Mode, tcpConfig.RemoteAddress, tcpConfig.Port);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "LinkManager: Failed to create TCP-LAPB link '{Name}'", tcpConfig.Name);
                    }
                }
            }

            // Create XOT links
            if (routing.XotInterfaces != null)
            {
                for (int i = 0; i < routing.XotInterfaces.Count; i++)
                {
                    var xotConfig = routing.XotInterfaces[i];
                    if (!xotConfig.Enabled)
                    {
                        _logger.LogDebug("LinkManager: Skipping disabled XOT interface '{Name}'", xotConfig.Name);
                        continue;
                    }

                    try
                    {
                        var linkConfig = new XotLinkConfig
                        {
                            Name = xotConfig.Name,
                            Mode = xotConfig.Mode == XotMode.Server ? TcpMode.Server : TcpMode.Client,
                            RemoteAddress = xotConfig.RemoteIPAddress ?? "127.0.0.1",
                            Port = xotConfig.Mode == XotMode.Server ? xotConfig.LocalPort : xotConfig.RemotePort,
                            RemoteMachineId = xotConfig.RemoteMachineId,
                            ConnectTimeoutMs = xotConfig.ConnectTimeoutMs,
                            ReconnectDelayMs = xotConfig.ReconnectDelayMs,
                            KeepAliveIntervalSeconds = xotConfig.KeepAliveIntervalSeconds,
                            ProtocolMode = new ProtocolModeConfig { Mode = xotConfig.ProtocolMode }
                        };

                        var link = _linkManager.CreateXotLink(linkConfig);
                        _logger.LogInformation("LinkManager: Created XOT link '{Name}' ({Mode})",
                            xotConfig.Name, xotConfig.Mode);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "LinkManager: Failed to create XOT link '{Name}'", xotConfig.Name);
                    }
                }
            }

            _logger.LogInformation("LinkManager: Initialized {Count} links from configuration",
                _linkManager.LinkCount);
        }

        #endregion

        #region Public API - Module Registration

        /// <summary>
        /// Register a protocol module
        /// </summary>
        public void RegisterModule(IProtocolModule module)
        {
            if (module == null)
                throw new ArgumentNullException(nameof(module));

            lock (_routingLock)
            {
                // Check if module is already registered
                if (_registeredModules.ContainsKey(module.Name))
                {
                    _logger.LogWarning($"[Routing] Module {module.Name} is already registered");
                    return;
                }

                _registeredModules[module.Name] = module;
                _logger.LogInformation($"[Routing] Registered module: {module.Name}");

                // Update statistics to reflect new module count
                UpdateStatistics();
            }
        }

        /// <summary>
        /// Unregister a protocol module
        /// </summary>
        public void UnregisterModule(IProtocolModule module)
        {
            if (module == null)
                throw new ArgumentNullException(nameof(module));

            lock (_routingLock)
            {

                // Unbind all LCNs bound to this module
                var lcnsToUnbind = new List<ushort>();
                foreach (var kvp in _lcnBindings)
                {
                    if (kvp.Value == module)
                        lcnsToUnbind.Add(kvp.Key);
                }
                foreach (var lcn in lcnsToUnbind)
                {
                    UnbindLcnFromModule(lcn);
                }

                // Remove from registered modules
                _registeredModules.Remove(module.Name);

                _logger.LogInformation($"[Routing] Unregistered module: {module.Name}");
            }
        }

        /// <summary>
        /// Get all registered modules (for routing layer access)
        /// </summary>
        public Dictionary<string, IProtocolModule> GetRegisteredModules()
        {
            return new Dictionary<string, IProtocolModule>(_registeredModules);
        }


        #endregion

        #region Public API - History and Statistics

        /// <summary>
        /// Get current history capacity
        /// </summary>
        public int HistoryCapacity => _historyCapacity;

        /// <summary>
        /// Set history capacity
        /// </summary>
        public void SetHistoryCapacity(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentException("History capacity must be positive", nameof(capacity));

            lock (_historyLock)
            {
                _historyCapacity = capacity;

                // Trim history if it exceeds new capacity
                while (_historyBuffer.Count > _historyCapacity)
                {
                    _historyBuffer.Dequeue();
                }

                _logger.LogInformation($"[History] Set history capacity to {capacity}");
            }
        }

        /// <summary>
        /// Get current history items
        /// </summary>
        public IReadOnlyList<HistoryItem> GetHistory()
        {
            lock (_historyLock)
            {
                var historyList = new List<HistoryItem>(_historyBuffer);
                return historyList.AsReadOnly();
            }
        }

        /// <summary>
        /// Export history to JSON file
        /// </summary>
        public void ExportHistoryAsJson(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            try
            {
                var history = GetHistory();
                var json = System.Text.Json.JsonSerializer.Serialize(history, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                System.IO.File.WriteAllText(path, json);
                _logger.LogInformation($"[History] Exported {history.Count} history items to {path}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[History] Failed to export history to {path}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Get current statistics snapshot
        /// </summary>
        public StatisticsSnapshot GetStatistics()
        {
            lock (_statsLock)
            {
                return _currentStats.Clone();
            }
        }

        /// <summary>
        /// Get shared LAPB frame ID
        /// </summary>
        public long GetSharedFrameId()
        {
            lock (_frameIdLock)
            {
                return _sharedFrameId;
            }
        }

        #endregion

        #region Public API - Timer Management (delegates to TimerManager)

        /// <summary>
        /// Start a timer with callback. Delegates to TimerManager.
        /// </summary>
        public void StartTimer(string timerId, TimeSpan duration, Action callback)
            => _timerManager.StartTimer(timerId, duration, callback);

        /// <summary>
        /// Stop a timer. Delegates to TimerManager.
        /// </summary>
        public void StopTimer(string timerId)
            => _timerManager.StopTimer(timerId);

        /// <summary>
        /// Check if timer is running. Delegates to TimerManager.
        /// </summary>
        public bool IsTimerRunning(string timerId)
            => _timerManager.IsTimerRunning(timerId);

        #endregion

        #region Public API - Routing

        /// <summary>
        /// Get module bound to LCN
        /// </summary>
        public IProtocolModule? GetBoundModule(ushort lcn)
        {
            lock (_routingLock)
            {
                return _lcnBindings.TryGetValue(lcn, out var module) ? module : null;
            }
        }

        /// <summary>
        /// Get all current LCN bindings
        /// </summary>
        public IReadOnlyDictionary<ushort, IProtocolModule> GetLcnBindings()
        {
            lock (_routingLock)
            {
                return new Dictionary<ushort, IProtocolModule>(_lcnBindings);
            }
        }

        #endregion

        #region Public API - Data Processing

        /// <summary>
        /// Process incoming physical layer data (from serial port, file, or simulation)
        /// Routes to LinkLayer for byte-stuffing, frame detection, and HDLC processing
        /// </summary>
        public void ProcessIncomingData(byte[] data, int length)
        {
            if (_disposed)
            {
                _logger.LogWarning("[ProtocolRuntime] Cannot process data: runtime not running");
                return;
            }

            try
            {
                // Legacy Stack A byte path retired; configure a link (LinkManager) to receive bytes.
                _logger.LogWarning("[ProtocolRuntime] ProcessIncomingData: legacy byte path retired; use a configured link ({Length} bytes ignored)", length);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ProtocolRuntime] Error processing incoming data: {ex.Message}");
            }
        }

        /// <summary>
        /// Simulate received LAPB frame and create unified packets
        /// This method is specifically for simulation purposes and ensures unified packet creation
        /// </summary>
        public void SimulateLapbFrame(byte[] frameData, int length)
        {
            if (_disposed)
            {
                _logger.LogWarning("[ProtocolRuntime] Cannot simulate frame: runtime not running");
                return;
            }

            try
            {
                // Create unified packet at entry point for simulation
                var globalId = CreateOrUpdateUnifiedPacket(0, Direction.RX, LayerType.HDLC);

                // Enrich with HDLC layer info (simulated)
                var hdlcLayerInfo = new HdlcLayerInfo
                {
                    FrameId = globalId,
                    FcsValid = true,
                    FrameData = frameData,
                    Length = length
                };
                EnrichUnifiedPacket(globalId, LayerType.HDLC, hdlcLayerInfo);

                // Directly enrich with LAPB layer info without firing events (to avoid duplicate packets)
                if (frameData != null && length >= 2)
                {
                    var lapbLayerInfo = new LapbLayerInfo
                    {
                        FrameType = LapbFrameUtils.DetectFrameType(frameData[1]),
                        Address = frameData[0],
                        Control = frameData[1],
                        PollFinal = (frameData[1] & 0x10) != 0,
                        Information = length > 2 ? frameData[2..length] : new byte[0],
                        InformationLength = length > 2 ? length - 2 : 0
                    };

                    // Parse sequence numbers for I-frames
                    if ((frameData[1] & 0x01) == 0) // I-frame
                    {
                        lapbLayerInfo.NS = (byte)((frameData[1] >> 1) & 0x07);
                        lapbLayerInfo.NR = (byte)((frameData[1] >> 5) & 0x07);
                    }
                    else if ((frameData[1] & 0x03) == 0x01) // S-frame
                    {
                        lapbLayerInfo.NR = (byte)((frameData[1] >> 5) & 0x07);
                    }

                    EnrichUnifiedPacket(globalId, LayerType.LAPB, lapbLayerInfo);
                }

                // Get the created unified packet and add it to history to fire events
                lock (_unifiedPacketsLock)
                {
                    if (_unifiedPackets.TryGetValue(globalId, out var unifiedItem))
                    {
                        AddToHistory(unifiedItem);
                    }
                }

                // Note: Don't call LAPBLayer.ReceiveHdlcFrame() to avoid creating duplicate regular HistoryItem objects
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ProtocolRuntime] Error simulating LAPB frame: {ex.Message}");
            }
        }

        /// <summary>
        /// Send data through the protocol stack
        /// </summary>
        public void SendData(byte[] data, int length)
        {
            if (_disposed)
            {
                _logger.LogWarning("[ProtocolRuntime] Cannot send data: runtime not running");
                return;
            }

            try
            {
                // Legacy Stack A transmit path retired; send via a configured link (LinkManager) instead.
                _logger.LogWarning("[ProtocolRuntime] SendData: legacy transmit path retired; use a configured link ({Length} bytes ignored)", length);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ProtocolRuntime] Error sending data: {ex.Message}");
            }
        }

        /// <summary>
        /// Inject LAPB frame via the default test link (new architecture).
        /// </summary>
        /// <param name="frameData">LAPB frame data (Address + Control + Information)</param>
        /// <param name="length">Frame length</param>
        public void InjectLapbFrame(byte[] frameData, int length)
        {
            if (_disposed)
            {
                _logger.LogWarning("[ProtocolRuntime] Cannot inject LAPB frame: runtime not running");
                return;
            }

            try
            {
                // Route through the default test link
                DefaultTestLink.InjectFrame(frameData, length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ProtocolRuntime] Error injecting LAPB frame");
            }
        }

        /// <summary>
        /// Update XOT TCP connection status
        /// </summary>
        public void UpdateXOTTcpConnectionStatus(bool connected)
        {
            if (_disposed)
            {
                _logger.LogWarning("[ProtocolRuntime] Cannot update XOT TCP status: runtime not running");
                return;
            }

            try
            {
                lock (_statsLock)
                {
                    _currentStats.XOT.TcpConnected = connected;
                    StatisticsUpdated?.Invoke(this, _currentStats.Clone());
                }

                _logger.LogInformation($"[XOT] TCP connection status changed to: {(connected ? "Connected" : "Disconnected")}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ProtocolRuntime] Error updating XOT TCP connection status: {ex.Message}");
            }
        }

        /// <summary>
        /// Process XOT data from TCP transport (called by XOT transport components)
        /// </summary>
        public void ProcessXOTData(byte[] data, int length)
        {
            if (_disposed)
            {
                _logger.LogWarning("[ProtocolRuntime] Cannot process XOT data: runtime not running");
                return;
            }

            try
            {
                // Legacy Stack A XOT byte path retired; XOT is handled by XOT links (LinkManager).
                _logger.LogWarning("[ProtocolRuntime] Legacy XOT byte path retired; use an XOT link ({Length} bytes ignored)", length);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ProtocolRuntime] Error processing XOT data: {ex.Message}");
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Fired when runtime state changes
        /// </summary>
        public event EventHandler<RuntimeStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// Fired when a history item is added
        /// </summary>
        public event EventHandler<HistoryItem>? HistoryItemAdded;

        /// <summary>
        /// Fired when statistics are updated
        /// </summary>
        public event EventHandler<StatisticsSnapshot>? StatisticsUpdated;

        /// <summary>
        /// Fired when a module is bound to an LCN
        /// </summary>
        public event EventHandler<(ushort Lcn, IProtocolModule Module)>? ModuleBound;

        /// <summary>
        /// Fired when a module is unbound from an LCN
        /// </summary>
        public event EventHandler<(ushort Lcn, IProtocolModule Module)>? ModuleUnbound;

        #endregion

        #region Private Methods

        /// <summary>
        /// Initialize X.25 routing system with transport interfaces and routes
        /// </summary>
        private void InitializeRouting()
        {
            if (_config.Routing?.Enabled != true)
            {
                _logger.LogDebug("X.25 routing is disabled in configuration");
                return;
            }

            _logger.LogInformation("Initializing X.25 routing system");

            try
            {
                // Initialize transport interfaces
                InitializeTransportInterfaces();

                // Load routing configuration
                LoadRoutingConfiguration();

                // Start all transport interfaces
                StartTransportInterfaces();

                // Initialize bridge if configured
                InitializeBridge();

                _logger.LogInformation("X.25 routing system initialized with {RouteCount} routes and {InterfaceCount} transport interfaces",
                                     _x25Router.GetRoutes().Count, _transportInterfaces.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize X.25 routing system");
                throw;
            }
        }

        /// <summary>
        /// Initialize transport interfaces from configuration
        /// </summary>
        private void InitializeTransportInterfaces()
        {
            _logger.LogInformation("Initializing transport interfaces from centralized routing configuration");

            try
            {
                // Initialize Serial interfaces
                InitializeSerialInterfaces();

                // Initialize XOT interfaces
                InitializeXotInterfaces();

                _logger.LogInformation("Transport interface initialization completed - {Count} interfaces configured",
                                     _transportInterfaces.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize transport interfaces");
                throw;
            }
        }

        /// <summary>
        /// Initialize serial transport interfaces from configuration
        /// </summary>
        private void InitializeSerialInterfaces()
        {
            if (_config.Routing?.SerialInterfaces == null) return;

            foreach (var serialConfig in _config.Routing.SerialInterfaces)
            {
                if (!serialConfig.Enabled) continue;
                try
                {
                    _logger.LogInformation("Creating serial transport interface: {Name} on port {Port}",
                                         serialConfig.Name, serialConfig.PortName);

                    var serialTransport = CreateSerialTransport(serialConfig);
                    _transportInterfaces[serialConfig.Name] = serialTransport;
                    _x25Router.RegisterInterface(serialTransport);

                    // Subscribe to transport events
                    serialTransport.IncomingCall += OnTransportIncomingCall;
                    serialTransport.StatusChanged += OnTransportStatusChanged;

                    _logger.LogInformation("Serial transport interface created: {Name}", serialConfig.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create serial transport interface: {Name}", serialConfig.Name);
                }
            }
        }

        /// <summary>
        /// Initialize XOT transport interfaces from configuration
        /// </summary>
        private void InitializeXotInterfaces()
        {
            if (_config.Routing?.XotInterfaces == null) return;

            foreach (var xotConfig in _config.Routing.XotInterfaces)
            {
                if (!xotConfig.Enabled) continue;
                try
                {
                    _logger.LogInformation("Creating XOT transport interface: {Name} ({Mode})",
                                         xotConfig.Name, xotConfig.Mode);

                    ITransportInterface xotTransport = xotConfig.Mode switch
                    {
                        XotMode.Client => CreateXotClientTransport(xotConfig),
                        XotMode.Server => CreateXotServerTransport(xotConfig),
                        _ => throw new ArgumentException($"Unknown XOT mode: {xotConfig.Mode}")
                    };

                    _transportInterfaces[xotConfig.Name] = xotTransport;
                    _x25Router.RegisterInterface(xotTransport);

                    // Subscribe to transport events
                    xotTransport.IncomingCall += OnTransportIncomingCall;
                    xotTransport.StatusChanged += OnTransportStatusChanged;
                    xotTransport.DataReceived += OnXotDataReceived;

                    _logger.LogInformation("XOT transport interface created: {Name}", xotConfig.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create XOT transport interface: {Name}", xotConfig.Name);
                }
            }
        }

        /// <summary>
        /// Create XOT client transport from configuration
        /// </summary>
        private ITransportInterface CreateXotClientTransport(XotInterfaceConfig xotConfig)
        {
            var clientConfig = new XotClientConfig
            {
                Name = xotConfig.Name,
                SourceAddress = "0.0.0.0",
                ConnectTimeout = xotConfig.ConnectTimeoutMs > 0 ? xotConfig.ConnectTimeoutMs : 30000,
                KeepAliveInterval = xotConfig.KeepAliveIntervalSeconds > 0 ? xotConfig.KeepAliveIntervalSeconds : 60
            };

            var transport = new XotClientTransport(_logger, clientConfig);
            transport.SetInterfaceConfig(xotConfig);
            transport.PacketLayerPlaceCall = PlaceCallViaEndpoint;
            return transport;
        }

        /// <summary>
        /// Create XOT server transport from configuration
        /// </summary>
        private ITransportInterface CreateXotServerTransport(XotInterfaceConfig xotConfig)
        {
            var serverConfig = new XotServerConfig
            {
                Name = xotConfig.Name,
                ListenAddress = "0.0.0.0", // Listen on all interfaces
                ListenPort = xotConfig.LocalPort,
                MaxConnections = 100 // Default value since MaxConnections doesn't exist in XotInterfaceConfig
            };

            return new XotServerTransport(_logger, serverConfig);
        }

        /// <summary>
        /// Handle incoming call from transport interface
        /// </summary>
        private void OnTransportIncomingCall(object? sender, X25CallRequest callRequest)
        {
            _logger.LogInformation("Incoming X.25 call from transport {Transport}: {CalledAddress} <- {CallingAddress}",
                                 sender?.GetType().Name, callRequest.CalledAddress, callRequest.CallingAddress);

            try
            {
                // A transport that owns an X25Endpoint never gets here: its call is decoded by its
                // own engine and routed by OnEndpointIncomingCall, which answers through the engine.
                // What is left is a transport with no engine, and there is no way to answer a call
                // on one - the routing layer used to build a Call Accepted by hand and write it to
                // the connection, which is the second packet layer this migration is removing.
                // The serial transport is the only such transport, and nothing raises its event
                // today; it gets an endpoint in Phase E.
                ITransportInterface? sourceTransport = sender as ITransportInterface;
                _logger.LogWarning(
                    "Incoming call to {CalledAddress} on transport {Transport} cannot be answered: that transport has no X.25 engine yet",
                    callRequest.CalledAddress, sourceTransport?.Name ?? sender?.GetType().Name ?? "unknown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling incoming call from transport");
            }
        }


        /// <summary>
        /// Handle transport status changes
        /// </summary>
        private void OnTransportStatusChanged(object? sender, TransportStatusEventArgs e)
        {
            if (sender is ITransportInterface transport)
            {
                _logger.LogInformation("Transport {Name} status changed: {OldStatus} -> {NewStatus} ({Reason})",
                                     transport.Name, e.OldStatus, e.NewStatus, e.Reason ?? "Unknown");

                // Update statistics and fire events as needed
                UpdateStatistics();
            }
        }

        /// <summary>
        /// Load routing configuration and add routes to X25Router
        /// </summary>
        private void LoadRoutingConfiguration()
        {
            _logger.LogInformation("Loading new centralized X.25 routing system");

            if (_config.Routing?.RoutingTable == null)
            {
                _logger.LogWarning("No routing table configured");
                return;
            }

            // Process routing table entries
            foreach (var routeEntry in _config.Routing.RoutingTable)
            {
                if (!routeEntry.Enabled) continue;
                try
                {
                    ProcessRouteEntry(routeEntry);
                    _logger.LogDebug("Processed X.25 route: {RouteName} ({Pattern} -> {Destination})",
                                   routeEntry.Name, routeEntry.X121AddressPattern, routeEntry.Destination);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process route entry: {RouteName}", routeEntry.Name);
                }
            }

            // Log default gateway configuration
            if (!string.IsNullOrEmpty(_config.Routing.DefaultGateway))
            {
                _logger.LogInformation("Default gateway configured: {Gateway}", _config.Routing.DefaultGateway);
            }

            // Count enabled routes manually
            int enabledRouteCount = 0;
            foreach (var route in _config.Routing.RoutingTable)
            {
                if (route.Enabled)
                    enabledRouteCount++;
            }
            _logger.LogInformation("New centralized routing system loaded with {RouteCount} enabled routes",
                                 enabledRouteCount);

            /*
            // OLD CODE - will be replaced with new routing system
            if (_config.Routing?.Routes == null) return;

            foreach (var routeConfig in _config.Routing.Routes)
            {
                if (!routeConfig.Enabled) continue;
            {
                try
                {
                    var route = new X25Route
                    {
                        Name = routeConfig.Name,
                        Description = routeConfig.Description ?? "",
                        X121AddressPattern = routeConfig.X121AddressPattern,
                        Type = Enum.Parse<RouteType>(routeConfig.Type, true),
                        Destination = routeConfig.Destination,
                        Priority = routeConfig.Priority,
                        Enabled = routeConfig.Enabled,
                        Direction = Enum.Parse<RouteDirection>(routeConfig.Direction ?? "Bidirectional", true),
                        BackupDestinations = routeConfig.BackupDestinations != null ?
                            new List<string>(routeConfig.BackupDestinations) : new List<string>(),
                        CallUserDataPattern = routeConfig.CallUserDataPattern
                    };

                    _x25Router.AddRoute(route);
                    _logger.LogDebug("Added X.25 route: {RouteName} ({Pattern} -> {Destination})",
                                   route.Name, route.X121AddressPattern, route.Destination);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to add route: {RouteName}", routeConfig.Name);
                }
            }
            */
        }

        /// <summary>
        /// Initialize transport bridge if configured
        /// Supports XOT-to-Serial, XOT-to-XOT, and Serial-to-Serial bridging
        /// </summary>
        private void InitializeBridge()
        {
            if (!_config.Bridge.Enabled)
            {
                _logger.LogDebug("Transport bridge not enabled");
                return;
            }

            _logger.LogInformation("Initializing transport bridge: {Interface1} <-> {Interface2}",
                                 _config.Bridge.Interface1Name, _config.Bridge.Interface2Name);

            try
            {
                // Find specified transports
                if (!_transportInterfaces.TryGetValue(_config.Bridge.Interface1Name, out var interface1))
                {
                    _logger.LogError("Cannot initialize bridge - Interface1 not found: {Name}",
                        _config.Bridge.Interface1Name);
                    return;
                }

                if (!_transportInterfaces.TryGetValue(_config.Bridge.Interface2Name, out var interface2))
                {
                    _logger.LogError("Cannot initialize bridge - Interface2 not found: {Name}",
                        _config.Bridge.Interface2Name);
                    return;
                }

                // Create and start bridge (bridge automatically detects mode from transport types)
                _bridge = new XotSerialBridge(_logger, interface1, interface2, this, _config.Bridge);
                _bridge.Start();

                _logger.LogInformation("Transport bridge initialized successfully: {Interface1} <-> {Interface2}",
                                     _config.Bridge.Interface1Name, _config.Bridge.Interface2Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize XOT-Serial bridge");
            }
        }

        /// <summary>
        /// Process a routing table entry and set up the route
        /// </summary>
        private void ProcessRouteEntry(X25RouteEntry routeEntry)
        {
            // Map from RuntimeConfig.RouteType to Routing.RouteType
            RetroIO.Protocols.X25.Transport.RouteType mappedType = routeEntry.Type switch
            {
                RouteType.Interface => RetroIO.Protocols.X25.Transport.RouteType.Serial, // Default interface type
                RouteType.Gateway => RetroIO.Protocols.X25.Transport.RouteType.Gateway,
                RouteType.Local => RetroIO.Protocols.X25.Transport.RouteType.Local,
                _ => RetroIO.Protocols.X25.Transport.RouteType.Serial
            };

            // For interface routes, check destination to determine specific interface type
            if (routeEntry.Type == RouteType.Interface && routeEntry.Destination.StartsWith("XOT"))
            {
                mappedType = RetroIO.Protocols.X25.Transport.RouteType.XOT_Client;
            }

            // Create X25Route from RouteEntry configuration
            var route = new X25Route
            {
                Name = routeEntry.Name,
                Description = routeEntry.Description ?? "",
                X121AddressPattern = routeEntry.X121AddressPattern,
                Type = mappedType,
                Destination = routeEntry.Destination,
                Priority = routeEntry.Priority,
                Enabled = routeEntry.Enabled,
                Direction = RouteDirection.Bidirectional // Default to bidirectional
            };

            // Add route to router
            _x25Router.AddRoute(route);

            switch (routeEntry.Type)
            {
                case RouteType.Local:
                    _logger.LogDebug("Local route configured: {Pattern} -> {Destination}",
                                   routeEntry.X121AddressPattern, routeEntry.Destination);
                    break;

                case RouteType.Interface:
                    _logger.LogDebug("Interface route configured: {Pattern} -> {Interface}",
                                   routeEntry.X121AddressPattern, routeEntry.Destination);
                    break;

                case RouteType.Gateway:
                    _logger.LogDebug("Gateway route configured: {Pattern} -> Gateway {Gateway}",
                                   routeEntry.X121AddressPattern, routeEntry.Destination);
                    break;

                default:
                    _logger.LogWarning("Unknown route type: {Type} for route {Name}",
                                     routeEntry.Type, routeEntry.Name);
                    break;
            }
        }

        /// <summary>
        /// Find route for X.121 address using the new centralized routing table
        /// </summary>
        public X25RouteEntry? FindRoute(string x121Address)
        {
            if (_config.Routing?.RoutingTable == null || string.IsNullOrEmpty(x121Address))
                return null;

            // Find matching routes ordered by priority (highest first)
            var matchingRoutes = new List<X25RouteEntry>();
            foreach (var route in _config.Routing.RoutingTable)
            {
                if (route.Enabled && MatchesAddressPattern(x121Address, route.X121AddressPattern))
                {
                    matchingRoutes.Add(route);
                }
            }

            // Sort by priority (highest first)
            X25RouteEntry? bestRoute = null;
            int highestPriority = int.MinValue;
            foreach (var route in matchingRoutes)
            {
                if (route.Priority > highestPriority)
                {
                    highestPriority = route.Priority;
                    bestRoute = route;
                }
            }

            return bestRoute;
        }

        /// <summary>
        /// Check if an address matches a routing pattern (supports regex patterns)
        /// </summary>
        private bool MatchesAddressPattern(string address, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            try
            {
                // Support exact match first
                if (address == pattern) return true;

                // Support wildcard patterns like "100.*"
                if (pattern.EndsWith(".*"))
                {
                    string prefix = pattern.Substring(0, pattern.Length - 2);
                    return address.StartsWith(prefix);
                }

                // Support regex patterns
                var regex = new System.Text.RegularExpressions.Regex(pattern);
                return regex.IsMatch(address);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid address pattern: {Pattern}", pattern);
                return false;
            }
        }

        /// <summary>
        /// Create serial transport from configuration
        /// </summary>
        private ITransportInterface CreateSerialTransport(SerialInterfaceConfig serialConfig)
        {
            var transportConfig = new SerialTransportConfig
            {
                Name = serialConfig.Name,
                PortName = serialConfig.PortName,
                BaudRate = serialConfig.BaudRate,
                LocalX121Address = serialConfig.RemoteMachineId,
                Description = serialConfig.Description
            };

            return new SerialTransport(_logger, this, transportConfig);
        }

        /// <summary>
        /// Subscribe to layer events
        /// </summary>
        private void SubscribeToLayerEvents()
        {
            // Stack A (Physical/Link/HDLC/LAPB singleton) event wiring has been retired. Stack B links
            // (LinkManager) deliver payloads to the X.25 layer via OnLinkPayloadReceived.

            // The packet history is the bus every view and service listens on. It used to be the
            // retired process-wide engine's second job; the engines now report through it directly.
            PacketHistory.PacketReceived += OnX25PacketReceived;
            PacketHistory.PacketTransmitted += OnX25PacketTransmitted;

            // Subscribe to new LinkManager events (multi-link architecture)
            _linkManager.PayloadReceived += OnLinkPayloadReceived;
            _linkManager.LinkStatusChanged += OnLinkStatusChanged;
            _linkManager.LapbStateChanged += OnLinkLapbStateChanged;
        }

        #region Layer Event Handlers

        private void OnPhysicalBytesToSend(object? sender, PhysicalLayerEventArgs e)
        {
            try
            {
                _logger.LogDebug("Physical: Bytes to send, {Length} bytes", e.Length);

                // Route the data to the appropriate serial interface
                // For serial routing, we need to determine which interface to use
                // For now, use the first configured serial interface as default
                SerialInterfaceConfig? firstSerialInterface = null;
                if (_config.Routing.SerialInterfaces != null)
                {
                    foreach (var s in _config.Routing.SerialInterfaces)
                    {
                        if (s.Enabled)
                        {
                            firstSerialInterface = s;
                            break;
                        }
                    }
                }
                if (firstSerialInterface != null)
                {
                    _logger.LogDebug("Routing {Length} bytes to serial interface {InterfaceName}",
                                   e.Length, firstSerialInterface.Name);

                    // Send data to the serial interface via PhysicalLayer
                    PhysicalLayer.SendToSerialInterface(firstSerialInterface.Name, e.Data);
                }
                else
                {
                    _logger.LogWarning("No enabled serial interface found for {Length} bytes", e.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Physical: Error handling bytes to send");
            }
        }

        private void OnHdlcFrameReceived(object? sender, HdlcFrameEventArgs e)
        {
            // Set frame ID and add to history
            e.FrameId = GetNextFrameId();

            var direction = e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX;

            // Create unified packet at HDLC layer (entry point)
            CreateOrUpdateUnifiedPacket(e.FrameId, direction, LayerType.HDLC);

            // Create HDLC layer information
            var hdlcLayerInfo = new HdlcLayerInfo
            {
                FrameId = e.FrameId,
                FcsValid = e.FcsValid,
                FrameData = e.FrameData,
                Length = e.Length
            };

            // Enrich unified packet with HDLC information
            EnrichUnifiedPacket(e.FrameId, LayerType.HDLC, hdlcLayerInfo);

            var hdlcItem = new HdlcHistoryItem(
                direction,
                e.FrameData,
                e.FcsValid,
                e.FrameId,
                null
            );

            // Check for FCS validation errors first
            string? errorReason = null;
            if (!e.FcsValid)
            {
                errorReason = "FCS_Error";
                _logger.LogWarning("⚠️ FCS ERROR: HDLC frame #{FrameId} has invalid FCS, marking as error", e.FrameId);

                // Mark unified packet with error
                MarkUnifiedPacketError(e.FrameId, errorReason, LayerType.HDLC);
            }

            // Simple dictionary approach: HDLC RX frames are always primary (from external source)
            if (e.Direction == FrameDirection.Receive)
            {
                AddPacketWithChainTracking(e.FrameId, "HDLC", Direction.RX, null, errorReason);
                if (errorReason != null)
                {
                    _logger.LogDebug("🔴 ERROR: HDLC RX frame #{FrameId} registered as primary with error: {ErrorReason}", e.FrameId, errorReason);
                }
                else
                {
                    _logger.LogDebug("📦 PRIMARY: HDLC RX frame #{FrameId} registered as primary", e.FrameId);
                }
            }
            else
            {
                // This shouldn't happen in normal flow, but handle it
                AddPacketWithChainTracking(e.FrameId, "HDLC", Direction.TX, null, errorReason);
            }

            AddToHistory(hdlcItem);

            // Update HDLC statistics
            lock (_statsLock)
            {
                if (e.Direction == FrameDirection.Receive)
                {
                    _currentStats.HDLC.BytesReceived += e.Length;
                    _currentStats.HDLC.FramesReceived++;
                    if (e.FcsValid)
                        _currentStats.HDLC.FramesValidFcs++;
                    else
                    {
                        _currentStats.HDLC.FramesInvalidFcs++;
                        _currentStats.HDLC.BadFramesReceived++;
                    }
                }
                else
                {
                    _currentStats.HDLC.BytesTransmitted += e.Length;
                    _currentStats.HDLC.FramesTransmitted++;
                }
            }

            // Use enriched logging
            _logger.LogHdlcFrame(e, "HDLC frame received");

            // Stack A LAPB routing retired (this legacy handler is no longer wired).
        }

        private void OnHdlcFrameTransmitted(object? sender, HdlcFrameEventArgs e)
        {
            // Set frame ID and add to history
            e.FrameId = GetNextFrameId();

            var hdlcItem = new HdlcHistoryItem(
                e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX,
                e.FrameData,
                e.FcsValid,
                e.FrameId
            );

            // Check for FCS validation errors first
            string? errorReason = null;
            if (!e.FcsValid)
            {
                errorReason = "FCS_Error";
                _logger.LogWarning("⚠️ FCS ERROR: HDLC transmitted frame #{FrameId} has invalid FCS, marking as error", e.FrameId);
            }

            // Simple dictionary approach: look for primary LAPB packets that need HDLC encapsulation
            var direction = e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX;
            var sourceLapbPacketId = FindPrimaryPacketNeedingDerivation("LAPB", direction);

            if (sourceLapbPacketId.HasValue)
            {
                // HDLC derived from LAPB packet
                AddPacketWithChainTracking(e.FrameId, "HDLC", direction, sourceLapbPacketId.Value, errorReason);
                if (errorReason != null)
                {
                    _logger.LogDebug("🔴 ERROR: HDLC frame #{HdlcId} derived from LAPB #{LapbId} with error: {ErrorReason}",
                        e.FrameId, sourceLapbPacketId.Value, errorReason);
                }
                else
                {
                    _logger.LogDebug("🔗 SIMPLE MATCH: HDLC frame #{HdlcId} derived from LAPB primary #{LapbId}",
                        e.FrameId, sourceLapbPacketId.Value);
                }
            }
            else
            {
                // No LAPB source - mark as primary (external HDLC frame)
                AddPacketWithChainTracking(e.FrameId, "HDLC", direction, null, errorReason);
                if (errorReason != null)
                {
                    _logger.LogDebug("🔴 ERROR: HDLC frame #{HdlcId} marked as primary with error: {ErrorReason}", e.FrameId, errorReason);
                }
                else
                {
                    _logger.LogDebug("❌ NO PRIMARY: HDLC frame #{HdlcId} has no primary LAPB source", e.FrameId);
                }
            }

            AddToHistory(hdlcItem);

            // Use enriched logging
            _logger.LogHdlcFrame(e, "HDLC frame transmitted");

            // Update HDLC statistics
            lock (_statsLock)
            {
                _currentStats.HDLC.BytesTransmitted += e.Length;
                _currentStats.HDLC.FramesTransmitted++;
            }

            // FIRE statistics updated event immediately
            StatisticsUpdated?.Invoke(this, _currentStats.Clone());

            // Stack A physical transmit routing retired (this legacy handler is no longer wired).
        }

        private void OnLapbFrameReceived(object? sender, LapbFrameEventArgs e)
        {
            try
            {
                // For unified system: LAPB enriches existing HDLC packet or creates new if needed
                // In RX flow: HDLC should have already created the unified packet
                // In TX flow: We might need to create it here

                long unifiedPacketId;
                var direction = e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX;

                // Try to use existing HDLC packet ID, or create new one
                if (e.FrameId > 0)
                {
                    // Frame ID already set by HDLC layer - use it for unified packet
                    unifiedPacketId = e.FrameId;
                }
                else
                {
                    // Set frame ID for new packet
                    e.FrameId = GetNextFrameId();
                    unifiedPacketId = e.FrameId;

                    // Create unified packet if it doesn't exist (TX flow)
                    CreateOrUpdateUnifiedPacket(unifiedPacketId, direction, LayerType.LAPB);
                }

                // Create LAPB layer information
                var lapbLayerInfo = new LapbLayerInfo
                {
                    FrameType = e.FrameType,
                    Address = e.Address,
                    Control = e.Control,
                    NS = e.NS,
                    NR = e.NR,
                    PollFinal = e.PF,
                    Information = e.Information,
                    InformationLength = e.Information?.Length ?? 0
                };

                // Enrich unified packet with LAPB information
                EnrichUnifiedPacket(unifiedPacketId, LayerType.LAPB, lapbLayerInfo);

                // Validate LAPB frame data for potential parsing errors
                string? errorReason = ValidateLapbFrame(e);

                if (errorReason != null)
                {
                    // Mark unified packet with LAPB error
                    MarkUnifiedPacketError(unifiedPacketId, errorReason, LayerType.LAPB);
                }

                // For RX direction, we add LAPB first, then the X.25 packet will be linked later
                // Create and add LAPB history item
                var lapbItem = new LapbHistoryItem(
                    direction,
                    e.FrameData, // This is already pure LAPB frame data (no FCS)
                    e.FrameType,
                    e.Address,
                    e.Control,
                    e.NS,
                    e.NR,
                    e.PF,
                    unifiedPacketId
                );

                AddToHistory(lapbItem);

                // Handle frame relationship tracking with error detection
                HandleLapbFrameRelationships(e, lapbItem, errorReason);
            }
            catch (Exception ex)
            {
                // LAPB parsing exception - create error packet
                _logger.LogError(ex, "⚠️ LAPB PARSE ERROR: Exception parsing LAPB frame #{FrameId}", e.FrameId);

                // Create minimal LAPB item for error tracking
                var errorLapbItem = new LapbHistoryItem(
                    e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX,
                    e.FrameData ?? Array.Empty<byte>(),
                    LapbFrameType.Unknown,
                    0x00,
                    0x00,
                    null,
                    null,
                    false,
                    e.FrameId
                );

                AddToHistory(errorLapbItem);

                // Track as error packet
                AddPacketWithChainTracking(e.FrameId, "LAPB",
                    e.Direction == FrameDirection.Receive ? Direction.RX : Direction.TX,
                    null, "LAPB_Parse_Error");

                _logger.LogDebug("🔴 ERROR: LAPB frame #{FrameId} registered as error: LAPB_Parse_Error", e.FrameId);

                // Update statistics
                lock (_statsLock)
                {
                    if (e.Direction == FrameDirection.Receive)
                        _currentStats.LAPB.BadFramesReceived++;
                }

                return; // Don't continue processing this frame
            }

            // Update LAPB statistics
            lock (_statsLock)
            {
                if (e.Direction == FrameDirection.Receive)
                {
                    _currentStats.LAPB.FramesReceived++;

                    // Track specific frame types
                    switch (e.FrameType)
                    {
                        case LapbFrameType.Information:
                            _currentStats.LAPB.IFramesReceived++;
                            break;
                        case LapbFrameType.Supervisory:
                            _currentStats.LAPB.SFramesReceived++;
                            break;
                        case LapbFrameType.Unnumbered:
                            _currentStats.LAPB.UFramesReceived++;
                            break;
                    }

                    // Track bad frames if we have error detection
                    // _currentStats.LAPB.BadFramesReceived++;
                }
                else
                {
                    _currentStats.LAPB.FramesTransmitted++;

                    // Track specific frame types
                    switch (e.FrameType)
                    {
                        case LapbFrameType.Information:
                            _currentStats.LAPB.IFramesTransmitted++;
                            break;
                        case LapbFrameType.Supervisory:
                            _currentStats.LAPB.SFramesTransmitted++;
                            break;
                        case LapbFrameType.Unnumbered:
                            _currentStats.LAPB.UFramesTransmitted++;
                            break;
                    }

                    // Track bad frames if we have error detection
                    // _currentStats.LAPB.BadFramesTransmitted++;
                }

                // Fire statistics updated event immediately
                StatisticsUpdated?.Invoke(this, _currentStats.Clone());
            }

            // Use enriched logging
            _logger.LogLapbFrame(e, "LAPB frame received");

            // NOTE: Removed automatic forwarding to X.25 layer - this bypassed LAPB state machine
            // The LAPB layer now properly handles forwarding via OnDataToX25() only when in DataTransfer state
            _logger.LogDebug("LAPB frame processed: {FrameType} {Direction}", e.FrameType, e.Direction);
        }

        private void OnLapbFrameTransmitted(object? sender, LapbFrameEventArgs e)
        {
            // Set frame ID and add to history
            e.FrameId = GetNextFrameId();

            // Simple dictionary approach: look for primary X25 packets that need LAPB encapsulation
            long? sourceX25PacketId = null;
            if (e.FrameType == LapbFrameType.Information && e.Information.Length > 0)
            {
                // Find any primary X25 TX packet that doesn't have a derived LAPB yet
                var direction = e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX;
                sourceX25PacketId = FindPrimaryPacketNeedingDerivation("X25", direction);

                if (sourceX25PacketId.HasValue)
                {
                    _logger.LogDebug("🔗 SIMPLE MATCH: LAPB frame #{LapbId} derived from X.25 primary #{X25Id}",
                        e.FrameId, sourceX25PacketId.Value);
                }
                else
                {
                    _logger.LogDebug("❌ NO PRIMARY: LAPB frame #{LapbId} has no primary X.25 source", e.FrameId);
                }
            }

            // Create LAPB history item
            var lapbItem = new LapbHistoryItem(
                e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX,
                e.FrameData, // This is already pure LAPB frame data (no FCS)
                e.FrameType,
                e.Address,
                e.Control,
                e.NS,
                e.NR,
                e.PF,
                e.FrameId
            );

            // Simple dictionary approach: add derived relationship if source X25 found
            if (sourceX25PacketId.HasValue)
            {
                // LAPB derived from X25 packet
                AddPacketWithChainTracking(lapbItem.FrameId, "LAPB",
                    e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX, sourceX25PacketId.Value);
                AddToHistory(lapbItem);
            }
            else
            {
                // No source - mark as primary (external LAPB frame)
                AddPacketWithChainTracking(lapbItem.FrameId, "LAPB",
                    e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX);
                AddToHistory(lapbItem);
            }

            // Update LAPB statistics
            lock (_statsLock)
            {
                _currentStats.LAPB.FramesTransmitted++;
            }

            // Use enriched logging
            _logger.LogLapbFrame(e, "LAPB frame transmitted");

            // Stack A HDLC transmit routing retired (this legacy handler is no longer wired).
        }

        private void OnLapbStateChanged(object? sender, LapbFrameEventArgs e)
        {
            // Stack A LAPB state handler retired (no longer wired). Stack B LAPB state changes are handled
            // by OnLinkLapbStateChanged.
        }

        #region Default Test Link Event Handlers

        private void OnDefaultTestLinkFrameReceived(object? sender, LapbFrameEventArgs e)
        {
            try
            {
                // Set frame ID
                e.FrameId = GetNextFrameId();

                // Create LAPB history item (same as OnLapbFrameReceived)
                var lapbItem = new LapbHistoryItem(
                    e.Direction == FrameDirection.Receive ? Direction.RX : Direction.TX,
                    e.FrameData,
                    e.FrameType,
                    e.Address,
                    e.Control,
                    e.NS,
                    e.NR,
                    e.PF,
                    e.FrameId
                );

                // Add to history (fires HistoryItemAdded event)
                AddPacketWithChainTracking(lapbItem.FrameId, "LAPB",
                    e.Direction == FrameDirection.Receive ? Direction.RX : Direction.TX);
                AddToHistory(lapbItem);

                // For I-frames with X.25 data, store in _recentLapbFrames for linking
                if (e.FrameType == LapbFrameType.Information && e.Information != null && e.Information.Length > 0)
                {
                    lock (_x25LinkingLock)
                    {
                        var frameKey = Convert.ToBase64String(e.Information);

                        // Check if there's a pending X.25 item waiting for this LAPB frame
                        if (_pendingX25LinksToLapb.TryGetValue(frameKey, out var pendingX25Item))
                        {
                            // Establish the relationship
                            lapbItem.RelatedItem = pendingX25Item;
                            pendingX25Item.RelatedItem = lapbItem;

                            // Set the two-line display flags (RX: LAPB primary, X.25 secondary)
                            lapbItem.IsMainLine = true;
                            lapbItem.IsSecondaryLine = false;
                            pendingX25Item.IsMainLine = false;
                            pendingX25Item.IsSecondaryLine = true;

                            _pendingX25LinksToLapb.Remove(frameKey);
                            _logger.LogInformation("[TestLink] LINKED: LAPB #{LapbId} <-> X.25 #{X25Id}",
                                lapbItem.Id, pendingX25Item.Id);
                        }
                        else
                        {
                            // Store for potential future matching when X.25 packet arrives
                            _recentLapbFrames[frameKey] = lapbItem;
                            _logger.LogDebug("[TestLink] STORED LAPB I-frame #{LapbId} for X.25 linking",
                                e.FrameId);
                        }

                        // Clean up old entries (keep last 10)
                        while (_recentLapbFrames.Count > 10)
                        {
                            string? oldestKey = null;
                            foreach (var key in _recentLapbFrames.Keys)
                            {
                                oldestKey = key;
                                break;
                            }
                            if (oldestKey != null)
                                _recentLapbFrames.Remove(oldestKey);
                        }
                    }

                    // Track for X.25 derivation
                    _lastLapbIFrame = lapbItem;
                }

                // Update LAPB statistics
                lock (_statsLock)
                {
                    _currentStats.LAPB.FramesReceived++;
                }

                // Use enriched logging
                _logger.LogLapbFrame(e, "TestLink LAPB frame received");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ProtocolRuntime] Error handling TestLink frame received");
            }
        }

        private void OnDefaultTestLinkFrameTransmitted(object? sender, LapbFrameEventArgs e)
        {
            try
            {
                // Set frame ID
                e.FrameId = GetNextFrameId();

                // Create LAPB history item (same as OnLapbFrameTransmitted)
                var lapbItem = new LapbHistoryItem(
                    e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX,
                    e.FrameData,
                    e.FrameType,
                    e.Address,
                    e.Control,
                    e.NS,
                    e.NR,
                    e.PF,
                    e.FrameId
                );

                // Add to history (fires HistoryItemAdded event)
                AddPacketWithChainTracking(lapbItem.FrameId, "LAPB",
                    e.Direction == FrameDirection.Transmit ? Direction.TX : Direction.RX);
                AddToHistory(lapbItem);

                // Update LAPB statistics
                lock (_statsLock)
                {
                    _currentStats.LAPB.FramesTransmitted++;
                }

                // Use enriched logging
                _logger.LogLapbFrame(e, "TestLink LAPB frame transmitted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ProtocolRuntime] Error handling TestLink frame transmitted");
            }
        }

        private void OnDefaultTestLinkPayloadReceived(ILink link, byte[] payload, int length)
        {
            // Nothing to do here any more. The default test link is registered with the LinkManager
            // like any other link, so its payload already reached that link's own X.25 engine
            // through OnLinkPayloadReceived. This handler used to feed the retired process-wide
            // engine as well, which decoded the same packet a second time.
            _logger.LogDebug("[TestLink] payload of {Length} byte(s) received on {Link}", length, link.Name);
        }

        private void OnDefaultTestLinkLapbStateChanged(object? sender, LapbStateChangedEventArgs e)
        {
            try
            {
                _logger.LogInformation("[TestLink] LAPB state changed: {OldState} -> {NewState}",
                    e.OldState, e.NewState);

                // Update LAPB statistics with TestLink state
                lock (_statsLock)
                {
                    // Map LapbState enum to string for statistics
                    string stateStr = e.NewState switch
                    {
                        LapbState.Disconnected => "DISCONNECTED",
                        LapbState.Connecting => "AWAITING_UA",
                        LapbState.DataTransfer => "CONNECTED",
                        LapbState.Disconnecting => "AWAITING_DM",
                        _ => e.NewState.ToString().ToUpperInvariant()
                    };
                    _currentStats.LAPB.CurrentState = stateStr;
                    StatisticsUpdated?.Invoke(this, _currentStats.Clone());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ProtocolRuntime] Error handling TestLink state change");
            }
        }

        #endregion

        private void OnX25PacketReceived(object? sender, X25PacketEventArgs e)
        {
            try
            {
                _logger.LogInformation("🔥 OnX25PacketReceived called! Direction: {Direction}, Packet Length: {Length}", e.Direction, e.Packet?.Length);

                // For unified system: X25 enriches existing LAPB packet or creates new if needed
                long unifiedPacketId;
                var direction = e.Direction == PacketDirection.Receive ? Direction.RX : Direction.TX;

                // Try to use existing LAPB packet ID, or create new one
                if (e.PacketId > 0)
                {
                    // Packet ID already set by LAPB layer - use it for unified packet
                    unifiedPacketId = e.PacketId;
                }
                else
                {
                    // Set packet ID for new packet
                    e.PacketId = GetNextFrameId();
                    unifiedPacketId = e.PacketId;

                    // Create unified packet if it doesn't exist (TX flow)
                    CreateOrUpdateUnifiedPacket(unifiedPacketId, direction, LayerType.X25);
                }

                // Create X25 layer information
                var x25LayerInfo = new X25LayerInfo
                {
                    PacketType = e.Packet?.PacketType ?? X25PacketType.Unknown,
                    LCN = e.Packet?.LCN ?? 0,
                    PacketId = unifiedPacketId,
                    CalledAddress = null, // TODO: Extract from packet data if needed
                    CallingAddress = null, // TODO: Extract from packet data if needed
                    Data = e.Packet?.Data ?? Array.Empty<byte>(),
                    Packet = e.Packet
                };

                // Enrich unified packet with X25 information
                EnrichUnifiedPacket(unifiedPacketId, LayerType.X25, x25LayerInfo);

                // Validate X25 packet for potential parsing errors
                string? errorReason = ValidateX25Packet(e);

                if (errorReason != null)
                {
                    // Mark unified packet with X25 error
                    MarkUnifiedPacketError(unifiedPacketId, errorReason, LayerType.X25);
                }

            // Create X.25 history item first
            // e.Packet is non-null by contract on this path (downstream code dereferences it and the
            // X25HistoryItem ctor requires it). Assert once so the whole block sees a non-null packet.
            if (e.Packet == null)
                throw new InvalidOperationException("X.25 packet event carried a null Packet.");

            var x25Item = new X25HistoryItem(
                e.Direction == PacketDirection.Transmit ? Direction.TX : Direction.RX,
                e.Packet,
                e.PacketId
            );

            // Check if there's a pending LAPB frame key for this X.25 packet
            // If so, store the X.25 item for later linking when LAPB history item is created
            if (e.Direction == PacketDirection.Receive && e.Packet.FullPacket != null)
            {
                lock (_x25LinkingLock)
                {
                    var frameKey = Convert.ToBase64String(e.Packet.FullPacket);
                    _logger.LogDebug("🔍 X.25 Packet RX: Checking for pending LAPB link. Key: {Key}, Pending keys: {Count}",
                        frameKey.Substring(0, Math.Min(16, frameKey.Length)) + "...", _pendingLapbFrameKeys.Count);

                    if (_pendingLapbFrameKeys.Contains(frameKey))
                    {
                        // Store this X.25 item for later linking when LAPB frame history item is created
                        _pendingX25LinksToLapb[frameKey] = x25Item;
                        _pendingLapbFrameKeys.Remove(frameKey);
                        _logger.LogInformation("🔗 DEFERRED LINK: X.25 #{X25Id} waiting for LAPB frame to be added to history",
                            e.PacketId);
                    }
                    else
                    {
                        _logger.LogDebug("❌ NO PENDING LAPB: X.25 packet #{X25Id} has no pending LAPB frame key", e.PacketId);
                    }
                }
            }

                // Handle X25 packet relationship tracking with error detection
                HandleX25PacketRelationships(e, x25Item, errorReason);

                // Use enriched logging
                _logger.LogX25Packet(e, "X.25 packet received");

                // Module routing now handled via RouteX25EventToBoundModule in protocol services
            }
            catch (Exception ex)
            {
                // X25 parsing exception - create error packet
                _logger.LogError(ex, "⚠️ X25 PARSE ERROR: Exception parsing X25 packet #{PacketId}", e.PacketId);

                // Create minimal X25 item for error tracking
                var errorPacket = new X25Packet();
                var errorX25Item = new X25HistoryItem(
                    e.Direction == PacketDirection.Transmit ? Direction.TX : Direction.RX,
                    errorPacket,
                    e.PacketId
                );

                AddToHistory(errorX25Item);

                // Track as error packet
                AddPacketWithChainTracking(e.PacketId, "X25",
                    e.Direction == PacketDirection.Receive ? Direction.RX : Direction.TX,
                    null, "X25_Parse_Error");

                _logger.LogDebug("🔴 ERROR: X25 packet #{PacketId} registered as error: X25_Parse_Error", e.PacketId);

                // Update statistics
                lock (_statsLock)
                {
                    if (e.Direction == PacketDirection.Receive)
                        _currentStats.X25.BadPacketsReceived++;
                }

                return; // Don't continue processing this packet
            }
        }

        private void OnX25PacketTransmitted(object? sender, X25PacketEventArgs e)
        {
            _logger.LogInformation("🔥 OnX25PacketTransmitted called! Direction: {Direction}, Packet Length: {Length}", e.Direction, e.Packet.Length);

            // Create and add X25 history item using centralized method
            var x25Item = CreateX25HistoryItem(e, "X.25 packet transmitted");

            // Simple dictionary approach: mark X25 TX packets as primary (will generate LAPB)
            AddPacketWithChainTracking(x25Item.PacketId, "X25", Direction.TX);

            AddToHistory(x25Item);

            // Route packet based on centralized routing system
            if (e.Direction == PacketDirection.Transmit)
            {
                _logger.LogInformation("🔥 Calling RouteOutgoingX25Packet for TRANSMIT packet");
                RouteOutgoingX25Packet(e.Packet);
            }
            else
            {
                _logger.LogDebug("🔥 Not routing packet - direction is {Direction}", e.Direction);
            }
        }

        // Simple dictionary-based relationships - tracks primary/derived metadata
        private readonly Dictionary<long, PacketRelationshipInfo> _packetRelationships = new();
        private readonly object _relationshipLock = new();

        /// <summary>
        /// Register a packet as primary (external source)
        /// </summary>
        private void RegisterPrimaryPacket(long packetId, string layer, Direction direction)
        {
            lock (_relationshipLock)
            {
                _packetRelationships[packetId] = new PacketRelationshipInfo
                {
                    IsPrimary = true,
                    Layer = layer,
                    Direction = direction
                };
            }
        }

        /// <summary>
        /// Register a packet as derived from another packet
        /// </summary>
        private void RegisterDerivedPacket(long derivedId, long primaryId, string layer, Direction direction)
        {
            lock (_relationshipLock)
            {
                // Add derived packet
                _packetRelationships[derivedId] = new PacketRelationshipInfo
                {
                    IsPrimary = false,
                    DerivedFromId = primaryId,
                    Layer = layer,
                    Direction = direction
                };

                // Update primary packet with generated ID
                if (_packetRelationships.TryGetValue(primaryId, out var primaryInfo))
                {
                    primaryInfo.GeneratedIds.Add(derivedId);
                }
            }
        }

        /// <summary>
        /// Get relationship info for a packet
        /// </summary>
        public PacketRelationshipInfo? GetPacketRelationship(long packetId)
        {
            lock (_relationshipLock)
            {
                return _packetRelationships.TryGetValue(packetId, out var info) ? info : null;
            }
        }

        /// <summary>
        /// Simple dictionary approach: Add primary packet (user requested simple naming)
        /// </summary>
        private void AddPrimaryPacketRelationship(long packetId, string layer, Direction direction)
        {
            RegisterPrimaryPacket(packetId, layer, direction);
        }

        /// <summary>
        /// Simple dictionary approach: Add derived packet (user requested simple naming)
        /// </summary>
        private void AddDerivedPacketRelationship(long derivedId, long primaryId, string layer, Direction direction)
        {
            RegisterDerivedPacket(derivedId, primaryId, layer, direction);
        }

        /// <summary>
        /// Simple dictionary approach: Find primary packet that needs derivation
        /// </summary>
        private long? FindPrimaryPacketNeedingDerivation(string layer, Direction direction)
        {
            lock (_relationshipLock)
            {
                // Find most recent primary packet of the specified layer/direction that has no derived packets yet
                long? mostRecentId = null;
                foreach (var kvp in _packetRelationships)
                {
                    var info = kvp.Value;
                    if (info.IsPrimary && info.Layer == layer && info.Direction == direction && info.GeneratedIds.Count == 0)
                    {
                        if (mostRecentId == null || kvp.Key > mostRecentId)
                        {
                            mostRecentId = kvp.Key;
                        }
                    }
                }
                return mostRecentId;
            }
        }

        /// <summary>
        /// Enhanced packet relationship creation with chain tracking for collapse functionality
        /// Creates packet relationship with terminal detection and error tracking
        /// </summary>
        private void AddPacketWithChainTracking(long packetId, string layer, Direction direction,
            long? derivedFromId = null, string? errorReason = null)
        {
            lock (_relationshipLock)
            {
                var hasError = !string.IsNullOrEmpty(errorReason);
                var isPrimary = derivedFromId == null;
                var chainRootId = isPrimary ? packetId : (derivedFromId.HasValue ? GetChainRootId(derivedFromId.Value) : packetId);

                // Create relationship with collapse functionality fields
                _packetRelationships[packetId] = new PacketRelationshipInfo
                {
                    IsPrimary = isPrimary,
                    DerivedFromId = derivedFromId,
                    Layer = layer,
                    Direction = direction,
                    Timestamp = DateTime.UtcNow,

                    // Collapse functionality fields
                    IsTerminal = true, // Start as terminal, will be updated if derived packets are created
                    HasProcessingError = hasError,
                    ErrorReason = errorReason,
                    ChainRootId = chainRootId
                };

                // If derived from another packet, update parent relationship
                if (derivedFromId.HasValue && !hasError)
                {
                    if (_packetRelationships.TryGetValue(derivedFromId.Value, out var parentRelationship))
                    {
                        // Parent is no longer terminal since we derived a packet from it
                        parentRelationship.IsTerminal = false;
                        parentRelationship.GeneratedIds.Add(packetId);

                        _logger.LogDebug("🔗 CHAIN: Parent packet #{ParentId} no longer terminal, derived packet #{DerivedId}",
                            derivedFromId.Value, packetId);
                    }
                }

                _logger.LogDebug("📦 CHAIN: Packet #{PacketId} ({Layer} {Direction}) - Primary: {IsPrimary}, Terminal: {IsTerminal}, Error: {HasError}",
                    packetId, layer, direction, isPrimary, true, hasError);
            }
        }

        /// <summary>
        /// Calculate chain root ID by walking up the derivation tree to find the primary packet
        /// Used for collapse functionality to group related packets
        /// </summary>
        private long GetChainRootId(long packetId)
        {
            lock (_relationshipLock)
            {
                var visited = new HashSet<long>(); // Circular reference protection
                var currentId = packetId;

                while (true)
                {
                    if (visited.Contains(currentId))
                    {
                        // Circular reference detected - return current as root
                        _logger.LogWarning("Circular reference detected in packet chain for ID {PacketId}", packetId);
                        return currentId;
                    }

                    visited.Add(currentId);

                    if (!_packetRelationships.TryGetValue(currentId, out var relationship))
                    {
                        // No relationship found - this packet is the root
                        return currentId;
                    }

                    if (relationship.IsPrimary || relationship.DerivedFromId == null)
                    {
                        // Found the primary packet - this is the root
                        return currentId;
                    }

                    // Move up the chain
                    currentId = relationship.DerivedFromId.Value;
                }
            }
        }

        private void OnX25DataReceived(object? sender, X25PacketEventArgs e)
        {
            try
            {
                _logger.LogInformation("🚨 OnX25DataReceived CALLED! LCN: {LCN}", e.Packet.LCN);

                // Validate X25 packet for potential parsing errors
                string? errorReason = ValidateX25Packet(e);

                // Create X25 history item using centralized method (no logging here, done later)
                var x25Item = CreateX25HistoryItem(e, "X.25 data received", logEvent: false);

                // Handle error cases first
                if (!string.IsNullOrEmpty(errorReason))
                {
                    AddPacketWithChainTracking(x25Item.PacketId, "X25", Direction.RX, null, errorReason);
                    _logger.LogDebug("🔴 ERROR: X25 data packet #{PacketId} registered with error: {ErrorReason}", e.PacketId, errorReason);
                    AddToHistory(x25Item);
                }
                else
                {
                    // Simple dictionary approach: if LAPB I-frame available, mark X.25 as derived
                    if (_lastLapbIFrame != null)
                    {
                        _logger.LogDebug("🔗 SIMPLE LINK: X.25 packet derived from LAPB I-frame ID {FrameId}", _lastLapbIFrame.FrameId);

                        // Add derived packet metadata to dictionary
                        AddPacketWithChainTracking(x25Item.PacketId, "X25", Direction.RX, _lastLapbIFrame.FrameId);

                        AddToHistory(x25Item);
                        // Clear after use
                        _lastLapbIFrame = null;
                    }
                    else
                    {
                        // No LAPB source - mark as primary (external input)
                        AddPacketWithChainTracking(x25Item.PacketId, "X25", Direction.RX);
                        AddToHistory(x25Item);
                        _logger.LogDebug("🔗 NO LINK: No recent LAPB I-frame for X.25 relationship");
                    }
                }

            _logger.LogDebug("X.25 data received: {PacketSummary}", e.GetPacketSummary());

                // Route to bound module (existing functionality)
                if (e.Direction == PacketDirection.Receive)
                {
                    RouteX25EventToBoundModule(e.Packet.LCN, e);
                }
            }
            catch (Exception ex)
            {
                // X25 data parsing exception - create error packet
                _logger.LogError(ex, "⚠️ X25 DATA PARSE ERROR: Exception parsing X25 data packet #{PacketId} LCN: {LCN}", e.PacketId, e.Packet?.LCN);

                // Create minimal X25 item for error tracking
                var errorPacket = new X25Packet();
                var errorX25Item = new X25HistoryItem(
                    Direction.RX, // OnX25DataReceived is always RX
                    errorPacket,
                    e.PacketId
                );

                AddToHistory(errorX25Item);

                // Track as error packet
                AddPacketWithChainTracking(e.PacketId, "X25", Direction.RX, null, "X25_Data_Parse_Error");

                _logger.LogDebug("🔴 ERROR: X25 data packet #{PacketId} registered as error: X25_Data_Parse_Error", e.PacketId);

                return; // Don't continue processing this packet
            }
        }


        private void OnXOTBytesToSend(object? sender, PhysicalLayerEventArgs e)
        {
            try
            {
                _logger.LogDebug("XOT: Bytes to send, {Length} bytes", e.Length);

                // Add to history using centralized method
                var eventArgs = new X25PacketEventArgs
                {
                    Packet = new X25Packet
                    {
                        FullPacket = e.Data,
                        Length = e.Length,
                        Direction = PacketDirection.Transmit,
                        Timestamp = e.Timestamp
                    },
                    Direction = PacketDirection.Transmit,
                    Timestamp = e.Timestamp,
                    PacketId = 0 // Will be assigned by centralized method
                };
                var x25Item = CreateX25HistoryItem(eventArgs, "XOT bytes to send");
                AddToHistory(x25Item);

                // Update XOT statistics
                lock (_statsLock)
                {
                    _currentStats.XOT.PacketsTransmitted++;
                    _currentStats.XOT.BytesTransmitted += e.Length;
                    StatisticsUpdated?.Invoke(this, _currentStats.Clone());
                }

                // This event would be used to send bytes over TCP connection
                // For now, we'll just log it as the TCP handling is in the GUI layer
                _logger.LogInformation("XOT: Encapsulated X.25 packet ready for TCP transmission");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "XOT: Error handling bytes to send");
            }
        }

        #region LinkManager Event Handlers (New Multi-Link Architecture)

        /// <summary>
        /// Handle X.25 packet received from any link via LinkManager
        /// </summary>
        /// <summary>
        /// Handle an opaque payload received from any link via LinkManager. This is where X.25 vs SINTRAN
        /// classification happens (above the protocol-agnostic link layer).
        /// </summary>
        private void OnLinkPayloadReceived(ILink link, byte[] payload, int length)
        {
            try
            {
                _linkProtocolDetector ??= new ProtocolDetector(_logger);
                DetectedProtocol protocol = _linkProtocolDetector.DetectProtocol(payload, length, link.ProtocolMode.Mode);

                if (protocol == DetectedProtocol.Sintran)
                {
                    byte protocolId = _linkProtocolDetector.ExtractSintranProtocolId(payload, length);

                    // TODO: Route SINTRAN frame to the SINTRAN PVC server.
                    _logger.LogInformation("LinkManager: SINTRAN frame from {LinkName} (Protocol: 0x{ProtocolId:X2}) - routing not yet implemented",
                        link.Name, protocolId);
                    return;
                }

                // X.25: build a packet from the opaque payload and route it to the X.25 layer.
                var packet = new X25Packet(payload) { Direction = PacketDirection.Receive };

                _logger.LogDebug("LinkManager: X.25 packet from {LinkName} ({LinkType}), LCN={LCN}, Type={Type}",
                    link.Name, link.Type, packet.LCN, packet.PacketType);

                var eventArgs = new X25PacketEventArgs
                {
                    Packet = packet,
                    Direction = PacketDirection.Receive,
                    Timestamp = DateTime.UtcNow
                };
                var historyItem = CreateX25HistoryItem(eventArgs, $"RX from {link.Name}");

                // Added through the relationship path, not straight to the history: the LAPB frame
                // that carried this packet was already recorded by the link's own frame handler, and
                // this is where the two lines are joined so the display can show the packet under
                // the frame it arrived in. Until the process-wide engine was removed, the linking
                // X.25 item was the one that engine produced.
                HandleX25PacketRelationships(eventArgs, historyItem, ValidateX25Packet(eventArgs));

                // Store link context for incoming call association (consumed by OnEndpointIncomingCall).
                _pendingLinkForNextCall = link.Name;

                // The link's own engine handles it, and its events fire synchronously inside this
                // call - which is why the link name is set before and cleared after.
                byte[] wire = packet.FullPacket;
                if (wire.Length != length && length > 0 && length <= wire.Length)
                {
                    byte[] trimmed = new byte[length];
                    Buffer.BlockCopy(wire, 0, trimmed, 0, length);
                    wire = trimmed;
                }

                EndpointForLink(link).Receive(wire);

                _pendingLinkForNextCall = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LinkManager: Error handling link payload");
            }
        }

        /// <summary>
        /// Handle link status change events
        /// </summary>
        private void OnLinkStatusChanged(ILink link, LinkStatus oldStatus, LinkStatus newStatus, string reason)
        {
            try
            {
                _logger.LogInformation("LinkManager: Link {LinkName} status: {OldStatus} -> {NewStatus} ({Reason})",
                    link.Name, oldStatus, newStatus, reason);

                // A link that is no longer carrying traffic takes its X.25 engine with it: every
                // circuit on it is reported cleared to whoever owned it, rather than left open on a
                // connection that cannot answer. A later payload on the same link builds a fresh
                // engine, which is what X.25 4.1 expects after the link is restored - a restart.
                if (newStatus != LinkStatus.Active && oldStatus == LinkStatus.Active)
                {
                    DropEndpoint(link);
                }

                // Update statistics when link status changes
                UpdateStatistics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LinkManager: Error handling link status change");
            }
        }

        /// <summary>
        /// Handle LAPB state change events from LAPB-capable links
        /// </summary>
        private void OnLinkLapbStateChanged(object? sender, LapbStateChangedEventArgs e)
        {
            try
            {
                _logger.LogInformation("LinkManager: LAPB state on {LinkName}: {OldState} -> {NewState}",
                    e.LinkName, e.OldState, e.NewState);

                // Notify subscribers of LAPB state change
                StateChanged?.Invoke(this, new RuntimeStateChangedEventArgs(
                    RuntimeState.Running,
                    RuntimeState.Running,
                    $"LAPB on {e.LinkName}: {e.NewState}"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LinkManager: Error handling LAPB state change");
            }
        }

        #endregion

        #endregion

        /// <summary>
        /// Load PVC bindings from configuration
        /// </summary>
        private void LoadPvcBindings()
        {
            if (_config.Routing?.PvcBindings == null)
            {
                _logger.LogInformation("[Routing] No PVC bindings configured");
                return;
            }

            foreach (var binding in _config.Routing.PvcBindings)
            {
                try
                {
                    // Find the module by name
                    if (_registeredModules.TryGetValue(binding.ModuleName, out var module))
                    {
                        // Bind the LCN to the module
                        BindLcnToModule(binding.Lcn, module);
                        _logger.LogInformation($"[Routing] Loaded PVC binding: LCN {binding.Lcn} -> {binding.ModuleName}");
                    }
                    else
                    {
                        _logger.LogWarning($"[Routing] PVC binding references unknown module: {binding.ModuleName}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[Routing] Error loading PVC binding LCN {binding.Lcn}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Initialize built-in PAD/TAD services
        /// </summary>
        private void InitializeBuiltinServices()
        {
            try
            {
                // Register TAD server for loopback testing on address "200"
                var tadServer = new Modules.TadServer(_logger, this);
                RegisterModule(tadServer);

                // Register SINTRAN PVC server for SINTRAN system protocols
                RegisterModule(_sintranPvcServer);

                // NOTE: Packet routing for SINTRAN PVC server is now handled by SintranTadProtocolService
                // which subscribes to PacketGenerated and properly handles direction flipping + history creation

                // Note: TAD server routing is now handled by centralized routing system
                // The RoutingConfigExample shows how to configure LocalTAD route

                _logger.LogInformation("[Services] Initialized built-in TAD server for address 200");
                _logger.LogInformation("[Services] Initialized SINTRAN PVC server for SINTRAN protocols");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Services] Failed to initialize built-in services: {ex.Message}");
            }
        }

        /// <summary>
        /// Create and register a TAD server for incoming connections
        /// </summary>
        public Modules.TadServer CreateTadServer(string name = "TADServer")
        {
            var tadServer = new Modules.TadServer(_logger, this);
            tadServer.Name = name;
            RegisterModule(tadServer);

            _logger.LogInformation("Created TAD server: {Name}", name);
            return tadServer;
        }

        /// <summary>
        /// Get access to the built-in LocalTadProtocol for session management
        /// </summary>
        public Protocols.LocalTadProtocol GetLocalTadProtocol()
        {
            return _localTadProtocol;
        }

        /// <summary>
        /// Get access to the built-in LocalXMSGProtocol for session management
        /// </summary>
        public Protocols.LocalXMSGProtocol GetLocalXMSGProtocol()
        {
            return _localXmsgProtocol;
        }

        /// <summary>
        /// Get access to the SINTRAN PVC server for SINTRAN protocol handling
        /// </summary>
        public SintranPvcServer GetSintranPvcServer()
        {
            return _sintranPvcServer;
        }

        /// <summary>
        /// Get access to the protocol detection service
        /// </summary>
        public Services.X25ProtocolDetectionService GetProtocolDetectionService()
        {
            return _protocolDetection;
        }

        /// <summary>
        /// Get protocol service registry
        /// </summary>
        public Services.ProtocolServiceRegistry GetProtocolServiceRegistry()
        {
            return _protocolServices;
        }

        /// <summary>
        /// Register protocol services for dynamic routing
        /// </summary>
        private void RegisterProtocolServices()
        {
            try
            {
                var localMachineId = _config.X25.LocalMachineId;

                // Register TAD protocol service for the local machine ID (standard X.29 TAD)
                var tadService = new Services.TadProtocolService(_logger, _localTadProtocol, this, localMachineId);
                _protocolServices.RegisterService(tadService);

                // Register SINTRAN TAD protocol service for addresses 100, 101, 102
                // These addresses are used for SINTRAN terminal connections with automatic terminal ID assignment
                var sintranTadService = new RetroIO.Protocols.X25.Sintran.Services.SintranTadProtocolService(_logger, _sintranPvcServer, this, "100", "101", "102");
                _protocolServices.RegisterService(sintranTadService);

                // Register XMSG protocol service for the local machine ID
                var xmsgService = new Services.XmsgProtocolService(_logger, _localXmsgProtocol, localMachineId);
                _protocolServices.RegisterService(xmsgService);

                _logger.LogInformation("Registered protocol services for local machine ID '{LocalMachineId}'", localMachineId);
                _logger.LogInformation("Registered SINTRAN TAD service for addresses: 100, 101, 102");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering protocol services");
            }
        }

        /// <summary>
        /// Create and register a PAD client for outbound connections
        /// </summary>
        public Modules.PadClient CreatePadClient(string name = "PADClient")
        {
            var padClient = new Modules.PadClient(_logger, this);
            padClient.Name = name;
            RegisterModule(padClient);
            return padClient;
        }

        /// <summary>
        /// Create a managed PAD client with unique ID
        /// </summary>
        public string CreateManagedPadClient(string? name = null)
        {
            lock (_routingLock)
            {
                var padId = $"PAD_{++_padClientIdCounter}";
                var padName = name ?? padId;
                var padClient = new Modules.PadClient(_logger, this);
                padClient.Name = padName;
                RegisterModule(padClient);
                _padClients[padId] = padClient;
                _logger.LogInformation("Created managed PAD client {PadId} with name {PadName}", padId, padName);
                return padId;
            }
        }

        /// <summary>
        /// Get a managed PAD client by ID
        /// </summary>
        public Modules.PadClient? GetPadClient(string padId)
        {
            lock (_routingLock)
            {
                return _padClients.TryGetValue(padId, out var padClient) ? padClient : null;
            }
        }

        /// <summary>
        /// Dispose a managed PAD client
        /// </summary>
        public bool DisposePadClient(string padId)
        {
            lock (_routingLock)
            {
                if (_padClients.TryGetValue(padId, out var padClient))
                {
                    padClient.Dispose();
                    _padClients.Remove(padId);
                    _logger.LogInformation("Disposed managed PAD client {PadId}", padId);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Get all managed PAD client IDs
        /// </summary>
        public string[] GetPadClientIds()
        {
            lock (_routingLock)
            {
                var keys = new string[_padClients.Count];
                int i = 0;
                foreach (var key in _padClients.Keys)
                {
                    keys[i++] = key;
                }
                return keys;
            }
        }

        /// <summary>
        /// Create and register a SINTRAN TAD client for outbound connections
        /// </summary>
        public RetroIO.Protocols.X25.Sintran.SintranTadClient CreateSintranTadClient(string name = "SintranTadClient")
        {
            var tadClient = new RetroIO.Protocols.X25.Sintran.SintranTadClient(_logger, this, name);
            tadClient.Name = name;
            RegisterModule(tadClient);
            return tadClient;
        }

        /// <summary>
        /// Create a managed SINTRAN TAD client with unique ID
        /// </summary>
        public string CreateManagedSintranTadClient(string? name = null)
        {
            lock (_routingLock)
            {
                var clientId = $"SINTRAN_TAD_{++_sintranTadClientIdCounter}";
                var clientName = name ?? clientId;
                var tadClient = new RetroIO.Protocols.X25.Sintran.SintranTadClient(_logger, this, clientName);
                tadClient.Name = clientName;
                RegisterModule(tadClient);
                _sintranTadClients[clientId] = tadClient;
                _logger.LogInformation("Created managed SINTRAN TAD client {ClientId} with name {ClientName}", clientId, clientName);
                return clientId;
            }
        }

        /// <summary>
        /// Get a managed SINTRAN TAD client by ID
        /// </summary>
        public RetroIO.Protocols.X25.Sintran.SintranTadClient? GetSintranTadClient(string clientId)
        {
            lock (_routingLock)
            {
                return _sintranTadClients.TryGetValue(clientId, out var tadClient) ? tadClient : null;
            }
        }

        /// <summary>
        /// Dispose a managed SINTRAN TAD client
        /// </summary>
        public bool DisposeSintranTadClient(string clientId)
        {
            lock (_routingLock)
            {
                if (_sintranTadClients.TryGetValue(clientId, out var tadClient))
                {
                    tadClient.Dispose();
                    _sintranTadClients.Remove(clientId);
                    _logger.LogInformation("Disposed managed SINTRAN TAD client {ClientId}", clientId);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Get all managed SINTRAN TAD client IDs
        /// </summary>
        public string[] GetSintranTadClientIds()
        {
            lock (_routingLock)
            {
                var keys = new string[_sintranTadClients.Count];
                int i = 0;
                foreach (var key in _sintranTadClients.Keys)
                {
                    keys[i++] = key;
                }
                return keys;
            }
        }

        /// <summary>
        /// Place an outgoing X.25 call via unified routing layer
        /// </summary>
        public bool PlaceX25CallWithModule(string calledAddress, string callingAddress, byte[]? userdata, IProtocolModule callingModule)
        {
            var traceId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var callStack = new System.Diagnostics.StackTrace();

            _logger.LogInformation("🟢 [TRACE:{TraceId}] ProtocolRuntime.PlaceX25CallWithModule() ENTER: CalledAddress={CalledAddress}, CallingAddress={CallingAddress}, Module={ModuleName}, CUD={CUD}",
                traceId, calledAddress, callingAddress, callingModule?.Name ?? "NULL",
                userdata != null ? BitConverter.ToString(userdata) : "NULL");
            _logger.LogDebug("🟢 [TRACE:{TraceId}] Call stack depth: {Depth}", traceId, callStack.FrameCount);

            // Use the unified routing layer for all outbound calls
            _logger.LogInformation("🟢 [TRACE:{TraceId}] Calling RoutingLayer.PlaceCallRequest()", traceId);
            var result = RoutingLayer.PlaceCallRequest(calledAddress, callingAddress, userdata, null, callingModule);
            _logger.LogInformation("🟢 [TRACE:{TraceId}] ProtocolRuntime.PlaceX25CallWithModule() EXIT: Result={Result}", traceId, result);

            return result;
        }

        /// <summary>
        /// Send X.25 data packet
        /// </summary>
        public bool SendX25Data(ushort lcn, byte[] data, bool mBit = false, bool qBit = false, bool dBit = false)
        {
            try
            {
                // Check if this LCN is XOT-mapped - send via XOT instead of serial path.
                // A flow-control refusal is reported to the caller as a failed send: the circuit
                // exists and is XOT, so there is no other route the data should take, and calling
                // it a success would hide the very back-pressure we just honoured.
                MappedSendOutcome xot = SendMappedData(lcn, data, data.Length);
                if (xot == MappedSendOutcome.Sent)
                    return true;
                if (xot == MappedSendOutcome.RefusedByFlowControl)
                    return false;

                _logger.LogDebug("Sending X.25 data on LCN {LCN}, {Length} bytes", lcn, data.Length);

                // Create data packet (PS and PR will be managed by X.25 layer)
                var dataPacket = X25PacketBuilder.CreateDataPacket(lcn, 0, 0, data, mBit, qBit, dBit);

                // For outgoing data from a bound module, route to the appropriate service
                if (_lcnBindings.ContainsKey(lcn))
                {
                    // Find which service should handle this data
                    var sessionService = FindServiceForEstablishedSession(lcn);
                    if (sessionService != null)
                    {
                        _logger.LogDebug("Routing outgoing data to service {ServiceName} for LCN {LCN}", sessionService.ServiceName, lcn);
                        sessionService.HandleData(lcn, data);
                    }
                    else
                    {
                        _logger.LogDebug("No service for LCN {LCN}; delivering in-process", lcn);
                        DeliverLocally(lcn, data, mBit, qBit, dBit);
                    }
                }
                else
                {
                    DeliverLocally(lcn, data, mBit, qBit, dBit);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send X.25 data on LCN {LCN}", lcn);
                return false;
            }
        }

        /// <summary>
        /// Find the service handling an established session
        /// Uses existing _lcnBindings to determine module, then finds appropriate service
        /// </summary>
        private IProtocolService? FindServiceForEstablishedSession(ushort lcn)
        {
            lock (_routingLock)
            {
                // Check if we have a module bound to this LCN
                if (!_lcnBindings.TryGetValue(lcn, out var module))
                {
                    _logger.LogDebug("No module bound to LCN {LCN}", lcn);
                    return null;
                }

                // For PAD clients (which connect to TAD services), find the TAD service
                if (module is Modules.PadClient)
                {
                    var tadService = _protocolServices.GetService("TAD");
                    if (tadService != null)
                    {
                        _logger.LogDebug("Found TAD service for PAD client on LCN {LCN}", lcn);
                        return tadService;
                    }
                }

                // For SINTRAN TAD clients (which connect to SINTRAN TAD services), find the SINTRAN_TAD service
                if (module is RetroIO.Protocols.X25.Sintran.SintranTadClient)
                {
                    var sintranTadService = _protocolServices.GetService("SINTRAN_TAD");
                    if (sintranTadService != null)
                    {
                        _logger.LogDebug("Found SINTRAN_TAD service for SintranTadClient on LCN {LCN}", lcn);
                        return sintranTadService;
                    }
                }

                _logger.LogDebug("No appropriate service found for module type {ModuleType} on LCN {LCN}",
                    module.GetType().Name, lcn);
                return null;
            }
        }

        /// <summary>
        /// Find an available LCN for outgoing calls
        /// </summary>
        /// <summary>
        /// Maps a local LCN onto the circuit an incoming XOT call arrived on. Called by the routing
        /// layer with the connection and the engine's channel it got from the Call Request.
        /// </summary>
        public void RegisterXotConnection(ushort lcn, object? connection, ushort remoteLcn)
        {
            if (connection == null)
            {
                return;
            }

            X25Endpoint? endpoint = FindEndpoint(connection);
            if (endpoint == null)
            {
                _logger.LogWarning("RegisterXotConnection: no endpoint for the connection of LCN {LCN}", lcn);
                return;
            }

            MapCircuit(lcn, endpoint, remoteLcn);
        }

        /// <summary>
        /// Forgets a local LCN's circuit and service binding.
        /// </summary>
        public void UnregisterXotConnection(ushort lcn)
        {
            UnmapCircuit(lcn);
            lock (_routingLock)
            {
                _lcnServiceBindings.Remove(lcn);
            }
        }

        /// <summary>Kept for the routing layer's call sequence; the mapping is made when the call is placed.</summary>
        public void RegisterOutboundXotCall(ushort localLcn, ushort remoteLcn)
        {
        }

        /// <summary>Kept for the routing layer's call sequence; the mapping is made when the call is placed.</summary>
        public void RegisterOutboundXotCallDeferred(ushort localLcn, ushort remoteLcn)
        {
        }

        /// <summary>Kept for the routing layer's call sequence; the mapping is made when the call is placed.</summary>
        public void FinalizeOutboundXotCall(ushort localLcn, object transport)
        {
        }

        public ushort FindAvailableLCN()
        {
            lock (_routingLock)
            {
                // Look for first available LCN (starting from 1, avoiding LCN 0)
                for (ushort lcn = 1; lcn <= 4095; lcn++)
                {
                    if (!_lcnBindings.ContainsKey(lcn))
                    {
                        return lcn;
                    }
                }
                return 0; // No available LCN
            }
        }



        /// <summary>
        /// Unbind a module from an LCN (cleanup on failed outbound call)
        /// </summary>
        public void UnbindModule(ushort lcn)
        {
            lock (_routingLock)
            {
                _lcnBindings.Remove(lcn);
            }
        }






        /// <summary>
        /// Bind a protocol service to an LCN for XOT data routing
        /// </summary>
        public void BindServiceToLcn(ushort lcn, Services.IProtocolService service)
        {
            lock (_routingLock)
            {
                _lcnServiceBindings[lcn] = service;
                _logger.LogDebug("Bound service '{ServiceName}' to LCN {LCN}", service.ServiceName, lcn);
            }
        }

        /// <summary>
        /// Find the protocol service bound to an LCN
        /// </summary>
        private Services.IProtocolService? FindServiceForLcn(ushort lcn)
        {
            lock (_routingLock)
            {
                _lcnServiceBindings.TryGetValue(lcn, out var service);
                return service;
            }
        }

        // Old direct integration methods removed - all handling now goes through service layer


        /// <summary>
        /// Extract cause and diagnostic codes from packet data
        /// </summary>
        private (byte cause, byte diagnostic) ExtractCauseAndDiagnostic(byte[]? data)
        {
            if (data == null || data.Length < 2)
            {
                return (0, 0); // Default values
            }

            return (data[0], data[1]);
        }

        /// <summary>
        /// Extract call request data from X.25 packet
        /// </summary>
        private X25CallRequest? ExtractCallRequestFromPacket(X25Packet packet)
        {
            try
            {
                // Simple implementation - in a real system, this would parse the actual X.25 call request format
                // For now, we'll create a basic call request with placeholder data

                if (packet.PacketType != X25PacketType.CallRequest)
                {
                    return null;
                }

                // Extract called and calling addresses from packet data
                // This is a simplified implementation - real X.25 parsing would be more complex
                string calledAddress = "UNKNOWN";
                string callingAddress = "UNKNOWN";

                if (packet.Data != null && packet.Data.Length > 0)
                {
                    try
                    {
                        // Simple ASCII extraction - real implementation would parse X.25 address format
                        calledAddress = System.Text.Encoding.ASCII.GetString(packet.Data);
                        callingAddress = "LOCAL"; // Default calling address
                    }
                    catch
                    {
                        // If parsing fails, use defaults
                        calledAddress = "UNKNOWN";
                        callingAddress = "UNKNOWN";
                    }
                }

                return new X25CallRequest(calledAddress, callingAddress, null, packet.Data ?? Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Routing] Error extracting call request from packet: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// Create LAPB frame from X.25 packet (legacy method)
        /// </summary>
        private byte[] CreateLapbFrameFromX25Packet(X25Packet packet)
        {
            // Simple implementation - in a real system, this would be more complex
            // with proper LAPB frame construction including address and control fields

            // For now, just return the packet data as-is
            // The LAPB layer will handle proper frame construction
            return packet.FullPacket ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Route outgoing X.25 packet using centralized routing system
        /// </summary>
        private void RouteOutgoingX25Packet(X25Packet packet)
        {
            try
            {
                _logger.LogInformation("🚀 ROUTING: RouteOutgoingX25Packet called with packet length {Length}", packet.Length);

                // Extract called address from the packet
                var calledAddress = ExtractCalledAddressFromX25Packet(packet);
                if (string.IsNullOrEmpty(calledAddress))
                {
                    _logger.LogWarning("🚀 ROUTING: Cannot route X.25 packet - no called address found");
                    return;
                }

                _logger.LogInformation("🚀 ROUTING: Extracted called address '{CalledAddress}' from X.25 packet", calledAddress);

                // Use centralized routing system
                if (_config.Routing?.Enabled == true)
                {
                    _logger.LogInformation("🚀 ROUTING: Centralized routing is enabled, checking {Count} routes", _config.Routing.RoutingTable.Count);

                    foreach (var route in _config.Routing.RoutingTable)
                    {
                        _logger.LogDebug("🚀 ROUTING: Checking route '{RouteName}' pattern '{Pattern}' (enabled: {Enabled})", route.Name, route.X121AddressPattern, route.Enabled);

                        if (route.Enabled && IsRouteMatch(route, calledAddress))
                        {
                            _logger.LogInformation("🚀 ROUTING: ✅ MATCH! Route '{RouteName}' matches address {CalledAddress} -> {RouteType}/{Destination}",
                                route.Name, calledAddress, route.Type, route.Destination);

                            RoutePacketToDestination(packet, route);
                            return;
                        }
                        else
                        {
                            _logger.LogDebug("🚀 ROUTING: ❌ No match for route '{RouteName}'", route.Name);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("🚀 ROUTING: Centralized routing is DISABLED");
                }

                // No route found - check protocol services before LAPB fallback
                _logger.LogWarning("🚀 ROUTING: No route found for address {CalledAddress} - checking protocol services", calledAddress);

                // Try to extract CUD and create a temporary call request to check protocol services
                byte[] cud = ExtractCallUserDataFromX25Packet(packet);
                var service = _protocolServices.FindServiceForCall(calledAddress, cud);

                if (service != null)
                {
                    _logger.LogInformation("🚀 ROUTING: Found protocol service '{ServiceName}' for address {CalledAddress}", service.ServiceName, calledAddress);

                    // Create a proper call request for the service
                    var tempCallRequest = new X25CallRequest(calledAddress, _config.X25.LocalMachineId, null, cud);

                    // Handle via protocol service
                    ushort lcn = (ushort)(packet.LCN > 0 ? packet.LCN : 1);
                    bool handled = service.HandleIncomingCall(lcn, tempCallRequest.CalledAddress, tempCallRequest.CallingAddress, tempCallRequest.CallUserData ?? new byte[0], new byte[0]);

                    if (handled)
                    {
                        _logger.LogInformation("🚀 ROUTING: Protocol service successfully handled call to {CalledAddress}", calledAddress);
                        return;
                    }
                    else
                    {
                        _logger.LogWarning("🚀 ROUTING: Protocol service failed to handle call to {CalledAddress}", calledAddress);
                    }
                }
                else
                {
                    _logger.LogWarning("🚀 ROUTING: No protocol service found for address {CalledAddress}", calledAddress);
                }

                // Final fallback to local LAPB (for backward compatibility)
                _logger.LogWarning("🚀 ROUTING: Using local LAPB fallback for address {CalledAddress}", calledAddress);
                _logger.LogWarning("Routing: legacy local-LAPB send retired; configure a link (LinkManager) to reach this address");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error routing outgoing X.25 packet");

                // Fallback to local LAPB on error
                _logger.LogWarning("Routing: legacy local-LAPB send retired; configure a link (LinkManager) to reach this address");
            }
        }

        /// <summary>
        /// Extract called address from X.25 Call Request packet
        /// </summary>
        private string ExtractCalledAddressFromX25Packet(X25Packet packet)
        {
            try
            {
                var data = packet.FullPacket;
                if (data == null || data.Length < 4)
                    return "";

                // Check if this is a Call Request packet (packet type 0x0B)
                byte packetType = data[2];
                if ((packetType & 0x1F) != 0x0B) // Call Request
                {
                    // Not a Call Request - no routing needed
                    return "";
                }

                // Parse Call Request format: GFI+LCN(2) + Type(1) + Addresses...
                int index = 3;
                if (index >= data.Length) return "";

                // Address lengths field
                byte addressLengths = data[index++];
                byte calledAddressLength = (byte)((addressLengths >> 4) & 0x0F);
                byte callingAddressLength = (byte)(addressLengths & 0x0F);

                if (calledAddressLength == 0) return "";
                if (index + (calledAddressLength + 1) / 2 > data.Length) return "";

                // Extract called address (BCD encoded)
                var calledAddress = "";
                for (int i = 0; i < (calledAddressLength + 1) / 2; i++)
                {
                    if (index + i >= data.Length) break;

                    byte bcdByte = data[index + i];

                    // First digit (upper nibble) - BCD digits are stored high nibble first
                    if (calledAddress.Length < calledAddressLength)
                        calledAddress += ((bcdByte >> 4) & 0x0F).ToString();

                    // Second digit (lower nibble)
                    if (calledAddress.Length < calledAddressLength)
                        calledAddress += (bcdByte & 0x0F).ToString();
                }

                return calledAddress;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting called address from X.25 packet");
                return "";
            }
        }

        /// <summary>
        /// Extract Call User Data from X.25 Call Request packet
        /// </summary>
        private byte[] ExtractCallUserDataFromX25Packet(X25Packet packet)
        {
            try
            {
                var data = packet.FullPacket;
                if (data == null || data.Length < 4)
                    return new byte[0];

                // Check if this is a Call Request packet (packet type 0x0B)
                byte packetType = data[2];
                if ((packetType & 0x1F) != 0x0B) // Call Request
                {
                    // Not a Call Request - no CUD
                    return new byte[0];
                }

                // Parse Call Request format: GFI+LCN(2) + Type(1) + AddressLengths(1) + Addresses + Facilities + CUD
                int index = 3;
                if (index >= data.Length) return new byte[0];

                // Address lengths field
                byte addressLengths = data[index++];
                byte calledAddressLength = (byte)((addressLengths >> 4) & 0x0F);
                byte callingAddressLength = (byte)(addressLengths & 0x0F);

                // Skip addresses (BCD packed, 2 digits per byte)
                int addressBytesToSkip = (calledAddressLength + callingAddressLength + 1) / 2;
                index += addressBytesToSkip;

                if (index >= data.Length) return new byte[0];

                // Skip facilities (first byte is facilities length)
                byte facilitiesLength = data[index++];
                index += facilitiesLength;

                if (index >= data.Length) return new byte[0];

                // Remaining bytes are Call User Data
                int cudLength = data.Length - index;
                byte[] cud = new byte[cudLength];
                Array.Copy(data, index, cud, 0, cudLength);

                _logger.LogDebug("Extracted CUD: {CUD} bytes from X.25 packet", cudLength);
                return cud;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting Call User Data from X.25 packet");
                return new byte[0];
            }
        }

        /// <summary>
        /// Check if route matches the called address
        /// </summary>
        private bool IsRouteMatch(X25RouteEntry route, string calledAddress)
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(route.X121AddressPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return regex.IsMatch(calledAddress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error matching route pattern '{Pattern}' against address '{Address}'", route.X121AddressPattern, calledAddress);
                return false;
            }
        }

        /// <summary>
        /// Route packet to the specified destination
        /// </summary>
        private void RoutePacketToDestination(X25Packet packet, X25RouteEntry route)
        {
            switch (route.Type)
            {
                case RouteType.Local:
                    _logger.LogDebug("Routing to local destination: {Destination}", route.Destination);

                    // Use dynamic protocol service registry instead of hardcoded handlers
                    if (packet.PacketType == X25PacketType.CallRequest)
                    {
                        try
                        {
                            // Parse the call request packet using constructor
                            var callRequest = new X25CallRequest(packet.FullPacket);

                            _logger.LogInformation("Processing local Call Request for address {CalledAddress} on LCN {LCN}",
                                callRequest.CalledAddress, packet.LCN);

                            // Find appropriate protocol service based on address and CUD
                            var service = _protocolServices.FindServiceForCall(callRequest.CalledAddress, callRequest.CallUserData);

                            if (service != null)
                            {
                                _logger.LogInformation("Found protocol service '{ServiceName}' for address {CalledAddress}",
                                    service.ServiceName, callRequest.CalledAddress);

                                // Handle the call using the protocol service
                                var success = service.HandleIncomingCall(packet.LCN, callRequest.CalledAddress,
                                    callRequest.CallingAddress, callRequest.CallUserData, callRequest.Facilities);

                                if (success)
                                {
                                    _logger.LogInformation("✅ Local call setup successful for address {CalledAddress} on LCN {LCN} via {ServiceName}",
                                        callRequest.CalledAddress, packet.LCN, service.ServiceName);
                                }
                                else
                                {
                                    _logger.LogWarning("❌ Local call setup failed for address {CalledAddress} on LCN {LCN} via {ServiceName}",
                                        callRequest.CalledAddress, packet.LCN, service.ServiceName);
                                }
                            }
                            else
                            {
                                _logger.LogWarning("No protocol service found for address {CalledAddress} with CUD pattern",
                                    callRequest.CalledAddress);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing local call request on LCN {LCN}", packet.LCN);
                        }
                    }
                    else
                    {
                        // For non-Call Request packets (Data, etc.), let them be handled normally
                        // The protocol services will receive them via the existing event system
                        _logger.LogDebug("Local route: {PacketType} packet on LCN {LCN} will be handled via existing protocol system",
                            packet.PacketType, packet.LCN);

                        // Don't route these packets - let them be processed normally by the protocol stack
                        // This allows Data packets to reach bound modules via the existing LCN binding system
                    }
                    break;

                case RouteType.Interface:
                    _logger.LogDebug("Routing to interface: {Destination}", route.Destination);

                    // Check if it's a serial interface
                    SerialInterfaceConfig? serialInterface = null;
                    if (_config.Routing?.SerialInterfaces != null)
                    {
                        foreach (var s in _config.Routing.SerialInterfaces)
                        {
                            if (s.Name == route.Destination)
                            {
                                serialInterface = s;
                                break;
                            }
                        }
                    }
                    if (serialInterface != null)
                    {
                        if (serialInterface.Enabled)
                        {
                            _logger.LogInformation("Routing packet to Serial interface {InterfaceName} ({Port})",
                                serialInterface.Name, serialInterface.PortName);

                            // Serial routing is handled by the serial link (LinkManager); legacy LAPB retired.
                            _logger.LogWarning("Routing: legacy serial-LAPB send retired; use the serial link for {InterfaceName}", serialInterface.Name);
                        }
                        else
                        {
                            _logger.LogWarning("Serial interface {InterfaceName} is disabled", serialInterface.Name);
                        }
                        return;
                    }

                    // Check if it's an XOT interface
                    XotInterfaceConfig? xotInterface = null;
                    if (_config.Routing?.XotInterfaces != null)
                    {
                        foreach (var x in _config.Routing.XotInterfaces)
                        {
                            if (x.Name == route.Destination)
                            {
                                xotInterface = x;
                                break;
                            }
                        }
                    }
                    if (xotInterface != null)
                    {
                        if (xotInterface.Enabled)
                        {
                            _logger.LogInformation("Routing packet to XOT interface {InterfaceName} ({RemoteIP}:{Port})",
                                xotInterface.Name, xotInterface.RemoteIPAddress, xotInterface.RemotePort);

                            // Route to XOT interface
                            // TODO: Implement XOT routing when XOT layer is ready
                            _logger.LogWarning("XOT routing not yet implemented");
                        }
                        else
                        {
                            _logger.LogWarning("XOT interface {InterfaceName} is disabled", xotInterface.Name);
                        }
                        return;
                    }

                    _logger.LogError("Interface {InterfaceName} not found in configuration", route.Destination);
                    break;

                case RouteType.Gateway:
                    _logger.LogDebug("Routing via gateway: {Destination}", route.Destination);
                    // TODO: Implement gateway routing
                    _logger.LogWarning("Gateway routing not yet implemented");
                    break;

                default:
                    _logger.LogError("Unknown route type: {RouteType}", route.Type);
                    break;
            }
        }


        /// <summary>
        /// Add item to history ring buffer
        /// </summary>
        private void AddToHistory(HistoryItem item)
        {
            AddToHistory(item, null, RelationshipType.None);
        }

        private void AddToHistory(HistoryItem item, HistoryItem? relatedItem, RelationshipType relationshipType)
        {
            if (item == null)
                return;

            lock (_historyLock)
            {
                // Set unique ID for the history item
                // For UnifiedHistoryItem, use the existing GlobalPacketId instead of generating a new one
                if (item is UnifiedHistoryItem unifiedItem)
                {
                    item.Id = unifiedItem.PacketInfo.GlobalPacketId;
                }
                else
                {
                    item.Id = GetNextFrameId();
                }

                // Set relationship if provided
                if (relatedItem != null)
                {
                    item.RelatedItem = relatedItem;
                    item.RelationshipType = relationshipType;

                    // Set reverse relationship
                    if (relationshipType == RelationshipType.Encapsulates)
                    {
                        relatedItem.RelatedItem = item;
                        relatedItem.RelationshipType = RelationshipType.EncapsulatedBy;
                    }
                    else if (relationshipType == RelationshipType.EncapsulatedBy)
                    {
                        relatedItem.RelatedItem = item;
                        relatedItem.RelationshipType = RelationshipType.Encapsulates;
                    }

                    // Apply two-line display business logic
                    ApplyTwoLineDisplayLogic(item, relatedItem);
                }

                // Add to ring buffer
                _historyBuffer.Enqueue(item);

                // Remove oldest items if capacity exceeded
                while (_historyBuffer.Count > _historyCapacity)
                {
                    _historyBuffer.Dequeue();
                }

                // Track X.25 items for potential linking with LAPB
                if (item is X25HistoryItem x25Item)
                {
                    lock (_x25LinkingLock)
                    {
                        // Store this X.25 item for potential LAPB linking
                        _recentX25Items[item.Id] = x25Item;

                        // Clean up old entries (keep last 10)
                        while (_recentX25Items.Count > 10)
                        {
                            var oldestKey = _recentX25Items.Keys.Min();
                            _recentX25Items.Remove(oldestKey);
                        }
                    }
                }

                // Fire event
                HistoryItemAdded?.Invoke(this, item);

                // Trigger GUI display update for real-time updates
                var relationship = GetPacketRelationship(item.Id);
                bool isNewDerivedPacket = relationship?.IsPrimary == false;
                TriggerDisplayUpdate(item.Id, isNewDerivedPacket);

                // Log history item added
                _logger.LogHistoryItemAdded(item);
            }
        }

        /// <summary>
        /// Get next shared frame ID
        /// </summary>
        private long GetNextFrameId()
        {
            lock (_frameIdLock)
            {
                return ++_sharedFrameId;
            }
        }

        // ===== UNIFIED PACKET SYSTEM METHODS =====

        /// <summary>
        /// Create or update a unified packet - single ID with layer enrichment
        /// </summary>
        /// <param name="packetId">Global packet ID (if known, otherwise 0 for new packet)</param>
        /// <param name="direction">Packet direction</param>
        /// <param name="originLayer">Layer where packet originated</param>
        /// <returns>The global packet ID</returns>
        private long CreateOrUpdateUnifiedPacket(long packetId, Direction direction, LayerType originLayer)
        {
            lock (_unifiedPacketsLock)
            {
                // Generate new packet ID if not provided
                if (packetId == 0)
                {
                    packetId = GetNextFrameId();
                }

                // Check if packet already exists
                if (_unifiedPackets.TryGetValue(packetId, out var existingPacket))
                {
                    // Update existing packet with new layer
                    existingPacket.UpdateLayer(originLayer);
                    return packetId;
                }

                // Create new unified packet
                var packetInfo = new UnifiedPacketInfo
                {
                    GlobalPacketId = packetId,
                    Direction = direction,
                    OriginLayer = originLayer,
                    HighestLayer = originLayer
                };

                packetInfo.LayersVisited.Add(originLayer);

                var unifiedItem = new UnifiedHistoryItem(packetInfo);
                _unifiedPackets[packetId] = unifiedItem;

                return packetId;
            }
        }

        /// <summary>
        /// Enrich a unified packet with layer-specific information
        /// </summary>
        /// <param name="packetId">Global packet ID</param>
        /// <param name="layerType">Layer providing the information</param>
        /// <param name="layerInfo">Layer-specific information</param>
        private void EnrichUnifiedPacket(long packetId, LayerType layerType, object layerInfo)
        {
            lock (_unifiedPacketsLock)
            {
                if (!_unifiedPackets.TryGetValue(packetId, out var unifiedItem))
                {
                    // Packet doesn't exist yet - this shouldn't happen in normal flow
                    return;
                }

                // Update layer
                unifiedItem.UpdateLayer(layerType);

                // Add layer-specific information
                switch (layerType)
                {
                    case LayerType.HDLC:
                        if (layerInfo is HdlcLayerInfo hdlcInfo)
                        {
                            unifiedItem.PacketInfo.HDLC = hdlcInfo;
                        }
                        break;

                    case LayerType.LAPB:
                        if (layerInfo is LapbLayerInfo lapbInfo)
                        {
                            unifiedItem.PacketInfo.LAPB = lapbInfo;
                        }
                        break;

                    case LayerType.X25:
                        if (layerInfo is X25LayerInfo x25Info)
                        {
                            unifiedItem.PacketInfo.X25 = x25Info;
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Mark a unified packet as having an error
        /// </summary>
        /// <param name="packetId">Global packet ID</param>
        /// <param name="errorReason">Error reason</param>
        /// <param name="errorLayer">Layer where error occurred</param>
        private void MarkUnifiedPacketError(long packetId, string errorReason, LayerType errorLayer)
        {
            lock (_unifiedPacketsLock)
            {
                if (_unifiedPackets.TryGetValue(packetId, out var unifiedItem))
                {
                    unifiedItem.MarkError(errorReason, errorLayer);
                }
            }
        }

        /// <summary>
        /// Get unified packets for display (replacement for GetPacketsForDisplay)
        /// </summary>
        /// <returns>List of unified packet display items</returns>
        public List<PacketDisplayItem> GetUnifiedPacketsForDisplay()
        {
            lock (_unifiedPacketsLock)
            {
                var displayItems = new List<PacketDisplayItem>();

                foreach (var unifiedItem in _unifiedPackets.Values)
                {
                    var displayItem = new PacketDisplayItem(unifiedItem, this);
                    displayItems.Add(displayItem);
                }

                // Sort manually by Packet.Id
                var sortedItems = new List<PacketDisplayItem>(displayItems);
                for (int i = 0; i < sortedItems.Count - 1; i++)
                {
                    for (int j = i + 1; j < sortedItems.Count; j++)
                    {
                        if (sortedItems[i].Packet.Id > sortedItems[j].Packet.Id)
                        {
                            var temp = sortedItems[i];
                            sortedItems[i] = sortedItems[j];
                            sortedItems[j] = temp;
                        }
                    }
                }
                return sortedItems;
            }
        }

        /// <summary>
        /// Apply two-line display business logic to related history items
        /// Determines which item is main line vs secondary line based on direction and layer type
        /// </summary>
        private void ApplyTwoLineDisplayLogic(HistoryItem currentItem, HistoryItem relatedItem)
        {
            // Business logic for two-line display:
            // TX direction: X.25 is primary (business logic), LAPB is secondary (transport)
            // RX direction: LAPB is primary (received first), X.25 is secondary (extracted content)

            bool currentIsX25 = currentItem.Layer == LayerType.X25;
            bool relatedIsX25 = relatedItem.Layer == LayerType.X25;
            bool currentIsTX = currentItem.Direction == Direction.TX;
            bool relatedIsTX = relatedItem.Direction == Direction.TX;

            // Determine primary item based on direction and protocol layer
            HistoryItem primaryItem;
            HistoryItem secondaryItem;

            if (currentIsTX)
            {
                // TX: X.25 is primary (business logic layer)
                if (currentIsX25)
                {
                    primaryItem = currentItem;
                    secondaryItem = relatedItem;
                }
                else
                {
                    primaryItem = relatedItem;
                    secondaryItem = currentItem;
                }
            }
            else
            {
                // RX: LAPB is primary (transport layer received first)
                if (!currentIsX25) // Current is LAPB
                {
                    primaryItem = currentItem;
                    secondaryItem = relatedItem;
                }
                else
                {
                    primaryItem = relatedItem;
                    secondaryItem = currentItem;
                }
            }

            // Set display flags
            primaryItem.IsMainLine = true;
            primaryItem.IsSecondaryLine = false;

            secondaryItem.IsMainLine = false;
            secondaryItem.IsSecondaryLine = true;

            _logger.LogDebug("Two-line display: {PrimaryLayer}[{PrimaryDir}] #{PrimaryId} is PRIMARY, {SecondaryLayer}[{SecondaryDir}] #{SecondaryId} is SECONDARY",
                            primaryItem.Layer, primaryItem.Direction, primaryItem.Id,
                            secondaryItem.Layer, secondaryItem.Direction, secondaryItem.Id);
        }

        /// <summary>
        /// Compare two byte arrays for equality
        /// </summary>
        private static bool ByteArraysEqual(byte[]? a, byte[]? b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Start LAPB T1 timer (response timeout) - called by LAPB layer when needed
        /// </summary>
        public void StartT1Timer()
        {
            if (_config.Timers?.CentralTimerManagement != true)
                return;

            try
            {
                StartTimer("LAPB_T1", _config.Lapb.T1Timeout, () =>
                {
                    if (!_disposed)
                    {
                        _logger.LogDebug("[Timer] LAPB: T1 timer elapsed (legacy Stack A dispatch retired)");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Timers] Error starting T1 timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Start LAPB T2 timer (acknowledgment delay) - called by LAPB layer when needed
        /// </summary>
        public void StartT2Timer()
        {
            if (_config.Timers?.CentralTimerManagement != true)
                return;

            try
            {
                StartTimer("LAPB_T2", _config.Lapb.T2Timeout, () =>
                {
                    if (!_disposed)
                    {
                        _logger.LogDebug("[Timer] LAPB: T2 timer elapsed (legacy Stack A dispatch retired)");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Timers] Error starting T2 timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Start LAPB T3 timer (idle supervision) - called by LAPB layer when needed
        /// </summary>
        public void StartT3Timer()
        {
            if (_config.Timers?.CentralTimerManagement != true)
                return;

            // ITU-T X.25: T3 timer disabled when timeout = 0
            if (_config.Lapb.T3Timeout.TotalMilliseconds <= 0)
            {
                _logger.LogDebug("[Timers] LAPB: T3 timer disabled (timeout=0) - no idle supervision");
                return;
            }

            try
            {
                StartTimer("LAPB_T3", _config.Lapb.T3Timeout, () =>
                {
                    if (!_disposed)
                    {
                        _logger.LogDebug("[Timer] LAPB: T3 timer elapsed (legacy Stack A dispatch retired)");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Timers] Error starting T3 timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop LAPB T1 timer - called by LAPB layer when acknowledgment received
        /// </summary>
        public void StopT1Timer()
        {
            StopTimer("LAPB_T1");
        }

        /// <summary>
        /// Stop LAPB T2 timer - called by LAPB layer when acknowledgment sent
        /// </summary>
        public void StopT2Timer()
        {
            StopTimer("LAPB_T2");
        }

        /// <summary>
        /// Stop LAPB T3 timer - called by LAPB layer when activity detected or link disconnected
        /// </summary>
        public void StopT3Timer()
        {
            StopTimer("LAPB_T3");
        }


        /// <summary>
        /// Update statistics
        /// </summary>
        private void UpdateStatistics()
        {
            lock (_statsLock)
            {
                // Update runtime statistics
                _currentStats.Runtime.TotalHistoryItems = _historyBuffer.Count;
                _currentStats.Runtime.HistoryCapacity = _historyCapacity;
                _currentStats.Runtime.ActiveTimers = _timerManager.RunningTimerCount;
                _currentStats.Runtime.RegisteredModules = _registeredModules.Count;
                _currentStats.Runtime.ActiveLcnBindings = _lcnBindings.Count;
                _currentStats.Uptime = DateTime.UtcNow - _startTime;

                // LAPB state is per-link now (multi-link); no single legacy LAPB state at the runtime level.

                // Update X.25 active virtual circuits count
                _currentStats.X25.ActiveVirtualCircuits = _lcnBindings.Count;

                // Update X.25 state (simplified - in real implementation this would come from X.25 layer)
                _currentStats.X25.CurrentState = _lcnBindings.Count > 0 ? "ACTIVE" : "IDLE";

                // Fire statistics updated event
                StatisticsUpdated?.Invoke(this, _currentStats.Clone());
            }
        }

        /// <summary>
        /// Evaluate routing using new centralized routing system with CUD-based protocol detection
        /// </summary>
        private IProtocolModule? EvaluateSvcRules(X25CallRequest callRequest, ushort lcn)
        {
            lock (_routingLock)
            {
                // First, detect protocol from Call User Data according to X.25 spec
                var protocolInfo = _protocolDetection.GetProtocolInfo(callRequest.CallUserData, callRequest.CalledAddress);
                _logger.LogInformation("Protocol detected for {CalledAddress}: {Protocol} ({Description})",
                    callRequest.CalledAddress, protocolInfo.Protocol, protocolInfo.Description);

                // Check if this is a local call to our machine ID
                bool isLocalCall = callRequest.CalledAddress == _config.X25.LocalMachineId;

                if (isLocalCall)
                {
                    // Use dynamic protocol service registry for local calls
                    _logger.LogInformation("Processing local call to machine ID {CalledAddress}, protocol: {Protocol}",
                        callRequest.CalledAddress, protocolInfo.Protocol);

                    // Find appropriate protocol service based on address and CUD
                    var service = _protocolServices.FindServiceForCall(callRequest.CalledAddress, callRequest.CallUserData);

                    if (service != null)
                    {
                        _logger.LogInformation("Found protocol service '{ServiceName}' for local call to {CalledAddress}",
                            service.ServiceName, callRequest.CalledAddress);

                        try
                        {
                            // Handle the call using the protocol service
                            var success = service.HandleIncomingCall(lcn, callRequest.CalledAddress,
                                callRequest.CallingAddress, callRequest.CallUserData, callRequest.Facilities);

                            if (success)
                            {
                                _logger.LogInformation("✅ Local call setup successful via {ServiceName} for address {CalledAddress} on LCN {LCN}",
                                    service.ServiceName, callRequest.CalledAddress, lcn);

                                // Return the associated module if available
                                return _registeredModules.ContainsKey(service.ServiceName + "Server") ?
                                    _registeredModules[service.ServiceName + "Server"] : null;
                            }
                            else
                            {
                                _logger.LogWarning("❌ Local call setup failed via {ServiceName} for address {CalledAddress} on LCN {LCN}",
                                    service.ServiceName, callRequest.CalledAddress, lcn);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error handling local call via {ServiceName} for address {CalledAddress} on LCN {LCN}",
                                service.ServiceName, callRequest.CalledAddress, lcn);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No protocol service found for local call to {CalledAddress} with protocol {Protocol}",
                            callRequest.CalledAddress, protocolInfo.Protocol);
                    }
                }

                // Use traditional address-based routing for remote calls
                if (_config.Routing?.Enabled == true)
                {
                    foreach (var route in _config.Routing.RoutingTable)
                    {
                        if (route.Enabled && route.Type == RouteType.Local)
                        {
                            try
                            {
                                var regex = new System.Text.RegularExpressions.Regex(route.X121AddressPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                if (regex.IsMatch(callRequest.CalledAddress))
                                {
                                    _logger.LogInformation("Found address-based route '{RouteName}' for address {CalledAddress}", route.Name, callRequest.CalledAddress);

                                    // For LocalTAD route, return the registered TAD server module
                                    if (route.Destination == "LocalTAD" && _registeredModules.ContainsKey("TadServer"))
                                    {
                                        return _registeredModules["TadServer"];
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError("Error evaluating route '{RouteName}': {Error}", route.Name, ex.Message);
                            }
                        }
                    }
                }

                _logger.LogWarning("No route found for call request to {CalledAddress} with protocol {Protocol}",
                    callRequest.CalledAddress, protocolInfo.Protocol);
                return null;
            }
        }


        /// <summary>
        /// Bind LCN to module
        /// </summary>
        public void BindLcnToModule(ushort lcn, IProtocolModule module)
        {
            lock (_routingLock)
            {
                // Unbind any existing module from this LCN
                if (_lcnBindings.TryGetValue(lcn, out var existingModule))
                {
                    _logger.LogWarning($"[Routing] LCN {lcn} already bound to {existingModule.Name}, unbinding");
                    existingModule.OnUnbound(lcn);
                }

                // Bind the new module
                _lcnBindings[lcn] = module;
                module.OnBound(lcn);

                _logger.LogInformation($"[Routing] Bound LCN {lcn} to module {module.Name}");
                ModuleBound?.Invoke(this, (lcn, module));
            }
        }

        /// <summary>
        /// Route X.25 event to bound module
        /// </summary>
        public void RouteX25EventToBoundModule(ushort lcn, X25PacketEventArgs eventArgs)
        {
            // NOTE: Events are already fired by X25Layer - no need to duplicate here
            // to prevent dual event firing creating multiple history entries

            lock (_routingLock)
            {
                if (_lcnBindings.TryGetValue(lcn, out var module))
                {
                    _logger.LogDebug("Routing X.25 event {PacketType} on LCN {LCN} to bound module {ModuleName}",
                        eventArgs.Packet.PacketType, lcn, module.Name);

                    // OnX25Event is a first-class member of IProtocolModule, so dispatch polymorphically
                    // to ANY bound module (PadClient, SintranTadClient, and app-provided modules such as
                    // the console-app clients built on this runtime). No concrete type-checking required.
                    module.OnX25Event(eventArgs);
                }
                else if (TryCircuit(lcn, out _, out _))
                {
                    // A packet the OLD engine built for a channel that an XOT endpoint carries. Until
                    // 2026-09-06 it was rewritten with the remote LCN and pushed onto the connection
                    // beside the endpoint's own traffic - two engines on one circuit. Now it is
                    // translated into an operation on the endpoint's engine (data, clear), or dropped
                    // when the endpoint already did it (Call Accepted).
                    ForwardOldLayerPacketToEndpoint(lcn, eventArgs);
                }
                else
                {
                    _logger.LogWarning("No module bound to LCN {LCN} for X.25 event {PacketType}", lcn, eventArgs.Packet.PacketType);
                }
            }
        }




        /// <summary>
        /// Unbind LCN from module
        /// </summary>
        private void UnbindLcnFromModule(ushort lcn)
        {
            lock (_routingLock)
            {
                if (_lcnBindings.TryGetValue(lcn, out var module))
                {
                    _lcnBindings.Remove(lcn);
                    module.OnUnbound(lcn);

                    _logger.LogInformation($"[Routing] Unbound LCN {lcn} from module {module.Name}");
                    ModuleUnbound?.Invoke(this, (lcn, module));
                }
            }
        }

        #region LCN-Link Association (Multi-Link Architecture)

        /// <summary>
        /// Associate an LCN with a specific link for TX routing
        /// When sending packets on this LCN, they will be routed through the specified link
        /// </summary>
        public void AssociateLcnWithLink(ushort lcn, string linkName)
        {
            lock (_routingLock)
            {
                if (_lcnToLink.TryGetValue(lcn, out var existingLink))
                {
                    _logger.LogWarning("Routing: LCN {LCN} already associated with link '{ExistingLink}', reassociating to '{NewLink}'",
                        lcn, existingLink, linkName);
                }
                _lcnToLink[lcn] = linkName;
                _logger.LogInformation("Routing: Associated LCN {LCN} with link '{LinkName}'", lcn, linkName);
            }
        }

        /// <summary>
        /// Remove the link association for an LCN
        /// </summary>
        public void DisassociateLcnFromLink(ushort lcn)
        {
            lock (_routingLock)
            {
                if (_lcnToLink.TryGetValue(lcn, out var linkName))
                {
                    _lcnToLink.Remove(lcn);
                    _logger.LogInformation("Routing: Disassociated LCN {LCN} from link '{LinkName}'", lcn, linkName);
                }
            }
        }

        /// <summary>
        /// Get the link name associated with an LCN
        /// </summary>
        public string? GetLinkForLcn(ushort lcn)
        {
            lock (_routingLock)
            {
                return _lcnToLink.TryGetValue(lcn, out var linkName) ? linkName : null;
            }
        }

        /// <summary>
        /// Get all LCN-Link associations (read-only copy)
        /// </summary>
        public IReadOnlyDictionary<ushort, string> GetLcnLinkAssociations()
        {
            lock (_routingLock)
            {
                return new Dictionary<ushort, string>(_lcnToLink);
            }
        }

        #endregion

        /// <summary>
        /// Centralized method for creating X25HistoryItem from X25PacketEventArgs
        /// Eliminates duplicate code across all X25 event handlers
        /// </summary>
        private X25HistoryItem CreateX25HistoryItem(X25PacketEventArgs eventArgs, string eventType, bool logEvent = true)
        {
            // Set packet ID if not already set
            if (eventArgs.PacketId == 0)
            {
                eventArgs.PacketId = GetNextFrameId();
            }

            var direction = eventArgs.Direction == PacketDirection.Transmit ? Direction.TX : Direction.RX;

            var x25Item = new X25HistoryItem(direction, eventArgs.Packet, eventArgs.PacketId);

            // Use enriched logging for all X25 events (optional)
            if (logEvent)
            {
                _logger.LogX25Packet(eventArgs, eventType);
            }

            return x25Item;
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Dispose runtime resources
        /// </summary>
        public void Dispose()
        {
            DisposeEndpoints();
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _logger.LogInformation("[ProtocolRuntime] Stopping protocol runtime");

                // Stop all transport interfaces
                StopTransportInterfaces();

                // Shutdown SINTRAN PVC server
                try
                {
                    _sintranPvcServer?.Shutdown();
                    _logger.LogInformation("SINTRAN PVC server shut down successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error shutting down SINTRAN PVC server");
                }

                // Stop all LAPB timers
                StopT1Timer();
                StopT2Timer();
                StopT3Timer();

                // Dispose bridge if active
                if (_bridge != null)
                {
                    try
                    {
                        _bridge.Dispose();
                        _logger.LogInformation("XOT-Serial bridge disposed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error disposing XOT-Serial bridge");
                    }
                }

                // Dispose LinkManager (stops and disposes all links)
                try
                {
                    _linkManager?.Dispose();
                    _logger.LogInformation("LinkManager disposed");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing LinkManager");
                }

                _disposed = true;
                _logger.LogInformation("[ProtocolRuntime] Protocol runtime stopped successfully");

                StateChanged?.Invoke(this, new RuntimeStateChangedEventArgs(
                    RuntimeState.Running,
                    RuntimeState.Stopped,
                    "Runtime stopped"
                ));
            }

            // Dispose timer manager
            _timerManager.Dispose();

            _logger.LogInformation("[ProtocolRuntime] ProtocolRuntime disposed");
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Create PhysicalLayerConfig from RuntimeConfig
        /// </summary>
        private PhysicalLayerConfig CreatePhysicalLayerConfig(RuntimeConfig config)
        {
            return new PhysicalLayerConfig
            {
                LocalMachineId = config.X25.LocalMachineId,
                TestMode = config.TestMode,
                SerialEnabled = false,  // Will be handled via InitializeSerialPorts
                SerialPortName = null,
                TcpServerEnabled = false,  // TODO: Configure from routing config
                TcpListenerPort = 0
            };
        }

        /// <summary>
        /// Pre-store LAPB frame key for relationship linking before X.25 processing
        /// This marks that an LAPB frame with this data is coming, so X.25 can wait for it
        /// </summary>
        public void PreStoreLapbFrameForLinking(byte[] x25Data)
        {
            if (x25Data == null || x25Data.Length == 0)
                return;

            lock (_x25LinkingLock)
            {
                var frameKey = Convert.ToBase64String(x25Data);

                // Just mark that we're expecting an LAPB frame with this data
                // The actual linking happens in HandleLapbFrameRelationships
                _pendingLapbFrameKeys.Add(frameKey);
                _logger.LogDebug("🔧 PRE-STORED LAPB frame KEY for X.25 relationship linking, key: {Key}",
                    frameKey.Substring(0, Math.Min(16, frameKey.Length)) + "...");

                // Clean up old entries (keep last 10)
                while (_pendingLapbFrameKeys.Count > 10)
                {
                    // Get first element without LINQ
                    string? oldest = null;
                    foreach (var key in _pendingLapbFrameKeys)
                    {
                        oldest = key;
                        break;
                    }
                    if (oldest != null)
                        _pendingLapbFrameKeys.Remove(oldest);
                }
            }
        }

        /// <summary>
        /// Set original packet data for X.25 relationship tracking.
        /// Implements ILapbFramePreHandler interface - delegates to X25Layer.
        /// </summary>
        public void SetOriginalPacketForRelationship(byte[] data)
        {
            // Nothing to hold on to. This existed so the retired engine could show the packet as it
            // arrived rather than one rebuilt from the decoded fields, because it kept no copy. The
            // engines report the packet they actually received or sent, so there is nothing to fix
            // up. Kept because ILapbFramePreHandler declares it.
        }

        #endregion

        #region GUI Display Formatting

        /// <summary>
        /// Get packets formatted for GUI display using original dictionary for relationships
        /// Primary packets show packet number, derived packets are indented without number
        /// </summary>
        public List<PacketDisplayItem> GetPacketsForDisplay()
        {
            var displayItems = new List<PacketDisplayItem>();

            lock (_historyLock)
            {
                foreach (var packet in _historyBuffer)
                {
                    // Simple approach - just pass packet and runtime reference
                    // GUI uses original dictionary via Runtime.GetPacketRelationship()
                    displayItems.Add(new PacketDisplayItem(packet, this));
                }
            }

            // Sort to keep derived packets immediately after their primary packets
            return SortPacketsForConsecutiveDisplay(displayItems);
        }

        /// <summary>
        /// Sort packets so derived packets appear immediately after their primary packets
        /// Uses original dictionary for relationship lookup
        /// </summary>
        private List<PacketDisplayItem> SortPacketsForConsecutiveDisplay(List<PacketDisplayItem> items)
        {
            var result = new List<PacketDisplayItem>();
            var processed = new HashSet<long>();

            // Sort items manually by Packet.Id
            var sortedItems = new List<PacketDisplayItem>(items);
            for (int i = 0; i < sortedItems.Count - 1; i++)
            {
                for (int j = i + 1; j < sortedItems.Count; j++)
                {
                    if (sortedItems[i].Packet.Id > sortedItems[j].Packet.Id)
                    {
                        var temp = sortedItems[i];
                        sortedItems[i] = sortedItems[j];
                        sortedItems[j] = temp;
                    }
                }
            }

            foreach (var item in sortedItems)
            {
                if (processed.Contains(item.Packet.Id))
                    continue;

                if (item.IsPrimary()) // Use original dictionary
                {
                    result.Add(item);
                    processed.Add(item.Packet.Id);

                    // Find all derived packets manually
                    var derived = new List<PacketDisplayItem>();
                    foreach (var d in items)
                    {
                        if (!d.IsPrimary() && d.GetPrimaryPacketId() == item.Packet.Id)
                        {
                            derived.Add(d);
                        }
                    }

                    // Sort derived items by Packet.Id
                    for (int i = 0; i < derived.Count - 1; i++)
                    {
                        for (int j = i + 1; j < derived.Count; j++)
                        {
                            if (derived[i].Packet.Id > derived[j].Packet.Id)
                            {
                                var temp = derived[i];
                                derived[i] = derived[j];
                                derived[j] = temp;
                            }
                        }
                    }

                    foreach (var derivedItem in derived)
                    {
                        if (!processed.Contains(derivedItem.Packet.Id))
                        {
                            result.Add(derivedItem);
                            processed.Add(derivedItem.Packet.Id);
                        }
                    }
                }
            }

            // Add any remaining items that weren't processed (orphaned derived packets)
            foreach (var item in items)
            {
                if (!processed.Contains(item.Packet.Id))
                {
                    result.Add(item);
                }
            }

            return result;
        }

        /// <summary>
        /// Event fired when packet display should be updated for GUI
        /// Supports real-time updates as requested
        /// </summary>
        public event EventHandler<PacketDisplayUpdateEventArgs>? DisplayUpdateNeeded;

        /// <summary>
        /// Trigger GUI display update - called when new packets arrive
        /// Enables real-time updates and scrolling to keep relationships visible
        /// </summary>
        private void TriggerDisplayUpdate(long newPacketId, bool isNewDerivedPacket = false)
        {
            // Get relationship info for the new packet
            var relationship = GetPacketRelationship(newPacketId);
            bool isTerminal = relationship?.IsTerminal ?? false;
            bool hasError = relationship?.HasProcessingError ?? false;
            string? errorReason = relationship?.ErrorReason;

            DisplayUpdateNeeded?.Invoke(this, new PacketDisplayUpdateEventArgs
            {
                NewPacketId = newPacketId,
                IsNewDerivedPacket = isNewDerivedPacket,
                AllDisplayItems = GetPacketsForDisplay(),
                IsTerminalPacket = isTerminal,
                HasError = hasError,
                ErrorReason = errorReason
            });
        }

        #endregion

        #region LAPB Error Detection Helper Methods

        /// <summary>
        /// Validates LAPB frame for potential parsing errors.
        /// Delegates to LapbFrameUtils.ValidateFrame for reusable validation logic.
        /// </summary>
        private string? ValidateLapbFrame(LapbFrameEventArgs e)
        {
            try
            {
                return LapbFrameUtils.ValidateFrame(e.FrameData, e.FrameData?.Length ?? 0, e.FrameType, e.NS, e.NR);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LAPB validation exception for frame #{FrameId}", e.FrameId);
                return "LAPB_Validation_Exception";
            }
        }

        /// <summary>
        /// Handles LAPB frame relationship tracking (extracted from original OnLapbFrameReceived)
        /// </summary>
        private void HandleLapbFrameRelationships(LapbFrameEventArgs e, LapbHistoryItem lapbItem, string? errorReason)
        {
            // Don't create relationships for error packets
            if (!string.IsNullOrEmpty(errorReason))
            {
                AddPacketWithChainTracking(e.FrameId, "LAPB",
                    e.Direction == FrameDirection.Receive ? Direction.RX : Direction.TX,
                    null, errorReason);
                _logger.LogDebug("🔴 ERROR: LAPB frame #{FrameId} registered with error: {ErrorReason}", e.FrameId, errorReason);
                return;
            }

            // Use the provided lapbItem for _lastLapbIFrame tracking

            // Simple dictionary approach: check if LAPB frame is derived from HDLC or primary for X.25
            if (e.FrameType == LapbFrameType.Information && e.Direction == FrameDirection.Receive && e.Information.Length > 0)
            {
                _lastLapbIFrame = lapbItem;

                // Check for pending X.25 link and establish relationship
                lock (_x25LinkingLock)
                {
                    var frameKey = Convert.ToBase64String(e.Information);

                    // Check if there's a pending X.25 item waiting for this LAPB frame
                    if (_pendingX25LinksToLapb.TryGetValue(frameKey, out var pendingX25Item))
                    {
                        // Establish the relationship now that we have the real LAPB item
                        lapbItem.RelatedItem = pendingX25Item;
                        pendingX25Item.RelatedItem = lapbItem;

                        // Set the two-line display flags
                        lapbItem.IsMainLine = true;
                        lapbItem.IsSecondaryLine = false;
                        pendingX25Item.IsMainLine = false;
                        pendingX25Item.IsSecondaryLine = true;

                        _pendingX25LinksToLapb.Remove(frameKey);
                        _logger.LogInformation("🔗 LINKED: LAPB #{LapbId} <-> X.25 #{X25Id} (deferred link resolved)",
                            lapbItem.Id, pendingX25Item.Id);
                    }
                    else
                    {
                        // Store for potential future matching (fallback)
                        _recentLapbFrames[frameKey] = lapbItem;
                        _logger.LogDebug("🔧 STORED LAPB I-frame #{LapbId} for X.25 linking, key len: {KeyLen}",
                            e.FrameId, frameKey.Length);
                    }

                    // Clean up old entries (keep last 10)
                    while (_recentLapbFrames.Count > 10)
                    {
                        // Get first key without LINQ
                        string? oldestKey = null;
                        foreach (var key in _recentLapbFrames.Keys)
                        {
                            oldestKey = key;
                            break;
                        }
                        if (oldestKey != null)
                            _recentLapbFrames.Remove(oldestKey);
                    }
                }

                // Check if derived from HDLC primary packet
                var sourceHdlcPacketId = FindPrimaryPacketNeedingDerivation("HDLC", Direction.RX);

                if (sourceHdlcPacketId.HasValue)
                {
                    // LAPB derived from HDLC frame
                    AddPacketWithChainTracking(e.FrameId, "LAPB", Direction.RX, sourceHdlcPacketId.Value);
                    _logger.LogDebug("🔗 HDLC→LAPB: LAPB I-frame #{LapbId} derived from HDLC primary #{HdlcId}",
                        e.FrameId, sourceHdlcPacketId.Value);
                }
                else
                {
                    // No HDLC source - mark as primary for X.25 derivation
                    AddPacketWithChainTracking(e.FrameId, "LAPB", Direction.RX);
                    _logger.LogDebug("📦 PRIMARY: LAPB I-frame #{LapbId} marked as primary for X.25 derivation", e.FrameId);
                }
            }
            else if (e.FrameType != LapbFrameType.Information)
            {
                // Non-I frames (S-frames, U-frames) can also be derived from HDLC
                var sourceHdlcPacketId = FindPrimaryPacketNeedingDerivation("HDLC",
                    e.Direction == FrameDirection.Receive ? Direction.RX : Direction.TX);

                if (sourceHdlcPacketId.HasValue)
                {
                    AddPacketWithChainTracking(e.FrameId, "LAPB",
                        e.Direction == FrameDirection.Receive ? Direction.RX : Direction.TX, sourceHdlcPacketId.Value);
                    _logger.LogDebug("🔗 HDLC→LAPB: LAPB {FrameType} #{LapbId} derived from HDLC #{HdlcId}",
                        e.FrameType, e.FrameId, sourceHdlcPacketId.Value);
                }
                else
                {
                    AddPacketWithChainTracking(e.FrameId, "LAPB",
                        e.Direction == FrameDirection.Receive ? Direction.RX : Direction.TX);
                }
            }
        }

        #endregion

        #region X25 Error Detection Helper Methods

        /// <summary>
        /// Validates X25 packet for potential parsing errors.
        /// Delegates to X25Utils.ValidatePacket for reusable validation logic.
        /// </summary>
        private string? ValidateX25Packet(X25PacketEventArgs e)
        {
            try
            {
                return X25Utils.ValidatePacket(e.Packet);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "X25 validation exception for packet #{PacketId}", e.PacketId);
                return "X25_Validation_Exception";
            }
        }

        /// <summary>
        /// Handles X25 packet relationship tracking (extracted from original OnX25PacketReceived)
        /// </summary>
        private void HandleX25PacketRelationships(X25PacketEventArgs e, X25HistoryItem x25Item, string? errorReason)
        {
            // Don't create relationships for error packets
            if (!string.IsNullOrEmpty(errorReason))
            {
                AddPacketWithChainTracking(e.PacketId, "X25",
                    e.Direction == PacketDirection.Receive ? Direction.RX : Direction.TX,
                    null, errorReason);
                _logger.LogDebug("🔴 ERROR: X25 packet #{PacketId} registered with error: {ErrorReason}", e.PacketId, errorReason);
                return;
            }

            // Original relationship logic (extracted from the method)
            // Look for matching LAPB frame for RX direction
            LapbHistoryItem? relatedLapbItem = null;
            if (e.Direction == PacketDirection.Receive && e.Packet.FullPacket != null)
            {
                lock (_x25LinkingLock)
                {
                    _logger.LogDebug("🔍 X.25 Packet RX: Looking for LAPB match. Data len: {DataLen}, Available LAPB frames: {LapbCount}",
                        e.Packet.FullPacket.Length, _recentLapbFrames.Count);

                    var frameKey = Convert.ToBase64String(e.Packet.FullPacket);
                    if (_recentLapbFrames.TryGetValue(frameKey, out relatedLapbItem))
                    {
                        _logger.LogInformation("🔗 FOUND MATCH: X.25 packet #{X25Id} matches LAPB #{LapbId}",
                            e.PacketId, relatedLapbItem.Id);
                        // Remove from tracking since we found the match
                        _recentLapbFrames.Remove(frameKey);
                    }
                    else
                    {
                        _logger.LogDebug("❌ NO MATCH: X.25 packet #{X25Id} has no matching LAPB frame", e.PacketId);
                        // Build key list manually
                        var keyList = new List<string>();
                        foreach (var key in _recentLapbFrames.Keys)
                        {
                            keyList.Add(key.Substring(0, Math.Min(16, key.Length)) + "...");
                        }
                        _logger.LogDebug("  Available LAPB frame keys: [{Keys}]",
                            string.Join(", ", keyList));
                    }
                }
            }

            // Add to history with relationship if LAPB match was found
            if (relatedLapbItem != null)
            {
                // X.25 RX packet is EncapsulatedBy the LAPB frame
                AddToHistory(x25Item, relatedLapbItem, RelationshipType.EncapsulatedBy);
                _logger.LogDebug("🔗 LINKED: X.25 #{X25Id} EncapsulatedBy LAPB #{LapbId}",
                    x25Item.Id, relatedLapbItem.Id);
            }
            else
            {
                AddToHistory(x25Item);
            }

            // Update statistics
            lock (_statsLock)
            {
                if (e.Direction == PacketDirection.Receive)
                {
                    _currentStats.X25.PacketsReceived++;

                    // Track specific packet types
                    switch (e.Packet.PacketType)
                    {
                        case X25PacketType.Data:
                            _currentStats.X25.DataPacketsReceived++;
                            break;
                        case X25PacketType.CallRequest:
                            _currentStats.X25.CallRequestsReceived++;
                            break;
                    }
                }
                else
                {
                    _currentStats.X25.PacketsTransmitted++;

                    // Track specific packet types
                    switch (e.Packet.PacketType)
                    {
                        case X25PacketType.Data:
                            _currentStats.X25.DataPacketsTransmitted++;
                            break;
                        case X25PacketType.CallRequest:
                            _currentStats.X25.CallRequestsTransmitted++;
                            break;
                    }
                }

                // Fire statistics updated event immediately
                StatisticsUpdated?.Invoke(this, _currentStats.Clone());
            }
        }

        #endregion
    }
}
