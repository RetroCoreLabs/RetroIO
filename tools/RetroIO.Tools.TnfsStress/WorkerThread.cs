//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen 
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.Security.Cryptography;
using System.Text;
using RetroIO.Protocols.Tnfs.Client;
using RetroIO.Protocols.Tnfs.Protocol;

namespace RetroIO.Tools.TnfsStress;

/// <summary>
/// Worker thread that performs TNFS stress testing
/// </summary>
public sealed class WorkerThread
{
    private readonly int _threadId;
    private readonly string _server;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly Statistics _stats;
    private readonly Random _random;
    private bool _shouldStop;

    public WorkerThread(int threadId, string server, int port, string username, string password, Statistics stats)
    {
        _threadId = threadId;
        _server = server;
        _port = port;
        _username = username;
        _password = password;
        _stats = stats;
        _random = new Random(threadId + Environment.TickCount);
    }

    public void Stop()
    {
        _shouldStop = true;
    }

    public void Run()
    {
        _stats.IncrementActiveThreads();

        try
        {
            LogInfo("Starting worker thread");

            using var client = new TnfsClient(_server, _port, useTcp: true);

            // Connect and mount with authentication
            LogInfo($"Mounting with user '{_username}'...");
            var mountResult = client.Mount("/", _username, _password);
            if (!mountResult.Success)
            {
                LogError($"Mount failed: {mountResult.ErrorMessage}");
                _stats.IncrementErrors();
                return;
            }
            LogInfo("Mount successful");

            // Create thread directory
            var dirPath = $"/thread{_threadId}";
            LogInfo($"Creating directory '{dirPath}'...");
            var mkdirResult = client.MkDir(dirPath);
            if (!mkdirResult.Success && mkdirResult.ReturnCode != TnfsReturnCode.EEXIST)
            {
                LogError($"MkDir failed: {mkdirResult.ErrorMessage}");
                _stats.IncrementErrors();
                return;
            }
            LogInfo("Directory ready");

            // Main test loop
            int iteration = 0;
            while (!_shouldStop)
            {
                iteration++;
                LogInfo($"Starting iteration {iteration}");

                if (!RunIteration(client, dirPath, iteration))
                {
                    LogError("Iteration failed - stopping thread");
                    return;
                }

                _stats.IncrementOperations();
            }

            LogInfo("Worker thread stopped gracefully");
        }
        catch (Exception ex)
        {
            LogError($"Unhandled exception: {ex.Message}");
            _stats.IncrementErrors();
        }
        finally
        {
            _stats.DecrementActiveThreads();
        }
    }

    private bool RunIteration(TnfsClient client, string dirPath, int iteration)
    {
        const int fileCount = 5;
        var testFiles = new TestFile[fileCount];

        try
        {
            // Generate random files in memory
            for (int i = 0; i < fileCount; i++)
            {
                var size = _random.Next(1024, 50 * 1024); // 1KB to 50KB
                var data = new byte[size];
                _random.NextBytes(data);
                var hash = MD5.HashData(data);

                testFiles[i] = new TestFile
                {
                    Name = $"file{i}_iter{iteration}.dat",
                    Data = data,
                    Hash = hash
                };
            }

            // Upload files
            for (int i = 0; i < fileCount; i++)
            {
                var file = testFiles[i];
                var filePath = $"{dirPath}/{file.Name}";

                // Open file for writing (create if not exists)
                var openResult = client.Open(filePath, TnfsOpenFlags.O_WRONLY | TnfsOpenFlags.O_CREAT | TnfsOpenFlags.O_TRUNC);
                if (!openResult.Success)
                {
                    LogError($"Failed to open '{filePath}': {openResult.ErrorMessage}");
                    _stats.IncrementErrors();
                    return false;
                }

                var fileHandle = openResult.Data;

                // Write data
                var writeResult = client.Write(fileHandle, file.Data);
                if (!writeResult.Success)
                {
                    LogError($"Failed to write '{filePath}': {writeResult.ErrorMessage}");
                    client.Close(fileHandle);
                    _stats.IncrementErrors();
                    return false;
                }

                // Close file
                var closeResult = client.Close(fileHandle);
                if (!closeResult.Success)
                {
                    LogError($"Failed to close '{filePath}': {closeResult.ErrorMessage}");
                    _stats.IncrementErrors();
                    return false;
                }

                _stats.AddBytesUploaded(file.Data.Length);
                _stats.IncrementFilesCreated();
            }

            // Download and verify files
            for (int i = 0; i < fileCount; i++)
            {
                var file = testFiles[i];
                var filePath = $"{dirPath}/{file.Name}";

                // Open file for reading
                var openResult = client.Open(filePath, TnfsOpenFlags.O_RDONLY);
                if (!openResult.Success)
                {
                    LogError($"Failed to open '{filePath}' for reading: {openResult.ErrorMessage}");
                    _stats.IncrementErrors();
                    return false;
                }

                var fileHandle = openResult.Data;

                // Read all data
                var downloadedData = new byte[0];
                while (true)
                {
                    var readResult = client.Read(fileHandle, 8192); // Read in 8KB chunks
                    if (!readResult.Success)
                    {
                        if (readResult.ReturnCode == TnfsReturnCode.EOF)
                        {
                            break; // End of file
                        }

                        LogError($"Failed to read '{filePath}': {readResult.ErrorMessage}");
                        client.Close(fileHandle);
                        _stats.IncrementErrors();
                        return false;
                    }

                    if (readResult.Data == null || readResult.Data.Length == 0)
                    {
                        break; // End of file
                    }

                    // Append to downloaded data
                    var newData = new byte[downloadedData.Length + readResult.Data.Length];
                    Array.Copy(downloadedData, 0, newData, 0, downloadedData.Length);
                    Array.Copy(readResult.Data, 0, newData, downloadedData.Length, readResult.Data.Length);
                    downloadedData = newData;
                }

                // Close file
                client.Close(fileHandle);

                _stats.AddBytesDownloaded(downloadedData.Length);

                // Verify MD5 hash
                var downloadedHash = MD5.HashData(downloadedData);
                if (!HashesMatch(file.Hash, downloadedHash))
                {
                    LogError($"Hash mismatch for '{filePath}'! Expected: {HashToString(file.Hash)}, Got: {HashToString(downloadedHash)}");
                    _stats.IncrementErrors();
                    return false;
                }
            }

            // Delete files
            for (int i = 0; i < fileCount; i++)
            {
                var file = testFiles[i];
                var filePath = $"{dirPath}/{file.Name}";

                var unlinkResult = client.Unlink(filePath);
                if (!unlinkResult.Success)
                {
                    LogError($"Failed to delete '{filePath}': {unlinkResult.ErrorMessage}");
                    _stats.IncrementErrors();
                    return false;
                }

                _stats.IncrementFilesDeleted();
            }

            return true;
        }
        catch (Exception ex)
        {
            LogError($"Exception in iteration: {ex.Message}");
            _stats.IncrementErrors();
            return false;
        }
    }

    private bool HashesMatch(byte[] hash1, byte[] hash2)
    {
        if (hash1.Length != hash2.Length)
            return false;

        for (int i = 0; i < hash1.Length; i++)
        {
            if (hash1[i] != hash2[i])
                return false;
        }

        return true;
    }

    private string HashToString(byte[] hash)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < hash.Length; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }

    private void LogInfo(string message)
    {
        Console.WriteLine($"[Thread-{_threadId}] {message}");
    }

    private void LogError(string message)
    {
        Console.WriteLine($"[Thread-{_threadId}] ERROR: {message}");
    }

    private struct TestFile
    {
        public string Name;
        public byte[] Data;
        public byte[] Hash;
    }
}
