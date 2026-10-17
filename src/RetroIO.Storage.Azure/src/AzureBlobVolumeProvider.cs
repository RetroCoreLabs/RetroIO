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
    /// <summary>
    /// Gets the volume type, which is azure.
    /// </summary>
    public string Type => "azure";

    // VFS Properties
    /// <summary>
    /// Gets the URI schemes this provider handles, azblob and azure.
    /// </summary>
    public string[] SupportedSchemes => new[] { "azblob", "azure" };
    /// <summary>
    /// Gets whether the volumes are remote, which is true.
    /// </summary>
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

    /// <summary>
    /// Creates the provider.
    /// </summary>
    /// <param name="logger">
    /// The logger for messages, or null for none.
    /// </param>
    public AzureBlobVolumeProvider(ILogger<AzureBlobVolumeProvider>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks that a mount entry has the azure provider type and a source URI.
    /// </summary>
    /// <param name="entry">
    /// The mount entry to check.
    /// </param>
    /// <param name="errorMessage">
    /// The reason when the check fails, otherwise null.
    /// </param>
    /// <returns>
    /// True when the entry can be mounted. False otherwise.
    /// </returns>
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

    /// <summary>
    /// Mounts a volume from a mount entry, using its source URI, volume identifier and read-only flag.
    /// </summary>
    /// <param name="entry">
    /// The mount entry.
    /// </param>
    /// <returns>
    /// Information about the mounted volume.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the entry fails the check of CanRestore, or when the volume identifier is already mounted.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when the mount fails.
    /// </exception>
    public MountInfo MountFromConfig(UnifiedMountEntry entry)
    {
        if (!CanRestore(entry, out string? error))
        {
            throw new ArgumentException(error);
        }

        return Mount(entry.SourceUri, entry.VolumeId, entry.IsReadOnly);
    }

    /// <summary>
    /// Mounts an Azure blob container as a volume.
    /// </summary>
    /// <remarks>
    /// The URI has the form connection string, a vertical bar character, then the container name. When the container name is left out, retrofs is used. The container is created when it does not exist.
    /// </remarks>
    /// <param name="uri">
    /// The connection string and the optional container name.
    /// </param>
    /// <param name="volumeId">
    /// The identifier for the new volume.
    /// </param>
    /// <param name="readOnly">
    /// True to refuse writes.
    /// </param>
    /// <returns>
    /// Information about the mounted volume. It reports no block access.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume identifier is already mounted.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when the connection or the container creation fails.
    /// </exception>
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

    /// <summary>
    /// Removes a volume from the provider.
    /// </summary>
    /// <param name="volumeId">
    /// The identifier of the volume.
    /// </param>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not found.
    /// </exception>
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

    /// <summary>
    /// Gets information about a mounted volume.
    /// </summary>
    /// <param name="volumeId">
    /// The identifier of the volume.
    /// </param>
    /// <returns>
    /// The mount information, or null when the volume is not found.
    /// </returns>
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

    /// <summary>
    /// Lists the mounted volumes.
    /// </summary>
    /// <returns>
    /// An array with the mount information of each volume.
    /// </returns>
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

    /// <summary>
    /// Downloads a blob into a byte array.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the blob. A leading slash is removed and backslashes are changed to forward slashes.
    /// </param>
    /// <returns>
    /// The content of the blob.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not found.
    /// </exception>
    /// <exception cref="System.IO.FileNotFoundException">
    /// Thrown when the blob does not exist.
    /// </exception>
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

    /// <summary>
    /// Uploads a byte array to a blob and replaces an existing blob.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the blob.
    /// </param>
    /// <param name="data">
    /// The content to write.
    /// </param>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not found.
    /// </exception>
    /// <exception cref="System.UnauthorizedAccessException">
    /// Thrown when the volume is mounted read-only.
    /// </exception>
    /// <exception cref="System.IO.IOException">
    /// Thrown when Azure reports an error.
    /// </exception>
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

    /// <summary>
    /// Deletes a blob if it exists. No error occurs when the blob does not exist.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the blob.
    /// </param>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not found.
    /// </exception>
    /// <exception cref="System.UnauthorizedAccessException">
    /// Thrown when the volume is mounted read-only.
    /// </exception>
    public void DeleteFile(UrnContext context, string path)
    {
        var mount = GetMount(context.VolumeId);

        if (mount.ReadOnly)
            throw new UnauthorizedAccessException("Volume is read-only");

        string blobName = NormalizePath(path);

        var blobClient = mount.ContainerClient.GetBlobClient(blobName);
        blobClient.DeleteIfExists();
    }

    /// <summary>
    /// Lists the blobs and the virtual directories directly under a path.
    /// </summary>
    /// <remarks>
    /// Directories are the blob name prefixes up to the slash character. Files are reported with the file type [BLB].
    /// </remarks>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the directory.
    /// </param>
    /// <returns>
    /// An array with one entry for each virtual directory and each blob.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not found.
    /// </exception>
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

    /// <summary>
    /// Gets the metadata of a blob, or of a virtual directory.
    /// </summary>
    /// <remarks>
    /// A path is a directory when at least one blob name starts with the path followed by a slash.
    /// </remarks>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the blob or the directory.
    /// </param>
    /// <returns>
    /// The metadata, or null when nothing is found or an error occurs.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not found.
    /// </exception>
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

    /// <summary>
    /// Not supported. Always throws.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="address">
    /// The block address.
    /// </param>
    /// <param name="buffer">
    /// The buffer that would receive the block.
    /// </param>
    /// <returns>
    /// Never returns.
    /// </returns>
    /// <exception cref="System.NotSupportedException">
    /// Always thrown, because Azure Blob Storage has no block-level access.
    /// </exception>
    public int ReadBlock(UrnContext context, BlockAddress address, Span<byte> buffer)
    {
        throw new NotSupportedException("Azure Blob Storage does not support block-level access");
    }

    /// <summary>
    /// Not supported. Always throws.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="address">
    /// The block address.
    /// </param>
    /// <param name="data">
    /// The data that would be written.
    /// </param>
    /// <exception cref="System.NotSupportedException">
    /// Always thrown, because Azure Blob Storage has no block-level access.
    /// </exception>
    public void WriteBlock(UrnContext context, BlockAddress address, ReadOnlySpan<byte> data)
    {
        throw new NotSupportedException("Azure Blob Storage does not support block-level access");
    }

    /// <summary>
    /// Returns null, because there is no block access.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <returns>
    /// Always null.
    /// </returns>
    public BlockDeviceInfo? GetBlockDeviceInfo(UrnContext context)
    {
        return null; // No block access
    }

    /// <summary>
    /// Removes every mounted volume. Errors while removing are ignored.
    /// </summary>
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

    /// <summary>
    /// Opens a blob for reading as a stream.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the blob.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels the operation.
    /// </param>
    /// <returns>
    /// A stream that reads the blob.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not found.
    /// </exception>
    public async Task<Stream> OpenReadAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
        AzureBlobMount mount = GetMount(context.VolumeId);
        string blobName = NormalizePath(path);

        BlobClient blobClient = mount.ContainerClient.GetBlobClient(blobName);
        return await blobClient.OpenReadAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Uploads a stream to a blob and replaces an existing blob.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the blob.
    /// </param>
    /// <param name="data">
    /// The stream with the content to write.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels the operation.
    /// </param>
    /// <returns>
    /// A task that completes when the upload is done.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// Thrown when the volume is not found.
    /// </exception>
    /// <exception cref="System.UnauthorizedAccessException">
    /// Thrown when the volume or the context is read-only.
    /// </exception>
    public async Task WriteAsync(UrnContext context, string path, Stream data, CancellationToken cancellationToken)
    {
        AzureBlobMount mount = GetMount(context.VolumeId);

        if (mount.ReadOnly || context.ReadOnly)
            throw new UnauthorizedAccessException("Volume is mounted read-only.");

        string blobName = NormalizePath(path);
        BlobClient blobClient = mount.ContainerClient.GetBlobClient(blobName);

        await blobClient.UploadAsync(data, overwrite: true, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Lists a directory by running ListDirectory on a thread pool thread.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the directory.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels waiting for the task to start.
    /// </param>
    /// <returns>
    /// An array of directory entries.
    /// </returns>
    public async Task<DirectoryEntry[]> ListDirectoryAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
        // Wrap synchronous method (or implement native async if preferred)
        return await Task.Run(() => ListDirectory(context, path), cancellationToken);
    }

    /// <summary>
    /// Gets metadata by running GetMetadata on a thread pool thread.
    /// </summary>
    /// <param name="context">
    /// The context that names the volume.
    /// </param>
    /// <param name="path">
    /// The path of the blob or the directory.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels waiting for the task to start.
    /// </param>
    /// <returns>
    /// The metadata, or null when nothing is found.
    /// </returns>
    public async Task<FileMetadata?> GetMetadataAsync(UrnContext context, string path, CancellationToken cancellationToken)
    {
        // Wrap synchronous method
        return await Task.Run(() => GetMetadata(context, path), cancellationToken);
    }
}
