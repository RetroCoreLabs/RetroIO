//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using Microsoft.Extensions.Logging;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Services
{
    /// <summary>
    /// X.25 Protocol Detection Service based on Call User Data (CUD) according to spec
    /// Implements protocol identification for PAD, TAD, XMSG, IP, CLNP, etc.
    /// </summary>
    public class X25ProtocolDetectionService
    {
        private readonly ILogger _logger;

        /// <summary>
        /// Creates the detection service.
        /// </summary>
        /// <param name="logger">
        /// Diagnostics logger.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="logger"/> is null.
        /// </exception>
        public X25ProtocolDetectionService(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Detect protocol from Call User Data according to X.25 spec
        /// </summary>
        public X25ProtocolType DetectProtocol(byte[] callUserData, string calledAddress)
        {
            if (callUserData == null || callUserData.Length == 0)
            {
                _logger.LogDebug("No CUD provided, defaulting to TAD protocol");
                return X25ProtocolType.TAD;
            }

            var protocol = AnalyzeCUD(callUserData);
            int previewLength = Math.Min(16, callUserData.Length);
            var preview = new byte[previewLength];
            Array.Copy(callUserData, preview, previewLength);
            _logger.LogInformation("Protocol detection: CUD={CUD} -> Protocol={Protocol} for address {Address}",
                BitConverter.ToString(preview),
                protocol, calledAddress);

            return protocol;
        }

        /// <summary>
        /// Analyze Call User Data according to spec protocol identification
        /// </summary>
        private X25ProtocolType AnalyzeCUD(byte[] cud)
        {
            // 1. PAD Protocol: CUD = 0x01000000 (4 bytes)
            if (cud.Length >= 4 && 
                cud[0] == 0x01 && cud[1] == 0x00 && cud[2] == 0x00 && cud[3] == 0x00)
            {
                return X25ProtocolType.PAD;
            }

            // 2. IP Protocol: First byte = 0xCC
            if (cud.Length >= 1 && cud[0] == 0xCC)
            {
                return X25ProtocolType.IP;
            }

            // 3. CLNP Protocol: First byte = 0x81
            if (cud.Length >= 1 && cud[0] == 0x81)
            {
                return X25ProtocolType.CLNP;
            }

            // 4. XMSG Protocol Detection: Check for XMSG pattern
            if (IsXMSGProtocol(cud))
            {
                return X25ProtocolType.XMSG;
            }

            // 5. IPX Protocol: 5-byte identifier 0x800000008137
            if (cud.Length >= 5 &&
                cud[0] == 0x80 && cud[1] == 0x00 && cud[2] == 0x00 && 
                cud[3] == 0x00 && cud[4] == 0x81 && 
                cud.Length > 5 && cud[5] == 0x37)
            {
                return X25ProtocolType.IPX;
            }

            // Default to Unknown for unrecognized protocols  
            return X25ProtocolType.Unknown;
        }

        /// <summary>
        /// Check if CUD indicates XMSG protocol based on spec patterns
        /// </summary>
        private bool IsXMSGProtocol(byte[] cud)
        {
            // Primary XMSG detection: Check for version identifier and port range
            if (cud.Length >= 6)
            {
                // Check for XMSG L03 version (0x4C03)
                if (cud[0] == 0x4C && cud[1] == 0x03)
                {
                    return true;
                }

                // Check for port number in XMSG range (0x1000-0x1FFF)
                if (cud.Length >= 6)
                {
                    ushort port = (ushort)((cud[4] << 8) | cud[5]);
                    if (port >= 0x1000 && port <= 0x1FFF)
                    {
                        return true;
                    }
                }
            }

            // Secondary check: Look for XMSG message patterns
            if (cud.Length >= 12)
            {
                // Check for XMSG header structure (12 bytes minimum)
                ushort messageType = (ushort)((cud[2] << 8) | cud[3]);
                ushort port = (ushort)((cud[4] << 8) | cud[5]);
                
                // Known XMSG message types and port ranges
                bool validMessageType = messageType == 0x0001 || messageType == 0x0002 || 
                                       messageType == 0x0010 || messageType == 0x0011 ||
                                       messageType == 0x0020 || messageType == 0x0021 ||
                                       messageType == 0x00FF;
                                       
                bool validPort = port >= 0x1000 && port <= 0x1FFF;
                
                if (validMessageType && validPort)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get detailed protocol information for logging
        /// </summary>
        public X25ProtocolInfo GetProtocolInfo(byte[] callUserData, string calledAddress)
        {
            var protocol = DetectProtocol(callUserData, calledAddress);
            var info = new X25ProtocolInfo
            {
                Protocol = protocol,
                CalledAddress = calledAddress,
                CallUserData = callUserData,
                Timestamp = DateTime.UtcNow
            };

            // Add protocol-specific details
            switch (protocol)
            {
                case X25ProtocolType.PAD:
                    info.Description = "X.29 PAD (Packet Assembler/Disassembler)";
                    info.ServiceType = "Interactive Terminal Access";
                    break;

                case X25ProtocolType.TAD:
                    info.Description = "TAD (Terminal Access Device) Server";
                    info.ServiceType = "Terminal Services";
                    break;

                case X25ProtocolType.XMSG:
                    info.Description = "XMSG Protocol (COSMOS File Service)";
                    info.ServiceType = ExtractXMSGServiceType(callUserData);
                    break;

                case X25ProtocolType.IP:
                    info.Description = "Internet Protocol";
                    info.ServiceType = "IP Networking";
                    break;

                case X25ProtocolType.CLNP:
                    info.Description = "Connectionless Network Protocol";
                    info.ServiceType = "OSI Networking";
                    break;

                case X25ProtocolType.IPX:
                    info.Description = "Novell IPX";
                    info.ServiceType = "IPX Networking";
                    break;

                default:
                    info.Description = "Unknown Protocol";
                    info.ServiceType = "Unknown";
                    break;
            }

            return info;
        }

        /// <summary>
        /// Extract XMSG service type from port number
        /// </summary>
        private string ExtractXMSGServiceType(byte[] cud)
        {
            if (cud.Length >= 6)
            {
                ushort port = (ushort)((cud[4] << 8) | cud[5]);
                return port switch
                {
                    0x1000 => "COSMOS File Service",
                    0x1001 => "Remote Procedure Service",
                    0x1002 => "System Monitor",
                    >= 0x1003 and <= 0x10FF => "User Services",
                    >= 0x1100 and <= 0x1FFF => "Reserved XMSG Services",
                    _ => "Unknown XMSG Service"
                };
            }
            return "XMSG Service";
        }
    }

    /// <summary>
    /// X.25 Protocol Types based on CUD analysis
    /// </summary>
    public enum X25ProtocolType
    {
        /// <summary>
        /// The protocol could not be identified.
        /// </summary>
        Unknown,
        /// <summary>
        /// X.29 PAD, call user data 0x01000000.
        /// </summary>
        PAD,           // X.29 PAD - CUD: 0x01000000
        /// <summary>
        /// TAD server, the default for local addresses.
        /// </summary>
        TAD,           // TAD Server - Default for local addresses
        /// <summary>
        /// XMSG protocol, COSMOS file services.
        /// </summary>
        XMSG,          // XMSG Protocol - COSMOS File Services
        /// <summary>
        /// Internet Protocol, call user data 0xCC.
        /// </summary>
        IP,            // Internet Protocol - CUD: 0xCC
        /// <summary>
        /// Connectionless Network Protocol, call user data 0x81.
        /// </summary>
        CLNP,          // Connectionless Network Protocol - CUD: 0x81  
        /// <summary>
        /// Novell IPX, call user data 0x800000008137.
        /// </summary>
        IPX,           // Novell IPX - CUD: 0x800000008137
        /// <summary>
        /// SINTRAN routing protocol.
        /// </summary>
        SintranRouting,// SINTRAN Routing Protocol
        /// <summary>
        /// SINTRAN XMSG messaging.
        /// </summary>
        SintranXmsg,   // SINTRAN XMSG Messaging
        /// <summary>
        /// SINTRAN file transfer.
        /// </summary>
        SintranFile    // SINTRAN File Transfer
    }

    /// <summary>
    /// Detailed protocol information
    /// </summary>
    public class X25ProtocolInfo
    {
        /// <summary>
        /// Gets or sets the detected protocol.
        /// </summary>
        public X25ProtocolType Protocol { get; set; }
        /// <summary>
        /// Gets or sets the called address.
        /// </summary>
        public string CalledAddress { get; set; } = "";
        /// <summary>
        /// Gets or sets the call user data bytes.
        /// </summary>
        public byte[] CallUserData { get; set; } = Array.Empty<byte>();
        /// <summary>
        /// Gets or sets the description text.
        /// </summary>
        public string Description { get; set; } = "";
        /// <summary>
        /// Gets or sets the service type text.
        /// </summary>
        public string ServiceType { get; set; } = "";
        /// <summary>
        /// Gets or sets the time of the detection.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Returns the call user data as hexadecimal bytes separated by spaces.
        /// </summary>
        /// <returns>
        /// The hexadecimal text, or No CUD when the call user data is empty.
        /// </returns>
        public string GetCUDHex()
        {
            return CallUserData.Length > 0 ? 
                BitConverter.ToString(CallUserData).Replace("-", " ") : 
                "No CUD";
        }

        /// <summary>
        /// Returns a one-line description of the detection.
        /// </summary>
        /// <returns>
        /// The protocol, the description, the called address and the call user data in hexadecimal
        /// (from <see cref="GetCUDHex"/>).
        /// </returns>
        public override string ToString()
        {
            return $"{Protocol} - {Description} for {CalledAddress} (CUD: {GetCUDHex()})";
        }
    }
}