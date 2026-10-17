//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer.Handles;

/// <summary>
/// One open file or directory of a client station.
/// </summary>
public sealed class OpenFileHandle
{
    /// <summary>
    /// Gets the handle number the client uses for this file.
    /// </summary>
    public byte UserHandle { get; init; }
    /// <summary>
    /// Gets the host path of the file.
    /// </summary>
    public string UnixPath { get; init; } = string.Empty;
    /// <summary>
    /// Gets the open stream of the file.
    /// </summary>
    public FileStream Stream { get; init; } = null!;
    /// <summary>
    /// Gets a value indicating whether the file was opened for reading only.
    /// </summary>
    public bool ReadOnly { get; init; }
    /// <summary>
    /// Gets a value indicating whether the handle is a directory.
    /// </summary>
    public bool IsDirectory { get; init; }
    /// <summary>
    /// Gets or sets the file position the next read or write starts at.
    /// </summary>
    public long Cursor { get; set; }
    /// <summary>
    /// Gets or sets the previous file position, set by SeekToCursor.
    /// </summary>
    public long CursorOld { get; set; }
    /// <summary>
    /// Gets or sets the sequence bit, the low bit of the control byte, of the last request that used this handle.
    /// </summary>
    public byte Sequence { get; set; }
    /// <summary>
    /// Gets or sets a value indicating whether the end of the file has been reached by a read or by an end-of-file check.
    /// </summary>
    public bool PastEof { get; set; }
    /// <summary>
    /// Gets the network number of the station that opened the file.
    /// </summary>
    public byte ClientNet { get; init; }
    /// <summary>
    /// Gets the station number of the station that opened the file.
    /// </summary>
    public byte ClientStation { get; init; }

    /// <summary>
    /// Sets Cursor to the current position of the stream.
    /// </summary>
    public void SyncCursorFromStream()
    {
        Cursor = Stream.Position;
    }

    /// <summary>
    /// Moves the stream to a saved position and stores that position in CursorOld.
    /// </summary>
    /// <param name="useOld">
    /// True to move to CursorOld, false to move to Cursor.
    /// </param>
    public void SeekToCursor(bool useOld)
    {
        Stream.Position = useOld ? CursorOld : Cursor;
        CursorOld = Stream.Position;
    }
}

/// <summary>
/// Keeps the table of open files, keyed by handle number.
/// </summary>
public sealed class FileServerHandleManager
{
    private readonly object _sync = new();
    private readonly Dictionary<byte, OpenFileHandle> _handles = new();

    /// <summary>
    /// Registers an open file under a handle number.
    /// </summary>
    /// <param name="userHandle">
    /// The handle number to use.
    /// </param>
    /// <param name="unixPath">
    /// The host path of the file.
    /// </param>
    /// <param name="stream">
    /// The open stream of the file.
    /// </param>
    /// <param name="readOnly">
    /// True when the file was opened for reading only.
    /// </param>
    /// <param name="net">
    /// The network number of the station that opened the file.
    /// </param>
    /// <param name="station">
    /// The station number of the station that opened the file.
    /// </param>
    /// <param name="handle">
    /// Receives the new handle entry, or null when the handle number is already in use.
    /// </param>
    /// <returns>
    /// True when the entry was added, false when the handle number is already in use.
    /// </returns>
    public bool TryOpen(
        byte userHandle,
        string unixPath,
        FileStream stream,
        bool readOnly,
        byte net,
        byte station,
        out OpenFileHandle handle)
    {
        lock (_sync)
        {
            if (_handles.ContainsKey(userHandle))
            {
                handle = null!;
                return false;
            }

            handle = new OpenFileHandle
            {
                UserHandle = userHandle,
                UnixPath = unixPath,
                Stream = stream,
                ReadOnly = readOnly,
                ClientNet = net,
                ClientStation = station,
            };
            _handles[userHandle] = handle;
            return true;
        }
    }

    /// <summary>
    /// Looks up an open file by handle number.
    /// </summary>
    /// <param name="userHandle">
    /// The handle number.
    /// </param>
    /// <returns>
    /// The handle entry, or null when the handle is not open.
    /// </returns>
    public OpenFileHandle? Get(byte userHandle)
    {
        lock (_sync)
        {
            _handles.TryGetValue(userHandle, out OpenFileHandle? handle);
            return handle;
        }
    }

    /// <summary>
    /// Closes the stream of an open file and removes the handle.
    /// </summary>
    /// <param name="userHandle">
    /// The handle number.
    /// </param>
    /// <returns>
    /// True when the handle was open, otherwise false.
    /// </returns>
    public bool Close(byte userHandle)
    {
        lock (_sync)
        {
            if (!_handles.TryGetValue(userHandle, out OpenFileHandle? handle))
                return false;

            handle.Stream.Dispose();
            _handles.Remove(userHandle);
            return true;
        }
    }

    /// <summary>
    /// Closes every open file that belongs to a station.
    /// </summary>
    /// <param name="net">
    /// The network number of the station.
    /// </param>
    /// <param name="station">
    /// The station number of the station.
    /// </param>
    public void CloseAllForStation(byte net, byte station)
    {
        lock (_sync)
        {
            List<byte> toClose = new List<byte>();
            Dictionary<byte, OpenFileHandle>.Enumerator enumerator = _handles.GetEnumerator();
            while (enumerator.MoveNext())
            {
                KeyValuePair<byte, OpenFileHandle> pair = enumerator.Current;
                if (pair.Value.ClientNet == net && pair.Value.ClientStation == station)
                    toClose.Add(pair.Key);
            }

            for (int i = 0; i < toClose.Count; i++)
                Close(toClose[i]);
        }
    }
}
