//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RetroIO.Protocols.X25.Protocols;

namespace RetroIO.Protocols.X25.Services
{
    /// <summary>
    /// XMSG Protocol Service for COSMOS file transfer and remote procedure calls
    /// Handles X.25 connections with XMSG protocol CUD patterns
    /// </summary>
    public class XmsgProtocolService : IProtocolService
    {
        private readonly ILogger _logger;
        private readonly LocalXMSGProtocol _xmsgProtocol;
        private readonly string[] _localAddresses;
        
        /// <summary>
        /// Creates the XMSG service.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <param name="xmsgProtocol">
        /// The local XMSG protocol handler.
        /// </param>
        /// <param name="localAddresses">
        /// The local addresses this service handles; at least one is required.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when any argument is null.
        /// </exception>
        /// <exception cref="System.ArgumentException">
        /// Thrown when <paramref name="localAddresses"/> is empty.
        /// </exception>
        public XmsgProtocolService(ILogger logger, LocalXMSGProtocol xmsgProtocol, params string[] localAddresses)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _xmsgProtocol = xmsgProtocol ?? throw new ArgumentNullException(nameof(xmsgProtocol));
            _localAddresses = localAddresses ?? throw new ArgumentNullException(nameof(localAddresses));
            
            if (_localAddresses.Length == 0)
                throw new ArgumentException("At least one local address must be specified", nameof(localAddresses));
                
            _logger.LogInformation("XMSG Protocol Service initialized for addresses: [{Addresses}]", 
                string.Join(", ", _localAddresses));
        }
        
        /// <inheritdoc/>
        public string ServiceName => "XMSG";
        /// <inheritdoc/>
        public string[] LocalAddresses => _localAddresses;
        /// <inheritdoc/>
        public int Priority => 200; // Higher priority than TAD for specific XMSG protocols
        
        /// <summary>
        /// Check if this service can handle the given CUD
        /// XMSG service handles:
        /// 1. XMSG L03 version identifier (CUD starts with 0x4C03)
        /// 2. XMSG port numbers in range 0x1000-0x1FFF
        /// </summary>
        public bool CanHandleCUD(byte[] cud)
        {
            if (cud == null || cud.Length == 0)
            {
                _logger.LogTrace("XMSG service rejecting empty CUD");
                return false;
            }
            
            // Check for XMSG L03 version identifier
            if (cud.Length >= 2 && cud[0] == 0x4C && cud[1] == 0x03)
            {
                _logger.LogTrace("XMSG service accepting L03 version identifier");
                return true;
            }
            
            // Check for XMSG port number range
            if (cud.Length >= 6)
            {
                ushort port = (ushort)((cud[4] << 8) | cud[5]);
                if (port >= 0x1000 && port <= 0x1FFF)
                {
                    _logger.LogTrace("XMSG service accepting port {Port:X4} in XMSG range", port);
                    return true;
                }
            }
            
            // Check for XMSG message structure (12-byte header minimum)
            if (cud.Length >= 12)
            {
                ushort messageType = (ushort)((cud[2] << 8) | cud[3]);
                ushort port = (ushort)((cud[4] << 8) | cud[5]);
                
                // Known XMSG message types
                bool validMessageType = messageType == 0x0001 || messageType == 0x0002 || 
                                       messageType == 0x0010 || messageType == 0x0011 ||
                                       messageType == 0x0020 || messageType == 0x0021 ||
                                       messageType == 0x00FF;
                                       
                bool validPort = port >= 0x1000 && port <= 0x1FFF;
                
                if (validMessageType && validPort)
                {
                    _logger.LogTrace("XMSG service accepting valid message type {MessageType:X4} and port {Port:X4}", 
                        messageType, port);
                    return true;
                }
            }
            
            _logger.LogTrace("XMSG service rejecting CUD - no XMSG patterns found");
            return false;
        }
        
        /// <summary>
        /// Handle incoming X.25 call request
        /// </summary>
        public bool HandleIncomingCall(ushort lcn, string calledAddress, string callingAddress, 
            byte[] cud, byte[] facilities)
        {
            try
            {
                _logger.LogInformation("XMSG service handling incoming call from {CallingAddress} to {CalledAddress} on LCN {LCN}",
                    callingAddress, calledAddress, lcn);
                
                // Extract service info from CUD
                var serviceInfo = ExtractServiceInfo(cud);
                _logger.LogDebug("XMSG call for service: {ServiceInfo}", serviceInfo);
                
                // Use the existing LocalXMSGProtocol to handle the call
                return _xmsgProtocol.HandleOutgoingCall(lcn, calledAddress, callingAddress, cud, 
                    (packet) => _logger.LogDebug("XMSG protocol event: {PacketType}", packet.Packet.PacketType));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling XMSG incoming call on LCN {LCN}", lcn);
                return false;
            }
        }
        
        /// <summary>
        /// Handle data received on established connection
        /// </summary>
        public void HandleData(ushort lcn, byte[] data)
        {
            _xmsgProtocol.HandleIncomingData(lcn, data);
        }
        
        /// <summary>
        /// Handle call clearing
        /// </summary>
        public void HandleClear(ushort lcn, byte cause, byte diagnostic)
        {
            _xmsgProtocol.HandleCallClearing(lcn);
        }
        
        /// <summary>
        /// Handle interrupt packets
        /// </summary>
        public void HandleInterrupt(ushort lcn, byte[] data)
        {
            // XMSG protocol may use interrupts for flow control
            _logger.LogDebug("XMSG service received interrupt on LCN {LCN}: {DataLength} bytes", lcn, data?.Length ?? 0);
            // TODO: Forward to XMSG protocol if needed
        }
        
        /// <summary>
        /// Extract service information from XMSG CUD
        /// </summary>
        private string ExtractServiceInfo(byte[] cud)
        {
            if (cud == null || cud.Length < 6)
                return "Unknown XMSG Service";
            
            // Try to extract port number
            ushort port = (ushort)((cud[4] << 8) | cud[5]);
            
            return port switch
            {
                0x1000 => "COSMOS File Service",
                0x1001 => "Remote Procedure Service", 
                0x1002 => "System Monitor",
                >= 0x1003 and <= 0x10FF => $"User Service (Port {port:X4})",
                >= 0x1100 and <= 0x1FFF => $"Reserved XMSG Service (Port {port:X4})",
                _ => $"XMSG Service (Port {port:X4})"
            };
        }
    }
}