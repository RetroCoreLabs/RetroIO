//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroIO.Storage.Utilities.Logging;

/// <summary>
/// Append-only JSON Lines server log (one JSON object per line) with 10 MB rotation.
/// Lives under <c>{dataPath}/logs/retrofs-server.jsonl</c>.
/// </summary>
public static class RetroFsServerJsonLog
{
    /// <summary>
    /// The default size at which the log file is rotated, which is 10 MB.
    /// </summary>
    public const long DefaultMaxBytes = 10L * 1024L * 1024L;
    /// <summary>
    /// The name of the log file, retrofs-server.jsonl.
    /// </summary>
    public const string LogFileName = "retrofs-server.jsonl";

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string? _logPath;
    private static long _maxBytes = DefaultMaxBytes;

    /// <summary>
    /// Gets whether Configure has set a log path.
    /// </summary>
    public static bool IsConfigured => _logPath != null;

    /// <summary>
    /// Gets the path of the log file, or null when the log is not configured.
    /// </summary>
    public static string? LogPath => _logPath;

    /// <summary>
    /// Sets the log file to the file logs/retrofs-server.jsonl inside a data directory and creates the logs directory.
    /// </summary>
    /// <param name="dataDirectory">
    /// The data directory.
    /// </param>
    /// <param name="maxBytes">
    /// The size at which the file is rotated. A value of zero or less selects the default of 10 MB.
    /// </param>
    public static void Configure(string dataDirectory, long maxBytes = DefaultMaxBytes)
    {
        string logsDir = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logsDir);
        _logPath = Path.Combine(logsDir, LogFileName);
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
    }

    /// <summary>
    /// Appends one JSON line with a time stamp, a type, a level and the given fields.
    /// </summary>
    /// <remarks>
    /// Nothing is written when the log is not configured. Field names are written in camel case, null values are left out, and the file is rotated first when it has reached the maximum size. The rotation moves the file to the same name with .old added and replaces an older .old file. An error while writing is ignored.
    /// </remarks>
    /// <param name="type">
    /// The record type.
    /// </param>
    /// <param name="level">
    /// The level text.
    /// </param>
    /// <param name="fields">
    /// More values for the record. A field with the name ts, type or level replaces the standard value.
    /// </param>
    public static void Write(string type, string level, IReadOnlyDictionary<string, object?> fields)
    {
        if (_logPath == null)
            return;

        try
        {
            Dictionary<string, object?> record = new Dictionary<string, object?>(fields.Count + 3)
            {
                ["ts"] = DateTime.UtcNow.ToString("O"),
                ["type"] = type,
                ["level"] = level,
            };

            foreach (KeyValuePair<string, object?> pair in fields)
                record[pair.Key] = pair.Value;

            string line = JsonSerializer.Serialize(record, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);

            lock (Sync)
            {
                RotateIfNeeded();
                using FileStream stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
        }
        catch
        {
            // Never break the server because logging failed.
        }
    }

    /// <summary>
    /// Appends a record of the type app-log with a category and a message.
    /// </summary>
    /// <param name="level">
    /// The level text.
    /// </param>
    /// <param name="category">
    /// The category text.
    /// </param>
    /// <param name="message">
    /// The message text.
    /// </param>
    public static void WriteAppLog(string level, string category, string message)
    {
        Write("app-log", level, new Dictionary<string, object?>
        {
            ["category"] = category,
            ["message"] = message,
        });
    }

    private static void RotateIfNeeded()
    {
        if (_logPath == null || !File.Exists(_logPath))
            return;

        FileInfo info = new FileInfo(_logPath);
        if (info.Length < _maxBytes)
            return;

        string backup = _logPath + ".old";
        if (File.Exists(backup))
            File.Delete(backup);

        File.Move(_logPath, backup);
    }
}
