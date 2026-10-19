//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// Counters for the Econet file server.
/// </summary>
public class EconetStatisticsResponse
{
    /// <summary>
    /// Gets or sets the number of sessions that are open now.
    /// </summary>
    public int ActiveSessions { get; set; }
    /// <summary>
    /// Gets or sets the total number of requests handled.
    /// </summary>
    public long TotalRequests { get; set; }
    /// <summary>
    /// Gets or sets the total number of bytes transferred.
    /// </summary>
    public long BytesTransferred { get; set; }
    /// <summary>
    /// Gets or sets the number of payload bytes received in requests.
    /// </summary>
    public long BytesReceived { get; set; }
    /// <summary>
    /// Gets or sets the number of payload bytes sent in replies.
    /// </summary>
    public long BytesSent { get; set; }
    /// <summary>
    /// Gets or sets the number of requests that were file operations.
    /// </summary>
    public long FileOperations { get; set; }
    /// <summary>
    /// Gets or sets the number of failures: requests that threw and replies that could not be sent.
    /// </summary>
    public long Errors { get; set; }
    /// <summary>
    /// Gets or sets the server uptime as text.
    /// </summary>
    public string Uptime { get; set; } = string.Empty;
}

/// <summary>
/// A list of Econet sessions.
/// </summary>
public class EconetSessionsResponse
{
    /// <summary>
    /// Gets or sets the sessions.
    /// </summary>
    public List<EconetSessionInfo> Sessions { get; set; } = new();
    /// <summary>
    /// Gets or sets the number of sessions.
    /// </summary>
    public int Total { get; set; }
}

/// <summary>
/// One Econet client session.
/// </summary>
public class EconetSessionInfo
{
    /// <summary>
    /// Client station, formatted net.station.
    /// </summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>
    /// User the session is logged in as.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Client network number.
    /// </summary>
    public string ClientNet { get; set; } = string.Empty;

    /// <summary>
    /// Disc the session is currently on.
    /// </summary>
    public int CurrentDisc { get; set; }

    /// <summary>
    /// Whether the session has administrator rights on the file server.
    /// </summary>
    /// <remarks>
    /// Added when RetroServeUI became a client: its Econet monitor shows this column, and the
    /// DTO had dropped it, so the view would have lost the field it used to read off the live
    /// session object.
    /// </remarks>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Session's current directory, in Unix form.
    /// </summary>
    public string CurrentDirectory { get; set; } = string.Empty;
}

/// <summary>
/// The Econet server settings as the management API returns them.
/// </summary>
public class EconetSettingsDto
{
    /// <summary>
    /// Gets or sets the UDP port.
    /// </summary>
    public int UdpPort { get; set; }
    /// <summary>
    /// Gets or sets whether the UDP transport is enabled.
    /// </summary>
    public bool UdpEnabled { get; set; }
    /// <summary>
    /// Gets or sets the network number of the server.
    /// </summary>
    public byte Net { get; set; }
    /// <summary>
    /// Gets or sets the station number of the server.
    /// </summary>
    public byte Station { get; set; }
    /// <summary>
    /// Gets or sets the root folder that is served.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the disc name.
    /// </summary>
    public string DiscName { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the version text, or null when not set.
    /// </summary>
    public string? VersionString { get; set; }
}

/// <summary>
/// A request to change Econet server settings. A property left null is not changed.
/// </summary>
public class UpdateEconetSettingsRequest
{
    /// <summary>
    /// Gets or sets the new UDP port, or null to keep the current one.
    /// </summary>
    public int? UdpPort { get; set; }
    /// <summary>
    /// Gets or sets whether the UDP transport is enabled, or null to keep the current value.
    /// </summary>
    public bool? UdpEnabled { get; set; }
    /// <summary>
    /// Gets or sets the new network number, or null to keep the current one.
    /// </summary>
    public byte? Net { get; set; }
    /// <summary>
    /// Gets or sets the new station number, or null to keep the current one.
    /// </summary>
    public byte? Station { get; set; }
    /// <summary>
    /// Gets or sets the new root folder, or null to keep the current one.
    /// </summary>
    public string? RootPath { get; set; }
    /// <summary>
    /// Gets or sets the new disc name, or null to keep the current one.
    /// </summary>
    public string? DiscName { get; set; }
    /// <summary>
    /// Gets or sets the new version text, or null to keep the current one.
    /// </summary>
    public string? VersionString { get; set; }
}

/// <summary>
/// One command an Econet client issued, as shown in the activity log.
/// </summary>
/// <remarks>
/// Mirrors EconetCommandEntry in RetroIO.Protocols.Econet. It is copied rather than shared
/// because the contract assembly must depend on nothing: a client that references the protocol
/// assembly to read a log entry would be back to depending on the server.
/// </remarks>
public class EconetCommandEntryDto
{
    /// <summary>
    /// When the command was handled, in UTC.
    /// </summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>
    /// Client station, formatted net.station.
    /// </summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>
    /// User the client was logged in as, empty when not logged in.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Function code of the command.
    /// </summary>
    public byte Opcode { get; set; }

    /// <summary>
    /// Command name.
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Arguments or other detail.
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// Whether the command succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Result text, "OK" when it succeeded.
    /// </summary>
    public string Result { get; set; } = "OK";
}

/// <summary>
/// One file transfer, as shown in the activity log.
/// </summary>
public class EconetFileTransferEntryDto
{
    /// <summary>
    /// When the transfer finished, in UTC.
    /// </summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>
    /// Client station, formatted net.station.
    /// </summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>
    /// User the client was logged in as.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// "Send" or "Receive", from the server's point of view.
    /// </summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>
    /// Path of the file transferred.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Bytes transferred.
    /// </summary>
    public long Bytes { get; set; }

    /// <summary>
    /// Whether the transfer completed.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Status text, "OK" when it completed.
    /// </summary>
    public string Status { get; set; } = "OK";
}

/// <summary>
/// Recent Econet activity: commands and file transfers.
/// </summary>
public class EconetActivityResponse
{
    /// <summary>
    /// Most recent commands, newest first.
    /// </summary>
    public List<EconetCommandEntryDto> Commands { get; set; } = new();

    /// <summary>
    /// Most recent file transfers, newest first.
    /// </summary>
    public List<EconetFileTransferEntryDto> Transfers { get; set; } = new();
}
