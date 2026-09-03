using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Protocols
{
    /// <summary>
    /// Local XMSG Protocol Handler for COSMOS File Transfer Services
    /// Implements XMSG protocol for file transfer and remote procedure calls
    /// Based on SINTRAN/COSMOS architecture
    /// </summary>
    public class LocalXMSGProtocol
    {
        private readonly ILogger _logger;
        private readonly Dictionary<ushort, LocalXMSGSession> _sessions = new Dictionary<ushort, LocalXMSGSession>();
        private readonly object _sessionsLock = new object();
        private readonly string _localMachineId;
        private readonly XMSGServiceRegistry _serviceRegistry;

        public LocalXMSGProtocol(ILogger logger, string localMachineId)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _localMachineId = localMachineId ?? throw new ArgumentNullException(nameof(localMachineId));
            _serviceRegistry = new XMSGServiceRegistry(_logger);
            
            InitializeServices();
        }

        /// <summary>
        /// Get the local machine ID configured for this XMSG server
        /// </summary>
        public string LocalMachineId => _localMachineId;

        /// <summary>
        /// Get current active sessions count
        /// </summary>
        public int ActiveSessionsCount 
        {
            get
            {
                lock (_sessionsLock)
                {
                    return _sessions.Count;
                }
            }
        }

        /// <summary>
        /// Handle outgoing call request to local XMSG service
        /// </summary>
        public bool HandleOutgoingCall(ushort lcn, string calledAddress, string callingAddress, byte[]? userData, Action<X25PacketEventArgs> notifyClient)
        {
            if (calledAddress != _localMachineId)
                return false; // Not for local XMSG

            try
            {
                _logger.LogInformation("LocalXMSG: Processing XMSG call to {CalledAddress} on LCN {LCN}", calledAddress, lcn);

                // Parse XMSG header from CUD
                var xmsgRequest = ParseXMSGRequest(userData);
                if (xmsgRequest == null)
                {
                    _logger.LogWarning("LocalXMSG: Invalid XMSG request on LCN {LCN}", lcn);
                    return false;
                }

                // Create XMSG session
                var session = new LocalXMSGSession
                {
                    LCN = lcn,
                    CalledAddress = calledAddress,
                    CallingAddress = callingAddress,
                    StartTime = DateTime.UtcNow,
                    NotifyClient = notifyClient,
                    XMSGRequest = xmsgRequest,
                    State = XMSGSessionState.Connecting
                };

                lock (_sessionsLock)
                {
                    _sessions[lcn] = session;
                }

                // Accept the call and start XMSG service immediately
                _logger.LogDebug("LocalXMSG: Starting XMSG service for port {Port} on LCN {LCN}", xmsgRequest.Port, lcn);
                
                SendCallAccepted(session);
                StartXMSGService(session);

                _logger.LogInformation("LocalXMSG: XMSG service connection established on LCN {LCN} for port {Port}", lcn, xmsgRequest.Port);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalXMSG: Error handling XMSG call on LCN {LCN}", lcn);
                return false;
            }
        }

        /// <summary>
        /// Handle incoming data from XMSG client
        /// </summary>
        public bool HandleIncomingData(ushort lcn, byte[] data)
        {
            lock (_sessionsLock)
            {
                if (!_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogWarning("LocalXMSG: No session found for LCN {LCN}", lcn);
                    return false;
                }

                try
                {
                    _logger.LogDebug("LocalXMSG: Received {Length} bytes on LCN {LCN}", data.Length, lcn);
                    
                    // Process XMSG message
                    var xmsgMessage = ParseXMSGMessage(data);
                    if (xmsgMessage != null)
                    {
                        ProcessXMSGMessage(session, xmsgMessage);
                    }
                    else
                    {
                        _logger.LogWarning("LocalXMSG: Failed to parse XMSG message on LCN {LCN}", lcn);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LocalXMSG: Error processing incoming data on LCN {LCN}", lcn);
                    return false;
                }
            }
        }

        /// <summary>
        /// Handle call clearing
        /// </summary>
        public void HandleCallClearing(ushort lcn)
        {
            lock (_sessionsLock)
            {
                if (_sessions.TryGetValue(lcn, out var session))
                {
                    _logger.LogInformation("LocalXMSG: Clearing XMSG session on LCN {LCN}", lcn);
                    
                    session.State = XMSGSessionState.Disconnected;
                    
                    _sessions.Remove(lcn);
                }
            }
        }

        /// <summary>
        /// Initialize XMSG services
        /// </summary>
        private void InitializeServices()
        {
            // Register standard XMSG services
            _serviceRegistry.RegisterService(0x1000, "COSMOS File Service", HandleFileService);
            _serviceRegistry.RegisterService(0x1001, "Remote Procedure Service", HandleRPCService);
            _serviceRegistry.RegisterService(0x1002, "System Monitor", HandleSystemMonitor);
            
            _logger.LogInformation("LocalXMSG: Initialized {Count} XMSG services", _serviceRegistry.ServiceCount);
        }

        /// <summary>
        /// Parse XMSG request from Call User Data
        /// </summary>
        private XMSGRequest? ParseXMSGRequest(byte[]? userData)
        {
            if (userData == null || userData.Length < 12)
                return null;

            try
            {
                var request = new XMSGRequest
                {
                    Version = (ushort)((userData[0] << 8) | userData[1]),
                    MessageType = (ushort)((userData[2] << 8) | userData[3]),
                    Port = (ushort)((userData[4] << 8) | userData[5]),
                    MessageLength = (ushort)((userData[6] << 8) | userData[7]),
                    SessionId = (uint)((userData[8] << 24) | (userData[9] << 16) | (userData[10] << 8) | userData[11])
                };

                _logger.LogDebug("LocalXMSG: Parsed XMSG request - Version:{Version:X4} Type:{MessageType:X4} Port:{Port:X4} Session:{SessionId:X8}",
                    request.Version, request.MessageType, request.Port, request.SessionId);

                return request;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalXMSG: Error parsing XMSG request");
                return null;
            }
        }

        /// <summary>
        /// Parse XMSG message from data
        /// </summary>
        private XMSGMessage? ParseXMSGMessage(byte[] data)
        {
            if (data.Length < 12)
                return null;

            try
            {
                var message = new XMSGMessage
                {
                    Version = (ushort)((data[0] << 8) | data[1]),
                    MessageType = (ushort)((data[2] << 8) | data[3]),
                    Port = (ushort)((data[4] << 8) | data[5]),
                    MessageLength = (ushort)((data[6] << 8) | data[7]),
                    SessionId = (uint)((data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11]),
                    Data = new byte[data.Length - 12]
                };

                if (data.Length > 12)
                {
                    Array.Copy(data, 12, message.Data, 0, data.Length - 12);
                }

                return message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalXMSG: Error parsing XMSG message");
                return null;
            }
        }

        /// <summary>
        /// Send Call Accepted packet
        /// </summary>
        private void SendCallAccepted(LocalXMSGSession session)
        {
            try
            {
                // Create Call Accepted packet (PTI = 0x0F)
                var packet = new X25Packet
                {
                    GFI = 0x10, // Modulo 8
                    LCN = session.LCN,
                    PTI = 0x0F, // Call Accepted
                    Data = Array.Empty<byte>(),
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow
                };

                var eventArgs = new X25PacketEventArgs { Packet = packet, Direction = packet.Direction };
                session.NotifyClient(eventArgs);
                session.State = XMSGSessionState.Connected;

                _logger.LogInformation("LocalXMSG: Sent Call Accepted for XMSG service on LCN {LCN}", session.LCN);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalXMSG: Error sending Call Accepted on LCN {LCN}", session.LCN);
            }
        }

        /// <summary>
        /// Start XMSG service based on port
        /// </summary>
        private void StartXMSGService(LocalXMSGSession session)
        {
            try
            {
                var service = _serviceRegistry.GetService(session.XMSGRequest.Port);
                if (service != null)
                {
                    _logger.LogInformation("LocalXMSG: Starting service {ServiceName} on LCN {LCN}", service.Name, session.LCN);
                    service.Handler(session);
                }
                else
                {
                    _logger.LogWarning("LocalXMSG: No service registered for port {Port} on LCN {LCN}", session.XMSGRequest.Port, session.LCN);
                    SendXMSGError(session, "Service not available");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalXMSG: Error starting XMSG service on LCN {LCN}", session.LCN);
            }
        }

        /// <summary>
        /// Process XMSG message
        /// </summary>
        private void ProcessXMSGMessage(LocalXMSGSession session, XMSGMessage message)
        {
            var service = _serviceRegistry.GetService(message.Port);
            if (service != null)
            {
                service.Handler(session);
            }
            else
            {
                SendXMSGError(session, "Unknown service");
            }
        }

        /// <summary>
        /// Send XMSG error response
        /// </summary>
        private void SendXMSGError(LocalXMSGSession session, string errorMessage)
        {
            var errorData = CreateXMSGMessage(0x00FF, session.XMSGRequest.Port, session.XMSGRequest.SessionId, 
                Encoding.ASCII.GetBytes(errorMessage));
            SendXMSGData(session, errorData);
        }

        /// <summary>
        /// Send XMSG data to client
        /// </summary>
        private void SendXMSGData(LocalXMSGSession session, byte[] data)
        {
            try
            {
                var packet = new X25Packet
                {
                    GFI = 0x10,
                    LCN = session.LCN,
                    PTI = 0x00, // Data packet
                    Data = data,
                    Direction = PacketDirection.Transmit,
                    Timestamp = DateTime.UtcNow
                };

                var eventArgs = new X25PacketEventArgs { Packet = packet, Direction = packet.Direction };
                session.NotifyClient(eventArgs);

                _logger.LogDebug("LocalXMSG: Sent {Length} bytes to LCN {LCN}", data.Length, session.LCN);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LocalXMSG: Error sending XMSG data on LCN {LCN}", session.LCN);
            }
        }

        /// <summary>
        /// Create XMSG message
        /// </summary>
        private byte[] CreateXMSGMessage(ushort messageType, ushort port, uint sessionId, byte[] data)
        {
            var message = new byte[12 + data.Length];
            
            // XMSG Header
            message[0] = 0x4C; message[1] = 0x03; // Version L03
            message[2] = (byte)(messageType >> 8); message[3] = (byte)messageType;
            message[4] = (byte)(port >> 8); message[5] = (byte)port;
            message[6] = (byte)(data.Length >> 8); message[7] = (byte)data.Length;
            message[8] = (byte)(sessionId >> 24); message[9] = (byte)(sessionId >> 16);
            message[10] = (byte)(sessionId >> 8); message[11] = (byte)sessionId;
            
            // Data
            Array.Copy(data, 0, message, 12, data.Length);
            
            return message;
        }

        #region Service Handlers

        /// <summary>
        /// Handle COSMOS File Service (port 0x1000)
        /// </summary>
        private void HandleFileService(LocalXMSGSession session)
        {
            var welcomeMessage = $"COSMOS File Service v1.0\r\nSystem: {_localMachineId}\r\nSession: {session.XMSGRequest.SessionId:X8}\r\n";
            var data = CreateXMSGMessage(0x0002, 0x1000, session.XMSGRequest.SessionId, Encoding.ASCII.GetBytes(welcomeMessage));
            SendXMSGData(session, data);
        }

        /// <summary>
        /// Handle Remote Procedure Service (port 0x1001)
        /// </summary>
        private void HandleRPCService(LocalXMSGSession session)
        {
            var welcomeMessage = $"Remote Procedure Service v1.0\r\nReady for RPC calls\r\n";
            var data = CreateXMSGMessage(0x0002, 0x1001, session.XMSGRequest.SessionId, Encoding.ASCII.GetBytes(welcomeMessage));
            SendXMSGData(session, data);
        }

        /// <summary>
        /// Handle System Monitor Service (port 0x1002)
        /// </summary>
        private void HandleSystemMonitor(LocalXMSGSession session)
        {
            var statusMessage = $"System Monitor v1.0\r\nSystem: {_localMachineId}\r\nActive Sessions: {ActiveSessionsCount}\r\nUptime: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\r\n";
            var data = CreateXMSGMessage(0x0002, 0x1002, session.XMSGRequest.SessionId, Encoding.ASCII.GetBytes(statusMessage));
            SendXMSGData(session, data);
        }

        #endregion

        /// <summary>
        /// Get current session information
        /// </summary>
        public List<XMSGSessionInfo> GetSessionInfo()
        {
            var sessions = new List<XMSGSessionInfo>();
            
            lock (_sessionsLock)
            {
                foreach (var session in _sessions.Values)
                {
                    sessions.Add(new XMSGSessionInfo
                    {
                        LCN = session.LCN,
                        CalledAddress = session.CalledAddress,
                        CallingAddress = session.CallingAddress,
                        Port = session.XMSGRequest.Port,
                        SessionId = session.XMSGRequest.SessionId,
                        State = session.State,
                        StartTime = session.StartTime,
                        ServiceName = _serviceRegistry.GetService(session.XMSGRequest.Port)?.Name ?? "Unknown"
                    });
                }
            }
            
            return sessions;
        }
    }

    /// <summary>
    /// XMSG Session state
    /// </summary>
    public enum XMSGSessionState
    {
        Connecting,
        Connected,
        DataTransfer,
        Disconnected
    }

    /// <summary>
    /// Local XMSG Session
    /// </summary>
    public class LocalXMSGSession
    {
        public ushort LCN { get; set; }
        public string CalledAddress { get; set; } = "";
        public string CallingAddress { get; set; } = "";
        public DateTime StartTime { get; set; }
        public XMSGSessionState State { get; set; }
        public Action<X25PacketEventArgs> NotifyClient { get; set; } = null!;
        public XMSGRequest XMSGRequest { get; set; } = null!;
    }

    /// <summary>
    /// XMSG Request structure
    /// </summary>
    public class XMSGRequest
    {
        public ushort Version { get; set; }
        public ushort MessageType { get; set; }
        public ushort Port { get; set; }
        public ushort MessageLength { get; set; }
        public uint SessionId { get; set; }
    }

    /// <summary>
    /// XMSG Message structure
    /// </summary>
    public class XMSGMessage
    {
        public ushort Version { get; set; }
        public ushort MessageType { get; set; }
        public ushort Port { get; set; }
        public ushort MessageLength { get; set; }
        public uint SessionId { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// XMSG Session information for UI
    /// </summary>
    public class XMSGSessionInfo
    {
        public ushort LCN { get; set; }
        public string CalledAddress { get; set; } = "";
        public string CallingAddress { get; set; } = "";
        public ushort Port { get; set; }
        public uint SessionId { get; set; }
        public XMSGSessionState State { get; set; }
        public DateTime StartTime { get; set; }
        public string ServiceName { get; set; } = "";
        
        public TimeSpan Duration => DateTime.UtcNow - StartTime;
        
        public string GetPortDescription()
        {
            return Port switch
            {
                0x1000 => "COSMOS File Service",
                0x1001 => "Remote Procedure Service",
                0x1002 => "System Monitor",
                >= 0x1003 and <= 0x10FF => "User Service",
                >= 0x1100 and <= 0x1FFF => "Reserved Service",
                _ => "Unknown Service"
            };
        }
    }

    /// <summary>
    /// XMSG Service Registry
    /// </summary>
    public class XMSGServiceRegistry
    {
        private readonly Dictionary<ushort, XMSGService> _services = new();
        private readonly ILogger _logger;

        public XMSGServiceRegistry(ILogger logger)
        {
            _logger = logger;
        }

        public void RegisterService(ushort port, string name, Action<LocalXMSGSession> handler)
        {
            _services[port] = new XMSGService { Port = port, Name = name, Handler = handler };
            _logger.LogDebug("XMSG: Registered service {Name} on port {Port:X4}", name, port);
        }

        public XMSGService? GetService(ushort port)
        {
            return _services.TryGetValue(port, out var service) ? service : null;
        }

        public int ServiceCount => _services.Count;
    }

    /// <summary>
    /// XMSG Service definition
    /// </summary>
    public class XMSGService
    {
        public ushort Port { get; set; }
        public string Name { get; set; } = "";
        public Action<LocalXMSGSession> Handler { get; set; } = null!;
    }
}