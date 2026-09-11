//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Storage.Azure;

/// <summary>
/// Azure Blob Storage implementation of IFileSystemBackend
/// Maps virtual file paths to Azure Blob Storage with directory simulation
/// </summary>
public sealed class AzureBlobFileSystemBackend : IFileSystemBackend
{
    private readonly AzureBlobOptions _options;
    private readonly BlobContainerClient _containerClient;
    private readonly ILogger<AzureBlobFileSystemBackend> _logger;

    public AzureBlobFileSystemBackend(
        AzureBlobOptions options,
        ILogger<AzureBlobFileSystemBackend> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Create blob service client
        BlobServiceClient blobServiceClient;

        if (_options.UseManagedIdentity)
        {
            if (string.IsNullOrEmpty(_options.StorageAccountUri))
                throw new InvalidOperationException("StorageAccountUri is required when using Managed Identity");

            _logger.LogInformation("Using Managed Identity for Azure Blob Storage authentication");
            blobServiceClient = new BlobServiceClient(
                new Uri(_options.StorageAccountUri),
                new DefaultAzureCredential());
        }
        else
        {
            if (string.IsNullOrEmpty(_options.ConnectionString))
                throw new InvalidOperationException("ConnectionString is required when not using Managed Identity");

            _logger.LogInformation("Using Connection String for Azure Blob Storage authentication");
            blobServiceClient = new BlobServiceClient(_options.ConnectionString);
        }

        // Get container client
        _containerClient = blobServiceClient.GetBlobContainerClient(_options.ContainerName);

        // Create container if needed
        if (_options.CreateContainerIfNotExists)
        {
            _containerClient.CreateIfNotExists();
            _logger.LogInformation("Azure Blob container '{Container}' ready", _options.ContainerName);
        }

        // Ensure temp cache directory exists
        Directory.CreateDirectory(_options.TempCacheDirectory);
        _logger.LogInformation("Temp cache directory: {TempDir}", _options.TempCacheDirectory);
    }

    /// <summary>
    /// Normalize path for Azure Blob Storage
    /// Removes leading slash and converts backslashes to forward slashes
    /// </summary>
    private string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return string.Empty;

        return path.TrimStart('/').Replace('\\', '/');
    }

    /// <summary>
    /// Check if a directory exists by checking if any blobs have this prefix
    /// </summary>
    public bool DirectoryExists(string path)
    {
        var prefix = NormalizePath(path);

        // Root always exists
        if (string.IsNullOrEmpty(prefix))
            return true;

        // Add trailing slash to ensure we're looking for directory contents
        if (!prefix.EndsWith('/'))
            prefix += "/";

        try
        {
            // Check if any blobs exist with this prefix
            var blobs = _containerClient.GetBlobs(prefix: prefix).Take(1);
            var exists = blobs.Any();

            _logger.LogDebug("DirectoryExists('{Path}'): {Exists}", path, exists);
            return exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if directory exists: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Create a directory by writing a zero-byte marker blob.
    /// </summary>
    /// <remarks>
    /// Blob storage has no real directories — a directory "exists" when a blob with its prefix
    /// exists. Writing an empty marker blob named <c>&lt;dir&gt;/.keep</c> makes an otherwise-empty
    /// directory visible to <see cref="DirectoryExists"/>.
    /// </remarks>
    /// <param name="path">
    /// The directory path to create.
    /// </param>
    public void CreateDirectory(string path)
    {
        var prefix = NormalizePath(path);
        if (string.IsNullOrEmpty(prefix))
            return;

        if (!prefix.EndsWith('/'))
            prefix += "/";

        string markerName = prefix + ".keep";
        try
        {
            var blobClient = _containerClient.GetBlobClient(markerName);
            using MemoryStream empty = new MemoryStream(Array.Empty<byte>());
            blobClient.Upload(empty, overwrite: true);
            _logger.LogDebug("CreateDirectory('{Path}') wrote marker {Marker}", path, markerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating directory: {Path}", path);
        }
    }

    /// <summary>
    /// Check if a file exists (as a blob)
    /// </summary>
    public bool FileExists(string path)
    {
        var blobName = NormalizePath(path);

        if (string.IsNullOrEmpty(blobName))
            return false;

        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            var exists = blobClient.Exists();

            _logger.LogDebug("FileExists('{Path}'): {Exists}", path, exists);
            return exists;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if file exists: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Enumerate all file system entries in a directory
    /// Returns both files (blobs) and subdirectories (prefixes)
    /// </summary>
    public IEnumerable<string> EnumerateFileSystemEntries(string path)
    {
        var prefix = NormalizePath(path);

        if (!string.IsNullOrEmpty(prefix))
            prefix += "/";

        _logger.LogDebug("EnumerateFileSystemEntries('{Path}') with prefix '{Prefix}'", path, prefix);

        var results = _containerClient.GetBlobsByHierarchy(
            delimiter: "/",
            prefix: prefix);

        var entries = new List<string>();

        foreach (var item in results)
        {
            if (item.IsPrefix)
            {
                // Virtual directory (common prefix)
                var dirPath = "/" + item.Prefix.TrimEnd('/');
                entries.Add(dirPath);
                _logger.LogTrace("  Found directory: {Dir}", dirPath);
            }
            else if (item.IsBlob)
            {
                // Actual blob (file)
                var filePath = "/" + item.Blob.Name;
                entries.Add(filePath);
                _logger.LogTrace("  Found file: {File}", filePath);
            }
        }

        _logger.LogDebug("EnumerateFileSystemEntries('{Path}'): Found {Count} entries", path, entries.Count);
        return entries;
    }

    /// <summary>
    /// Get the file name from a path (local operation)
    /// </summary>
    public string GetFileName(string path)
    {
        return Path.GetFileName(path);
    }

    /// <summary>
    /// Determine if a path represents a directory
    /// Checks if path exists as a prefix (has child blobs)
    /// </summary>
    public bool IsDirectory(string path)
    {
        var prefix = NormalizePath(path);

        // Root is always a directory
        if (string.IsNullOrEmpty(prefix))
            return true;

        // Check if it's a virtual directory (has children)
        if (!prefix.EndsWith('/'))
            prefix += "/";

        try
        {
            var hasChildren = _containerClient.GetBlobs(prefix: prefix).Take(1).Any();
            _logger.LogDebug("IsDirectory('{Path}'): {IsDir}", path, hasChildren);
            return hasChildren;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if path is directory: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Get file information from blob properties
    /// </summary>
    public FileInfo? GetFileInfo(string path)
    {
        var blobName = NormalizePath(path);

        if (string.IsNullOrEmpty(blobName))
            return null;

        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);

            if (!blobClient.Exists())
                return null;

            var properties = blobClient.GetProperties();

            // Create a temporary file to return FileInfo
            // This is a workaround since we can't create FileInfo without a real file
            var tempFile = Path.Combine(_options.TempCacheDirectory, $"info_{Guid.NewGuid()}.tmp");
            File.WriteAllText(tempFile, "");

            var fileInfo = new FileInfo(tempFile)
            {
                // Note: FileInfo is immutable for most properties, so we return a temp file
                // The caller should use the Length property from this workaround
            };

            // Clean up temp file
            File.Delete(tempFile);

            _logger.LogDebug("GetFileInfo('{Path}'): Size={Size}, LastModified={Modified}",
                path, properties.Value.ContentLength, properties.Value.LastModified);

            return fileInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting file info: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Open a file stream
    /// Downloads blob to temp file for reading, creates temp file for writing
    /// Upload occurs when stream is disposed
    /// </summary>
    public FileStream OpenFile(string path, FileMode mode, FileAccess access)
    {
        var blobName = NormalizePath(path);
        var blobClient = _containerClient.GetBlobClient(blobName);
        var tempFile = Path.Combine(_options.TempCacheDirectory, $"{Guid.NewGuid()}.tmp");

        _logger.LogInformation("OpenFile('{Path}', Mode={Mode}, Access={Access})", path, mode, access);

        try
        {
            // Download existing blob to temp file if opening for read or append
            if ((mode == FileMode.Open || mode == FileMode.OpenOrCreate || mode == FileMode.Append) &&
                (access == FileAccess.Read || access == FileAccess.ReadWrite))
            {
                if (blobClient.Exists())
                {
                    _logger.LogDebug("Downloading blob {BlobName} to temp file {TempFile}", blobName, tempFile);
                    blobClient.DownloadTo(tempFile);
                }
                else if (mode == FileMode.Open)
                {
                    throw new FileNotFoundException($"Blob not found: {blobName}", path);
                }
            }

            // Open temp file with specified mode and access
            var fileStream = File.Open(tempFile, mode, access);

            // Wrap in AzureBlobFileStream for upload-on-close behavior
            var azureStream = new AzureBlobFileStream(fileStream, blobClient, tempFile, access, _logger);

            _logger.LogDebug("Opened temp file {TempFile} for blob {BlobName}", tempFile, blobName);

            return azureStream;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening file: {Path}", path);

            // Clean up temp file on error
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }

            throw;
        }
    }

    /// <summary>
    /// Get the full path (normalized)
    /// </summary>
    public string GetFullPath(string path)
    {
        // Normalize to Azure blob path format
        var normalized = NormalizePath(path);
        return "/" + normalized;
    }

    /// <summary>
    /// Combine path segments
    /// </summary>
    public string CombinePath(params string[] paths)
    {
        if (paths == null || paths.Length == 0)
            return "/";

        // Use Path.Combine then normalize to forward slashes
        var combined = Path.Combine(paths);
        return combined.Replace('\\', '/');
    }
}
