//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Protocols.Econet.FileServer.Bulk;
using RetroIO.Protocols.Econet.FileServer.Configuration;
using RetroIO.Protocols.Econet.FileServer.Discs;
using RetroIO.Protocols.Econet.FileServer.Handles;
using RetroIO.Protocols.Econet.FileServer.Paths;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.FileServer.Users;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Security.Implementation.Auditing;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer;

/// <summary>
/// Holds the services that the file server handlers share.
/// </summary>
public sealed class FileServerContext
{
    /// <summary>
    /// Creates the context from its services.
    /// </summary>
    /// <param name="fileSystem">
    /// The file system backend.
    /// </param>
    /// <param name="users">
    /// The user directory.
    /// </param>
    /// <param name="discs">
    /// The disc catalogue.
    /// </param>
    /// <param name="sessions">
    /// The session manager.
    /// </param>
    /// <param name="handles">
    /// The open file handle manager.
    /// </param>
    /// <param name="paths">
    /// The path resolver.
    /// </param>
    /// <param name="bulkTransfers">
    /// The bulk transfer manager.
    /// </param>
    /// <param name="configuration">
    /// The file server configuration.
    /// </param>
    /// <param name="serverClock">
    /// The server clock.
    /// </param>
    /// <param name="rootDirectory">
    /// The root directory of the file server.
    /// </param>
    /// <param name="discName">
    /// The disc name.
    /// </param>
    /// <param name="versionString">
    /// The version text the server reports.
    /// </param>
    /// <param name="activityLog">
    /// The activity log, or null for none.
    /// </param>
    /// <param name="securityEvents">
    /// The security event emitter, or null for none.
    /// </param>
    /// <param name="wireProfiles">
    /// The wire profile resolver. When null, WireProfileResolver.AllLaterFs is used.
    /// </param>
    public FileServerContext(
        IFileSystemBackend fileSystem,
        EconetUserDirectory users,
        DiscCatalog discs,
        FileServerSessionManager sessions,
        FileServerHandleManager handles,
        PathResolver paths,
        Bulk.BulkTransferManager bulkTransfers,
        FileServerConfiguration configuration,
        FileServerClock serverClock,
        string rootDirectory,
        string discName,
        string versionString,
        IEconetActivityLog? activityLog = null,
        SecurityEventEmitter? securityEvents = null,
        WireProfileResolver? wireProfiles = null)
    {
        FileSystem = fileSystem;
        Users = users;
        Discs = discs;
        Sessions = sessions;
        Handles = handles;
        Paths = paths;
        BulkTransfers = bulkTransfers;
        Configuration = configuration;
        ServerClock = serverClock;
        RootDirectory = rootDirectory;
        DiscName = discName;
        VersionString = versionString;
        ActivityLog = activityLog;
        SecurityEvents = securityEvents;
        // Default to the all-LaterFS resolver so every existing construction keeps the verified bytes;
        // only a server that configures a profile/override gets anything else (task 0.4).
        WireProfiles = wireProfiles ?? WireProfileResolver.AllLaterFs;
    }

    /// <summary>
    /// Gets the security event emitter, or null when there is none.
    /// </summary>
    public SecurityEventEmitter? SecurityEvents { get; }
    /// <summary>
    /// Gets the activity log, or null when there is none.
    /// </summary>
    public IEconetActivityLog? ActivityLog { get; }

    /// <summary>
    /// Gets the file system backend.
    /// </summary>
    public IFileSystemBackend FileSystem { get; }
    /// <summary>
    /// Gets the user directory.
    /// </summary>
    public EconetUserDirectory Users { get; }
    /// <summary>
    /// Gets the disc catalogue.
    /// </summary>
    public DiscCatalog Discs { get; }
    /// <summary>
    /// Gets the session manager.
    /// </summary>
    public FileServerSessionManager Sessions { get; }
    /// <summary>
    /// Gets the open file handle manager.
    /// </summary>
    public FileServerHandleManager Handles { get; }
    /// <summary>
    /// Gets the path resolver.
    /// </summary>
    public PathResolver Paths { get; }
    /// <summary>
    /// Gets the bulk transfer manager.
    /// </summary>
    public Bulk.BulkTransferManager BulkTransfers { get; }
    /// <summary>
    /// Gets the file server configuration.
    /// </summary>
    public FileServerConfiguration Configuration { get; }
    /// <summary>
    /// Gets the server clock.
    /// </summary>
    public FileServerClock ServerClock { get; }
    /// <summary>
    /// Gets the root directory of the file server.
    /// </summary>
    public string RootDirectory { get; }
    /// <summary>
    /// Gets the disc name.
    /// </summary>
    public string DiscName { get; }
    /// <summary>
    /// Gets the version text the server reports.
    /// </summary>
    public string VersionString { get; }

    /// <summary>
    /// Resolves the wire-format profile per client machine (task 0.4). Defaults to all-LaterFS so
    /// unconfigured servers emit the verified later-FS bytes; consulted only where the format diverges.
    /// </summary>
    public WireProfileResolver WireProfiles { get; }
}
