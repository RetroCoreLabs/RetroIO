//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.PacketLayer
{
    /// <summary>
    /// Whether a virtual circuit is a switched call (SVC) or a permanent circuit (PVC).
    /// </summary>
    public enum X25VcKind
    {
        /// <summary>
        /// Switched virtual circuit — set up and cleared per call.
        /// </summary>
        Svc = 0,

        /// <summary>
        /// Permanent virtual circuit — always in the data-transfer state.
        /// </summary>
        Pvc = 1
    }

    /// <summary>
    /// The state of a virtual circuit, following the ITU-T X.25 packet-layer channel states.
    /// </summary>
    public enum X25VcState
    {
        /// <summary>
        /// Idle / free channel available for a new call (X.25 state p1).
        /// </summary>
        Ready = 0,

        /// <summary>
        /// Outgoing call sent, awaiting Call Connected (p2).
        /// </summary>
        CallRequest = 1,

        /// <summary>
        /// Incoming call received, awaiting local accept or clear (p3).
        /// </summary>
        CallIncoming = 2,

        /// <summary>
        /// Call established, data flowing (p4).
        /// </summary>
        DataTransfer = 3,

        /// <summary>
        /// Simultaneous incoming and outgoing call on the same channel (p5).
        /// </summary>
        CallCollision = 4,

        /// <summary>
        /// Clear sent, awaiting Clear Confirmation (p6).
        /// </summary>
        ClearRequest = 5,

        /// <summary>
        /// Clear received, transient (p7).
        /// </summary>
        ClearIndication = 6,

        /// <summary>
        /// Reset sent, awaiting Reset Confirmation (d2).
        /// </summary>
        ResetRequest = 7,

        /// <summary>
        /// Reset received, transient (d3).
        /// </summary>
        ResetIndication = 8,

        /// <summary>
        /// Permanent circuit in the data-transfer state.
        /// </summary>
        PvcReady = 9
    }
}
