//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Net;
using System.Net.Sockets;
using RetroIO.Protocols.DriveWire.Drives;
using RetroIO.Protocols.DriveWire.Protocol;
using RetroIO.Protocols.DriveWire.Server;

namespace RetroIO.Protocols.DriveWire.Tests;

/// <summary>
/// A drive held in memory, for protocol tests that need no image file.
/// </summary>
internal sealed class MemoryDrive : IDriveWireDrive
{
    private readonly byte[] _data;

    public MemoryDrive(uint sectors, bool readOnly = false)
    {
        _data = new byte[sectors * 256];
        TotalSectors = sectors;
        IsReadOnly = readOnly;
        for (int i = 0; i < _data.Length; i++)
        {
            // Every sector differs, and the sums do not fit in 8 bits, so a checksum that drops bits shows.
            _data[i] = (byte)((i * 31) + (i / 256) + 0x80);
        }
    }

    public string Name => "memory";

    public bool IsReadOnly { get; }

    public uint TotalSectors { get; }

    public bool Disposed { get; private set; }

    public byte[] Sector(uint lsn)
    {
        return _data.AsSpan((int)(lsn * 256), 256).ToArray();
    }

    public byte ReadSector(uint lsn, Span<byte> buffer)
    {
        if (lsn >= TotalSectors)
        {
            return DriveWireError.Read;
        }

        _data.AsSpan((int)(lsn * 256), 256).CopyTo(buffer);
        return DriveWireError.None;
    }

    public byte WriteSector(uint lsn, ReadOnlySpan<byte> data)
    {
        if (IsReadOnly)
        {
            return DriveWireError.WriteProtect;
        }

        if (lsn >= TotalSectors)
        {
            return DriveWireError.Write;
        }

        data.Slice(0, 256).CopyTo(_data.AsSpan((int)(lsn * 256), 256));
        return DriveWireError.None;
    }

    public void Dispose()
    {
        Disposed = true;
    }
}

/// <summary>
/// A clock that always gives the same local time.
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
}

/// <summary>
/// One DriveWire session on a real loopback TCP connection, with the client end in the test's hands.
/// </summary>
internal sealed class SessionHarness : IAsyncDisposable
{
    private static readonly TimeSpan ReceiveLimit = TimeSpan.FromSeconds(5);

    private readonly TcpClient _client;
    private readonly TcpClient _serverSide;

    private SessionHarness(TcpClient client, TcpClient serverSide, DriveWireSession session, Task run)
    {
        _client = client;
        _serverSide = serverSide;
        Session = session;
        Run = run;
        Client = client.GetStream();
    }

    public DriveWireSession Session { get; }

    public Task Run { get; }

    public NetworkStream Client { get; }

    public static async Task<SessionHarness> StartAsync(
        DriveWireDriveTable drives,
        DriveWireServerStatistics statistics,
        DriveWireServerOptions? options = null,
        TimeProvider? time = null)
    {
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            TcpClient client = new TcpClient();
            Task connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            TcpClient serverSide = await listener.AcceptTcpClientAsync();
            await connect;
            client.NoDelay = true;
            serverSide.NoDelay = true;

            DriveWireSession session = new DriveWireSession(
                drives, statistics, options ?? new DriveWireServerOptions(), "test", null, time);
            NetworkStream serverStream = serverSide.GetStream();
            Task run = Task.Run(() => session.RunAsync(serverStream, CancellationToken.None));
            return new SessionHarness(client, serverSide, session, run);
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task SendAsync(params byte[] bytes)
    {
        await Client.WriteAsync(bytes);
        await Client.FlushAsync();
    }

    public async Task<byte[]> ReceiveAsync(int count)
    {
        byte[] result = new byte[count];
        using CancellationTokenSource limit = new CancellationTokenSource(ReceiveLimit);
        await Client.ReadExactlyAsync(result, limit.Token);
        return result;
    }

    /// <summary>
    /// Checks that the server sends nothing for a while.
    /// </summary>
    public async Task<bool> IsSilentAsync(int milliseconds)
    {
        byte[] one = new byte[1];
        using CancellationTokenSource limit = new CancellationTokenSource(milliseconds);
        try
        {
            // Any byte means the server answered; 0 means the connection ended without one.
            int read = await Client.ReadAsync(one, limit.Token);
            return read == 0;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Sends OP_TIME and checks six bytes come back: proof that the session reads the next byte as an operation code.
    /// </summary>
    public async Task AssertInStepAsync()
    {
        await SendAsync(DriveWireOpCode.Time);
        byte[] time = await ReceiveAsync(6);
        Assert.That(time[1], Is.InRange(1, 12), "the month of the TIME answer");
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the client ends the server's stream, which ends the session.
        _client.Dispose();
        await Run.WaitAsync(TimeSpan.FromSeconds(5));
        _serverSide.Dispose();
    }
}

/// <summary>
/// The client side of the sector transactions, the way the NitrOS-9 drivers do them.
/// </summary>
internal static class DriveWireTestClient
{
    /// <summary>
    /// Reads a sector the way rbdw.asm Read and boot_dw.asm HWRead do: OP_READEX, 256 bytes in, the checksum out, the status
    /// in, and OP_REREADEX again on E$CRC.
    /// </summary>
    public static async Task<(byte Status, byte[] Data)> ReadExtendedAsync(Stream stream, byte drive, uint lsn)
    {
        byte op = DriveWireOpCode.ReadExtended;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            byte[] request = { op, drive, (byte)(lsn >> 16), (byte)(lsn >> 8), (byte)lsn };
            await stream.WriteAsync(request);
            byte[] data = new byte[256];
            using CancellationTokenSource limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await stream.ReadExactlyAsync(data, limit.Token);
            ushort sum = DriveWireChecksum.Compute(data);
            await stream.WriteAsync(new byte[] { (byte)(sum >> 8), (byte)sum });
            byte[] status = new byte[1];
            await stream.ReadExactlyAsync(status, limit.Token);
            if (status[0] != DriveWireError.Crc)
            {
                return (status[0], data);
            }

            op = DriveWireOpCode.ReReadExtended;
        }

        return (DriveWireError.Crc, Array.Empty<byte>());
    }

    /// <summary>
    /// Writes a sector the way rbdw.asm Write does: OP_WRITE, drive, LSN, 256 bytes, the checksum, then the status in.
    /// </summary>
    public static async Task<byte> WriteAsync(Stream stream, byte drive, uint lsn, byte[] data)
    {
        byte[] request = new byte[263];
        request[0] = DriveWireOpCode.Write;
        request[1] = drive;
        request[2] = (byte)(lsn >> 16);
        request[3] = (byte)(lsn >> 8);
        request[4] = (byte)lsn;
        data.CopyTo(request, 5);
        ushort sum = DriveWireChecksum.Compute(data);
        request[261] = (byte)(sum >> 8);
        request[262] = (byte)sum;
        await stream.WriteAsync(request);
        byte[] status = new byte[1];
        using CancellationTokenSource limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(status, limit.Token);
        return status[0];
    }
}

/// <summary>
/// Builds small OS-9 (RBF) volumes from the field layout in NitrOS-9 defs/rbf.d, so the image drive can be tested without
/// image files.
/// </summary>
internal static class Os9VolumeBuilder
{
    private const int SectorSize = 256;

    /// <summary>
    /// Builds a headerless OS-9 volume of <paramref name="totalSectors"/> sectors holding one file, HELLO. With
    /// <paramref name="shortFile"/> the image stops after the last used sector, as the NitrOS-9 DriveWire disks do.
    /// </summary>
    public static byte[] Build(int totalSectors, bool shortFile, out int usedSectors)
    {
        int mapBytes = (totalSectors + 7) / 8;
        int mapSectors = (mapBytes + SectorSize - 1) / SectorSize;
        int rootFd = 1 + mapSectors;
        int rootData = rootFd + 1;
        int fileFd = rootFd + 2;
        int fileData = rootFd + 3;
        usedSectors = fileData + 1;

        byte[] image = new byte[(shortFile ? usedSectors : totalSectors) * SectorSize];

        // DD.TOT, DD.TKS, DD.MAP, DD.BIT, DD.DIR, DD.FMT, DD.SPT (defs/rbf.d).
        image[0] = (byte)(totalSectors >> 16);
        image[1] = (byte)(totalSectors >> 8);
        image[2] = (byte)totalSectors;
        image[3] = 18;
        image[4] = (byte)(mapBytes >> 8);
        image[5] = (byte)mapBytes;
        image[7] = 1;
        image[8] = (byte)(rootFd >> 16);
        image[9] = (byte)(rootFd >> 8);
        image[10] = (byte)rootFd;
        image[16] = 2;
        image[18] = 18;
        PutName(image, 31, "DWTEST");

        for (int s = 0; s < usedSectors; s++)
        {
            image[SectorSize + (s / 8)] |= (byte)(0x80 >> (s % 8));
        }

        int at = rootFd * SectorSize;
        image[at] = 0xBF;
        image[at + 12] = 3 * 32;
        PutSegment(image, at + 16, rootData, 1);

        at = rootData * SectorSize;
        PutName(image, at, "..");
        PutUInt24(image, at + 29, rootFd);
        PutName(image, at + 32, ".");
        PutUInt24(image, at + 32 + 29, rootFd);
        PutName(image, at + 64, "HELLO");
        PutUInt24(image, at + 64 + 29, fileFd);

        at = fileFd * SectorSize;
        image[at] = 0x0B;
        image[at + 12] = 5;
        PutSegment(image, at + 16, fileData, 1);
        image[fileData * SectorSize] = (byte)'H';
        image[(fileData * SectorSize) + 1] = (byte)'E';
        image[(fileData * SectorSize) + 2] = (byte)'L';
        image[(fileData * SectorSize) + 3] = (byte)'L';
        image[(fileData * SectorSize) + 4] = (byte)'O';
        return image;
    }

    private static void PutName(byte[] data, int at, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            data[at + i] = (byte)name[i];
        }

        data[at + name.Length - 1] |= 0x80;
    }

    private static void PutUInt24(byte[] data, int at, int value)
    {
        data[at] = (byte)(value >> 16);
        data[at + 1] = (byte)(value >> 8);
        data[at + 2] = (byte)value;
    }

    private static void PutSegment(byte[] data, int at, int lsn, int count)
    {
        PutUInt24(data, at, lsn);
        data[at + 3] = (byte)(count >> 8);
        data[at + 4] = (byte)count;
    }
}
