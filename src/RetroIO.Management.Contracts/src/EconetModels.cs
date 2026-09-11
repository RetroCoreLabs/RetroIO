//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Management.Contracts;

public class EconetStatisticsResponse
{
    public int ActiveSessions { get; set; }
    public long TotalRequests { get; set; }
    public long BytesTransferred { get; set; }
    public string Uptime { get; set; } = string.Empty;
}

public class EconetSessionsResponse
{
    public List<EconetSessionInfo> Sessions { get; set; } = new();
    public int Total { get; set; }
}

public class EconetSessionInfo
{
    /// <summary>Client station, formatted net.station.</summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>User the session is logged in as.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Client network number.</summary>
    public string ClientNet { get; set; } = string.Empty;

    /// <summary>Disc the session is currently on.</summary>
    public int CurrentDisc { get; set; }

    /// <summary>Whether the session has administrator rights on the file server.</summary>
    /// <remarks>
    /// Added when RetroServeUI became a client: its Econet monitor shows this column, and the
    /// DTO had dropped it, so the view would have lost the field it used to read off the live
    /// session object.
    /// </remarks>
    public bool IsAdmin { get; set; }

    /// <summary>Session's current directory, in Unix form.</summary>
    public string CurrentDirectory { get; set; } = string.Empty;
}

public class EconetSettingsDto
{
    public int UdpPort { get; set; }
    public bool UdpEnabled { get; set; }
    public byte Net { get; set; }
    public byte Station { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string DiscName { get; set; } = string.Empty;
    public string? VersionString { get; set; }
}

public class UpdateEconetSettingsRequest
{
    public int? UdpPort { get; set; }
    public bool? UdpEnabled { get; set; }
    public byte? Net { get; set; }
    public byte? Station { get; set; }
    public string? RootPath { get; set; }
    public string? DiscName { get; set; }
    public string? VersionString { get; set; }
}

/// <summary>One command an Econet client issued, as shown in the activity log.</summary>
/// <remarks>
/// Mirrors EconetCommandEntry in RetroIO.Protocols.Econet. It is copied rather than shared
/// because the contract assembly must depend on nothing: a client that references the protocol
/// assembly to read a log entry would be back to depending on the server.
/// </remarks>
public class EconetCommandEntryDto
{
    /// <summary>When the command was handled, in UTC.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Client station, formatted net.station.</summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>User the client was logged in as, empty when not logged in.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Function code of the command.</summary>
    public byte Opcode { get; set; }

    /// <summary>Command name.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Arguments or other detail.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Whether the command succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Result text, "OK" when it succeeded.</summary>
    public string Result { get; set; } = "OK";
}

/// <summary>One file transfer, as shown in the activity log.</summary>
public class EconetFileTransferEntryDto
{
    /// <summary>When the transfer finished, in UTC.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Client station, formatted net.station.</summary>
    public string Station { get; set; } = string.Empty;

    /// <summary>User the client was logged in as.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>"Send" or "Receive", from the server's point of view.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>Path of the file transferred.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Bytes transferred.</summary>
    public long Bytes { get; set; }

    /// <summary>Whether the transfer completed.</summary>
    public bool Success { get; set; }

    /// <summary>Status text, "OK" when it completed.</summary>
    public string Status { get; set; } = "OK";
}

/// <summary>Recent Econet activity: commands and file transfers.</summary>
public class EconetActivityResponse
{
    /// <summary>Most recent commands, newest first.</summary>
    public List<EconetCommandEntryDto> Commands { get; set; } = new();

    /// <summary>Most recent file transfers, newest first.</summary>
    public List<EconetFileTransferEntryDto> Transfers { get; set; } = new();
}
