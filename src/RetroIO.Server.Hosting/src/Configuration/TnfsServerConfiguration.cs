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
