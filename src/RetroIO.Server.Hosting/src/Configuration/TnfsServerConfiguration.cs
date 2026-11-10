//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Storage.Utilities;

namespace RetroIO.Server.Hosting.Configuration;

/// <summary>
/// Settings of the TNFS server.
/// </summary>
public sealed class TnfsServerConfiguration
{
    /// <summary>
    /// Gets or sets whether the TNFS server runs. Added 2026-11-10 as the per-protocol switch in its own file, like
    /// DriveWire and the XMSG services have. A file written before then has no such key and reads as true, so an
    /// existing install keeps running as it did. (Phase 6 of the plan turns every protocol off by default.)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the root directory of the served files.
    /// </summary>
    public string RootDirectory { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the UDP port. The default is 16384.
    /// </summary>
    public int UdpPort { get; set; } = 16384;
    /// <summary>
    /// Gets or sets the TCP port. The default is 16384.
    /// </summary>
    public int TcpPort { get; set; } = 16384;
    /// <summary>
    /// Gets or sets whether TCP is enabled. The default is true.
    /// </summary>
    public bool EnableTcp { get; set; } = true;
    /// <summary>
    /// Gets or sets whether UDP is enabled. The default is true.
    /// </summary>
    public bool EnableUdp { get; set; } = true;

    // The four limits below were added 2026-11-10. The TNFS server always had them, but they were
    // fixed at the reference daemon's numbers because nothing passed them in from this file.
    // Defaults are tnfsd config.h MAX_TCP_CONN / MAX_SESSIONS / MAX_SESSIONS_PER_IP (4096) and
    // its 600-second session timeout (TnfsConstants in RetroIO.Protocols.Tnfs, not referenced here).

    /// <summary>
    /// Gets or sets the most TCP connections open at one time. The default is 4096.
    /// </summary>
    public int MaxTcpConnections { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the most sessions open at one time. The default is 4096.
    /// </summary>
    public int MaxSessions { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the most sessions one IP address may hold. The default is 4096.
    /// </summary>
    public int MaxSessionsPerIP { get; set; } = 4096;

    /// <summary>
    /// Gets or sets how long a session may be idle before it is dropped, in seconds. The default is 600.
    /// </summary>
    public int SessionTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Creates the default configuration.
    /// </summary>
    /// <remarks>
    /// The root directory is the folder tnfs-root inside the RetroFS data folder. Both ports are 16384 and both TCP and UDP are enabled.
    /// </remarks>
    /// <returns>
    /// A new configuration with the default values.
    /// </returns>
    public static TnfsServerConfiguration GetDefault()
    {
        return new TnfsServerConfiguration
        {
            RootDirectory = Path.Combine(PathHelper.GetRetroFSPath(), "tnfs-root"),
            UdpPort = 16384,
            TcpPort = 16384,
            EnableTcp = true,
            EnableUdp = true,
        };
    }
}
