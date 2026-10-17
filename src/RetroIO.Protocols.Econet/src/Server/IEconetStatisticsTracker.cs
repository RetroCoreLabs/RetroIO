//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Protocols.Econet.Server;

/// <summary>
/// Receives the size of each handled request for statistics.
/// </summary>
public interface IEconetStatisticsTracker
{
    /// <summary>
    /// Records one handled request.
    /// </summary>
    /// <param name="bytesTransferred">
    /// The number of request and reply payload bytes.
    /// </param>
    /// <param name="isFileOperation">
    /// True when the request was a file operation.
    /// </param>
    void RecordRequest(int bytesTransferred, bool isFileOperation);
}
