//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading.Tasks;

namespace RetroIO.Protocols.X25.Services
{
    /// <summary>
    /// Interface for protocol services that can handle incoming X.25 calls
    /// Services register themselves with the protocol service registry
    /// </summary>
    public interface IProtocolService
    {
        /// <summary>
        /// Unique name of this service
        /// </summary>
        string ServiceName { get; }
        
        /// <summary>
        /// Local X.121 addresses this service handles (e.g., ["200", "201"])
        /// </summary>
        string[] LocalAddresses { get; }
        
        /// <summary>
        /// Priority for this service (higher = more specific, checked first)
        /// Allows multiple services to handle same address with different CUD patterns
        /// </summary>
        int Priority { get; }
        
        /// <summary>
        /// Check if this service can handle the given Call User Data
        /// </summary>
        /// <param name="cud">
        /// Call User Data from Call Request packet
        /// </param>
        /// <returns>
        /// True if this service can handle this CUD pattern
        /// </returns>
        bool CanHandleCUD(byte[] cud);
        
        /// <summary>
        /// Handle an incoming X.25 call request
        /// </summary>
        /// <param name="lcn">
        /// Logical Channel Number
        /// </param>
        /// <param name="calledAddress">
        /// Called X.121 address
        /// </param>
        /// <param name="callingAddress">
        /// Calling X.121 address
        /// </param>
        /// <param name="cud">
        /// Call User Data
        /// </param>
        /// <param name="facilities">
        /// Facilities data
        /// </param>
        /// <returns>
        /// True if call was accepted and handled
        /// </returns>
        bool HandleIncomingCall(ushort lcn, string calledAddress, string callingAddress, 
            byte[] cud, byte[] facilities);
            
        /// <summary>
        /// Handle data received on an established call
        /// </summary>
        /// <param name="lcn">
        /// Logical Channel Number
        /// </param>
        /// <param name="data">
        /// Data received
        /// </param>
        void HandleData(ushort lcn, byte[] data);
        
        /// <summary>
        /// Handle call clearing
        /// </summary>
        /// <param name="lcn">
        /// Logical Channel Number
        /// </param>
        /// <param name="cause">
        /// Clear cause
        /// </param>
        /// <param name="diagnostic">
        /// Diagnostic code
        /// </param>
        void HandleClear(ushort lcn, byte cause, byte diagnostic);
        
        /// <summary>
        /// Handle interrupt packets
        /// </summary>
        /// <param name="lcn">
        /// Logical Channel Number
        /// </param>
        /// <param name="data">
        /// Interrupt data
        /// </param>
        void HandleInterrupt(ushort lcn, byte[] data);
    }
}