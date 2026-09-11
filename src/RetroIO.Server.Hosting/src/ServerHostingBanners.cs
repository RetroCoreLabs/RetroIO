//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Storage.Azure;
using RetroIO.Security.Implementation.RateLimiting;
using RetroIO.Server.Hosting.Configuration;
using RetroIO.Storage.Utilities;

namespace RetroIO.Server.Hosting;

public static class ServerHostingBanners
{
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

    public static void PrintLocalBackendHeader()
    {
        ConsoleBox.Top();
        ConsoleBox.Title("LOCAL FILESYSTEM BACKEND");
        ConsoleBox.Bottom();
        Console.WriteLine();
    }

    public static void PrintAzureBackendHeader()
    {
        ConsoleBox.Top();
        ConsoleBox.Title("AZURE BLOB STORAGE BACKEND");
        ConsoleBox.Divider();
    }

    public static void PrintAzureBackendDetails(AzureBlobOptions azureOptions)
    {
        ConsoleBox.LabelValue("  Container:       ", azureOptions.ContainerName);
        ConsoleBox.LabelValue("  Auth Method:     ", azureOptions.UseManagedIdentity ? "Managed Identity" : "Connection String");
        ConsoleBox.LabelValue("  Temp Cache:      ", azureOptions.TempCacheDirectory);
        ConsoleBox.Bottom();
        Console.WriteLine();
    }

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
}
