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
    /// Gets or sets whether the Econet file server runs.
    /// </summary>
    public bool Enabled { get; set; }
    /// <summary>
    /// Gets or sets which byte of a client's IP address is its station number (0 to 3; the default 3 is the last).
    /// </summary>
    public byte AunStationOctetIndex { get; set; }
    /// <summary>
    /// Gets or sets the reply format for every machine not listed in <see cref="WireProfileOverrides"/>:
    /// <c>LaterFS</c> (ANFS / DNFS 3.60) or <c>Classic1983</c>.
    /// </summary>
    public string WireProfile { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the machines that get another reply format than the default.
    /// </summary>
    public List<EconetWireProfileOverrideDto> WireProfileOverrides { get; set; } = new();
    /// <summary>
    /// Gets or sets the discs, each from its own storage. Empty: the discs are the folders under the root folder.
    /// </summary>
    public List<EconetDiscDto> Discs { get; set; } = new();
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
    /// Gets or sets whether the Econet file server runs, or null to keep the current value.
    /// </summary>
    public bool? Enabled { get; set; }
    /// <summary>
    /// Gets or sets which byte of a client's IP address is its station number, or null to keep it.
    /// </summary>
    public byte? AunStationOctetIndex { get; set; }
    /// <summary>
    /// Gets or sets the default reply format (<c>LaterFS</c> or <c>Classic1983</c>), or null to keep it.
    /// </summary>
    public string? WireProfile { get; set; }
    /// <summary>
    /// Gets or sets the whole list of per-machine reply formats, or null to keep it.
    /// </summary>
    public List<EconetWireProfileOverrideDto>? WireProfileOverrides { get; set; }
    /// <summary>
    /// Gets or sets the whole disc list, or null to keep it. An empty list goes back to one folder per disc.
    /// </summary>
    public List<EconetDiscDto>? Discs { get; set; }
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

/// <summary>
/// One machine that is served another reply format than the default.
/// </summary>
public class EconetWireProfileOverrideDto
{
    /// <summary>
    /// Gets or sets the machine's network number.
    /// </summary>
    public byte Net { get; set; }

    /// <summary>
    /// Gets or sets the machine's station number.
    /// </summary>
    public byte Station { get; set; }

    /// <summary>
    /// Gets or sets the reply format: <c>LaterFS</c> or <c>Classic1983</c>.
    /// </summary>
    public string Profile { get; set; } = string.Empty;
}

/// <summary>
/// One Econet disc served from its own storage: either a storage given here (provider and address) or a volume from
/// the mounted volumes list (mounts.json), never both.
/// </summary>
public class EconetDiscDto
{
    /// <summary>
    /// Gets or sets the disc number, from 0.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the disc name the BBC sees.
    /// </summary>
    public string AcornName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the folder name in the root, or null for the number followed by the name.
    /// </summary>
    public string? DirName { get; set; }

    /// <summary>
    /// Gets or sets the storage kind (native or tnfs; the server does not take ftp or sftp for a disc yet), or null when <see cref="MountRef"/> is used.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Gets or sets the storage address, or null when <see cref="MountRef"/> is used.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>
    /// Gets or sets whether the disc refuses writes.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Gets or sets the id of a volume in mounts.json, or null when <see cref="Provider"/> and <see cref="Uri"/> are used.
    /// </summary>
    public string? MountRef { get; set; }
}
