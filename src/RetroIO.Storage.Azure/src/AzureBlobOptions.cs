//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

namespace RetroIO.Storage.Azure;

/// <summary>
/// Configuration options for Azure Blob Storage backend
/// </summary>
public sealed class AzureBlobOptions
{
    /// <summary>
    /// Azure Storage connection string
    /// Example: "DefaultEndpointsProtocol=https;AccountName=myaccount;AccountKey=...;EndpointSuffix=core.windows.net"
    /// For local development with Azurite: "UseDevelopmentStorage=true"
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Azure Blob container name where files will be stored
    /// Default: "retrofs"
    /// </summary>
    public string ContainerName { get; set; } = "retrofs";

    /// <summary>
    /// Use Azure Managed Identity for authentication instead of connection string
    /// When true, StorageAccountUri must be provided
    /// </summary>
    public bool UseManagedIdentity { get; set; } = false;

    /// <summary>
    /// Storage account URI (required when using Managed Identity)
    /// Example: "https://myaccount.blob.core.windows.net"
    /// </summary>
    public string? StorageAccountUri { get; set; }

    /// <summary>
    /// Local temporary directory for file caching
    /// Files are downloaded to this directory before being opened as FileStream
    /// and uploaded back on close
    /// </summary>
    public string TempCacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "retrofs-azure-cache");

    /// <summary>
    /// Automatically create the container if it doesn't exist
    /// Default: true
    /// </summary>
    public bool CreateContainerIfNotExists { get; set; } = true;

    /// <summary>
    /// Maximum number of cached temp files to keep
    /// Older files are cleaned up when this limit is reached
    /// Default: 100
    /// </summary>
    public int MaxCachedFiles { get; set; } = 100;

    /// <summary>
    /// How long to keep temp cache files before cleanup (in minutes)
    /// Default: 60 minutes
    /// </summary>
    public int TempCacheRetentionMinutes { get; set; } = 60;
}
