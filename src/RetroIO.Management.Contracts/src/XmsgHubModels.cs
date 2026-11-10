//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// The COSMOS Ethernet hub's state and counters.
/// </summary>
public class XmsgHubStatusResponse
{
    /// <summary>
    /// Gets or sets whether this server hosts the hub at all (it is switched on in its settings).
    /// </summary>
    public bool IsHosted { get; set; }

    /// <summary>
    /// Gets or sets the state: Stopped, Running or Failed.
    /// </summary>
    public string State { get; set; } = "Stopped";

    /// <summary>
    /// Gets or sets why the hub could not start, or empty.
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address and port it listens on, or empty.
    /// </summary>
    public string Listening { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the uplink to another hub, or empty.
    /// </summary>
    public string Uplink { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the capture file, or empty.
    /// </summary>
    public string CaptureFile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how long the hub has been running, as text.
    /// </summary>
    public string Uptime { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of members joined.
    /// </summary>
    public int Members { get; set; }

    /// <summary>
    /// Gets or sets how many of the members are machines rather than hubs.
    /// </summary>
    public int Machines { get; set; }

    /// <summary>
    /// Gets or sets the frames received from members.
    /// </summary>
    public long FramesIn { get; set; }

    /// <summary>
    /// Gets or sets the frame copies sent on to other members.
    /// </summary>
    public long FramesForwarded { get; set; }

    /// <summary>
    /// Gets or sets the copies lost because a member had fallen behind.
    /// </summary>
    public long FramesDroppedSlow { get; set; }

    /// <summary>
    /// Gets or sets the frames dropped because they had been round a hub loop.
    /// </summary>
    public long FramesDroppedLoop { get; set; }

    /// <summary>
    /// Gets or sets the frames dropped because their hop count ran out.
    /// </summary>
    public long FramesDroppedTtl { get; set; }

    /// <summary>
    /// Gets or sets the frames written to the capture file.
    /// </summary>
    public long FramesCaptured { get; set; }

    /// <summary>
    /// Gets or sets the number of the newest frame in the frame buffer (0 when none).
    /// </summary>
    public long LastFrame { get; set; }

    /// <summary>
    /// Gets or sets how many frames the buffer holds.
    /// </summary>
    public int FrameBufferSize { get; set; }

    /// <summary>
    /// Gets or sets the number of the newest log line (0 when none).
    /// </summary>
    public long LastLogLine { get; set; }
}

/// <summary>
/// The hub's members.
/// </summary>
public class XmsgHubMembersResponse
{
    /// <summary>
    /// Gets or sets the members, in the order they joined.
    /// </summary>
    public List<XmsgHubMemberInfo> Members { get; set; } = new();
}

/// <summary>
/// One member of the hub.
/// </summary>
public class XmsgHubMemberInfo
{
    /// <summary>
    /// Gets or sets the member's remote address.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the kind: Machine, Hub or Uplink.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it joined, in UTC.
    /// </summary>
    public DateTime JoinedUtc { get; set; }

    /// <summary>
    /// Gets or sets the frames it sent to the hub.
    /// </summary>
    public long FramesFromMember { get; set; }

    /// <summary>
    /// Gets or sets the frames the hub queued to it.
    /// </summary>
    public long FramesToMember { get; set; }

    /// <summary>
    /// Gets or sets the frames it lost because it had fallen behind.
    /// </summary>
    public long FramesDropped { get; set; }

    /// <summary>
    /// Gets or sets the frames waiting in its queue now.
    /// </summary>
    public int QueueLength { get; set; }
}

/// <summary>
/// A page of the hub's log lines.
/// </summary>
public class XmsgHubLogResponse
{
    /// <summary>
    /// Gets or sets the lines, oldest first.
    /// </summary>
    public List<XmsgHubLogLine> Lines { get; set; } = new();

    /// <summary>
    /// Gets or sets the number of the newest line the hub has; ask for lines after this next time.
    /// </summary>
    public long LastLine { get; set; }
}

/// <summary>
/// One hub log line.
/// </summary>
public class XmsgHubLogLine
{
    /// <summary>
    /// Gets or sets the line's number.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Gets or sets when it was logged, in UTC.
    /// </summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>
    /// Gets or sets the text.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// A page of the frames the hub carried.
/// </summary>
/// <remarks>
/// Each frame appears once, as it went from its sender to the hub; the hub repeats it to every other member.
/// </remarks>
public class XmsgHubFramesResponse
{
    /// <summary>
    /// Gets or sets the frames, oldest first.
    /// </summary>
    public List<XmsgHubFrameInfo> Frames { get; set; } = new();

    /// <summary>
    /// Gets or sets the number of the newest frame the hub has.
    /// </summary>
    public long LastFrame { get; set; }

    /// <summary>
    /// Gets or sets whether frames after the requested number were already gone from the buffer; the first frame returned
    /// is then not the one right after it.
    /// </summary>
    public bool Gap { get; set; }
}

/// <summary>
/// One frame the hub carried.
/// </summary>
public class XmsgHubFrameInfo
{
    /// <summary>
    /// Gets or sets the frame's number, counting from 1 since the server started.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Gets or sets when it arrived, in UTC.
    /// </summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>
    /// Gets or sets the member it came from.
    /// </summary>
    public string Member { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sending system number, or 0 when not an ND frame.
    /// </summary>
    public int SourceSystem { get; set; }

    /// <summary>
    /// Gets or sets the destination system number, or 0 when not an ND frame.
    /// </summary>
    public int DestinationSystem { get; set; }

    /// <summary>
    /// Gets or sets whether it parsed as an ND/COSMOS 802.3 frame.
    /// </summary>
    public bool IsNdFrame { get; set; }

    /// <summary>
    /// Gets or sets the frame length in bytes.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Gets or sets what the hub did with it: Forwarded, DroppedLoop or DroppedTtl.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many members it was sent to.
    /// </summary>
    public int CopiesSent { get; set; }

    /// <summary>
    /// Gets or sets how many members lost it.
    /// </summary>
    public int CopiesDropped { get; set; }

    /// <summary>
    /// Gets or sets the raw Ethernet frame as hexadecimal.
    /// </summary>
    public string Hex { get; set; } = string.Empty;
}

/// <summary>
/// The hub settings as stored in xmsghub-config.json.
/// </summary>
public class XmsgHubSettingsDto
{
    /// <summary>
    /// Gets or sets whether the hub runs.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the TCP port.
    /// </summary>
    public int Port { get; set; } = 5010;

    /// <summary>
    /// Gets or sets the address to listen on.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets another hub to join, as host:port, or empty.
    /// </summary>
    public string Uplink { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the capture file, or empty.
    /// </summary>
    public string CaptureFile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many frames are kept.
    /// </summary>
    public int FrameBufferSize { get; set; } = 4096;

    /// <summary>
    /// Gets or sets how many log lines are kept.
    /// </summary>
    public int LogBufferSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the seconds between status lines.
    /// </summary>
    public int StatusIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets whether the saved settings differ from the ones the running hub uses, so a restart is needed.
    /// </summary>
    public bool RestartRequired { get; set; }
}
