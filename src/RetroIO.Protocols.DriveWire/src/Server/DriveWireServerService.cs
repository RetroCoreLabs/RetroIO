//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroIO.Protocols.DriveWire.Drives;

namespace RetroIO.Protocols.DriveWire.Server;

/// <summary>
/// The DriveWire server as a hosted service: mounts the configured drives, listens on TCP and runs one
/// <see cref="DriveWireSession"/> per connection.
/// </summary>
/// <remarks>
/// <para>
/// A Becker port connects once per emulated machine and keeps the connection, so each connection is one client. Several
/// clients may be connected at the same time; they share the drive table.
/// </para>
/// <para>
/// Stopping closes the listener and every connection and ejects every drive, which closes the image files. Starting again
/// mounts the configured drives again, so a host can stop the service, change an image file and start it.
/// </para>
/// </remarks>
public sealed class DriveWireServerService : BackgroundService
{
    private readonly DriveWireServerOptions _options;
    private readonly DriveWireDriveTable _drives;
    private readonly DriveWireServerStatistics _statistics;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider? _timeProvider;
    private readonly ConcurrentDictionary<long, DriveWireSession> _sessions = new ConcurrentDictionary<long, DriveWireSession>();
    private TaskCompletionSource<IPEndPoint> _listeningNow =
        new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="options">
    /// The server settings.
    /// </param>
    /// <param name="drives">
    /// The drive table to mount the configured drives in.
    /// </param>
    /// <param name="statistics">
    /// The counters to update.
    /// </param>
    /// <param name="loggerFactory">
    /// Makes the loggers, or null for no logging.
    /// </param>
    /// <param name="timeProvider">
    /// The clock OP_TIME reads, or null for the system clock.
    /// </param>
    public DriveWireServerService(
        DriveWireServerOptions options,
        DriveWireDriveTable drives,
        DriveWireServerStatistics statistics,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<DriveWireServerService>();
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Raised when a client finishes a print job. The arguments are the session and the job's bytes.
    /// </summary>
    public event Action<DriveWireSession, byte[]>? PrintJobReceived;

    /// <summary>
    /// Raised when a client connects, before its first transaction is read. A host can attach to the session's virtual
    /// serial channels here.
    /// </summary>
    public event Action<DriveWireSession>? SessionStarted;

    /// <summary>
    /// Gets the drive table.
    /// </summary>
    public DriveWireDriveTable Drives => _drives;

    /// <summary>
    /// Gets the counters.
    /// </summary>
    public DriveWireServerStatistics Statistics => _statistics;

    /// <summary>
    /// Gets the settings.
    /// </summary>
    public DriveWireServerOptions Options => _options;

    /// <summary>
    /// Gets the end point the server listens on once it has started, or null while it is not listening.
    /// </summary>
    /// <remarks>
    /// With <see cref="DriveWireServerOptions.Port"/> set to 0 the system picks a free port; this gives the one it picked.
    /// </remarks>
    public IPEndPoint? LocalEndPoint { get; private set; }

    /// <summary>
    /// Waits until the server listens.
    /// </summary>
    /// <param name="cancellationToken">
    /// Stops the wait.
    /// </param>
    /// <returns>
    /// The end point it listens on.
    /// </returns>
    public Task<IPEndPoint> WaitUntilListeningAsync(CancellationToken cancellationToken)
    {
        return _listeningNow.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the sessions that are connected now.
    /// </summary>
    /// <returns>
    /// A new array of the sessions.
    /// </returns>
    public DriveWireSession[] GetSessions()
    {
        ICollection<DriveWireSession> values = _sessions.Values;
        DriveWireSession[] result = new DriveWireSession[values.Count];
        values.CopyTo(result, 0);
        return result;
    }

    /// <summary>
    /// Mounts an image file in a drive number while the server runs.
    /// </summary>
    /// <param name="driveNumber">
    /// The drive number.
    /// </param>
    /// <param name="path">
    /// The image file.
    /// </param>
    /// <param name="readOnly">
    /// True to refuse writes.
    /// </param>
    /// <remarks>
    /// The exceptions of <see cref="DriveWireImageDrive.Open"/> are passed on; the drive number keeps what it had.
    /// </remarks>
    public void Mount(byte driveNumber, string path, bool readOnly)
    {
        DriveWireImageDrive drive = DriveWireImageDrive.Open(path, readOnly);
        _drives.Mount(driveNumber, drive);
        _logger.LogInformation(
            "DriveWire drive {Drive}: {Path} ({Sectors} sectors, {FileSystem}{ReadOnly})",
            driveNumber,
            drive.Name,
            drive.TotalSectors,
            drive.FileSystemType,
            readOnly ? ", read-only" : string.Empty);
    }

    /// <summary>
    /// Starts the service. A service that was stopped can be started again.
    /// </summary>
    /// <param name="cancellationToken">
    /// Stops the start.
    /// </param>
    /// <returns>
    /// A task that completes when the service has started.
    /// </returns>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listeningNow.Task.IsCompleted)
        {
            _listeningNow = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Mounts the configured drives, listens and accepts connections until the service is stopped.
    /// </summary>
    /// <param name="stoppingToken">
    /// Signals that the service is stopping.
    /// </param>
    /// <returns>
    /// A task that completes when the listener and every session have ended.
    /// </returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MountConfiguredDrives();

        IPAddress address = ParseBindAddress(_options.BindAddress);
        TcpListener listener = new TcpListener(address, _options.Port);
        List<Task> sessionTasks = new List<Task>();
        try
        {
            listener.Start();
            LocalEndPoint = (IPEndPoint)listener.LocalEndpoint;
            _logger.LogInformation("DriveWire server listening on {EndPoint}", LocalEndPoint);
            _listeningNow.TrySetResult(LocalEndPoint);

            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                sessionTasks.Add(RunClientAsync(client, stoppingToken));
                sessionTasks.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A normal stop. It must not be thrown on: a hosted service that faults stops the whole host, and the management
            // API can stop this service on its own while the host keeps running.
        }
        catch (SocketException ex)
        {
            _listeningNow.TrySetException(ex);
            _logger.LogError(ex, "DriveWire server could not listen on {Address}:{Port}", address, _options.Port);
            throw;
        }
        finally
        {
            listener.Stop();
            LocalEndPoint = null;
            await Task.WhenAll(sessionTasks).ConfigureAwait(false);
            _drives.EjectAll();
            _logger.LogInformation("DriveWire server stopped");
        }
    }

    private void MountConfiguredDrives()
    {
        List<DriveWireDriveOptions> drives = _options.Drives;
        for (int i = 0; i < drives.Count; i++)
        {
            DriveWireDriveOptions entry = drives[i];
            if (entry.Drive < 0 || entry.Drive > 255)
            {
                _logger.LogError("DriveWire drive number {Drive} for {Path} is not 0 to 255; not mounted", entry.Drive, entry.Path);
                continue;
            }

            try
            {
                Mount((byte)entry.Drive, entry.Path, entry.ReadOnly);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is NotSupportedException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // One bad image must not keep the other drives from being served. The drive stays empty, so the client gets
                // E$NotRdy for it, and the reason is in the log.
                _logger.LogError(ex, "DriveWire drive {Drive}: {Path} could not be mounted", entry.Drive, entry.Path);
            }
        }
    }

    private async Task RunClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        DriveWireSession session = new DriveWireSession(
            _drives,
            _statistics,
            _options,
            remote,
            _loggerFactory.CreateLogger<DriveWireSession>(),
            _timeProvider);

        session.PrintJobReceived += OnPrintJob;
        _sessions[session.Id] = session;
        _statistics.RecordConnectionOpened();
        _logger.LogInformation("DriveWire client {Remote} connected (session {Id})", remote, session.Id);
        try
        {
            client.NoDelay = true;
            SessionStarted?.Invoke(session);
            using NetworkStream stream = client.GetStream();
            await session.RunAsync(stream, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
            _sessions.TryRemove(session.Id, out _);
            session.PrintJobReceived -= OnPrintJob;
            _statistics.RecordConnectionClosed();
            _logger.LogInformation("DriveWire client {Remote} disconnected (session {Id})", remote, session.Id);
        }
    }

    private void OnPrintJob(DriveWireSession session, byte[] job)
    {
        PrintJobReceived?.Invoke(session, job);
    }

    private static IPAddress ParseBindAddress(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(text, out IPAddress? address))
        {
            return address;
        }

        throw new ArgumentException("The DriveWire bind address is not an IP address: " + text, nameof(text));
    }
}
