//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
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
    public const long DefaultMaxBytes = 10L * 1024L * 1024L;
    public const string LogFileName = "retrofs-server.jsonl";

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string? _logPath;
    private static long _maxBytes = DefaultMaxBytes;

    public static bool IsConfigured => _logPath != null;

    public static string? LogPath => _logPath;

    public static void Configure(string dataDirectory, long maxBytes = DefaultMaxBytes)
    {
        string logsDir = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logsDir);
        _logPath = Path.Combine(logsDir, LogFileName);
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
    }

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
