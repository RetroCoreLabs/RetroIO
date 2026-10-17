//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroIO.Identity;
using RetroIO.Security.Firewall;
using RetroIO.Security.Implementation.Auditing;
using RetroIO.Security.Implementation.RateLimiting;
using RetroIO.Server.Core.FileSystem;
using RetroIO.Storage.Azure;
using RetroIO.Storage.Utilities;
using RetroIO.Storage.Utilities.Logging;

namespace RetroIO.Server.Hosting;

/// <summary>
/// Service registration helpers for the RetroFS servers.
/// </summary>
public static class RetroServerHostingExtensions
{
    /// <summary>
    /// Creates the RetroFS data folder when needed and points the server JSON log at it.
    /// </summary>
    /// <returns>
    /// The path of the RetroFS data folder.
    /// </returns>
    public static string EnsureRetroFsDataPath()
    {
        string retroFsPath = PathHelper.GetRetroFSPath();
        PathHelper.EnsureDirectory(retroFsPath);
        RetroFsServerJsonLog.Configure(retroFsPath);
        return retroFsPath;
    }

    /// <summary>
    /// Registers a JSON file identity provider and the authentication provider.
    /// </summary>
    /// <remarks>
    /// The user and group files are users.json and groups.json in the idp folder inside the RetroFS data folder.
    /// </remarks>
    /// <param name="services">
    /// The service collection to add to.
    /// </param>
    /// <param name="retroFsPath">
    /// The RetroFS data folder.
    /// </param>
    /// <returns>
    /// The same service collection.
    /// </returns>
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

    /// <summary>
    /// Registers the firewall, the audit logging and the rate limiter.
    /// </summary>
    /// <remarks>
    /// The firewall configuration is loaded from firewall-config.json, or created when missing. The audit logs go to the audit-logs folder with a flush every 5 seconds or at 1000 entries. The rate limiter uses fixed limits: 200 requests per second and 100 MB per hour per user, 200 requests per second and 50 MB per hour per IP address, and 1000 requests per second and 10000 MB per hour for the whole server. Administrators skip the limits, and the limiter state is kept in ratelimit-state.json.
    /// </remarks>
    /// <param name="services">
    /// The service collection to add to.
    /// </param>
    /// <param name="retroFsPath">
    /// The RetroFS data folder.
    /// </param>
    /// <param name="logToConsole">
    /// True to print the rate limit settings to the console.
    /// </param>
    /// <returns>
    /// The same service collection.
    /// </returns>
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

    /// <summary>
    /// Registers the file system backend chosen by the FileSystem:Backend setting.
    /// </summary>
    /// <remarks>
    /// When the setting is Azure, an Azure blob backend is registered, with the options read from the Azure:Storage keys. Any other value, or no value, registers the local file system backend.
    /// </remarks>
    /// <param name="services">
    /// The service collection to add to.
    /// </param>
    /// <param name="configuration">
    /// The application configuration.
    /// </param>
    /// <param name="retroFsPath">
    /// The RetroFS data folder. For Azure, the cache folder azure-cache inside it is used.
    /// </param>
    /// <param name="logToConsole">
    /// True to print the chosen backend to the console.
    /// </param>
    /// <returns>
    /// The same service collection.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the backend is Azure and Azure:Storage:ConnectionString is not set.
    /// </exception>
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
