//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Security.Implementation.RateLimiting;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Azure;
using RetroIO.Storage.Utilities;

namespace RetroIO.Server.Hosting;

/// <summary>
/// Prints the start-up settings of the servers to the console.
/// </summary>
public static class ServerHostingBanners
{
    /// <summary>
    /// Prints the rate limit settings in a box.
    /// </summary>
    /// <param name="rateLimitConfig">
    /// The settings to print.
    /// </param>
    public static void PrintRateLimitConfiguration(RateLimitConfig rateLimitConfig)
    {
        ConsoleBox.Top();
        ConsoleBox.Title("RATE LIMIT CONFIGURATION");
        ConsoleBox.Divider();
        ConsoleBox.Line("  Per-User Limits (Authenticated):");
        ConsoleBox.Bullet("Requests/sec", rateLimitConfig.PerUserRequestsPerSecond);
        ConsoleBox.Bullet("MB/hour", rateLimitConfig.PerUserMegabytesPerHour);
        ConsoleBox.Bullet("Skip for admins", rateLimitConfig.SkipLimitsForAdmins ? "Yes" : "No");
        ConsoleBox.Line(string.Empty);
        ConsoleBox.Line("  Per-IP Limits (Guest/Unauthenticated):");
        ConsoleBox.Bullet("Requests/sec", rateLimitConfig.PerIpRequestsPerSecond);
        ConsoleBox.Bullet("MB/hour", rateLimitConfig.PerIpMegabytesPerHour);
        ConsoleBox.Line(string.Empty);
        ConsoleBox.Line("  Server-Wide Limits:");
        ConsoleBox.Bullet("Requests/sec", rateLimitConfig.ServerRequestsPerSecond);
        ConsoleBox.Bullet("MB/hour", rateLimitConfig.ServerMegabytesPerHour);
        ConsoleBox.Bottom();
    }

    /// <summary>
    /// Prints the header for the local file system backend in a box.
    /// </summary>
    public static void PrintLocalBackendHeader()
    {
        ConsoleBox.Top();
        ConsoleBox.Title("LOCAL FILESYSTEM BACKEND");
        ConsoleBox.Bottom();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the header for the Azure blob storage backend. The box stays open for PrintAzureBackendDetails.
    /// </summary>
    public static void PrintAzureBackendHeader()
    {
        ConsoleBox.Top();
        ConsoleBox.Title("AZURE BLOB STORAGE BACKEND");
        ConsoleBox.Divider();
    }

    /// <summary>
    /// Prints the Azure container, authentication method and cache folder and closes the box.
    /// </summary>
    /// <param name="azureOptions">
    /// The Azure options to print.
    /// </param>
    public static void PrintAzureBackendDetails(AzureBlobOptions azureOptions)
    {
        ConsoleBox.LabelValue("  Container:       ", azureOptions.ContainerName);
        ConsoleBox.LabelValue("  Auth Method:     ", azureOptions.UseManagedIdentity ? "Managed Identity" : "Connection String");
        ConsoleBox.LabelValue("  Temp Cache:      ", azureOptions.TempCacheDirectory);
        ConsoleBox.Bottom();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the TNFS server settings in a box.
    /// </summary>
    /// <param name="config">
    /// The settings to print.
    /// </param>
    public static void PrintTnfsConfiguration(TnfsServerConfiguration config)
    {
        ConsoleBox.Top();
        ConsoleBox.Title("TNFS SERVER CONFIGURATION");
        ConsoleBox.Divider();
        ConsoleBox.LabelValue("  Root Directory:  ", config.RootDirectory);
        ConsoleBox.LabelValue("  UDP Enabled:     ", config.EnableUdp ? "Yes" : "No");
        ConsoleBox.LabelValue("  UDP Port:        ", config.UdpPort);
        ConsoleBox.LabelValue("  TCP Enabled:     ", config.EnableTcp ? "Yes" : "No");
        ConsoleBox.LabelValue("  TCP Port:        ", config.TcpPort);
        ConsoleBox.Bottom();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the Econet file server settings in a box.
    /// </summary>
    /// <param name="config">
    /// The settings to print.
    /// </param>
    public static void PrintEconetConfiguration(EconetServerConfiguration config)
    {
        ConsoleBox.Top();
        ConsoleBox.Title("ECONET FILE SERVER CONFIGURATION");
        ConsoleBox.Divider();
        ConsoleBox.LabelValue("  Root Directory:  ", config.RootDirectory);
        ConsoleBox.LabelValue("  Disc Name:       ", config.DiscName);
        ConsoleBox.LabelValue("  Station:         ", $"{config.Net}.{config.Station}");
        ConsoleBox.LabelValue("  UDP Enabled:     ", config.EnableUdp ? "Yes" : "No");
        ConsoleBox.LabelValue("  UDP Port:        ", config.UdpPort);
        ConsoleBox.Bottom();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the COSMOS Ethernet hub settings in a box.
    /// </summary>
    /// <param name="config">
    /// The settings to print.
    /// </param>
    public static void PrintXmsgHubConfiguration(XmsgHubServerConfiguration config)
    {
        ConsoleBox.Top();
        ConsoleBox.Title("XMSG ETHERNET HUB CONFIGURATION");
        ConsoleBox.Divider();
        ConsoleBox.LabelValue("  Listen:          ", $"{config.BindAddress}:{config.Port}");
        ConsoleBox.LabelValue("  Uplink:          ", string.IsNullOrWhiteSpace(config.Uplink) ? "none" : config.Uplink);
        ConsoleBox.LabelValue("  Capture file:    ", string.IsNullOrWhiteSpace(config.CaptureFile) ? "none" : config.CaptureFile);
        ConsoleBox.LabelValue("  Frames kept:     ", config.FrameBufferSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ConsoleBox.Bottom();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints RetroServer's XMSG node settings in a box.
    /// </summary>
    /// <param name="config">
    /// The settings to print.
    /// </param>
    public static void PrintXmsgNodeConfiguration(XmsgNodeServerConfiguration config)
    {
        ConsoleBox.Top();
        ConsoleBox.Title("XMSG NODE CONFIGURATION");
        ConsoleBox.Divider();
        ConsoleBox.LabelValue("  System number:   ", config.SystemNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ConsoleBox.LabelValue("  Peer:            ", config.PeerSystemNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ConsoleBox.LabelValue("  Ethernet:        ", config.Ethernet);
        ConsoleBox.LabelValue("  Programs:        ", $"{config.ProgramBindAddress}:{config.ProgramPort}");
        // Never print the token itself; only whether one is set.
        ConsoleBox.LabelValue("  Program logs:    ", config.ProgramLogMinimum + " and above");
        ConsoleBox.LabelValue("  Program token:   ", string.IsNullOrEmpty(config.ProgramToken) ? "none (insecure mode)" : "set");
        ConsoleBox.Bottom();
        Console.WriteLine();
    }

    /// <summary>
    /// Prints the DriveWire server settings and drives in a box.
    /// </summary>
    /// <param name="config">
    /// The settings to print.
    /// </param>
    public static void PrintDriveWireConfiguration(DriveWireServerConfiguration config)
    {
        ConsoleBox.Top();
        ConsoleBox.Title("DRIVEWIRE SERVER CONFIGURATION");
        ConsoleBox.Divider();
        ConsoleBox.LabelValue("  Listen:          ", $"{config.BindAddress}:{config.Port}");
        ConsoleBox.LabelValue("  Byte Timeout:    ", $"{config.TransactionTimeoutMilliseconds} ms");
        if (config.Drives.Count == 0)
        {
            ConsoleBox.LabelValue("  Drives:          ", "none");
        }

        for (int i = 0; i < config.Drives.Count; i++)
        {
            DriveWireDriveConfiguration drive = config.Drives[i];
            ConsoleBox.LabelValue($"  Drive {drive.Drive,-3}       ", drive.ReadOnly ? drive.Path + " (read-only)" : drive.Path);
        }

        ConsoleBox.Bottom();
        Console.WriteLine();
    }
}
