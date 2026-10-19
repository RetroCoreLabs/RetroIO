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
    /// <param name="bytesReceived">
    /// The number of payload bytes in the request.
    /// </param>
    /// <param name="bytesSent">
    /// The number of payload bytes in the reply that was sent, or 0 when no reply was sent.
    /// </param>
    /// <param name="isFileOperation">
    /// <c>true</c> when the request was a file operation.
    /// </param>
    void RecordRequest(int bytesReceived, int bytesSent, bool isFileOperation);

    /// <summary>
    /// Records one failure: a request that threw while it was handled, or a reply that could not be sent.
    /// </summary>
    void RecordError();
}
