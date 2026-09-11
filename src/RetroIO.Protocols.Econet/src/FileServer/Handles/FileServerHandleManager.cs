//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroFS -- Accessing yesterday's data with today's standards"
//

using RetroIO.Server.Core.FileSystem;

namespace RetroIO.Protocols.Econet.FileServer.Handles;

public sealed class OpenFileHandle
{
    public byte UserHandle { get; init; }
    public string UnixPath { get; init; } = string.Empty;
    public FileStream Stream { get; init; } = null!;
    public bool ReadOnly { get; init; }
    public bool IsDirectory { get; init; }
    public long Cursor { get; set; }
    public long CursorOld { get; set; }
    public byte Sequence { get; set; }
    public bool PastEof { get; set; }
    public byte ClientNet { get; init; }
    public byte ClientStation { get; init; }

    public void SyncCursorFromStream()
    {
        Cursor = Stream.Position;
    }

    public void SeekToCursor(bool useOld)
    {
        Stream.Position = useOld ? CursorOld : Cursor;
        CursorOld = Stream.Position;
    }
}

public sealed class FileServerHandleManager
{
    private readonly object _sync = new();
    private readonly Dictionary<byte, OpenFileHandle> _handles = new();

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

    public OpenFileHandle? Get(byte userHandle)
    {
        lock (_sync)
        {
            _handles.TryGetValue(userHandle, out OpenFileHandle? handle);
            return handle;
        }
    }

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
