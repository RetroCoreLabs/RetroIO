//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Protocols.DriveWire.Drives;
using RetroIO.Protocols.DriveWire.Protocol;

namespace RetroIO.Protocols.DriveWire.Server;

/// <summary>
/// The server side of one DriveWire connection: reads the client's transactions from a byte stream and answers them.
/// </summary>
/// <remarks>
/// <para>
/// Written from "DriveWire Protocol Version 4.0.0" (https://github.com/DrPitre/DriveWire/blob/main/DriveWire%20Specification.md)
/// and checked against what the NitrOS-9 client drivers send: rbdw.asm (the RBF disk driver), boot_dw.asm (the boot
/// module), dwio.asm (the low level module and its virtual serial poller), clock2_dw.asm (the clock), scdwv.asm (virtual
/// serial) and scdwp.asm (printer), all in the NitrOS-9 source tree under level1/modules and level1/coco1/modules.
/// </para>
/// <para>
/// The session works on any <see cref="Stream"/>: a TCP connection from an emulator's Becker port today, a serial port's
/// stream later. The Becker port is a byte pipe with no framing of its own (dwread_becker.asm polls a status bit at $FF41 and
/// reads the data register at $FF42; dwwrite_becker.asm writes each byte to $FF42), so the bytes on the TCP connection are
/// exactly the bytes of the protocol.
/// </para>
/// <para>
/// Operation codes handled: NOP, RESET (three codes), INIT, TERM, DWINIT, TIME, SETTIME, READ, REREAD, READEX, REREADEX,
/// WRITE, REWRITE, GETSTAT, SETSTAT, PRINT, PRINTFLUSH, NAMEOBJ_MOUNT, NAMEOBJ_CREATE, SERREAD, SERREADM, SERWRITE,
/// FASTWRITE ($80 to $8F), SERWRITEM, SERGETSTAT, SERSETSTAT, SERINIT and SERTERM. Every other byte that arrives where an
/// operation code is expected is counted and logged and nothing more is read for it: the specification gives no length for
/// an operation it does not define, so there is nothing to skip. The next byte is read as a new operation code.
/// </para>
/// <para>
/// The wait for an operation code has no limit. Once a transaction has started, each further byte must arrive within
/// <see cref="DriveWireServerOptions.TransactionTimeoutMilliseconds"/> (250 ms by default, from the specification); if it
/// does not, the transaction is given up and the next byte is read as an operation code.
/// </para>
/// </remarks>
public sealed class DriveWireSession
{
    /// <summary>
    /// The version byte this server answers to OP_DWINIT: 4.
    /// </summary>
    /// <remarks>
    /// The specification ("The DWINIT Transaction") leaves the meaning of the byte open. The NitrOS-9 low level module decides
    /// with it: dwio.asm ChkVer installs the virtual serial poller when the answer is 4 ("Check to see if this is version 4
    /// of DriveWire") or $FF, and leaves it out otherwise. This server is a DriveWire 4 server and answers the poll, so it
    /// answers 4.
    /// </remarks>
    public const byte ServerVersion = 4;

    private const int SectorSize = 256;

    private static long _nextId;

    private readonly DriveWireDriveTable _drives;
    private readonly DriveWireServerStatistics _statistics;
    private readonly DriveWireServerOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly byte[] _frame = new byte[SectorSize + 6];
    private readonly byte[] _sector = new byte[SectorSize];
    private readonly byte[] _reply = new byte[SectorSize + 3];
    private readonly List<byte> _printBuffer = new List<byte>();
    private Stream? _stream;
    private DriveWireByteReader? _reader;
    private CancellationToken _cancellation;

    /// <summary>
    /// Creates a session.
    /// </summary>
    /// <param name="drives">
    /// The drive table, shared with the other sessions.
    /// </param>
    /// <param name="statistics">
    /// The server counters, shared with the other sessions.
    /// </param>
    /// <param name="options">
    /// The server settings.
    /// </param>
    /// <param name="remoteName">
    /// Who is connected, for the log and the management API; for TCP the remote end point.
    /// </param>
    /// <param name="logger">
    /// The log to write to, or null for none.
    /// </param>
    /// <param name="timeProvider">
    /// The clock OP_TIME reads, or null for the system clock.
    /// </param>
    public DriveWireSession(
        DriveWireDriveTable drives,
        DriveWireServerStatistics statistics,
        DriveWireServerOptions options,
        string remoteName,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _timeout = TimeSpan.FromMilliseconds(options.TransactionTimeoutMilliseconds);
        RemoteName = remoteName;
        Id = Interlocked.Increment(ref _nextId);
        ConnectedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Raised when the client finishes a print job with OP_PRINTFLUSH. The argument is the bytes it sent with OP_PRINT.
    /// </summary>
    public event Action<DriveWireSession, byte[]>? PrintJobReceived;

    /// <summary>
    /// Gets the number of this session, unique in the process.
    /// </summary>
    public long Id { get; }

    /// <summary>
    /// Gets who is connected.
    /// </summary>
    public string RemoteName { get; }

    /// <summary>
    /// Gets when the session started, in UTC.
    /// </summary>
    public DateTime ConnectedAt { get; }

    /// <summary>
    /// Gets the driver version byte the client sent with OP_DWINIT, or -1 when it has not sent one.
    /// </summary>
    public int ClientDriverVersion { get; private set; } = -1;

    /// <summary>
    /// Gets the six date and time bytes the client last sent with OP_SETTIME, or null. The server does not change any clock
    /// with them.
    /// </summary>
    public byte[]? LastClientTime { get; private set; }

    /// <summary>
    /// Gets the virtual serial channels of this connection.
    /// </summary>
    public DriveWireVirtualSerial VirtualSerial { get; } = new DriveWireVirtualSerial();

    /// <summary>
    /// Runs the session until the stream ends or the token is cancelled.
    /// </summary>
    /// <param name="stream">
    /// The connection to the client. The session reads and writes it but does not close it.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the session.
    /// </param>
    /// <returns>
    /// A task that completes when the session has ended.
    /// </returns>
    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _cancellation = cancellationToken;
        DriveWireByteReader reader = new DriveWireByteReader(stream);
        _reader = reader;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int op = await reader.ReadByteAsync(null, cancellationToken).ConfigureAwait(false);
                if (op < 0)
                {
                    break;
                }

                bool more;
                try
                {
                    more = await DispatchAsync((byte)op).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    _statistics.RecordTimeout();
                    _logger.LogWarning(
                        "DriveWire session {Id}: transaction ${Op:X2} given up: {Message}", Id, op, ex.Message);
                    more = true;
                }

                if (!more)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            // The client went away, or the server closed the connection while stopping. Either ends the session.
            _logger.LogInformation("DriveWire session {Id}: connection lost: {Message}", Id, ex.Message);
        }
        finally
        {
            _statistics.RecordBytesReceived(reader.BytesReceived);
            await reader.DisposeAsync().ConfigureAwait(false);
            _reader = null;
            _stream = null;
        }
    }

    /// <summary>
    /// Handles one transaction whose operation code has been read.
    /// </summary>
    /// <param name="op">
    /// The operation code.
    /// </param>
    /// <returns>
    /// False when the stream ended in the middle of the transaction.
    /// </returns>
    private async ValueTask<bool> DispatchAsync(byte op)
    {
        if (op >= DriveWireOpCode.FastWriteBase && op <= DriveWireOpCode.FastWriteLast)
        {
            _statistics.RecordTransaction();
            return await FastWriteAsync(op - DriveWireOpCode.FastWriteBase).ConfigureAwait(false);
        }

        switch (op)
        {
            case DriveWireOpCode.Nop:
            case DriveWireOpCode.Init:
            case DriveWireOpCode.Term:
                _statistics.RecordTransaction();
                _logger.LogDebug("DriveWire session {Id}: ${Op:X2}", Id, op);
                return true;

            case DriveWireOpCode.Reset1:
            case DriveWireOpCode.Reset2:
            case DriveWireOpCode.Reset3:
                _statistics.RecordTransaction();
                return Reset(op);

            case DriveWireOpCode.DriveWireInit:
                _statistics.RecordTransaction();
                return await DriveWireInitAsync().ConfigureAwait(false);

            case DriveWireOpCode.Time:
                _statistics.RecordTransaction();
                return await TimeAsync().ConfigureAwait(false);

            case DriveWireOpCode.SetTime:
                _statistics.RecordTransaction();
                return await SetTimeAsync().ConfigureAwait(false);

            case DriveWireOpCode.Read:
                _statistics.RecordTransaction();
                return await ReadAsync(false).ConfigureAwait(false);

            case DriveWireOpCode.ReRead:
                _statistics.RecordTransaction();
                _statistics.RecordReread();
                return await ReadAsync(true).ConfigureAwait(false);

            case DriveWireOpCode.ReadExtended:
                _statistics.RecordTransaction();
                return await ReadExtendedAsync(false).ConfigureAwait(false);

            case DriveWireOpCode.ReReadExtended:
                _statistics.RecordTransaction();
                _statistics.RecordReread();
                return await ReadExtendedAsync(true).ConfigureAwait(false);

            case DriveWireOpCode.Write:
                _statistics.RecordTransaction();
                return await WriteAsync(false).ConfigureAwait(false);

            case DriveWireOpCode.ReWrite:
                _statistics.RecordTransaction();
                _statistics.RecordRewrite();
                return await WriteAsync(true).ConfigureAwait(false);

            case DriveWireOpCode.GetStat:
            case DriveWireOpCode.SetStat:
                _statistics.RecordTransaction();
                return await StatAsync(op).ConfigureAwait(false);

            case DriveWireOpCode.Print:
                _statistics.RecordTransaction();
                return await PrintAsync().ConfigureAwait(false);

            case DriveWireOpCode.PrintFlush:
                _statistics.RecordTransaction();
                PrintFlush();
                return true;

            case DriveWireOpCode.NamedObjectMount:
            case DriveWireOpCode.NamedObjectCreate:
                _statistics.RecordTransaction();
                return await NamedObjectAsync(op).ConfigureAwait(false);

            case DriveWireOpCode.SerialRead:
                _statistics.RecordTransaction();
                return await SerialPollAsync().ConfigureAwait(false);

            case DriveWireOpCode.SerialReadMultiple:
                _statistics.RecordTransaction();
                return await SerialReadMultipleAsync().ConfigureAwait(false);

            case DriveWireOpCode.SerialWrite:
                _statistics.RecordTransaction();
                return await SerialWriteAsync().ConfigureAwait(false);

            case DriveWireOpCode.SerialWriteMultiple:
                _statistics.RecordTransaction();
                return await SerialWriteMultipleAsync().ConfigureAwait(false);

            case DriveWireOpCode.SerialGetStat:
                _statistics.RecordTransaction();
                return await ReceiveAsync(2).ConfigureAwait(false);

            case DriveWireOpCode.SerialSetStat:
                _statistics.RecordTransaction();
                return await SerialSetStatAsync().ConfigureAwait(false);

            case DriveWireOpCode.SerialInit:
            case DriveWireOpCode.SerialTerm:
                _statistics.RecordTransaction();
                return await SerialInitTermAsync(op == DriveWireOpCode.SerialInit).ConfigureAwait(false);

            default:
                _statistics.RecordUnknownOpCode();
                _logger.LogWarning("DriveWire session {Id}: unknown operation code ${Op:X2}, ignored", Id, op);
                return true;
        }
    }

    // OP_RESET1/2/3: "Reset any statistical values related to read/write and sector transfer throughput for ALL drives" and
    // "Flush all caches connected to the virtual drives" (specification, "The RESET Transaction"). Every write already went to
    // the image through the driver, so there is no cache to flush.
    private bool Reset(byte op)
    {
        _drives.ResetCounters();
        _statistics.RecordReset();
        _logger.LogInformation("DriveWire session {Id}: client reset (${Op:X2})", Id, op);
        return true;
    }

    // OP_DWINIT: one byte in, the server version out (specification, "The DWINIT Transaction"; dwio.asm Init).
    private async ValueTask<bool> DriveWireInitAsync()
    {
        if (!await ReceiveAsync(1).ConfigureAwait(false))
        {
            return false;
        }

        ClientDriverVersion = _frame[0];
        _logger.LogInformation(
            "DriveWire session {Id}: DWINIT, client driver version {Version}", Id, ClientDriverVersion);
        _reply[0] = ServerVersion;
        await SendAsync(1).ConfigureAwait(false);
        return true;
    }

    // OP_TIME: six bytes out, year - 1900, month 1-12, day 1-31, hour 0-23, minute, second (specification, "The TIME
    // Transaction"). clock2_dw.asm GetTime reads them straight into D.Year. The time is the server's local time, because the
    // client keeps one clock with no time zone.
    private async ValueTask<bool> TimeAsync()
    {
        DateTimeOffset now = _time.GetLocalNow();
        _reply[0] = (byte)(now.Year - 1900);
        _reply[1] = (byte)now.Month;
        _reply[2] = (byte)now.Day;
        _reply[3] = (byte)now.Hour;
        _reply[4] = (byte)now.Minute;
        _reply[5] = (byte)now.Second;
        await SendAsync(6).ConfigureAwait(false);
        return true;
    }

    // OP_SETTIME: six bytes in, no answer (clock2_dw.asm SetTime). Not in the specification. The bytes are kept for display
    // only; the server's clock is not the client's to set.
    private async ValueTask<bool> SetTimeAsync()
    {
        if (!await ReceiveAsync(6).ConfigureAwait(false))
        {
            return false;
        }

        LastClientTime = _frame.AsSpan(0, 6).ToArray();
        return true;
    }

    // OP_READ and OP_REREAD (specification, "The Read Transaction" and "The ReRead Transaction"): drive and LSN in; on error
    // one error byte out; on success 0, the checksum high byte first, then the 256 sector bytes. No answer comes back.
    private async ValueTask<bool> ReadAsync(bool reread)
    {
        if (!await ReceiveAsync(4).ConfigureAwait(false))
        {
            return false;
        }

        byte drive = _frame[0];
        uint lsn = Lsn(_frame, 1);
        byte error = ReadSector(drive, lsn, _sector);
        if (error != DriveWireError.None)
        {
            _statistics.RecordReadError();
            _reply[0] = error;
            await SendAsync(1).ConfigureAwait(false);
            LogSectorError("READ", drive, lsn, error);
            return true;
        }

        ushort checksum = DriveWireChecksum.Compute(_sector);
        _reply[0] = DriveWireError.None;
        _reply[1] = (byte)(checksum >> 8);
        _reply[2] = (byte)checksum;
        _sector.CopyTo(_reply.AsSpan(3));
        await SendAsync(3 + SectorSize).ConfigureAwait(false);
        _statistics.RecordSectorRead();
        _logger.LogTrace("DriveWire session {Id}: {Op} drive {Drive} LSN {Lsn}", Id, reread ? "REREAD" : "READ", drive, lsn);
        return true;
    }

    // OP_READEX and OP_REREADEX (specification, "The Read Extended Transaction"): drive and LSN in; 256 bytes out (all zero
    // when the sector cannot be read); the client's checksum in, high byte first; one byte out: the read error, or 0 when the
    // checksums match, or E$CRC when they do not. This is what rbdw.asm Read and boot_dw.asm HWRead do.
    private async ValueTask<bool> ReadExtendedAsync(bool reread)
    {
        if (!await ReceiveAsync(4).ConfigureAwait(false))
        {
            return false;
        }

        byte drive = _frame[0];
        uint lsn = Lsn(_frame, 1);
        byte error = ReadSector(drive, lsn, _sector);
        _sector.CopyTo(_reply.AsSpan());
        await SendAsync(SectorSize).ConfigureAwait(false);

        if (!await ReceiveAsync(2).ConfigureAwait(false))
        {
            return false;
        }

        ushort clientChecksum = (ushort)((_frame[0] << 8) | _frame[1]);
        byte answer;
        if (error != DriveWireError.None)
        {
            // Action 2, error condition: the read error, whatever the checksum.
            answer = error;
            _statistics.RecordReadError();
            LogSectorError(reread ? "REREADEX" : "READEX", drive, lsn, error);
        }
        else if (clientChecksum == DriveWireChecksum.Compute(_sector))
        {
            answer = DriveWireError.None;
            _statistics.RecordSectorRead();
        }
        else
        {
            answer = DriveWireError.Crc;
            _statistics.RecordChecksumError();
            _logger.LogWarning(
                "DriveWire session {Id}: checksum mismatch on read of drive {Drive} LSN {Lsn}", Id, drive, lsn);
        }

        _reply[0] = answer;
        await SendAsync(1).ConfigureAwait(false);
        return true;
    }

    // OP_WRITE and OP_REWRITE (specification, "The Write Transaction" and "The ReWrite Transaction"): drive, LSN, 256 bytes
    // and the client's checksum in, high byte first; one byte out: E$CRC when the checksums differ (nothing is written), else
    // the result of the write. This is what rbdw.asm Write does.
    private async ValueTask<bool> WriteAsync(bool rewrite)
    {
        if (!await ReceiveAsync(4 + SectorSize + 2).ConfigureAwait(false))
        {
            return false;
        }

        byte drive = _frame[0];
        uint lsn = Lsn(_frame, 1);
        ReadOnlySpan<byte> data = _frame.AsSpan(4, SectorSize);
        ushort clientChecksum = (ushort)((_frame[4 + SectorSize] << 8) | _frame[5 + SectorSize]);

        byte answer;
        if (clientChecksum != DriveWireChecksum.Compute(data))
        {
            answer = DriveWireError.Crc;
            _statistics.RecordChecksumError();
            _logger.LogWarning(
                "DriveWire session {Id}: checksum mismatch on write of drive {Drive} LSN {Lsn}", Id, drive, lsn);
        }
        else
        {
            answer = WriteSector(drive, lsn, data);
            if (answer == DriveWireError.None)
            {
                _statistics.RecordSectorWritten();
            }
            else
            {
                _statistics.RecordWriteError();
                LogSectorError(rewrite ? "REWRITE" : "WRITE", drive, lsn, answer);
            }
        }

        _reply[0] = answer;
        await SendAsync(1).ConfigureAwait(false);
        return true;
    }

    // OP_GETSTAT and OP_SETSTAT: drive and status code in, no answer (specification, "The GetStat/SetStat Transaction": "The
    // Server may wish to log this information, but does not act upon the packet itself").
    private async ValueTask<bool> StatAsync(byte op)
    {
        if (!await ReceiveAsync(2).ConfigureAwait(false))
        {
            return false;
        }

        _logger.LogDebug(
            "DriveWire session {Id}: {Op} drive {Drive} code ${Code:X2}",
            Id,
            op == DriveWireOpCode.GetStat ? "GETSTAT" : "SETSTAT",
            _frame[0],
            _frame[1]);
        return true;
    }

    // OP_PRINT: one data byte in, added to the print buffer (specification, "Print Transaction").
    private async ValueTask<bool> PrintAsync()
    {
        if (!await ReceiveAsync(1).ConfigureAwait(false))
        {
            return false;
        }

        _printBuffer.Add(_frame[0]);
        return true;
    }

    // OP_PRINTFLUSH: the buffer is a finished job (specification, "Print Flush Transaction"). It goes to the event and, when a
    // print folder is set, to a new file there.
    private void PrintFlush()
    {
        byte[] job = _printBuffer.ToArray();
        _printBuffer.Clear();

        if (!string.IsNullOrEmpty(_options.PrintDirectory))
        {
            string name = "print-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) +
                "-" + Id + ".prn";
            string path = Path.Combine(_options.PrintDirectory, name);
            try
            {
                Directory.CreateDirectory(_options.PrintDirectory);
                File.WriteAllBytes(path, job);
                _logger.LogInformation("DriveWire session {Id}: print job of {Count} bytes written to {Path}", Id, job.Length, path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogError(ex, "DriveWire session {Id}: print job of {Count} bytes could not be written to {Path}", Id, job.Length, path);
            }
        }

        PrintJobReceived?.Invoke(this, job);
    }

    // OP_NAMEOBJ_MOUNT and OP_NAMEOBJ_CREATE: a length byte and the name in; one byte out, the drive number (1 to 255) or 0
    // (specification, "Named Objects"). The NitrOS-9 drivers do not send these.
    private async ValueTask<bool> NamedObjectAsync(byte op)
    {
        if (!await ReceiveAsync(1).ConfigureAwait(false))
        {
            return false;
        }

        int length = _frame[0];
        if (!await ReceiveAsync(length).ConfigureAwait(false))
        {
            return false;
        }

        string name = Encoding.ASCII.GetString(_frame, 0, length);
        _reply[0] = op == DriveWireOpCode.NamedObjectMount ? MountNamedObject(name) : CreateNamedObject(name);
        await SendAsync(1).ConfigureAwait(false);
        return true;
    }

    // Mounts a file of the named object folder by name. "If a named object is already mounted, the server may return the
    // drive number already containing the object rather than remounting it" - this server does, so one file is never open in
    // two drives. A new mount goes to the lowest empty drive number from 1, because 0 is the failure answer.
    private byte MountNamedObject(string name)
    {
        string? directory = _options.NamedObjectDirectory;
        if (string.IsNullOrEmpty(directory))
        {
            _logger.LogInformation("DriveWire session {Id}: named object \"{Name}\" refused: no named object folder is set", Id, name);
            return 0;
        }

        if (!IsPlainFileName(name))
        {
            _logger.LogWarning("DriveWire session {Id}: named object \"{Name}\" refused: not a plain file name", Id, name);
            return 0;
        }

        string path = Path.GetFullPath(Path.Combine(directory, name));
        if (!File.Exists(path))
        {
            _logger.LogInformation("DriveWire session {Id}: named object \"{Name}\" not found", Id, name);
            return 0;
        }

        int existing = _drives.Find(path);
        if (existing > 0)
        {
            return (byte)existing;
        }

        if (existing == 0)
        {
            _logger.LogWarning(
                "DriveWire session {Id}: named object \"{Name}\" is mounted in drive 0, which the answer cannot name (0 means failure)",
                Id,
                name);
            return 0;
        }

        int free = _drives.FindFree(1);
        if (free < 0)
        {
            _logger.LogWarning("DriveWire session {Id}: named object \"{Name}\" refused: no empty drive", Id, name);
            return 0;
        }

        try
        {
            _drives.Mount((byte)free, DriveWireImageDrive.Open(path, false));
            _logger.LogInformation("DriveWire session {Id}: named object \"{Name}\" mounted in drive {Drive}", Id, name, free);
            return (byte)free;
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is NotSupportedException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "DriveWire session {Id}: named object \"{Name}\" could not be mounted", Id, name);
            return 0;
        }
    }

    // OP_NAMEOBJ_CREATE fails "if it does exist" and "if the object ultimately cannot be mounted in a drive". A new object is an
    // empty file, and the drivers this server mounts with need an image with a recognisable layout, so a new object could never
    // be mounted: the answer is always 0 and no file is created.
    private byte CreateNamedObject(string name)
    {
        _logger.LogInformation(
            "DriveWire session {Id}: named object create \"{Name}\" refused: an empty new image cannot be mounted", Id, name);
        return 0;
    }

    // OP_SERREAD: two bytes out (specification, "The SERREAD / POLL Transaction").
    private async ValueTask<bool> SerialPollAsync()
    {
        VirtualSerial.BuildPollAnswer(_reply.AsSpan(0, 2));
        await SendAsync(2).ConfigureAwait(false);
        return true;
    }

    // OP_SERREADM: channel and count in; that many bytes out, or nothing when the channel holds fewer (specification, "The
    // SERREADM Transaction").
    private async ValueTask<bool> SerialReadMultipleAsync()
    {
        if (!await ReceiveAsync(2).ConfigureAwait(false))
        {
            return false;
        }

        int channel = _frame[0];
        int count = _frame[1];
        if (!VirtualSerial.TryTake(channel, _reply.AsSpan(0, count)))
        {
            _logger.LogWarning(
                "DriveWire session {Id}: SERREADM asked for {Count} bytes on channel {Channel}, which holds {Pending}; nothing sent",
                Id,
                count,
                channel,
                VirtualSerial.Pending(channel));
            return true;
        }

        await SendAsync(count).ConfigureAwait(false);
        return true;
    }

    // OP_SERWRITE: channel and one data byte in (specification, "The SERWRITE Transaction").
    private async ValueTask<bool> SerialWriteAsync()
    {
        if (!await ReceiveAsync(2).ConfigureAwait(false))
        {
            return false;
        }

        VirtualSerial.Receive(_frame[0], _frame.AsSpan(1, 1));
        return true;
    }

    // OP_FASTWRITE: the channel is in the operation code, one data byte in (specification, "The FASTWRITE Transaction";
    // scdwv.asm Write adds the port number to 128).
    private async ValueTask<bool> FastWriteAsync(int channel)
    {
        if (!await ReceiveAsync(1).ConfigureAwait(false))
        {
            return false;
        }

        VirtualSerial.Receive(channel, _frame.AsSpan(0, 1));
        return true;
    }

    // OP_SERWRITEM: channel, count and that many bytes in (specification, "The SERWRITEM Transaction").
    private async ValueTask<bool> SerialWriteMultipleAsync()
    {
        if (!await ReceiveAsync(2).ConfigureAwait(false))
        {
            return false;
        }

        int channel = _frame[0];
        int count = _frame[1];
        if (!await ReceiveAsync(count).ConfigureAwait(false))
        {
            return false;
        }

        VirtualSerial.Receive(channel, _frame.AsSpan(0, count));
        return true;
    }

    // OP_SERSETSTAT: channel and code in, and 26 more bytes when the code is SS.ComSt; SS.Open opens the channel and SS.Close
    // closes it (specification, "The SERSETSTAT Transaction").
    private async ValueTask<bool> SerialSetStatAsync()
    {
        if (!await ReceiveAsync(2).ConfigureAwait(false))
        {
            return false;
        }

        int channel = _frame[0];
        byte code = _frame[1];
        if (code == DriveWireVirtualSerial.SetStatComSt)
        {
            return await ReceiveAsync(DriveWireVirtualSerial.ComStBytes).ConfigureAwait(false);
        }

        if (code == DriveWireVirtualSerial.SetStatOpen)
        {
            VirtualSerial.SetOpen(channel, true);
        }
        else if (code == DriveWireVirtualSerial.SetStatClose)
        {
            VirtualSerial.SetOpen(channel, false);
        }

        return true;
    }

    // OP_SERINIT and OP_SERTERM: channel in; open or close it (specification, "The SERINIT Transaction", "The SERTERM
    // Transaction").
    private async ValueTask<bool> SerialInitTermAsync(bool open)
    {
        if (!await ReceiveAsync(1).ConfigureAwait(false))
        {
            return false;
        }

        VirtualSerial.SetOpen(_frame[0], open);
        return true;
    }

    // Reads a sector for an answer. On any error the buffer is all zero bytes, which is the specification's Read Failure
    // packet for READEX ("0-255 | 0 (256 bytes of 0)").
    private byte ReadSector(byte driveNumber, uint lsn, Span<byte> buffer)
    {
        IDriveWireDrive? drive = _drives.Get(driveNumber);
        byte error = drive == null ? DriveWireError.NotReady : drive.ReadSector(lsn, buffer);
        if (error != DriveWireError.None)
        {
            buffer.Slice(0, SectorSize).Clear();
        }

        _drives.GetCounters(driveNumber).RecordRead(error == DriveWireError.None);
        return error;
    }

    private byte WriteSector(byte driveNumber, uint lsn, ReadOnlySpan<byte> data)
    {
        IDriveWireDrive? drive = _drives.Get(driveNumber);
        byte error = drive == null ? DriveWireError.NotReady : drive.WriteSector(lsn, data);
        _drives.GetCounters(driveNumber).RecordWrite(error == DriveWireError.None);
        return error;
    }

    private void LogSectorError(string op, byte driveNumber, uint lsn, byte error)
    {
        IDriveWireDrive? drive = _drives.Get(driveNumber);
        string reason = drive is DriveWireImageDrive image && image.LastError != null
            ? image.LastError
            : (drive == null ? "no image in the drive" : "the drive reported an error");
        _logger.LogWarning(
            "DriveWire session {Id}: {Op} drive {Drive} LSN {Lsn} answered ${Error:X2}: {Reason}",
            Id,
            op,
            driveNumber,
            lsn,
            error,
            reason);
    }

    // The 24-bit LSN, high byte first (specification, "The Read Transaction", bytes 2 to 4).
    private static uint Lsn(byte[] frame, int offset)
    {
        return (uint)((frame[offset] << 16) | (frame[offset + 1] << 8) | frame[offset + 2]);
    }

    // A named object name may name a file in the named object folder and nothing else.
    private static bool IsPlainFileName(string name)
    {
        if (name.Length == 0 || name == "." || name == ".." || name.Contains('/') || name.Contains('\\') || name.Contains(':'))
        {
            return false;
        }

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    // Reads the next bytes of the transaction into the start of the frame buffer, each within the transaction time limit.
    private ValueTask<bool> ReceiveAsync(int count)
    {
        return _reader!.ReadExactlyAsync(_frame.AsMemory(0, count), _timeout, _cancellation);
    }

    // Sends the first bytes of the reply buffer.
    private async ValueTask SendAsync(int count)
    {
        await _stream!.WriteAsync(_reply.AsMemory(0, count), _cancellation).ConfigureAwait(false);
        await _stream.FlushAsync(_cancellation).ConfigureAwait(false);
        _statistics.RecordBytesSent(count);
    }
}
