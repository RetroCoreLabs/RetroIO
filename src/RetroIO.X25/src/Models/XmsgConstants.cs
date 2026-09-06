using System;

namespace RetroIO.Protocols.X25.Models
{
    /// <summary>
    /// XMSG (Extended Message Protocol) Service Port Numbers for the COSMOS X.25 network.
    /// XMSG is a proprietary protocol extension used in COSMOS satellite ground station networks
    /// for distributed system communication and service discovery over X.25 networks.
    /// Port numbers are encoded in the X.25 Call User Data (CUD) field for service identification.
    /// </summary>
    public enum XmsgServicePort : ushort
    {
        /// <summary>
        /// COSMOS File Service (0x1000) - Distributed file system service.
        /// Provides remote file access, transfer, and synchronization capabilities
        /// across COSMOS ground stations. Supports file metadata exchange,
        /// directory browsing, and distributed file storage management.
        /// Port: 4096 decimal
        /// </summary>
        CosmosFileService = 0x1000,

        /// <summary>
        /// Remote Procedure Service (0x1001) - RPC mechanism for COSMOS systems.
        /// Enables remote procedure calls between distributed COSMOS components,
        /// allowing transparent invocation of services across the X.25 network.
        /// Supports parameter marshaling, return value handling, and error propagation.
        /// Port: 4097 decimal
        /// </summary>
        RemoteProcedureService = 0x1001,

        /// <summary>
        /// System Monitor (0x1002) - Network and system monitoring service.
        /// Provides centralized monitoring of COSMOS ground station systems,
        /// including performance metrics, health status, and operational statistics.
        /// Collects and aggregates monitoring data from distributed nodes.
        /// Port: 4098 decimal
        /// </summary>
        SystemMonitor = 0x1002,

        /// <summary>
        /// User Service Range Start (0x1003) - Beginning of user-defined services.
        /// User services occupy the range 0x1003-0x10FF for custom applications
        /// and site-specific COSMOS extensions. These ports are available for
        /// local customization and specialized ground station functionality.
        /// Port: 4099 decimal
        /// </summary>
        UserServiceRangeStart = 0x1003,

        /// <summary>
        /// User Service Range End (0x10FF) - End of user-defined services.
        /// Marks the upper boundary of the user service range. Services in this
        /// range (0x1003-0x10FF) are available for site-specific implementations
        /// and do not conflict with standard COSMOS system services.
        /// Port: 4351 decimal
        /// </summary>
        UserServiceRangeEnd = 0x10FF,

        /// <summary>
        /// Reserved Service Range Start (0x1100) - Beginning of reserved service range.
        /// Reserved for future COSMOS system expansion and standardized services.
        /// These port numbers are allocated but not yet assigned to specific services.
        /// Provides room for future protocol extensions and new system services.
        /// Port: 4352 decimal
        /// </summary>
        ReservedServiceRangeStart = 0x1100,

        /// <summary>
        /// Reserved Service Range End (0x1FFF) - End of XMSG service port range.
        /// Marks the upper boundary of the XMSG protocol service port allocation.
        /// Ports beyond this range are not recognized as valid XMSG services
        /// and will not be processed by the XMSG protocol handler.
        /// Port: 8191 decimal
        /// </summary>
        ReservedServiceRangeEnd = 0x1FFF
    }

    /// <summary>
    /// XMSG Protocol Version Identifiers used in X.25 Call User Data (CUD) field.
    /// These identifiers appear at the beginning of the CUD to indicate XMSG protocol support.
    /// </summary>
    public enum XmsgVersionIdentifier : ushort
    {
        /// <summary>
        /// XMSG L03 Version (0x4C03) - Current XMSG protocol version.
        /// This identifier appears as the first two bytes of the X.25 CUD field
        /// to indicate XMSG protocol capability and version compatibility.
        /// "L03" indicates XMSG protocol level 3 implementation.
        /// Byte sequence: 0x4C, 0x03
        /// </summary>
        L03 = 0x4C03
    }

    /// <summary>
    /// XMSG Message Types used in XMSG protocol communication.
    /// These values identify the type of XMSG message being transmitted
    /// and determine the expected message format and handling procedures.
    /// </summary>
    public enum XmsgMessageType : ushort
    {
        /// <summary>
        /// Service Request (0x0001) - Client service request message.
        /// Sent by a client to request a specific service from a server.
        /// Contains service parameters and identification information.
        /// Expects a corresponding Service Response from the server.
        /// </summary>
        ServiceRequest = 0x0001,

        /// <summary>
        /// Service Response (0x0002) - Server service response message.
        /// Sent by a server in response to a Service Request.
        /// Contains service results, status information, and any returned data.
        /// Completes the request/response transaction cycle.
        /// </summary>
        ServiceResponse = 0x0002,

        /// <summary>
        /// Status Query (0x0010) - Request for service status information.
        /// Used to query the operational status of a remote service or system.
        /// Does not invoke service functionality, only retrieves status data.
        /// Expects a Status Report in response.
        /// </summary>
        StatusQuery = 0x0010,

        /// <summary>
        /// Status Report (0x0011) - Service status information response.
        /// Sent in response to a Status Query message.
        /// Contains current operational status, performance metrics,
        /// and health information about the requested service or system.
        /// </summary>
        StatusReport = 0x0011,

        /// <summary>
        /// Event Notification (0x0020) - Asynchronous event notification.
        /// Used to notify interested parties of significant events or state changes.
        /// Sent asynchronously without a corresponding request.
        /// May trigger Event Acknowledgment from recipients.
        /// </summary>
        EventNotification = 0x0020,

        /// <summary>
        /// Event Acknowledgment (0x0021) - Acknowledgment of received event.
        /// Sent to acknowledge receipt and processing of an Event Notification.
        /// Confirms that the event has been received and handled appropriately.
        /// Provides confirmation for critical event delivery.
        /// </summary>
        EventAcknowledgment = 0x0021,

        /// <summary>
        /// System Broadcast (0x00FF) - System-wide broadcast message.
        /// Used for system-wide announcements, alerts, and general notifications.
        /// Typically sent to multiple recipients simultaneously.
        /// May contain system status updates, maintenance notifications, or alerts.
        /// </summary>
        SystemBroadcast = 0x00FF
    }

    /// <summary>
    /// Extension methods for XmsgServicePort enum to provide additional functionality.
    /// </summary>
    public static class XmsgServicePortExtensions
    {
        /// <summary>
        /// Determines if this port number is within the valid XMSG service range.
        /// </summary>
        /// <param name="port">The XMSG service port</param>
        /// <returns>True if this is a valid XMSG service port</returns>
        public static bool IsValidXmsgPort(this XmsgServicePort port)
        {
            ushort portValue = (ushort)port;
            return portValue >= (ushort)XmsgServicePort.CosmosFileService && 
                   portValue <= (ushort)XmsgServicePort.ReservedServiceRangeEnd;
        }

        /// <summary>
        /// Determines if this port is a standard COSMOS system service.
        /// </summary>
        /// <param name="port">The XMSG service port</param>
        /// <returns>True if this is a standard system service port</returns>
        public static bool IsSystemService(this XmsgServicePort port)
        {
            return port == XmsgServicePort.CosmosFileService ||
                   port == XmsgServicePort.RemoteProcedureService ||
                   port == XmsgServicePort.SystemMonitor;
        }

        /// <summary>
        /// Determines if this port is in the user-defined service range.
        /// </summary>
        /// <param name="port">The XMSG service port</param>
        /// <returns>True if this is a user-defined service port</returns>
        public static bool IsUserService(this XmsgServicePort port)
        {
            ushort portValue = (ushort)port;
            return portValue >= (ushort)XmsgServicePort.UserServiceRangeStart &&
                   portValue <= (ushort)XmsgServicePort.UserServiceRangeEnd;
        }

        /// <summary>
        /// Determines if this port is in the reserved service range.
        /// </summary>
        /// <param name="port">The XMSG service port</param>
        /// <returns>True if this is a reserved service port</returns>
        public static bool IsReservedService(this XmsgServicePort port)
        {
            ushort portValue = (ushort)port;
            return portValue >= (ushort)XmsgServicePort.ReservedServiceRangeStart &&
                   portValue <= (ushort)XmsgServicePort.ReservedServiceRangeEnd;
        }

        /// <summary>
        /// Gets a human-readable description of the service port.
        /// </summary>
        /// <param name="port">The XMSG service port</param>
        /// <returns>A descriptive string explaining the service port</returns>
        public static string GetServiceDescription(this XmsgServicePort port)
        {
            return port switch
            {
                XmsgServicePort.CosmosFileService => "COSMOS distributed file system service",
                XmsgServicePort.RemoteProcedureService => "Remote procedure call mechanism for COSMOS systems",
                XmsgServicePort.SystemMonitor => "Network and system monitoring service",
                _ when port.IsUserService() => $"User-defined service (Port {(ushort)port:X4})",
                _ when port.IsReservedService() => $"Reserved XMSG service (Port {(ushort)port:X4})",
                _ => $"Unknown XMSG service port {(ushort)port:X4}"
            };
        }

        /// <summary>
        /// Gets the decimal representation of the port number.
        /// </summary>
        /// <param name="port">The XMSG service port</param>
        /// <returns>The port number in decimal format</returns>
        public static int GetDecimalPort(this XmsgServicePort port)
        {
            return (ushort)port;
        }

        /// <summary>
        /// Gets the hexadecimal representation of the port number.
        /// </summary>
        /// <param name="port">The XMSG service port</param>
        /// <returns>The port number in hexadecimal format (e.g., "1000")</returns>
        public static string GetHexPort(this XmsgServicePort port)
        {
            return $"{(ushort)port:X4}";
        }
    }

    /// <summary>
    /// Extension methods for XmsgVersionIdentifier enum to provide additional functionality.
    /// </summary>
    public static class XmsgVersionIdentifierExtensions
    {
        /// <summary>
        /// Gets the byte array representation of the version identifier for CUD encoding.
        /// </summary>
        /// <param name="version">The XMSG version identifier</param>
        /// <returns>Two-byte array containing the version identifier</returns>
        public static byte[] GetCudBytes(this XmsgVersionIdentifier version)
        {
            ushort value = (ushort)version;
            return new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) };
        }

        /// <summary>
        /// Gets a human-readable description of the version identifier.
        /// </summary>
        /// <param name="version">The XMSG version identifier</param>
        /// <returns>A descriptive string explaining the version</returns>
        public static string GetVersionDescription(this XmsgVersionIdentifier version)
        {
            return version switch
            {
                XmsgVersionIdentifier.L03 => "XMSG Protocol Level 3 - Current version supporting distributed COSMOS services",
                _ => $"Unknown XMSG version identifier {(ushort)version:X4}"
            };
        }
    }

    /// <summary>
    /// Extension methods for XmsgMessageType enum to provide additional functionality.
    /// </summary>
    public static class XmsgMessageTypeExtensions
    {
        /// <summary>
        /// Determines if this message type is a request that expects a response.
        /// </summary>
        /// <param name="messageType">The XMSG message type</param>
        /// <returns>True if this message type expects a response</returns>
        public static bool IsRequest(this XmsgMessageType messageType)
        {
            return messageType switch
            {
                XmsgMessageType.ServiceRequest => true,
                XmsgMessageType.StatusQuery => true,
                _ => false
            };
        }

        /// <summary>
        /// Determines if this message type is a response to a request.
        /// </summary>
        /// <param name="messageType">The XMSG message type</param>
        /// <returns>True if this message type is a response</returns>
        public static bool IsResponse(this XmsgMessageType messageType)
        {
            return messageType switch
            {
                XmsgMessageType.ServiceResponse => true,
                XmsgMessageType.StatusReport => true,
                XmsgMessageType.EventAcknowledgment => true,
                _ => false
            };
        }

        /// <summary>
        /// Determines if this message type is sent asynchronously (without a request).
        /// </summary>
        /// <param name="messageType">The XMSG message type</param>
        /// <returns>True if this message type is asynchronous</returns>
        public static bool IsAsynchronous(this XmsgMessageType messageType)
        {
            return messageType switch
            {
                XmsgMessageType.EventNotification => true,
                XmsgMessageType.SystemBroadcast => true,
                _ => false
            };
        }

        /// <summary>
        /// Gets the corresponding response message type for a request.
        /// </summary>
        /// <param name="messageType">The XMSG request message type</param>
        /// <returns>The corresponding response message type, or null if not applicable</returns>
        public static XmsgMessageType? GetResponseType(this XmsgMessageType messageType)
        {
            return messageType switch
            {
                XmsgMessageType.ServiceRequest => XmsgMessageType.ServiceResponse,
                XmsgMessageType.StatusQuery => XmsgMessageType.StatusReport,
                XmsgMessageType.EventNotification => XmsgMessageType.EventAcknowledgment,
                _ => null
            };
        }

        /// <summary>
        /// Gets a human-readable description of the message type.
        /// </summary>
        /// <param name="messageType">The XMSG message type</param>
        /// <returns>A descriptive string explaining the message type</returns>
        public static string GetDescription(this XmsgMessageType messageType)
        {
            return messageType switch
            {
                XmsgMessageType.ServiceRequest => "Client request for a specific service operation",
                XmsgMessageType.ServiceResponse => "Server response containing service results",
                XmsgMessageType.StatusQuery => "Request for service operational status information",
                XmsgMessageType.StatusReport => "Response containing current service status and metrics",
                XmsgMessageType.EventNotification => "Asynchronous notification of significant events",
                XmsgMessageType.EventAcknowledgment => "Acknowledgment of received event notification",
                XmsgMessageType.SystemBroadcast => "System-wide broadcast message or announcement",
                _ => $"Unknown XMSG message type {(ushort)messageType:X4}"
            };
        }
    }

    /// <summary>
    /// Static helper methods for XMSG protocol validation and utility functions.
    /// </summary>
    public static class XmsgProtocolHelper
    {
        /// <summary>
        /// Validates if the given port number is within the valid XMSG service range.
        /// </summary>
        /// <param name="port">The port number to validate</param>
        /// <returns>True if the port is within the valid XMSG range</returns>
        public static bool IsValidXmsgPortRange(ushort port)
        {
            return port >= (ushort)XmsgServicePort.CosmosFileService &&
                   port <= (ushort)XmsgServicePort.ReservedServiceRangeEnd;
        }

        /// <summary>
        /// Extracts the XMSG version identifier from Call User Data (CUD).
        /// </summary>
        /// <param name="cud">The Call User Data byte array</param>
        /// <returns>The XMSG version identifier, or null if not valid XMSG CUD</returns>
        public static XmsgVersionIdentifier? ExtractVersionFromCud(byte[] cud)
        {
            if (cud?.Length >= 2)
            {
                ushort version = (ushort)((cud[0] << 8) | cud[1]);
                if (Enum.IsDefined(typeof(XmsgVersionIdentifier), version))
                {
                    return (XmsgVersionIdentifier)version;
                }
            }
            return null;
        }

        /// <summary>
        /// Extracts the XMSG service port from Call User Data (CUD).
        /// </summary>
        /// <param name="cud">The Call User Data byte array</param>
        /// <returns>The service port number, or null if CUD is not valid or too short</returns>
        public static ushort? ExtractPortFromCud(byte[] cud)
        {
            if (cud?.Length >= 4)
            {
                // Port is typically in bytes 2-3 after the version identifier
                ushort port = (ushort)((cud[2] << 8) | cud[3]);
                return port;
            }
            return null;
        }

        /// <summary>
        /// Validates if the Call User Data contains valid XMSG protocol information.
        /// </summary>
        /// <param name="cud">The Call User Data byte array</param>
        /// <returns>True if CUD contains valid XMSG protocol data</returns>
        public static bool IsValidXmsgCud(byte[] cud)
        {
            var version = ExtractVersionFromCud(cud);
            var port = ExtractPortFromCud(cud);
            
            return version.HasValue && port.HasValue && IsValidXmsgPortRange(port.Value);
        }
    }
}