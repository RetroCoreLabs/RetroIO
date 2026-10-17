//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RetroIO.Security.Implementation.Auditing
{
    /// <summary>
    /// File-based audit logger
    /// Writes audit entries to JSON Lines format (one JSON per line)
    /// Daily rotation with automatic archival
    /// </summary>
    public sealed class FileAuditLogger : IDisposable
    {
        private readonly string _logDirectory;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Creates a logger that writes to a directory.
        /// </summary>
        /// <remarks>
        /// The directory is created when it does not exist.
        /// </remarks>
        /// <param name="logDirectory">
        /// The directory for the audit files.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="logDirectory"/> is null.
        /// </exception>
        public FileAuditLogger(string logDirectory)
        {
            _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));

            // Create directory if not exists
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = false // Single line JSON for JSONL format
            };
        }

        /// <summary>
        /// Appends an entry as one line of JSON to the audit file of the current UTC day.
        /// </summary>
        /// <remarks>
        /// The file is named with the date, in the form yyyy-MM-dd.audit.jsonl. Writes are serialized with a lock.
        /// </remarks>
        /// <param name="entry">
        /// The entry to write.
        /// </param>
        /// <param name="ct">
        /// A token that cancels waiting for the lock and the write.
        /// </param>
        /// <exception cref="System.ArgumentNullException">
        /// Thrown when <paramref name="entry"/> is null.
        /// </exception>
        public async ValueTask LogSecurityEventAsync(
            SecurityAuditEntry entry, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            // Format: YYYY-MM-DD.audit.jsonl (one JSON per line)
            string fileName = $"{DateTime.UtcNow:yyyy-MM-dd}.audit.jsonl";
            string filePath = Path.Combine(_logDirectory, fileName);

            string json = JsonSerializer.Serialize(entry, _jsonOptions);

            await _writeLock.WaitAsync(ct);
            try
            {
                await File.AppendAllTextAsync(filePath, json + "\n", ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Releases the write lock.
        /// </summary>
        public void Dispose()
        {
            _writeLock?.Dispose();
        }
    }
}
