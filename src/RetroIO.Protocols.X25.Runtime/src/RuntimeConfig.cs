using System;
using RetroIO.Link;
using RetroIO.Protocols.X25.Modules;
using RetroIO.Protocols.X25.Runtime.Transport;
using RetroIO.Protocols.X25.Transport;

using RetroIO.Protocols.X25;
namespace RetroIO.Protocols.X25.Runtime
{

    /// <summary>
    /// Configuration for ProtocolRuntime
    /// </summary>
    public class RuntimeConfig
    {
        /// <summary>
        /// Test mode - enables test-friendly behavior for unit tests
        /// </summary>
        public bool TestMode { get; set; } = false;

        /// <summary>
        /// Default history capacity
        /// </summary>
        public int HistoryCapacity { get; set; } = 100;

        /// <summary>
        /// LAPB configuration
        /// </summary>
        public LapbConfig Lapb { get; set; } = new();

        /// <summary>
        /// X.25 configuration
        /// </summary>
        public X25Config X25 { get; set; } = new();

        /// <summary>
        /// Routing configuration
        /// </summary>
        public X25RoutingConfig Routing { get; set; } = new();

        /// <summary>
        /// SINTRAN protocol configuration
        /// </summary>
        public SintranConfig Sintran { get; set; } = new();

        /// <summary>
        /// Timer configuration
        /// </summary>
        public TimerConfig Timers { get; set; } = new();

        /// <summary>
        /// Logging configuration
        /// </summary>
        public LoggingConfig Logging { get; set; } = new();

        /// <summary>
        /// XOT-to-Serial bridge configuration for high-performance pass-through mode
        /// </summary>
        public XotSerialBridgeConfig Bridge { get; set; } = new();

        /// <summary>
        /// Create default runtime configuration with example routing setup
        /// </summary>
        public static RuntimeConfig CreateDefault()
        {
            var config = new RuntimeConfig();
            
            // Basic example with one serial port and local TAD server
            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                Enabled = false, // Disabled by default
                PortName = "COM1",
                BaudRate = 9600,
                RemoteMachineId = "100",
                Description = "Connection to remote X.25 switch at machine ID 100"
            });
            
            // Add routing table entries
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "SerialRoute100",
                X121AddressPattern = "100.*",
                Type = RouteType.Interface,
                Destination = "Serial0",
                Priority = 100,
                Enabled = false, // Disabled by default
                Description = "Route addresses 100.* to Serial0 interface"
            });
            
            config.Routing.RoutingTable.Add(new X25RouteEntry
            {
                Name = "LocalTAD",
                X121AddressPattern = "200",
                Type = RouteType.Local,
                Destination = "LocalTAD",
                Priority = 200,
                Enabled = true,
                Description = "Route address 200 to built-in TAD server"
            });
            
            return config;
        }

        /// <summary>
        /// Create comprehensive example configuration showing full Cisco-style routing
        /// </summary>
        public static RuntimeConfig CreateComprehensiveExample()
        {
            var config = new RuntimeConfig();
            
            // Multiple serial interfaces
            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial0",
                Enabled = false,
                PortName = "COM1",
                BaudRate = 9600,
                RemoteMachineId = "100",
                Description = "Primary serial link to X.25 network A (machine 100)"
            });
            
            config.Routing.SerialInterfaces.Add(new SerialInterfaceConfig
            {
                Name = "Serial1", 
                Enabled = false,
                PortName = "COM2",
                BaudRate = 19200,
                RemoteMachineId = "150",
                Description = "Secondary serial link to X.25 network B (machine 150)"
            });

            // XOT interfaces
            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT0",
                Enabled = false,
                Mode = XotMode.Client,
                RemoteIPAddress = "192.168.1.100",
                RemotePort = 1998,
                RemoteMachineId = "300",
                Description = "XOT connection to remote site (machine 300)"
            });

            config.Routing.XotInterfaces.Add(new XotInterfaceConfig
            {
                Name = "XOT_Server",
                Enabled = false,
                Mode = XotMode.Server,
                LocalPort = 1998,
                RemoteMachineId = "350",
                Description = "XOT server listening for incoming connections"
            });
            
            // Comprehensive routing table
            config.Routing.RoutingTable.AddRange(new[]
            {
                new X25RouteEntry
                {
                    Name = "Network_A_Route",
                    X121AddressPattern = "10.*",
                    Type = RouteType.Interface,
                    Destination = "Serial0",
                    Priority = 100,
                    Description = "Route 10.* addresses to Serial0 (Network A)"
                },
                new X25RouteEntry
                {
                    Name = "Network_B_Route", 
                    X121AddressPattern = "15.*",
                    Type = RouteType.Interface,
                    Destination = "Serial1", 
                    Priority = 100,
                    Description = "Route 15.* addresses to Serial1 (Network B)"
                },
                new X25RouteEntry
                {
                    Name = "XOT_Network_Route",
                    X121AddressPattern = "30.*",
                    Type = RouteType.Interface,
                    Destination = "XOT0",
                    Priority = 100,
                    Description = "Route 30.* addresses to XOT0 (Remote site)"
                },
                new X25RouteEntry
                {
                    Name = "Local_TAD_Route",
                    X121AddressPattern = "200",
                    Type = RouteType.Local,
                    Destination = "LocalTAD",
                    Priority = 200,
                    Enabled = true,
                    Description = "Route address 200 to built-in TAD server"
                },
                new X25RouteEntry
                {
                    Name = "Gateway_Route_Example",
                    X121AddressPattern = "101.*",
                    Type = RouteType.Gateway,
                    Destination = "100", // Route through machine 100
                    Priority = 50,
                    Description = "Route 101.* through gateway machine 100"
                }
            });
            
            // Known hosts with gateway routing
            config.Routing.KnownHosts.AddRange(new[]
            {
                new KnownHostEntry
                {
                    Name = "SerialGateway",
                    MachineId = "100",
                    Reachability = HostReachability.Direct,
                    Via = "Serial0",
                    Description = "Primary serial gateway machine",
                    Enabled = true
                },
                new KnownHostEntry
                {
                    Name = "SecondaryGateway",
                    MachineId = "150",
                    Reachability = HostReachability.Direct,
                    Via = "Serial1",
                    Description = "Secondary serial gateway machine",
                    Enabled = true
                },
                new KnownHostEntry
                {
                    Name = "RemoteHost",
                    MachineId = "101",
                    Reachability = HostReachability.Gateway,
                    Via = "100",
                    Description = "Remote host reachable through gateway 100",
                    Enabled = true
                }
            });
            
            // Set default gateway
            config.Routing.DefaultGateway = "100";
            
            return config;
        }
    }

    /// <summary>
    /// LAPB-specific configuration
    /// </summary>
    public class LapbConfig
    {
        /// <summary>
        /// T1 timer duration (response timeout) - Maximum time to wait for frame acknowledgment
        /// LAPB standard: Used for retransmission timeout
        /// </summary>
        public TimeSpan T1Timeout { get; set; } = TimeSpan.FromMilliseconds(3000);

        /// <summary>
        /// T2 timer duration (acknowledgment delay) - Time to send acknowledgment
        /// LAPB standard: Must be ≤ T1/2, typically 100-200ms
        /// ITU-T X.25 specification: T2 ≤ T1/2 (1500ms for T1=3000ms)
        /// </summary>
        public TimeSpan T2Timeout { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// T3 timer duration (idle supervision) - DCE side idle channel detection
        /// ITU-T X.25 standard: Default 0 (disabled), when enabled must be > T1
        /// Only used by DCE for idle channel monitoring, DTE should not use T3
        /// </summary>
        public TimeSpan T3Timeout { get; set; } = TimeSpan.FromMilliseconds(0);

        /// <summary>
        /// Maximum retry count (N2)
        /// ITU-T X.25 specification: Typical value is 3
        /// </summary>
        public int MaxRetries { get; set; } = 5;

        /// <summary>
        /// Window size (k)
        /// </summary>
        public byte WindowSize { get; set; } = 7;

        /// <summary>
        /// X.25 Station Type Configuration
        /// 
        /// DCE (Data Circuit-terminating Equipment) = Network-side equipment:
        /// - X.25 packet switches, PADs, network nodes, modems with X.25
        /// - Provides link supervision using T3 idle timer
        /// - Manages network timing and synchronization
        /// - Typically accepts incoming calls from DTE devices
        /// - Uses LAPB address 0x01 for command frames
        /// 
        /// DTE (Data Terminal Equipment) = User-side equipment:
        /// - Computer terminals, hosts, applications, test equipment
        /// - In traditional networks: does NOT use T3 timer (relies on DCE for supervision)
        /// - In peer-to-peer scenarios: one side should act as "primary" for T3 supervision
        /// - Initiates most X.25 call requests to DCE
        /// - Uses LAPB address 0x03 for command frames
        /// 
        /// PEER-TO-PEER SCENARIOS (DTE-to-DTE over serial/XOT):
        /// - Both sides are DTE equipment (X.25 emulators)
        /// - Both should be configured as DTE (IsDCE=false) for peer-to-peer communication
        /// - With T3=0 (disabled), no duplicate supervision conflicts occur
        /// - Traditional DTE behavior: relies on network for supervision
        /// 
        /// Default: false - DTE mode suitable for peer-to-peer X.25 emulation
        /// </summary>
        public bool IsDCE { get; set; } = false;

        /// <summary>
        /// Sequence modulo (8 or 128)
        /// </summary>
        public byte Modulo { get; set; } = 8;

        /// <summary>
        /// Maximum information field size (N1)
        /// </summary>
        public ushort MaxInfoFieldSize { get; set; } = 128;
    }

    /// <summary>
    /// X.25-specific configuration
    /// </summary>
    public class X25Config
    {
        /// <summary>
        /// Default packet window size
        /// </summary>
        public byte DefaultPacketWindowSize { get; set; } = 2;

        /// <summary>
        /// Maximum packet size
        /// </summary>
        public ushort MaxPacketSize { get; set; } = 128;

        /// <summary>
        /// T20 timer duration (restart request)
        /// </summary>
        public TimeSpan T20Timeout { get; set; } = TimeSpan.FromSeconds(180);

        /// <summary>
        /// T21 timer duration (call request)
        /// </summary>
        public TimeSpan T21Timeout { get; set; } = TimeSpan.FromSeconds(200);

        /// <summary>
        /// T22 timer duration (reset request)
        /// </summary>
        public TimeSpan T22Timeout { get; set; } = TimeSpan.FromSeconds(180);

        /// <summary>
        /// T23 timer duration (clear request)
        /// </summary>
        public TimeSpan T23Timeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Local DTE address
        /// </summary>
        public string LocalDteAddress { get; set; } = "";

        /// <summary>
        /// Local machine ID (X.121 address for this machine)
        /// Default 200 is used by the built-in LocalTadProtocol server
        /// </summary>
        public string LocalMachineId { get; set; } = "200";

        /// <summary>
        /// Support extended packet sequencing
        /// </summary>
        public bool ExtendedPacketSequencing { get; set; } = false;
    }

    /// <summary>
    /// Timer configuration
    /// </summary>
    public class TimerConfig
    {
        /// <summary>
        /// Enable central timer management
        /// </summary>
        public bool CentralTimerManagement { get; set; } = true;

        /// <summary>
        /// Timer callback marshalling enabled
        /// </summary>
        public bool MarshalCallbacks { get; set; } = true;

        /// <summary>
        /// Default timer precision
        /// </summary>
        public TimeSpan DefaultPrecision { get; set; } = TimeSpan.FromMilliseconds(10);
    }

    /// <summary>
    /// Logging configuration
    /// </summary>
    public class LoggingConfig
    {
        /// <summary>
        /// Enable layer and direction tags
        /// </summary>
        public bool EnableLayerTags { get; set; } = true;

        /// <summary>
        /// Enable frame/packet ID tags
        /// </summary>
        public bool EnableIdTags { get; set; } = true;

        /// <summary>
        /// Enable statistics logging
        /// </summary>
        public bool EnableStatisticsLogging { get; set; } = true;

        /// <summary>
        /// Statistics update interval
        /// </summary>
        public TimeSpan StatisticsUpdateInterval { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Comprehensive X.25 routing configuration based on Cisco router standards
    /// Supports multiple serial ports, XOT connections, and routing table
    /// </summary>
    public class X25RoutingConfig
    {
        /// <summary>
        /// Enable X.25 routing system
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Serial interface configurations (0-10 serial ports like Cisco routers)
        /// Each serial port can connect to a remote machine with known reachable hosts
        /// </summary>
        public List<SerialInterfaceConfig> SerialInterfaces { get; set; } = new List<SerialInterfaceConfig>();

        /// <summary>
        /// XOT (X.25 over TCP) interface configurations (0-10 XOT connections)
        /// Each XOT connection targets a remote X.25 system over IP
        /// </summary>
        public List<XotInterfaceConfig> XotInterfaces { get; set; } = new List<XotInterfaceConfig>();

        /// <summary>
        /// TCP-LAPB interface configurations - HDLC byte-stuffing with LAPB over TCP
        /// Each TCP-LAPB link has its own independent LAPB state machine
        /// </summary>
        public List<TcpLapbInterfaceConfig> TcpLapbInterfaces { get; set; } = new List<TcpLapbInterfaceConfig>();

        /// <summary>
        /// X.25 routing table entries - maps X.121 address patterns to interfaces/gateways
        /// Similar to Cisco "x25 route" command
        /// </summary>
        public List<X25RouteEntry> RoutingTable { get; set; } = new List<X25RouteEntry>();

        /// <summary>
        /// Default gateway machine ID for unmatched routes
        /// If a host is not directly reachable, route through this gateway
        /// </summary>
        public string? DefaultGateway { get; set; }

        /// <summary>
        /// Known host definitions - defines which machines are reachable through which interfaces
        /// Allows multi-hop routing (e.g., host 101 reachable via gateway 100 on Serial0)
        /// </summary>
        public List<KnownHostEntry> KnownHosts { get; set; } = new List<KnownHostEntry>();

        /// <summary>
        /// Enable automatic route failover
        /// </summary>
        public bool EnableFailover { get; set; } = true;

        /// <summary>
        /// Route failover timeout in milliseconds
        /// </summary>
        public int FailoverTimeout { get; set; } = 5000;

        /// <summary>
        /// PVC (Permanent Virtual Circuit) bindings
        /// </summary>
        public List<PvcBinding> PvcBindings { get; set; } = new List<PvcBinding>();
    }

    /// <summary>
    /// Serial interface configuration (based on Cisco serial interface settings)
    /// Equivalent to Cisco "interface Serial0" configuration
    /// </summary>
    public class SerialInterfaceConfig
    {
        /// <summary>
        /// Interface name (e.g., "Serial0", "Serial1")
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Enable this serial interface
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Serial port name (e.g., "COM1", "COM2", "/dev/ttyUSB0")
        /// </summary>
        public string PortName { get; set; } = "COM1";

        /// <summary>
        /// Baud rate (speed)
        /// </summary>
        public int BaudRate { get; set; } = 9600;

        /// <summary>
        /// Data bits
        /// </summary>
        public int DataBits { get; set; } = 8;

        /// <summary>
        /// Parity setting
        /// </summary>
        public System.IO.Ports.Parity Parity { get; set; } = System.IO.Ports.Parity.None;

        /// <summary>
        /// Stop bits
        /// </summary>
        public System.IO.Ports.StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;

        /// <summary>
        /// Remote machine ID directly connected via this serial interface
        /// Equivalent to remote X.25 switch/router ID
        /// </summary>
        public string RemoteMachineId { get; set; } = string.Empty;

        /// <summary>
        /// Description for this serial interface
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Enable LAPB auto-connect - automatically send SABM when interface is enabled
        /// When enabled, SABM is sent immediately upon interface activation
        /// </summary>
        public bool LapbAutoConnect { get; set; } = false;

        /// <summary>
        /// Protocol mode for this link - determines how traffic is interpreted
        /// X25Only: All traffic is X.25 packets
        /// SintranOnly: All traffic is SINTRAN frames
        /// AutoDetect: Detect per-frame based on 0x21 0x13 markers
        /// </summary>
        public ProtocolDetectionMode ProtocolMode { get; set; } = ProtocolDetectionMode.X25Only;

        /// <summary>
        /// LAPB window size (K) - number of outstanding unacknowledged frames
        /// </summary>
        public byte LapbWindowSize { get; set; } = 7;

        /// <summary>
        /// LAPB T1 timeout in milliseconds (response timeout)
        /// </summary>
        public int LapbT1TimeoutMs { get; set; } = 3000;

        /// <summary>
        /// LAPB T2 timeout in milliseconds (acknowledgment delay)
        /// </summary>
        public int LapbT2TimeoutMs { get; set; } = 1500;

        /// <summary>
        /// LAPB T3 timeout in milliseconds (idle supervision, 0 = disabled)
        /// </summary>
        public int LapbT3TimeoutMs { get; set; } = 0;

        /// <summary>
        /// LAPB maximum retries (N2)
        /// </summary>
        public byte LapbMaxRetries { get; set; } = 10;

        /// <summary>
        /// Whether this side is primary (provides T3 supervision)
        /// </summary>
        public bool IsPrimary { get; set; } = true;

        /// <summary>
        /// Byte-stuffed framing method for this serial link.
        /// Hdlc (RFC 1662) uses the classic serial link; Slip (RFC 1055) uses the codec-based link over
        /// this serial port. Both run LAPB.
        /// </summary>
        public FramingType Framing { get; set; } = FramingType.Hdlc;
    }

    /// <summary>
    /// TCP-LAPB interface configuration - HDLC byte-stuffing with LAPB over TCP
    /// Each TCP-LAPB link has its own independent LAPB state machine
    /// </summary>
    public class TcpLapbInterfaceConfig
    {
        /// <summary>
        /// Interface name (e.g., "TcpLapb0", "TcpLapb1")
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Enable this TCP-LAPB interface
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// TCP mode (Client or Server)
        /// </summary>
        public TcpMode Mode { get; set; } = TcpMode.Client;

        /// <summary>
        /// Remote IP address (for client mode)
        /// </summary>
        public string RemoteAddress { get; set; } = "127.0.0.1";

        /// <summary>
        /// TCP port (remote port for client, local port for server)
        /// </summary>
        public int Port { get; set; } = 2000;

        /// <summary>
        /// Remote machine ID directly connected via this interface
        /// </summary>
        public string RemoteMachineId { get; set; } = string.Empty;

        /// <summary>
        /// Description for this interface
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Connection timeout in milliseconds (for client mode)
        /// </summary>
        public int ConnectTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// Reconnect delay in milliseconds (0 = no auto-reconnect)
        /// </summary>
        public int ReconnectDelayMs { get; set; } = 5000;

        /// <summary>
        /// Protocol mode for this link
        /// </summary>
        public ProtocolDetectionMode ProtocolMode { get; set; } = ProtocolDetectionMode.X25Only;

        /// <summary>
        /// LAPB window size (K)
        /// </summary>
        public byte LapbWindowSize { get; set; } = 7;

        /// <summary>
        /// LAPB T1 timeout in milliseconds
        /// </summary>
        public int LapbT1TimeoutMs { get; set; } = 3000;

        /// <summary>
        /// LAPB T2 timeout in milliseconds
        /// </summary>
        public int LapbT2TimeoutMs { get; set; } = 1500;

        /// <summary>
        /// LAPB T3 timeout in milliseconds (0 = disabled)
        /// </summary>
        public int LapbT3TimeoutMs { get; set; } = 0;

        /// <summary>
        /// LAPB maximum retries (N2)
        /// </summary>
        public byte LapbMaxRetries { get; set; } = 10;

        /// <summary>
        /// Whether this side is primary
        /// </summary>
        public bool IsPrimary { get; set; } = true;

        /// <summary>
        /// Enable LAPB auto-connect
        /// </summary>
        public bool LapbAutoConnect { get; set; } = false;

        /// <summary>
        /// Byte-stuffed framing method for this TCP link.
        /// Hdlc (RFC 1662) uses the classic TCP-LAPB link; Slip (RFC 1055) uses the codec-based link over
        /// TCP. Both run LAPB.
        /// </summary>
        public FramingType Framing { get; set; } = FramingType.Hdlc;
    }


    /// <summary>
    /// X.25 routing table entry (equivalent to Cisco "x25 route" command)
    /// Maps X.121 address patterns to destination interfaces or gateways
    /// </summary>
    public class X25RouteEntry
    {
        /// <summary>
        /// Route name for identification
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// X.121 address pattern (regex pattern like "100.*" for addresses starting with 100)
        /// Equivalent to Cisco address pattern matching
        /// </summary>
        public string X121AddressPattern { get; set; } = string.Empty;

        /// <summary>
        /// Route type
        /// </summary>
        public RouteType Type { get; set; } = RouteType.Interface;

        /// <summary>
        /// Destination interface name (for Interface routes) or gateway machine ID (for Gateway routes)
        /// </summary>
        public string Destination { get; set; } = string.Empty;

        /// <summary>
        /// Route priority (higher = preferred, used for multiple matching routes)
        /// </summary>
        public int Priority { get; set; } = 100;

        /// <summary>
        /// Enable this route
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Optional call user data pattern for additional routing criteria
        /// </summary>
        public string? CallUserDataPattern { get; set; }

        /// <summary>
        /// Description for this route
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Route type enumeration
    /// </summary>
    public enum RouteType
    {
        /// <summary>
        /// Route directly to an interface (Serial0, XOT0, etc.)
        /// </summary>
        Interface,

        /// <summary>
        /// Route through a gateway machine (multi-hop routing)
        /// </summary>
        Gateway,

        /// <summary>
        /// Route to local machine (built-in TAD server)
        /// </summary>
        Local
    }

    /// <summary>
    /// Known host entry - defines which machines are reachable through interfaces/gateways
    /// Supports multi-hop routing scenarios
    /// </summary>
    public class KnownHostEntry
    {
        /// <summary>
        /// Friendly name for this host (for name-based connections)
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Host machine ID (X.121 address)
        /// </summary>
        public string MachineId { get; set; } = string.Empty;

        /// <summary>
        /// How this host is reachable
        /// </summary>
        public HostReachability Reachability { get; set; } = HostReachability.Direct;

        /// <summary>
        /// Interface name (for Direct reachability) or gateway machine ID (for Gateway reachability)
        /// </summary>
        public string Via { get; set; } = string.Empty;

        /// <summary>
        /// Description for this host entry
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Enable this host entry
        /// </summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Host reachability type
    /// </summary>
    public enum HostReachability
    {
        /// <summary>
        /// Host is directly reachable through an interface
        /// </summary>
        Direct,

        /// <summary>
        /// Host is reachable through a gateway
        /// </summary>
        Gateway
    }

    /// <summary>
    /// PVC (Permanent Virtual Circuit) binding configuration
    /// </summary>
    public class PvcBinding
    {
        /// <summary>
        /// Logical Channel Number (LCN) for this PVC
        /// </summary>
        public ushort Lcn { get; set; }

        /// <summary>
        /// Module name to bind to this PVC
        /// </summary>
        public string ModuleName { get; set; } = string.Empty;

        /// <summary>
        /// Configuration for the module
        /// </summary>
        public Dictionary<string, object> ModuleConfig { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// Description for this PVC binding
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// SINTRAN protocol configuration
    /// </summary>
    public class SintranConfig
    {
        /// <summary>
        /// Standard PVC definitions for SINTRAN protocols
        /// </summary>
        public List<PvcDefinition> StandardPvcs { get; set; } = new List<PvcDefinition>();

        /// <summary>
        /// Local SINTRAN system ID
        /// </summary>
        public string LocalSintranId { get; set; } = "200";

        /// <summary>
        /// Remote SINTRAN system ID
        /// </summary>
        public string RemoteSintranId { get; set; } = string.Empty;

        /// <summary>
        /// Automatically establish PVCs at startup
        /// </summary>
        public bool AutoEstablishPvcs { get; set; } = true;

        /// <summary>
        /// Enable SINTRAN routing protocol handler
        /// </summary>
        public bool EnableRoutingProtocol { get; set; } = true;

        /// <summary>
        /// Enable SINTRAN XMSG protocol handler
        /// </summary>
        public bool EnableXmsgProtocol { get; set; } = true;

        /// <summary>
        /// Enable SINTRAN file transfer protocol handler
        /// </summary>
        public bool EnableFileProtocol { get; set; } = true;

        /// <summary>
        /// PVC establishment timeout in milliseconds
        /// </summary>
        public int PvcEstablishmentTimeout { get; set; } = 5000;
    }

    /// <summary>
    /// PVC definition for SINTRAN protocols
    /// </summary>
    public class PvcDefinition
    {
        /// <summary>
        /// Logical Channel Number (LCN) for this PVC
        /// </summary>
        public ushort Lcn { get; set; }

        /// <summary>
        /// Protocol type (ROUTING, XMSG, FILE)
        /// </summary>
        public string Protocol { get; set; } = string.Empty;

        /// <summary>
        /// Called address for PVC establishment
        /// </summary>
        public string CalledAddress { get; set; } = string.Empty;

        /// <summary>
        /// Calling address for PVC establishment
        /// </summary>
        public string CallingAddress { get; set; } = string.Empty;

        /// <summary>
        /// Call User Data for service identification
        /// </summary>
        public byte[] CallUserData { get; set; } = new byte[0];

        /// <summary>
        /// Enable this PVC
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Description for this PVC definition
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

}
