//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using Microsoft.AspNetCore.SignalR;
using RetroIO.Management.Api.Hubs;
using RetroIO.Management.Contracts;
using RetroIO.Protocols.Econet.FileServer.Sessions;
using RetroIO.Protocols.Econet.Server;
using RetroIO.Protocols.Tnfs.Server;

namespace RetroIO.Management.Api.Services;

/// <summary>
/// Background service that pushes live data to connected clients
/// </summary>
public class LiveDataService : BackgroundService
{
    private readonly IHubContext<ServerHub, IServerHubClient> _hubContext;
    private readonly InMemoryLogProvider _logProvider;
    private readonly IServerManager _serverManager;
    private readonly ILogger<LiveDataService> _logger;
    // Both are null when no TNFS server runs in this process (the same rule TnfsController follows).
    private readonly SessionManager? _tnfsSessions;
    private readonly TnfsServerStatistics? _tnfsStatistics;
    // Both are null when no Econet server runs in this process (the same rule EconetController follows).
    private readonly FileServerSessionManager? _econetSessions;
    private readonly EconetServerStatistics? _econetStatistics;
    // How many log entries have been pushed so far. This is a running total, not a position in the
    // buffer: the buffer drops its oldest entries once it is full, so a position would stop moving.
    private long _logsPushed = 0;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="hubContext">
    /// The hub context used to send to all clients.
    /// </param>
    /// <param name="logProvider">
    /// The provider whose entries are pushed.
    /// </param>
    /// <param name="serverManager">
    /// The server manager that supplies the status.
    /// </param>
    /// <param name="logger">
    /// The logger for service messages.
    /// </param>
    /// <param name="tnfsSessions">
    /// The TNFS session manager, or null when no TNFS server runs in this process.
    /// </param>
    /// <param name="tnfsStatistics">
    /// The TNFS server statistics, or null when no TNFS server runs in this process.
    /// </param>
    /// <param name="econetSessions">
    /// The Econet session manager, or null when no Econet server runs in this process.
    /// </param>
    /// <param name="econetStatistics">
    /// The Econet server statistics, or null when no Econet server runs in this process.
    /// </param>
    public LiveDataService(
        IHubContext<ServerHub, IServerHubClient> hubContext,
        InMemoryLogProvider logProvider,
        IServerManager serverManager,
        ILogger<LiveDataService> logger,
        SessionManager? tnfsSessions = null,
        TnfsServerStatistics? tnfsStatistics = null,
        FileServerSessionManager? econetSessions = null,
        EconetServerStatistics? econetStatistics = null)
    {
        _hubContext = hubContext;
        _logProvider = logProvider;
        _serverManager = serverManager;
        _logger = logger;
        _tnfsSessions = tnfsSessions;
        _tnfsStatistics = tnfsStatistics;
        _econetSessions = econetSessions;
        _econetStatistics = econetStatistics;
    }

    /// <summary>
    /// Once a second, pushes the server status, new log entries, TNFS statistics and Econet statistics to all clients until the token is cancelled.
    /// </summary>
    /// <remarks>
    /// An exception in one round is logged and the loop continues. The TNFS statistics are the live counters of the TNFS server in this process, and 0 when there is none.
    /// </remarks>
    /// <param name="stoppingToken">
    /// A token that stops the loop.
    /// </param>
    /// <returns>
    /// A task that completes when the loop ends.
    /// </returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Live data service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PushServerStatusAsync();
                await PushNewLogsAsync();
                await PushTnfsStatsAsync();
                await PushEconetStatsAsync();

                await Task.Delay(1000, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error pushing live data");
            }
        }

        _logger.LogInformation("Live data service stopped");
    }

    // Every push below goes through the typed IServerHubClient, so the compiler checks the
    // payload against what the clients deserialise. These used to be anonymous objects sent by
    // method-name string: the Category and Exception fields of a log entry were quietly dropped
    // on the way out, and nothing would have caught a renamed property on either side.

    private async Task PushServerStatusAsync()
    {
        ServerStatusResponse statusResponse = _serverManager.GetStatus();

        ServerStatusUpdate status = new ServerStatusUpdate
        {
            IsRunning = statusResponse.IsRunning,
            Uptime = statusResponse.Uptime
        };

        await _hubContext.Clients.All.ServerStatus(status);
    }

    private async Task PushNewLogsAsync()
    {
        var logs = _logProvider.GetLogEntriesSince(_logsPushed, out long totalAdded);
        if (logs.Count == 0) return;

        int newCount = logs.Count;
        LogEntryDto[] newLogs = new LogEntryDto[newCount];

        for (int i = 0; i < newCount; i++)
        {
            var entry = logs[i];
            newLogs[i] = new LogEntryDto
            {
                Timestamp = entry.Timestamp,
                Level = entry.Level,
                Category = entry.Category,
                Message = entry.Message,
                Exception = entry.Exception
            };
        }

        _logsPushed = totalAdded;

        await _hubContext.Clients.All.NewLogs(newLogs);
    }

    private async Task PushTnfsStatsAsync()
    {
        // Same sources and same zero-when-absent rule as TnfsController.GetStatistics.
        SessionStatistics? sessionStats = _tnfsSessions?.GetStatistics();
        TnfsStatsUpdate stats = new TnfsStatsUpdate
        {
            ActiveConnections = sessionStats?.TotalSessions ?? 0,
            TotalRequests = _tnfsStatistics?.TotalRequests ?? 0L,
            TotalBytesTransferred = _tnfsStatistics?.BytesTransferred ?? 0L
        };

        await _hubContext.Clients.All.TnfsStats(stats);
    }

    private async Task PushEconetStatsAsync()
    {
        // Same sources and same zero-when-absent rule as EconetController.GetStatistics.
        EconetStatsUpdate stats = new EconetStatsUpdate
        {
            ActiveSessions = _econetSessions?.GetActiveSessionCount() ?? 0,
            TotalRequests = _econetStatistics?.TotalRequests ?? 0L,
            TotalBytesTransferred = _econetStatistics?.BytesTransferred ?? 0L
        };

        await _hubContext.Clients.All.EconetStats(stats);
    }
}
