//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Concurrent;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using RetroIO.Storage.Core;

namespace RetroIO.Storage.Azure;

/// <summary>
/// Volume provider for Azure Blob Storage
/// Allows direct mounting of Azure containers as RetroFS volumes
/// </summary>
public sealed class AzureBlobVolumeProvider : IVolumeProvider
{
    public string Type => "azure";

    // VFS Properties
    public string[] SupportedSchemes => new[] { "azblob", "azure" };
    public bool IsRemote => true;

    private readonly ILogger<AzureBlobVolumeProvider>? _logger;
    private readonly ConcurrentDictionary<string, AzureBlobMount> _mounts = new();

    private sealed class AzureBlobMount
    {
        public required string VolumeId { get; init; }
        public required string ConnectionString { get; init; }
        public required string ContainerName { get; init; }
        public required string SourceUri { get; init; }
        public required bool ReadOnly { get; init; }
        public required BlobContainerClient ContainerClient { get; init; }
    }

    public AzureBlobVolumeProvider(ILogger<AzureBlobVolumeProvider>? logger = null)
    {
        _logger = logger;
    }

    public bool CanRestore(UnifiedMountEntry entry, out string? errorMessage)
    {
        if (entry.ProviderType != Type)
        {
            errorMessage = $"Type mismatch: expected '{Type}', got '{entry.ProviderType}'";
            return false;
        }

        if (string.IsNullOrEmpty(entry.SourceUri))
        {
            errorMessage = "SourceUri (connection string) is required";
            return false;
        }

        errorMessage = null;
        return true;
    }

    public MountInfo MountFromConfig(UnifiedMountEntry entry)
    {
        if (!CanRestore(entry, out string? error))
        {
            throw new ArgumentException(error);
        }

        return Mount(entry.SourceUri, entry.VolumeId, entry.IsReadOnly);
    }

    public MountInfo Mount(string uri, string volumeId, bool readOnly)
    {
        if (_mounts.ContainsKey(volumeId))
        {
            throw new ArgumentException($"Volume '{volumeId}' is already mounted");
        }

        // URI format: connection_string|container_name
        // Or for Azurite: UseDevelopmentStorage=true|container_name
        string[] parts = uri.Split('|', 2);
        string connectionString = parts.Length > 0 ? parts[0] : throw new ArgumentException("Connection string required in URI");
        string containerName = parts.Length > 1 ? parts[1] : "retrofs";

        try
        {
            BlobServiceClient blobServiceClient =
                new BlobServiceClient(connectionString, BuildClientOptions(connectionString));
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(containerName);

            // Create container if it doesn't exist
            containerClient.CreateIfNotExists();

            var mount = new AzureBlobMount
            {
                VolumeId = volumeId,
                ConnectionString = connectionString,
                ContainerName = containerName,
                SourceUri = uri,
                ReadOnly = readOnly,
                ContainerClient = containerClient
            };

            _mounts[volumeId] = mount;

            _logger?.LogInformation("Mounted Azure Blob container '{Container}' as volume '{Volume}'",
                containerName, volumeId);

            return new MountInfo
            {
                VolumeId = volumeId,
                Type = Type,
                FileSystemType = "Azure Blob",
                VolumeLabel = containerName,
                IsReadOnly = readOnly,
                HasBlockAccess = false,
                SourceUri = uri
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to mount Azure Blob container '{Container}'", containerName);
            throw new IOException($"Failed to mount Azure Blob container: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds the blob client options for a connection string, shortening the retry ladder for
    /// local emulator endpoints.
    /// </summary>
    /// <param name="connectionString">
    /// The Azure Storage connection string being mounted.
    /// </param>
    /// <returns>
    /// Client options tuned for the endpoint, or default options for real Azure Storage.
    /// </returns>
    /// <remarks>
    /// <para><b>Why the endpoint matters</b></para>
    /// Against real Azure Storage the SDK default of six retries with exponential backoff is
    /// exactly right: the network is far away, and transient failures are normal and worth riding
    /// out. Against a local emulator it is not — a refused connection on <c>127.0.0.1</c> means
    /// nothing is listening, and no amount of waiting changes that. The default ladder turns that
    /// instant, certain answer into roughly 37 seconds of pointless waiting per operation.
    /// <para>
    /// So the ladder is shortened only when the connection string names the development storage
    /// emulator or a loopback endpoint. Real deployments keep the SDK defaults untouched.
    /// </para>
    /// </remarks>
    private static BlobClientOptions? BuildClientOptions(string connectionString)
    {
        if (!IsLocalEmulator(connectionString))
            return null;   // real Azure - leave the SDK's own resilience alone

        BlobClientOptions options = new BlobClientOptions();
        options.Retry.MaxRetries = 1;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
        return options;
    }

    /// <summary>
    /// Determines whether a connection string points at a local storage emulator.
    /// </summary>
    /// <param name="connectionString">
    /// The Azure Storage connection string to inspect.
    /// </param>
    /// <returns>
    /// True for Azurite and other loopback endpoints.
    /// </returns>
    private static bool IsLocalEmulator(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
            return false;

        // "UseDevelopmentStorage=true" is the shorthand; an explicit devstoreaccount1 string spells
        // the loopback endpoints out instead, so check for both forms.
        return connectionString.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains("127.0.0.1", StringComparison.Ordinal)
            || connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase);
    }

    public void Unmount(string volumeId)
    {
        if (_mounts.TryRemove(volumeId, out var mount))
        {
            _logger?.LogInformation("Unmounted Azure Blob volume '{Volume}'", volumeId);
        }
        else
        {
            throw new ArgumentException($"Volume '{volumeId}' not found");
        }
    }

    public MountInfo? GetMountInfo(string volumeId)
    {
        if (!_mounts.TryGetValue(volumeId, out var mount))
            return null;

        return new MountInfo
        {
            VolumeId = mount.VolumeId,
            Type = Type,
            FileSystemType = "Azure Blob",
            VolumeLabel = mount.ContainerName,
            IsReadOnly = mount.ReadOnly,
            HasBlockAccess = false,
            SourceUri = mount.SourceUri
        };
    }

    public MountInfo[] ListMounts()
    {
        var mounts = new List<MountInfo>();
        foreach (var mount in _mounts.Values)
        {
            var info = GetMountInfo(mount.VolumeId);
            if (info != null)
                mounts.Add(info);
        }
        return mounts.ToArray();
    }

    public byte[] ReadFile(UrnContext context, string path)
    {
        var mount = GetMount(context.VolumeId);
        string blobName = NormalizePath(path);

        try
        {
            var blobClient = mount.ContainerClient.GetBlobClient(blobName);
            using var stream = new MemoryStream();
            blobClient.DownloadTo(stream);
            return stream.ToArray();
        }
        catch (global::Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            throw new FileNotFoundException($"File not found: {path}");
        }
    }

    public void WriteFile(UrnContext context, string path, byte[] data)
    {
        var mount = GetMount(context.VolumeId);

        if (mount.ReadOnly)
            throw new UnauthorizedAccessException("Volume is read-only");

        string blobName = NormalizePath(path);

        try
        {
            var blobClient = mount.ContainerClient.GetBlobClient(blobName);
            using var stream = new MemoryStream(data);
            blobClient.Upload(stream, overwrite: true);
        }
        catch (global::Azure.RequestFailedException ex)
        {
            throw new IOException($"Azure Blob Storage error writing '{path}' (blob: '{blobName}'): {ex.Message} (Status: {ex.Status}, ErrorCode: {ex.ErrorCode})", ex);
        }
    }

    public void DeleteFile(UrnContext context, string path)
    {
        var mount = GetMount(context.VolumeId);

        if (mount.ReadOnly)
            throw new UnauthorizedAccessException("Volume is read-only");

        string blobName = NormalizePath(path);

        var blobClient = mount.ContainerClient.GetBlobClient(blobName);
        blobClient.DeleteIfExists();
    }

    public DirectoryEntry[] ListDirectory(UrnContext context, string path)
    {
        var mount = GetMount(context.VolumeId);
        string prefix = NormalizePath(path);

        if (!string.IsNullOrEmpty(prefix))
            prefix += "/";

        var entries = new List<DirectoryEntry>();

        var results = mount.ContainerClient.GetBlobsByHierarchy(
            delimiter: "/",
            prefix: prefix);

        foreach (var item in results)
        {
            if (item.IsPrefix)
            {
                // Virtual directory
                string dirName = item.Prefix.TrimEnd('/');
                int lastSlash = dirName.LastIndexOf('/');
                dirName = lastSlash >= 0 ? dirName.Substring(lastSlash + 1) : dirName;

                entries.Add(new DirectoryEntry
                {
                    Name = dirName,
                    Metadata = new FileMetadata
                    {
                        IsDirectory = true,
                        Size = 0
                    }
                });
            }
            else if (item.IsBlob)
            {
                // Actual file
                string fileName = item.Blob.Name;
                int lastSlash = fileName.LastIndexOf('/');
                fileName = lastSlash >= 0 ? fileName.Substring(lastSlash + 1) : fileName;

                entries.Add(new DirectoryEntry
                {
                    Name = fileName,
                    Metadata = new FileMetadata
                    {
                        IsDirectory = false,
                        Size = item.Blob.Properties.ContentLength ?? 0,
                        FileType = "[BLB]"
                    }
                });
            }
        }

        return entries.ToArray();
    }

    public FileMetadata? GetMetadata(UrnContext context, string path)
    {
        var mount = GetMount(context.VolumeId);
        string blobName = NormalizePath(path);

        try
        {
            var blobClient = mount.ContainerClient.GetBlobClient(blobName);
            if (blobClient.Exists())
            {
                var props = blobClient.GetProperties();
                return new FileMetadata
                {
                    IsDirectory = false,
                    Size = props.Value.ContentLength,
                    FileType = "[BLB]"
                };
            }

            // Check if it's a virtual directory
            string dirPrefix = blobName + "/";
            var hasChildren = mount.ContainerClient.GetBlobs(prefix: dirPrefix).Take(1).Any();
            if (hasChildren)
            {
                return new FileMetadata
                {
                    IsDirectory = true,
                    Size = 0
                };
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
    {
        throw new NotSupportedException("Azure Blob Storage does not support block-level access");
    }

    public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
    {
        throw new NotSupportedException("Azure Blob Storage does not support block-level access");
    }

    public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
    {
        return null; // No block access
    }

    public void Dispose()
    {
        foreach (var volumeId in _mounts.Keys.ToArray())
        {
            try
            {
                Unmount(volumeId);
            }
            catch { }
        }
    }

    private AzureBlobMount GetMount(string volumeId)
    {
        if (!_mounts.TryGetValue(volumeId, out var mount))
            throw new ArgumentException($"Volume '{volumeId}' not found");
        return mount;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return string.Empty;

        return path.TrimStart('/').Replace('\\', '/');
    }

    // ==================== VFS Async Methods ====================

    public async Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
        AzureBlobMount mount = GetMount(context.VolumeId);
        string blobName = NormalizePath(path);

        BlobClient blobClient = mount.ContainerClient.GetBlobClient(blobName);
        return await blobClient.OpenReadAsync(cancellationToken: cancellationToken);
    }

    public async Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
    {
        AzureBlobMount mount = GetMount(context.VolumeId);

        if (mount.ReadOnly || context.ReadOnly)
            throw new UnauthorizedAccessException("Volume is mounted read-only.");

        string blobName = NormalizePath(path);
        BlobClient blobClient = mount.ContainerClient.GetBlobClient(blobName);

        await blobClient.UploadAsync(data, overwrite: true, cancellationToken: cancellationToken);
    }

    public async Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
        // Wrap synchronous method (or implement native async if preferred)
        return await Task.Run(() => ListDirectory(context, path), cancellationToken);
    }

    public async Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
        // Wrap synchronous method
        return await Task.Run(() => GetMetadata(context, path), cancellationToken);
    }
}
