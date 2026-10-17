//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace RetroIO.Storage.Azure;

/// <summary>
/// FileStream wrapper that uploads to Azure Blob Storage on close
/// Uses a temporary local file for read/write operations
/// </summary>
public sealed class AzureBlobFileStream : FileStream
{
    private readonly BlobClient _blobClient;
    private readonly string _tempFilePath;
    private readonly FileAccess _access;
    private readonly ILogger? _logger;
    private bool _disposed;

    public AzureBlobFileStream(
        FileStream innerStream,
        BlobClient blobClient,
        string tempFilePath,
        FileAccess access,
        ILogger? logger = null)
        : base(innerStream.SafeFileHandle, access)
    {
        _blobClient = blobClient ?? throw new ArgumentNullException(nameof(blobClient));
        _tempFilePath = tempFilePath ?? throw new ArgumentNullException(nameof(tempFilePath));
        _access = access;
        _logger = logger;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        try
        {
            // Flush any pending writes
            if (disposing && CanWrite)
            {
                Flush();
            }

            // Upload to blob if file was opened for writing
            if (disposing && (_access == FileAccess.Write || _access == FileAccess.ReadWrite))
            {
                try
                {
                    _logger?.LogDebug("Uploading temp file {TempFile} to blob {BlobName}",
                        _tempFilePath, _blobClient.Name);

                    _blobClient.Upload(_tempFilePath, overwrite: true);

                    _logger?.LogDebug("Successfully uploaded {BlobName} ({Size} bytes)",
                        _blobClient.Name, new FileInfo(_tempFilePath).Length);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to upload temp file {TempFile} to blob {BlobName}",
                        _tempFilePath, _blobClient.Name);
                    throw;
                }
            }
        }
        finally
        {
            // Clean up temp file
            try
            {
                if (File.Exists(_tempFilePath))
                {
                    File.Delete(_tempFilePath);
                    _logger?.LogDebug("Deleted temp file {TempFile}", _tempFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete temp file {TempFile}", _tempFilePath);
                // Don't throw - temp file cleanup is not critical
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            await base.DisposeAsync();
            return;
        }

        try
        {
            // Flush any pending writes
            if (CanWrite)
            {
                await FlushAsync();
            }

            // Upload to blob if file was opened for writing
            if (_access == FileAccess.Write || _access == FileAccess.ReadWrite)
            {
                try
                {
                    _logger?.LogDebug("Async uploading temp file {TempFile} to blob {BlobName}",
                        _tempFilePath, _blobClient.Name);

                    await _blobClient.UploadAsync(_tempFilePath, overwrite: true);

                    _logger?.LogDebug("Successfully uploaded {BlobName} ({Size} bytes)",
                        _blobClient.Name, new FileInfo(_tempFilePath).Length);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to async upload temp file {TempFile} to blob {BlobName}",
                        _tempFilePath, _blobClient.Name);
                    throw;
                }
            }
        }
        finally
        {
            // Clean up temp file
            try
            {
                if (File.Exists(_tempFilePath))
                {
                    File.Delete(_tempFilePath);
                    _logger?.LogDebug("Deleted temp file {TempFile}", _tempFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete temp file {TempFile}", _tempFilePath);
                // Don't throw - temp file cleanup is not critical
            }

            _disposed = true;
            await base.DisposeAsync();
        }
    }
}
