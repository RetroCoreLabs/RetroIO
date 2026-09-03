//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroIO.Storage.Azure;
using RetroIO.Identity;
using RetroIO.Security.Firewall;
using RetroIO.Security.Implementation.Auditing;
using RetroIO.Security.Implementation.RateLimiting;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Storage.Utilities;
using RetroIO.Storage.Utilities.Logging;

namespace RetroIO.Server.Hosting;

public static class RetroServerHostingExtensions
{
    public static string EnsureRetroFsDataPath()
    {
        string retroFsPath = PathHelper.GetRetroFSPath();
        PathHelper.EnsureDirectory(retroFsPath);
        RetroFsServerJsonLog.Configure(retroFsPath);
        return retroFsPath;
    }

    public static IServiceCollection AddRetroIdentity(this IServiceCollection services, string retroFsPath)
    {
        string idpPath = Path.Combine(retroFsPath, "idp");
        PathHelper.EnsureDirectory(idpPath);

        JsonRetroIDP idp = new JsonRetroIDP(
            usersFilePath: Path.Combine(idpPath, "users.json"),
            groupsFilePath: Path.Combine(idpPath, "groups.json"));

        services.AddSingleton<IRetroIDP>(idp);
        services.AddSingleton<RetroIDPAuthenticationProvider>();
        return services;
    }

    public static IServiceCollection AddRetroSecurity(this IServiceCollection services, string retroFsPath, bool logToConsole = true)
    {
        string firewallPath = Path.Combine(retroFsPath, "firewall-config.json");
        FirewallPersistence firewallPersistence = new FirewallPersistence(firewallPath);
        FirewallConfiguration firewallConfig = firewallPersistence.EnsureDefaultConfigAsync().GetAwaiter().GetResult();
        FirewallEngine firewallEngine = new FirewallEngine(firewallConfig);

        services.AddSingleton(firewallEngine);
        services.AddSingleton(firewallPersistence);
        services.AddSingleton(new FirewallRuleManager(firewallEngine, firewallPersistence));

        string auditPath = Path.Combine(retroFsPath, "audit-logs");
        PathHelper.EnsureDirectory(auditPath);
        FileAuditLogger auditLogger = new FileAuditLogger(auditPath);
        SecurityEventEmitter securityEvents = new SecurityEventEmitter(
            auditLogger, batchSize: 1000, flushInterval: TimeSpan.FromSeconds(5));

        services.AddSingleton(auditLogger);
        services.AddSingleton(securityEvents);

        RateLimitConfig rateLimitConfig = new RateLimitConfig
        {
            PerUserRequestsPerSecond = 200,
            PerUserMegabytesPerHour = 100,
            SkipLimitsForAdmins = true,
            PerIpRequestsPerSecond = 200,
            PerIpMegabytesPerHour = 50,
            ServerRequestsPerSecond = 1000,
            ServerMegabytesPerHour = 10000,
            StatePersistencePath = Path.Combine(retroFsPath, "ratelimit-state.json"),
            PersistenceInterval = TimeSpan.FromMinutes(1),
            RestoreStateOnStartup = true
        };
        InMemoryRateLimiter rateLimiter = new InMemoryRateLimiter(rateLimitConfig);
        services.AddSingleton(rateLimiter);

        if (logToConsole)
            ServerHostingBanners.PrintRateLimitConfiguration(rateLimitConfig);

        return services;
    }

    public static IServiceCollection AddRetroFileSystemBackend(
        this IServiceCollection services,
        IConfiguration configuration,
        string retroFsPath,
        bool logToConsole = true)
    {
        string backendType = configuration["FileSystem:Backend"] ?? "Local";

        if (backendType.Equals("Azure", StringComparison.OrdinalIgnoreCase))
        {
            if (logToConsole)
                ServerHostingBanners.PrintAzureBackendHeader();

            AzureBlobOptions azureOptions = new AzureBlobOptions
            {
                ConnectionString = configuration["Azure:Storage:ConnectionString"]
                    ?? throw new InvalidOperationException("Azure:Storage:ConnectionString is required when using Azure backend"),
                ContainerName = configuration["Azure:Storage:ContainerName"] ?? "retrofs",
                UseManagedIdentity = bool.Parse(configuration["Azure:Storage:UseManagedIdentity"] ?? "false"),
                StorageAccountUri = configuration["Azure:Storage:StorageAccountUri"],
                TempCacheDirectory = Path.Combine(retroFsPath, "azure-cache"),
                CreateContainerIfNotExists = bool.Parse(configuration["Azure:Storage:CreateContainerIfNotExists"] ?? "true")
            };

            PathHelper.EnsureDirectory(azureOptions.TempCacheDirectory);

            if (logToConsole)
                ServerHostingBanners.PrintAzureBackendDetails(azureOptions);

            services.AddSingleton<IFileSystemBackend>(sp =>
                new AzureBlobFileSystemBackend(
                    azureOptions,
                    sp.GetRequiredService<ILogger<AzureBlobFileSystemBackend>>()));
        }
        else
        {
            if (logToConsole)
                ServerHostingBanners.PrintLocalBackendHeader();

            services.AddSingleton<IFileSystemBackend, LocalFileSystemBackend>();
        }

        return services;
    }
}
