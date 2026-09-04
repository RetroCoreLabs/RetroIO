using System;
using RetroIO.Link.Models;
using RetroIO.Protocols.X25.Models;

namespace RetroIO.Protocols.X25.Modules
{
    /// <summary>
    /// Interface for X.25 protocol modules
    /// </summary>
    public interface IProtocolModule
    {
        /// <summary>
        /// Module name
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Module description
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Module version
        /// </summary>
        string Version { get; }

        /// <summary>
        /// Called when module is bound to an LCN
        /// </summary>
        void OnBound(ushort lcn);

        /// <summary>
        /// Called when module is unbound from an LCN
        /// </summary>
        void OnUnbound(ushort lcn);

        /// <summary>
        /// Called when X.25 data packet is received (legacy interface - use OnX25Event instead)
        /// </summary>
        void OnX25Data(ushort lcn, byte[] data);

        /// <summary>
        /// Called when X.25 event occurs (new event-based interface)
        /// </summary>
        void OnX25Event(X25PacketEventArgs args);

        /// <summary>
        /// Called when X.25 reset is requested
        /// </summary>
        void OnX25Reset(ushort lcn, byte cause, byte diagnostic);

        /// <summary>
        /// Called when X.25 clear is requested
        /// </summary>
        void OnX25Clear(ushort lcn, byte cause, byte diagnostic);

        /// <summary>
        /// Called when X.25 interrupt is received
        /// </summary>
        void OnX25Interrupt(ushort lcn, byte[] data);

        /// <summary>
        /// Send X.25 data packet
        /// </summary>
        void SendX25Data(ushort lcn, byte[] data);

        /// <summary>
        /// Send X.25 reset request
        /// </summary>
        void SendX25Reset(ushort lcn, byte cause, byte diagnostic);

        /// <summary>
        /// Send X.25 clear request
        /// </summary>
        void SendX25Clear(ushort lcn, byte cause, byte diagnostic);

        /// <summary>
        /// Send X.25 interrupt
        /// </summary>
        void SendX25Interrupt(ushort lcn, byte[] data);

        /// <summary>
        /// Get module status
        /// </summary>
        ModuleStatus GetStatus();

        /// <summary>
        /// Initialize module
        /// </summary>
        void Initialize();

        /// <summary>
        /// Shutdown module
        /// </summary>
        void Shutdown();
    }

    /// <summary>
    /// Module status information
    /// </summary>
    public class ModuleStatus
    {
        /// <summary>
        /// Module is active
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Number of active connections
        /// </summary>
        public int ActiveConnections { get; set; }

        /// <summary>
        /// Total data received
        /// </summary>
        public long TotalDataReceived { get; set; }

        /// <summary>
        /// Total data transmitted
        /// </summary>
        public long TotalDataTransmitted { get; set; }

        /// <summary>
        /// Last activity timestamp
        /// </summary>
        public DateTime LastActivity { get; set; }

        /// <summary>
        /// Error count
        /// </summary>
        public int ErrorCount { get; set; }

        /// <summary>
        /// Status message
        /// </summary>
        public string StatusMessage { get; set; } = "";
    }
}
