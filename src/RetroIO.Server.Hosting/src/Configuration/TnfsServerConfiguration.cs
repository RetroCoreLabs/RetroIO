//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Storage.Utilities;

namespace RetroIO.Server.Hosting.Configuration;

public sealed class TnfsServerConfiguration
{
    public string RootDirectory { get; set; } = string.Empty;
    public int UdpPort { get; set; } = 16384;
    public int TcpPort { get; set; } = 16384;
    public bool EnableTcp { get; set; } = true;
    public bool EnableUdp { get; set; } = true;

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
