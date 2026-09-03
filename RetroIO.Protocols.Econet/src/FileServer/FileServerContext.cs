//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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

public sealed class FileServerContext
{
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

    public SecurityEventEmitter? SecurityEvents { get; }
    public IEconetActivityLog? ActivityLog { get; }

    public IFileSystemBackend FileSystem { get; }
    public EconetUserDirectory Users { get; }
    public DiscCatalog Discs { get; }
    public FileServerSessionManager Sessions { get; }
    public FileServerHandleManager Handles { get; }
    public PathResolver Paths { get; }
    public Bulk.BulkTransferManager BulkTransfers { get; }
    public FileServerConfiguration Configuration { get; }
    public FileServerClock ServerClock { get; }
    public string RootDirectory { get; }
    public string DiscName { get; }
    public string VersionString { get; }

    /// <summary>
    /// Resolves the wire-format profile per client machine (task 0.4). Defaults to all-LaterFS so
    /// unconfigured servers emit the verified later-FS bytes; consulted only where the format diverges.
    /// </summary>
    public WireProfileResolver WireProfiles { get; }
}
