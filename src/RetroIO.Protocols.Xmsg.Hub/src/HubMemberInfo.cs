//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;

namespace RetroIO.Protocols.Xmsg.Hub
{
    /// <summary>
    /// A snapshot of one member of the hub, for listing.
    /// </summary>
    /// <param name="Name">
    /// The member's remote address; an outgoing hub link is named "uplink host:port".
    /// </param>
    /// <param name="IsHub">
    /// True for another hub, false for a machine.
    /// </param>
    /// <param name="IsUplink">
    /// True when this is our own outgoing link to another hub.
    /// </param>
    /// <param name="JoinedUtc">
    /// When it joined.
    /// </param>
    /// <param name="FramesFromMember">
    /// Frames it sent to the hub.
    /// </param>
    /// <param name="FramesToMember">
    /// Frames the hub queued to it.
    /// </param>
    /// <param name="FramesDropped">
    /// Frames it lost because it had fallen behind.
    /// </param>
    /// <param name="QueueLength">
    /// Frames waiting in its queue right now.
    /// </param>
    public sealed record HubMemberInfo(
        string Name,
        bool IsHub,
        bool IsUplink,
        DateTime JoinedUtc,
        long FramesFromMember,
        long FramesToMember,
        long FramesDropped,
        int QueueLength);
}
