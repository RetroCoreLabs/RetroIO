//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// HackerCorp Labs — https://github.com/HackerCorpLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using System;
using System.IO;
using RetroIO.Protocols.Econet.FileServer.Dispatch;
using RetroIO.Protocols.Econet.Protocol.FileServer;

namespace RetroIO.Protocols.Econet.FileServer.Commands;

/// <summary>
/// <c>*COPY</c> — copies a file server-side.
/// </summary>
/// <remarks>
/// <para><b>Previously missing.</b></para>
/// Copies a single source file to a destination, both resolved relative to the current directory.
/// The reference also supports wildcard multi-file copies; this covers the common single-file case a
/// filer uses. Copy is done through the filesystem backend, so it works on any backend (local or
/// blob), not just the local disk.
/// </remarks>
public sealed class CopyCommand : IOsCliCommand
{
    /// <inheritdoc/>
    public string Name => "COPY";

    /// <inheritdoc/>
    public int MinimumAbbreviation => 2;

    /// <inheritdoc/>
    public bool RequiresLogin => true;

    /// <inheritdoc/>
    public bool RequiresSystem => false;

    /// <inheritdoc/>
    public byte[] Execute(in OsCliCall call)
    {
        // Argument is "<source> <destination>".
        string[] parts = call.Argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad parameters");

        if (!call.Context.Paths.TryResolve(call.Session!, parts[0], out string sourcePath, out _, out _) ||
            !call.Context.FileSystem.FileExists(sourcePath))
        {
            return FileServerReply.Error(FileServerErrors.NotFound, "Not found");
        }

        // The destination usually does NOT exist yet, so TryResolve returns false for the common
        // case — but it still assigns the computed host path to its out-parameter. Accept that path
        // when it is non-empty; only a genuinely unresolvable destination (empty path) is an error.
        call.Context.Paths.TryResolve(call.Session!, parts[1], out string destPath, out _, out _);
        if (string.IsNullOrEmpty(destPath))
            return FileServerReply.Error(FileServerErrors.BadCommand, "Bad destination");

        byte[] data = ReadAll(call.Context, sourcePath);
        WriteAll(call.Context, destPath, data);
        return FileServerReply.Success();
    }

    private static byte[] ReadAll(FileServerContext context, string path)
    {
        using FileStream stream = context.FileSystem.OpenFile(path, FileMode.Open, FileAccess.Read);
        using MemoryStream buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WriteAll(FileServerContext context, string path, byte[] data)
    {
        using FileStream stream = context.FileSystem.OpenFile(path, FileMode.Create, FileAccess.Write);
        stream.Write(data, 0, data.Length);
    }
}
