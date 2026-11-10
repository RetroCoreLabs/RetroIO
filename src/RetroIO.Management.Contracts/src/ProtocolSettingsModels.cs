//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// The DriveWire settings, as stored in drivewire-config.json. Read at start: a change applies after a restart.
/// </summary>
public class DriveWireSettingsDto
{
    /// <summary>
    /// Gets or sets whether the DriveWire server runs.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the TCP port (default 65504).
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Gets or sets the address to listen on (default 127.0.0.1, this computer only).
    /// </summary>
    public string BindAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the byte time limit inside a transaction, in milliseconds (default 250).
    /// </summary>
    public int TransactionTimeoutMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets the drives to mount.
    /// </summary>
    public List<DriveWireDriveSettingDto> Drives { get; set; } = new();

    /// <summary>
    /// Gets or sets the folder named objects are mounted from, or null for none.
    /// </summary>
    public string? NamedObjectDirectory { get; set; }

    /// <summary>
    /// Gets or sets the folder print jobs are written to, or null for none.
    /// </summary>
    public string? PrintDirectory { get; set; }
}

/// <summary>
/// One DriveWire drive in the settings.
/// </summary>
public class DriveWireDriveSettingDto
{
    /// <summary>
    /// Gets or sets the drive number, 0 to 255.
    /// </summary>
    public int Drive { get; set; }

    /// <summary>
    /// Gets or sets the image file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether writes are refused.
    /// </summary>
    public bool ReadOnly { get; set; }
}

/// <summary>
/// RetroServer's own XMSG node settings, as stored in xmsgnode-config.json. Read at start: a change applies after
/// a restart.
/// </summary>
public class XmsgNodeSettingsDto
{
    /// <summary>
    /// Gets or sets whether the node runs.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets RetroServer's own XMSG system number; 0 means not set.
    /// </summary>
    public int SystemNumber { get; set; }

    /// <summary>
    /// Gets or sets the one machine the node talks to; 0 means not set.
    /// </summary>
    public int PeerSystemNumber { get; set; }

    /// <summary>
    /// Gets or sets the Ethernet segment to join, for example <c>tcp:127.0.0.1:5010</c>.
    /// </summary>
    public string Ethernet { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where XMSG programs connect: the address.
    /// </summary>
    public string ProgramBindAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where XMSG programs connect: the port (default 5011).
    /// </summary>
    public int ProgramPort { get; set; }

    /// <summary>
    /// Gets or sets whether a program token is set. The token itself is never sent back.
    /// </summary>
    public bool ProgramTokenSet { get; set; }

    /// <summary>
    /// Gets or sets, when SAVING only, a new token: null keeps the one stored, empty removes it (no check).
    /// Never filled when reading.
    /// </summary>
    public string? NewProgramToken { get; set; }

    /// <summary>
    /// Gets or sets the lowest level of log line kept from programs: Trace, Debug, Information, Warning, Error,
    /// Critical or None.
    /// </summary>
    public string ProgramLogMinimum { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the middle line of the login banner of RetroServer's own terminal server; null for the built-in one.
    /// </summary>
    public string? Motd { get; set; }
}

/// <summary>
/// The traffic limits and the audit log settings, as stored in security-config.json. Read at start: a change
/// applies after a restart. A limit of null means no limit.
/// </summary>
public class SecuritySettingsDto
{
    /// <summary>
    /// Gets or sets the most requests one user may make per second, or null for no limit.
    /// </summary>
    public int? PerUserRequestsPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the most megabytes one user may move per hour, or null for no limit.
    /// </summary>
    public int? PerUserMegabytesPerHour { get; set; }

    /// <summary>
    /// Gets or sets whether administrators are exempt from the per-user limits.
    /// </summary>
    public bool SkipLimitsForAdmins { get; set; }

    /// <summary>
    /// Gets or sets the most requests one IP address may make per second, or null for no limit.
    /// </summary>
    public int? PerIpRequestsPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the most megabytes one IP address may move per hour, or null for no limit.
    /// </summary>
    public int? PerIpMegabytesPerHour { get; set; }

    /// <summary>
    /// Gets or sets the most requests the whole server takes per second, or null for no limit.
    /// </summary>
    public int? ServerRequestsPerSecond { get; set; }

    /// <summary>
    /// Gets or sets the most megabytes the whole server moves per hour, or null for no limit.
    /// </summary>
    public int? ServerMegabytesPerHour { get; set; }

    /// <summary>
    /// Gets or sets how often the limit counters are saved, in seconds.
    /// </summary>
    public int RateLimitSaveIntervalSeconds { get; set; }

    /// <summary>
    /// Gets or sets whether the saved counters are read back at start.
    /// </summary>
    public bool RestoreRateLimitStateOnStartup { get; set; }

    /// <summary>
    /// Gets or sets how many audit entries are held before they are written.
    /// </summary>
    public int AuditBatchSize { get; set; }

    /// <summary>
    /// Gets or sets the longest time audit entries are held before they are written, in seconds.
    /// </summary>
    public int AuditFlushIntervalSeconds { get; set; }
}

/// <summary>
/// One entry of the mounted volumes list (mounts.json), which Econet discs can name. The password is never sent back.
/// </summary>
public class MountDto
{
    /// <summary>
    /// Gets or sets the volume's id, which a disc names.
    /// </summary>
    public string VolumeId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the storage kind, for example native or tnfs.
    /// </summary>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the storage address.
    /// </summary>
    public string SourceUri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the volume refuses writes.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Gets or sets the file system inside the volume, or null to work it out.
    /// </summary>
    public string? FileSystemType { get; set; }

    /// <summary>
    /// Gets or sets the user name for the storage, or null for none.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets whether a password is stored. The password itself is never sent back.
    /// </summary>
    public bool PasswordSet { get; set; }

    /// <summary>
    /// Gets or sets, when SAVING only, a new password: null keeps the stored one, empty removes it.
    /// </summary>
    public string? NewPassword { get; set; }
}

/// <summary>
/// RetroServer's start-up file (appsettings.json next to the program): log levels, where files are stored, Azure and
/// the management addresses. Read at start; the log levels are also picked up while it runs.
/// </summary>
public class StartupSettingsDto
{
    /// <summary>
    /// Gets or sets the file these settings are in. Shown only; never changed by a save.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the data folder all the other settings files are in. Shown only: it is set by the
    /// RETROFS_DATA_PATH environment variable, not by a file.
    /// </summary>
    public string DataFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the log levels: a log category (Default, Microsoft, RetroIO, ...) and its lowest level kept.
    /// </summary>
    public Dictionary<string, string> LogLevels { get; set; } = new();

    /// <summary>
    /// Gets or sets where files are stored: Local or Azure.
    /// </summary>
    public string FileSystemBackend { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether an Azure connection string is stored. The string itself is never sent back.
    /// </summary>
    public bool AzureConnectionStringSet { get; set; }

    /// <summary>
    /// Gets or sets, when SAVING only, a new Azure connection string: null keeps the stored one, empty removes it.
    /// </summary>
    public string? NewAzureConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the Azure blob container name.
    /// </summary>
    public string AzureContainerName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether Azure is reached with the machine's own identity instead of a connection string.
    /// </summary>
    public bool AzureUseManagedIdentity { get; set; }

    /// <summary>
    /// Gets or sets the Azure storage account address, used with the machine's own identity.
    /// </summary>
    public string AzureStorageAccountUri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the container is made when it is missing.
    /// </summary>
    public bool AzureCreateContainerIfNotExists { get; set; }

    /// <summary>
    /// Gets or sets the addresses the management API listens on, separated by ';'. Empty means the built-in ones
    /// (http://localhost:5000 and https://localhost:5001). A wrong address can stop RetroAdmin reaching the server
    /// after a restart.
    /// </summary>
    public string ManagementUrls { get; set; } = string.Empty;
}
