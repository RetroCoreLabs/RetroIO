//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;
using RetroIO.Protocols.X25.Runtime.Models;
using RetroIO.Protocols.X25.Sintran;
using RetroIO.Link.Physical;

using RetroIO.Link.Lapb;
using RetroIO.Protocols.X25.Layers;
namespace RetroIO.Protocols.X25.Runtime.Layers
{
    /// <summary>
    /// Physical Layer that owns serial port, TCP connections, and XOT transport
    /// Provides synchronous, thread-based transport without async/await
    /// Routes calls based on address: 100→Serial, 300→TCP, 200→LocalTAD
    /// </summary>
    public class PhysicalLayer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private readonly string _localMachineId;
        
        // Serial transport - support multiple serial interfaces
        private readonly Dictionary<string, Serial> _serialPorts = new();
        private readonly Dictionary<string, bool> _serialPortStatus = new();
        private readonly Dictionary<Serial, string> _serialToRemoteMachineId = new();
        
        // Mock serial support for testing (MOCK1-MOCK9)
        private readonly Dictionary<int, Queue<byte[]>> _mockSerialQueues = new();
        private readonly object _mockLock = new object();
        
        // TCP server for inbound XOT connections
        private TcpListener? _tcpListener;
        private Thread? _tcpListenerThread;
        private readonly List<TcpClient> _tcpClients = new();
        private readonly Dictionary<TcpClient, Thread> _tcpClientThreads = new();
        private bool _tcpServerActive = false;
        
        // TCP client for outbound XOT connections
        private readonly Dictionary<string, TcpClient> _outboundConnections = new();
        private readonly Dictionary<TcpClient, string> _tcpClientToRemoteMachineId = new();
        
        // Configuration
        public PhysicalLayerConfig Config { get; private set; }
        
        // Events for incoming data and calls
        public event EventHandler<PhysicalLayerDataEventArgs>? DataReceived;
#pragma warning disable CS0067 // Event for future incoming call support
        public event EventHandler<PhysicalLayerCallEventArgs>? IncomingCall;
#pragma warning restore CS0067
        public event EventHandler<PhysicalLayerStatusEventArgs>? StatusChanged;
        
        // Mock serial events for testing
        public event EventHandler<MockSerialEventArgs>? MockSerialTransmit;
        public event EventHandler<MockSerialEventArgs>? MockSerialReceive;
        
        // Statistics
        private PhysicalLayerStatistics _statistics = new();
        
        public PhysicalLayer(ILogger logger, PhysicalLayerConfig config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _localMachineId = config.LocalMachineId ?? throw new ArgumentException("LocalMachineId must be configured in PhysicalLayerConfig");
        }
        
        /// <summary>
        /// Initialize multiple serial ports from configuration
        /// </summary>
        public void InitializeSerialPorts(IEnumerable<SerialInterfaceConfig> serialInterfaces)
        {
            foreach (var serialConfig in serialInterfaces)
            {
                if (!serialConfig.Enabled)
                {
                    _logger.LogDebug("Skipping disabled serial interface: {Name}", serialConfig.Name);
                    continue;
                }

                try
                {
                    var config = new SerialConfig
                    {
                        PortName = serialConfig.PortName,
                        BaudRate = serialConfig.BaudRate,
                        DataBits = serialConfig.DataBits,
                        Parity = serialConfig.Parity,
                        StopBits = serialConfig.StopBits,
                        DtrEnable = true,
                        RtsEnable = true
                    };

                    var serial = new Serial(_logger, config, Config.TestMode);
                    
                    // Subscribe to events
                    serial.DataReceived += OnSerialDataReceived;
                    serial.StatusChanged += OnSerialStatusChanged;
                    
                    _serialPorts[serialConfig.Name] = serial;
                    _serialPortStatus[serialConfig.Name] = false;
                    _serialToRemoteMachineId[serial] = serialConfig.RemoteMachineId;
                    
                    _logger.LogInformation("Initialized serial interface: {Name} on {Port}", 
                                         serialConfig.Name, serialConfig.PortName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize serial interface: {Name} on {Port}", 
                                   serialConfig.Name, serialConfig.PortName);
                }
            }
        }
        
        /// <summary>
        /// Start all configured serial ports
        /// </summary>
        private bool StartAllSerialPorts()
        {
            bool allSuccess = true;
            
            foreach (var kvp in _serialPorts)
            {
                string interfaceName = kvp.Key;
                Serial serial = kvp.Value;
                
                try
                {
                    bool started = false;
                    
                    if (Config.TestMode)
                    {
                        // In test mode, don't try to start real hardware
                        _serialPortStatus[interfaceName] = true;
                        _logger.LogInformation("Started serial interface in test mode: {Name}", interfaceName);
                        started = true;
                    }
                    else
                    {
                        started = serial.Start();
                    }
                    
                    if (started)
                    {
                        _serialPortStatus[interfaceName] = true;
                        _logger.LogInformation("Started serial interface: {Name}", interfaceName);
                    }
                    else
                    {
                        _serialPortStatus[interfaceName] = false;
                        _logger.LogError("Failed to start serial interface: {Name}", interfaceName);
                        allSuccess = false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception starting serial interface: {Name}", interfaceName);
                    _serialPortStatus[interfaceName] = false;
                    allSuccess = false;
                }
            }
            
            return allSuccess;
        }
        
        /// <summary>
        /// Stop all serial ports
        /// </summary>
        private void StopAllSerialPorts()
        {
            foreach (var kvp in _serialPorts)
            {
                string interfaceName = kvp.Key;
                Serial serial = kvp.Value;
                
                try
                {
                    serial.Stop();
                    _serialPortStatus[interfaceName] = false;
                    _serialToRemoteMachineId.Remove(serial);
                    _logger.LogInformation("Stopped serial interface: {Name}", interfaceName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception stopping serial interface: {Name}", interfaceName);
                }
            }
        }
        
        /// <summary>
        /// Send data to specific serial interface
        /// </summary>
        public bool SendToSerialInterface(string interfaceName, byte[] data)
        {
            if (!_serialPorts.TryGetValue(interfaceName, out var serial))
            {
                _logger.LogWarning("Serial interface not found: {Name}", interfaceName);
                return false;
            }
            
            if (!_serialPortStatus.TryGetValue(interfaceName, out var isActive) || !isActive)
            {
                _logger.LogWarning("Serial interface not active: {Name}", interfaceName);
                return false;
            }
            
            if (Config.TestMode)
            {
                // Check if this is a MOCK port - get the port name from the serial config
                string portName = GetPortNameForInterface(interfaceName);
                int mockPort = GetMockPortNumber(portName);
                
                if (mockPort > 0)
                {
                    // This is a MOCK port - fire transmit event for test capture
                    _logger.LogDebug("Mock serial transmit to {PortName}: {ByteCount} bytes", portName, data.Length);
                    
                    // Fire the mock transmit event so tests can capture the data
                    MockSerialTransmit?.Invoke(this, new MockSerialEventArgs(mockPort, portName, data));
                    
                    _statistics.SerialBytesTransmitted += data.Length;
                    return true;
                }
                else
                {
                    // Regular test mode simulation for non-MOCK ports
                    _logger.LogDebug("Simulated send to serial interface {Name}: {ByteCount} bytes", interfaceName, data.Length);
                    _statistics.SerialBytesTransmitted += data.Length;
                    return true;
                }
            }
            
            return serial.SendData(data);
        }
        
        /// <summary>
        /// Check if serial interface is active
        /// </summary>
        public bool IsSerialInterfaceActive(string interfaceName)
        {
            return _serialPortStatus.TryGetValue(interfaceName, out var isActive) && isActive;
        }
        
        /// <summary>
        /// Start all configured transport interfaces
        /// </summary>
        public bool Start()
        {
            _logger.LogInformation("Starting Physical Layer with Serial={SerialEnabled}, TCP={TcpEnabled}", 
                                 Config.SerialEnabled, Config.TcpServerEnabled);
            
            var success = true;
            
            try
            {
                // Start all configured serial ports
                success &= StartAllSerialPorts();
                
                // Start TCP server if enabled
                if (Config.TcpServerEnabled && Config.TcpListenerPort > 0)
                {
                    success &= StartTcpServer();
                }
                
                _statistics.StartTime = DateTime.UtcNow;
                
                if (success)
                {
                    NotifyStatusChanged("Started", "Physical layer initialized successfully");
                }
                
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Physical Layer");
                return false;
            }
        }
        
        /// <summary>
        /// Stop all transport interfaces
        /// </summary>
        public void Stop()
        {
            _logger.LogInformation("Stopping Physical Layer");
            
            StopAllSerialPorts();
            StopTcpServer();
            StopOutboundConnections();
            
            NotifyStatusChanged("Stopped", "Physical layer stopped");
        }
        
        /// <summary>
        /// Route outbound call based on routing decision from ProtocolRuntime
        /// This method now delegates routing decisions to the centralized routing system
        /// </summary>
        public bool RouteOutboundCall(string calledAddress, string callingAddress, byte[] callData)
        {
            _logger.LogInformation("PhysicalLayer: Processing outbound call to {CalledAddress} from {CallingAddress}", 
                                 calledAddress, callingAddress);
            
            // The actual routing decision should be made by ProtocolRuntime's centralized routing
            // This method is now primarily responsible for physical transport
            // The ProtocolRuntime will determine which interface (Serial, XOT, etc.) to use
            
            _logger.LogDebug("PhysicalLayer: Ready to route call via configured transport interface");
            return false; // Return false to indicate routing should be handled by ProtocolRuntime
        }
        
        /// <summary>
        /// Send data to specific interface based on routing decision
        /// </summary>
        public bool SendToInterface(string interfaceName, byte[] callData)
        {
            _logger.LogInformation("PhysicalLayer: Sending to interface {Interface}", interfaceName);
            
            try
            {
                if (interfaceName.StartsWith("Serial"))
                {
                    return SendToSerialInterface(interfaceName, callData);
                }
                else if (interfaceName.StartsWith("XOT"))
                {
                    // For XOT interfaces, extract the remote address from configuration
                    return SendToTcp("", callData); // Address will be determined from XOT interface config
                }
                else
                {
                    _logger.LogWarning("PhysicalLayer: Unknown interface type {Interface}", interfaceName);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PhysicalLayer: Error sending to interface {Interface}", interfaceName);
                return false;
            }
        }
        
        
        /// <summary>
        /// Send data to TCP connection (XOT)
        /// </summary>
        private bool SendToTcp(string address, byte[] data)
        {
            try
            {
                // For simplicity, use a fixed remote host for address 300x
                var remoteHost = Config.TcpRemoteHost ?? "127.0.0.1";
                var remotePort = Config.TcpRemotePort;
                
                var connectionKey = $"{remoteHost}:{remotePort}";
                
                // Get or create outbound connection
                if (!_outboundConnections.TryGetValue(connectionKey, out var tcpClient) || !tcpClient.Connected)
                {
                    tcpClient = new TcpClient();
                    tcpClient.Connect(remoteHost, remotePort);
                    _outboundConnections[connectionKey] = tcpClient;
                    
                    _logger.LogInformation("PhysicalLayer: Established TCP connection to {RemoteHost}:{RemotePort}", 
                                         remoteHost, remotePort);
                }
                
                // Send XOT framed data (simplified - just send raw data for now)
                var stream = tcpClient.GetStream();
                stream.Write(data, 0, data.Length);
                stream.Flush();
                
                _statistics.TcpBytesTransmitted += data.Length;
                _logger.LogDebug("PhysicalLayer: Sent {ByteCount} bytes to TCP {Address}", data.Length, address);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PhysicalLayer: Failed to send data to TCP for address {Address}", address);
                _statistics.TcpErrors++;
                return false;
            }
        }
        
        
        
        /// <summary>
        /// Handle data received from serial transport
        /// </summary>
        private void OnSerialDataReceived(object? sender, SerialDataEventArgs e)
        {
            try
            {
                _statistics.SerialBytesReceived += e.Data.Length;
                
                // Get the remote machine ID for this serial interface
                string remoteMachineId = "UNKNOWN";
                if (sender is Serial serial && _serialToRemoteMachineId.TryGetValue(serial, out string? configuredId))
                {
                    remoteMachineId = configuredId;
                }
                
                // Notify data received from serial transport
                NotifyDataReceived(remoteMachineId, PhysicalTransportType.Serial, e.Data);
                
                _logger.LogDebug("PhysicalLayer: Received {ByteCount} bytes from serial transport", e.Data.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PhysicalLayer: Error handling serial data");
                _statistics.SerialErrors++;
            }
        }
        
        /// <summary>
        /// Handle serial transport status changes
        /// </summary>
        private void OnSerialStatusChanged(object? sender, SerialStatusEventArgs e)
        {
            try
            {
                _logger.LogInformation("PhysicalLayer: Serial status changed to {Status}", e.Status);
                
                if (e.Error != null)
                {
                    _logger.LogError(e.Error, "PhysicalLayer: Serial transport error");
                    _statistics.SerialErrors++;
                }
                
                // Serial status is now managed per interface in _serialPortStatus
                // Individual serial port status changes are tracked separately
                
                // Notify status change
                var statusEventArgs = new PhysicalLayerStatusEventArgs();
                statusEventArgs.TransportType = "Serial";
                statusEventArgs.Status = e.Status;
                statusEventArgs.Error = e.Error;
                statusEventArgs.Timestamp = e.Timestamp;
                StatusChanged?.Invoke(this, statusEventArgs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PhysicalLayer: Error handling serial status change");
            }
        }
        
        /// <summary>
        /// Start TCP server for inbound XOT connections
        /// </summary>
        private bool StartTcpServer()
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, Config.TcpListenerPort);
                _tcpListener.Start();
                _tcpServerActive = true;
                
                // Start listener thread
                _tcpListenerThread = new Thread(TcpListenerLoop)
                {
                    Name = "PhysicalLayer-TCPListener",
                    IsBackground = true
                };
                _tcpListenerThread.Start();
                
                _logger.LogInformation("PhysicalLayer: TCP server listening on port {Port}", Config.TcpListenerPort);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PhysicalLayer: Failed to start TCP server on port {Port}", Config.TcpListenerPort);
                return false;
            }
        }
        
        /// <summary>
        /// Stop TCP server
        /// </summary>
        private void StopTcpServer()
        {
            _tcpServerActive = false;
            
            try
            {
                _tcpListener?.Stop();
            }
            catch { }
            
            _tcpListener = null;
            
            // Stop all client threads
            lock (_lock)
            {
                // Copy to array without LINQ for safe iteration
                var clientsCopy = new TcpClient[_tcpClients.Count];
                _tcpClients.CopyTo(clientsCopy, 0);
                foreach (var client in clientsCopy)
                {
                    client.Close();
                }
                _tcpClients.Clear();
                
                foreach (var thread in _tcpClientThreads.Values)
                {
                    thread.Join(1000);
                }
                _tcpClientThreads.Clear();
            }
            
            _tcpListenerThread?.Join(1000);
            _tcpListenerThread = null;
            
            _logger.LogInformation("PhysicalLayer: TCP server stopped");
        }
        
        /// <summary>
        /// Stop all outbound TCP connections
        /// </summary>
        private void StopOutboundConnections()
        {
            foreach (var connection in _outboundConnections.Values)
            {
                try
                {
                    connection.Close();
                }
                catch { }
            }
            _outboundConnections.Clear();
        }
        
        /// <summary>
        /// TCP listener thread
        /// </summary>
        private void TcpListenerLoop()
        {
            while (_tcpServerActive && _tcpListener != null)
            {
                try
                {
                    var tcpClient = _tcpListener.AcceptTcpClient();
                    
                    lock (_lock)
                    {
                        _tcpClients.Add(tcpClient);
                        
                        // Start client handler thread
                        var clientThread = new Thread(() => TcpClientHandler(tcpClient))
                        {
                            Name = $"PhysicalLayer-TCPClient-{tcpClient.GetHashCode()}",
                            IsBackground = true
                        };
                        
                        _tcpClientThreads[tcpClient] = clientThread;
                        clientThread.Start();
                    }
                    
                    _logger.LogInformation("PhysicalLayer: TCP client connected from {RemoteEndpoint}", 
                                         tcpClient.Client.RemoteEndPoint);
                }
                catch (Exception ex)
                {
                    if (_tcpServerActive)
                    {
                        _logger.LogError(ex, "PhysicalLayer: TCP listener error");
                        _statistics.TcpErrors++;
                    }
                    Thread.Sleep(100);
                }
            }
        }
        
        /// <summary>
        /// Handle TCP client connection
        /// </summary>
        private void TcpClientHandler(TcpClient tcpClient)
        {
            var buffer = new byte[4096];
            
            try
            {
                var stream = tcpClient.GetStream();
                
                while (tcpClient.Connected && _tcpServerActive)
                {
                    var bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        var data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);
                        
                        _statistics.TcpBytesReceived += bytesRead;
                        
                        // Get the remote machine ID for this TCP client (if known)
                        string remoteMachineId = "UNKNOWN";
                        if (_tcpClientToRemoteMachineId.TryGetValue(tcpClient, out string? configuredId))
                        {
                            remoteMachineId = configuredId;
                        }
                        
                        // Notify data received from TCP transport
                        NotifyDataReceived(remoteMachineId, PhysicalTransportType.XOT_Client, data);
                    }
                    else
                    {
                        break; // Connection closed
                    }
                }
            }
            catch (Exception ex)
            {
                if (_tcpServerActive)
                {
                    _logger.LogError(ex, "PhysicalLayer: TCP client handler error");
                    _statistics.TcpErrors++;
                }
            }
            finally
            {
                lock (_lock)
                {
                    _tcpClients.Remove(tcpClient);
                    _tcpClientThreads.Remove(tcpClient);
                    _tcpClientToRemoteMachineId.Remove(tcpClient);
                }
                
                tcpClient.Close();
                
                _logger.LogInformation("PhysicalLayer: TCP client disconnected");
            }
        }
        
        /// <summary>
        /// Notify data received event
        /// </summary>
        private void NotifyDataReceived(string sourceAddress, PhysicalTransportType transportType, byte[] data)
        {
            try
            {
                DataReceived?.Invoke(this, new PhysicalLayerDataEventArgs
                {
                    SourceAddress = sourceAddress,
                    TransportType = transportType,
                    Data = data,
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PhysicalLayer: Error in data received event handler");
            }
        }
        
        /// <summary>
        /// Notify status changed event
        /// </summary>
        private void NotifyStatusChanged(string status, string reason)
        {
            try
            {
                var statusEventArgs = new PhysicalLayerStatusEventArgs();
                statusEventArgs.TransportType = "PhysicalLayer";
                statusEventArgs.Status = $"{status}: {reason}";
                statusEventArgs.Error = null;
                statusEventArgs.Timestamp = DateTime.UtcNow;
                StatusChanged?.Invoke(this, statusEventArgs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PhysicalLayer: Error in status changed event handler");
            }
        }
        
        /// <summary>
        /// Get mock serial port for testing (test mode only)
        /// </summary>
        public Serial? GetMockSerial(string interfaceName)
        {
            if (!Config.TestMode)
                return null;
                
            return _serialPorts.TryGetValue(interfaceName, out var serial) ? serial : null;
        }
        
        /// <summary>
        /// Get current statistics
        /// </summary>
        public PhysicalLayerStatistics GetStatistics()
        {
            lock (_lock)
            {
                _statistics.ActiveTcpConnections = _tcpClients.Count;
                // Check if any serial port is active (no LINQ)
                bool anySerialActive = false;
                foreach (var serial in _serialPorts.Values)
                {
                    if (serial.IsActive)
                    {
                        anySerialActive = true;
                        break;
                    }
                }
                _statistics.SerialActive = anySerialActive;
                _statistics.TcpServerActive = _tcpServerActive;
                return _statistics;
            }
        }
        
        /// <summary>
        /// Set remote machine ID for a TCP client (when known from XOT handshake or configuration)
        /// </summary>
        public void SetTcpClientRemoteMachineId(TcpClient tcpClient, string remoteMachineId)
        {
            if (tcpClient != null && !string.IsNullOrEmpty(remoteMachineId))
            {
                _tcpClientToRemoteMachineId[tcpClient] = remoteMachineId;
                _logger.LogDebug("PhysicalLayer: Set remote machine ID '{RemoteMachineId}' for TCP client", remoteMachineId);
            }
        }
        
        #region Mock Serial Port Support
        
        /// <summary>
        /// Inject mock data into a MOCK serial port for testing
        /// </summary>
        /// <param name="mockPort">Mock port number (1-9)</param>
        /// <param name="data">Data to inject</param>
        public void InjectMockSerialData(int mockPort, byte[] data)
        {
            if (mockPort < 1 || mockPort > 9)
            {
                _logger.LogError("Invalid mock port number: {MockPort}. Must be 1-9.", mockPort);
                return;
            }
            
            if (data == null || data.Length == 0)
            {
                _logger.LogWarning("No data to inject into MOCK{MockPort}", mockPort);
                return;
            }
            
            string portName = $"MOCK{mockPort}";
            
            lock (_mockLock)
            {
                if (!_mockSerialQueues.TryGetValue(mockPort, out var queue))
                {
                    queue = new Queue<byte[]>();
                    _mockSerialQueues[mockPort] = queue;
                }
                
                // Copy data to prevent external modification
                var dataCopy = new byte[data.Length];
                Array.Copy(data, dataCopy, data.Length);
                queue.Enqueue(dataCopy);
                
                _logger.LogDebug("Injected {ByteCount} bytes into {PortName}", data.Length, portName);
            }
            
            // Fire receive event
            MockSerialReceive?.Invoke(this, new MockSerialEventArgs(mockPort, portName, data));
            
            // Process the injected data through the protocol stack
            ProcessMockSerialData(mockPort, data);
        }
        
        /// <summary>
        /// Check if a port name is a MOCK port (MOCK1-MOCK9)
        /// </summary>
        /// <param name="portName">Port name to check</param>
        /// <returns>Mock port number if valid, 0 if not a mock port</returns>
        private int GetMockPortNumber(string portName)
        {
            if (string.IsNullOrEmpty(portName) || !portName.StartsWith("MOCK") || portName.Length != 5)
                return 0;
                
            if (char.IsDigit(portName[4]) && portName[4] >= '1' && portName[4] <= '9')
                return int.Parse(portName.Substring(4, 1));
                
            return 0;
        }
        
        /// <summary>
        /// Get the port name for a given interface name by looking up the serial configuration
        /// </summary>
        /// <param name="interfaceName">Interface name (e.g., "Serial0")</param>
        /// <returns>Port name (e.g., "MOCK1", "COM1") or empty string if not found</returns>
        private string GetPortNameForInterface(string interfaceName)
        {
            // Look up the interface in our serial ports to get the port name
            if (_serialPorts.TryGetValue(interfaceName, out var serial))
            {
                // Get the port name from the serial object's configuration
                return serial.Config?.PortName ?? string.Empty;
            }
            
            return string.Empty;
        }
        
        /// <summary>
        /// Process mock serial data through the protocol stack
        /// </summary>
        private void ProcessMockSerialData(int mockPort, byte[] data)
        {
            try
            {
                // Forward data to the protocol stack as if it came from a real serial port
                string interfaceName = $"MOCK{mockPort}";
                
                // Create event args for received data
                var eventArgs = new PhysicalLayerDataEventArgs
                {
                    SourceAddress = interfaceName,
                    TransportType = PhysicalTransportType.Serial,
                    Data = data,
                    Timestamp = DateTime.UtcNow
                };
                
                // Fire the DataReceived event to trigger protocol processing
                DataReceived?.Invoke(this, eventArgs);
                
                _logger.LogDebug("Processed {ByteCount} bytes from {InterfaceName} through protocol stack", 
                               data.Length, interfaceName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing mock serial data from MOCK{MockPort}", mockPort);
            }
        }
        
        #endregion
        
        public void Dispose()
        {
            Stop();
        }
    }
    
    /// <summary>
    /// Physical layer configuration
    /// </summary>
    public class PhysicalLayerConfig
    {
        /// <summary>
        /// Local machine ID (X.121 address) - REQUIRED from centralized configuration
        /// </summary>
        public string? LocalMachineId { get; set; }
        
        /// <summary>
        /// Test mode - allows serial interfaces to be active without real hardware
        /// </summary>
        public bool TestMode { get; set; } = false;
        
        // Serial configuration
        public bool SerialEnabled { get; set; } = false;
        public string? SerialPortName { get; set; } = "COM1";
        public int SerialBaudRate { get; set; } = 9600;
        public int SerialDataBits { get; set; } = 8;
        public Parity SerialParity { get; set; } = Parity.None;
        public StopBits SerialStopBits { get; set; } = StopBits.One;
        
        // TCP server configuration
        public bool TcpServerEnabled { get; set; } = false;
        public int TcpListenerPort { get; set; } = 1998; // Standard XOT port
        
        // TCP client configuration  
        public string? TcpRemoteHost { get; set; } = "127.0.0.1";
        public int TcpRemotePort { get; set; } = 1998;
    }
    
    /// <summary>
    /// Physical layer statistics
    /// </summary>
    public class PhysicalLayerStatistics
    {
        public DateTime StartTime { get; set; }
        public TimeSpan Uptime => DateTime.UtcNow - StartTime;
        
        // Serial statistics
        public bool SerialActive { get; set; }
        public long SerialBytesTransmitted { get; set; }
        public long SerialBytesReceived { get; set; }
        public int SerialErrors { get; set; }
        
        // TCP statistics
        public bool TcpServerActive { get; set; }
        public int ActiveTcpConnections { get; set; }
        public long TcpBytesTransmitted { get; set; }
        public long TcpBytesReceived { get; set; }
        public int TcpErrors { get; set; }
        
        public string GetSummary()
        {
            return $"PhysicalLayer: Serial={SerialActive} TCP={TcpServerActive} Connections={ActiveTcpConnections} " +
                   $"TX={SerialBytesTransmitted + TcpBytesTransmitted} RX={SerialBytesReceived + TcpBytesReceived}";
        }
    }
    
    /// <summary>
    /// Event args for mock serial port events
    /// </summary>
    public class MockSerialEventArgs : EventArgs
    {
        /// <summary>
        /// Mock port number (1-9)
        /// </summary>
        public int MockPort { get; set; }
        
        /// <summary>
        /// Port name (e.g., "MOCK1")
        /// </summary>
        public string PortName { get; set; } = string.Empty;
        
        /// <summary>
        /// Data being transmitted/received
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();
        
        /// <summary>
        /// Timestamp of the event
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        public MockSerialEventArgs(int mockPort, string portName, byte[] data)
        {
            MockPort = mockPort;
            PortName = portName;
            Data = data ?? Array.Empty<byte>();
            Timestamp = DateTime.UtcNow;
        }
    }
}