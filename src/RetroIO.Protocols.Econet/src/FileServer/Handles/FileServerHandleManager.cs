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
/// Keeps the table of open files. Each client station has its own table: handle numbers 4 to 32
/// are handed out per station, so two stations can both hold a handle 4.
/// </summary>
public sealed class FileServerHandleManager
{
    private const byte FirstHandle = 4;
    private const byte LastHandle = 32;

    // Without the ManyHandle flag no handle above 8 is given out (fs.c lines 1464 and 1495: the limit is 9
    // slots counted from 1, so the largest handle is 8). Handles 1 to 3 are the directory handles.
    private const byte LastHandleWithoutManyHandle = 8;

    private readonly object _sync = new();
    private readonly Dictionary<int, OpenFileHandle> _handles = new();
    private readonly Dictionary<int, byte> _nextHandle = new();

    // One key per (net, station, handle).
    private static int Key(byte net, byte station, byte handle) => (net << 16) | (station << 8) | handle;

    private static int StationKey(byte net, byte station) => (net << 8) | station;

    /// <summary>
    /// Hands out the next free file handle number of a station, counting up from 4 and starting
    /// again at 4 after the last handle. The last handle is 32 with the ManyHandle flag set and 8 with it
    /// clear. A number that the station still has open is skipped.
    /// </summary>
    /// <param name="net">
    /// The network number of the station.
    /// </param>
    /// <param name="station">
    /// The station number of the station.
    /// </param>
    /// <param name="manyHandles">
    /// True when FileServerConfiguration.FlagManyHandle is set: handles 4 to 32 can be used. False: only 4 to 8.
    /// </param>
    /// <returns>
    /// The handle number, or 0 when all the allowed handles are in use by that station. The number is not
    /// reserved: it is registered when the caller opens the file with <see cref="TryOpen"/>.
    /// </returns>
    public byte AllocateHandle(byte net, byte station, bool manyHandles)
    {
        lock (_sync)
        {
            int stationKey = StationKey(net, station);
            if (!_nextHandle.TryGetValue(stationKey, out byte next))
                next = FirstHandle;

            byte lastHandle = manyHandles ? LastHandle : LastHandleWithoutManyHandle;

            // The flag can be changed while a station is logged in, so a saved number can be above the new limit.
            if (next > lastHandle)
                next = FirstHandle;

            for (int tried = 0; tried <= lastHandle - FirstHandle; tried++)
            {
                byte handle = next;
                next++;
                if (next > lastHandle)
                    next = FirstHandle;

                if (!_handles.ContainsKey(Key(net, station, handle)))
                {
                    _nextHandle[stationKey] = next;
                    return handle;
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Registers an open file under a handle number of a station.
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
    /// Receives the new handle entry, or null when the station already uses the handle number.
    /// </param>
    /// <returns>
    /// True when the entry was added, false when the station already uses the handle number.
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
            int key = Key(net, station, userHandle);
            if (_handles.ContainsKey(key))
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
            _handles[key] = handle;
            return true;
        }
    }

    /// <summary>
    /// Looks up an open file of a station by handle number.
    /// </summary>
    /// <param name="net">
    /// The network number of the station.
    /// </param>
    /// <param name="station">
    /// The station number of the station.
    /// </param>
    /// <param name="userHandle">
    /// The handle number.
    /// </param>
    /// <returns>
    /// The handle entry, or null when that station has no such handle open.
    /// </returns>
    public OpenFileHandle? Get(byte net, byte station, byte userHandle)
    {
        lock (_sync)
        {
            _handles.TryGetValue(Key(net, station, userHandle), out OpenFileHandle? handle);
            return handle;
        }
    }

    /// <summary>
    /// Closes the stream of an open file of a station and removes the handle.
    /// </summary>
    /// <param name="net">
    /// The network number of the station.
    /// </param>
    /// <param name="station">
    /// The station number of the station.
    /// </param>
    /// <param name="userHandle">
    /// The handle number.
    /// </param>
    /// <returns>
    /// True when the station had the handle open, otherwise false.
    /// </returns>
    public bool Close(byte net, byte station, byte userHandle)
    {
        lock (_sync)
        {
            int key = Key(net, station, userHandle);
            if (!_handles.TryGetValue(key, out OpenFileHandle? handle))
                return false;

            handle.Stream.Dispose();
            _handles.Remove(key);
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
            for (int handle = 0; handle <= 255; handle++)
                Close(net, station, (byte)handle);
            _nextHandle.Remove(StationKey(net, station));
        }
    }
}
