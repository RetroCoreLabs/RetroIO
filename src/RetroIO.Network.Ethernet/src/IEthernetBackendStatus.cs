//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Network.Ethernet
{
    /// <summary>
    /// An optional extra an <see cref="IEthernetBackend"/> implements when it has counters
    /// worth showing: frames it dropped and why, checksums it repaired, the filter it set.
    /// </summary>
    /// <remarks>
    /// The <c>net.status</c> command prints these lines under the backend line, so a frame the
    /// backend dropped on purpose is never mistaken for a broken link.
    /// </remarks>
    public interface IEthernetBackendStatus
    {
        /// <summary>
        /// Gets a few short lines that describe the backend's state and counters.
        /// </summary>
        /// <returns>
        /// One entry per line, without line breaks; empty when there is nothing to say.
        /// </returns>
        string[] DescribeStatus();
    }
}
