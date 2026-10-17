//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.X25.Common
{
    /// <summary>
    /// Interface for timer functionality to enable testing
    /// </summary>
    public interface ILapbTimerInterface
    {
        void Start();
        void Stop();
        bool HasExpired();
        bool Running { get; }
    }

    /// <summary>
    /// Interface for LAPB timer management by ProtocolRuntime
    /// </summary>
    public interface ILapbTimerManager
    {
        /// <summary>
        /// Start T1 timer (response timeout) when sending frames requiring acknowledgment
        /// </summary>
        void StartT1Timer();

        /// <summary>
        /// Stop T1 timer when acknowledgment received
        /// </summary>
        void StopT1Timer();

        /// <summary>
        /// Start T2 timer (acknowledgment delay) when delaying sending acknowledgment
        /// </summary>
        void StartT2Timer();

        /// <summary>
        /// Stop T2 timer when acknowledgment sent
        /// </summary>
        void StopT2Timer();

        /// <summary>
        /// Start T3 timer (idle supervision) when link becomes connected
        /// </summary>
        void StartT3Timer();

        /// <summary>
        /// Stop T3 timer when activity detected or link disconnected
        /// </summary>
        void StopT3Timer();
    }

    /// <summary>
    /// Interface for handling LAPB frame pre-storage for relationship linking.
    /// Decouples LapbLayer from ProtocolRuntime implementation.
    /// </summary>
    public interface ILapbFramePreHandler
    {
        /// <summary>
        /// Pre-store LAPB frame data for later X.25 relationship linking.
        /// </summary>
        void PreStoreLapbFrameForLinking(byte[] data);

        /// <summary>
        /// Set original packet data for X.25 relationship tracking.
        /// </summary>
        void SetOriginalPacketForRelationship(byte[] data);
    }

}
